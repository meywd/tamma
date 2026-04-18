# Finding 008: `POST /orgs` Emits No `TENANT.CREATED.SUCCESS` Event

**Scope**: orgs
**Severity**: P2 (correctness/observability)
**Status**: Not-yet-implemented
**Estimated port effort**: 0.5h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/orgs/index.ts`.

- File: `packages/api/src/routes/orgs/index.ts:139-144`.
- Contract/behavior: after a successful tenant create, TS emitted a log line tagged as the DCB event `TENANT.CREATED.SUCCESS`. The same pattern recurs for member lifecycle events: `TENANT.MEMBER_REMOVED.SUCCESS` (L389-394), `TENANT.MEMBER_INVITED.SUCCESS` (L458-463), `TENANT.MEMBER_JOINED.SUCCESS` (L592-597), `TENANT.OWNERSHIP_TRANSFERRED.SUCCESS` (L746-751), `TENANT.DELETED.SUCCESS` (L843-847), `TENANT.PURGED.SUCCESS` (L819-823).
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/orgs/index.ts (9e9a57c~1) L137-L151
// Set as active tenant
await userStore.updateActiveTenant(jwt.sub, tenant.id);

request.log.info({
  event: 'TENANT.CREATED.SUCCESS',
  tenantId: tenant.id,
  userId: jwt.sub,
}, 'Organization created');

return reply.status(201).send({
  id: tenant.id,
  name: tenant.name,
  slug: tenant.slug,
  plan: tenant.plan,
});
```

- Dependencies: Pino logger injected by Fastify. Event-collection was indirect: the application log stream was aggregated and filtered on `event:` for the audit trail. Story 17-3 defined a real `IEventStore.record()` DCB pathway that was the eventual destination.
- Tests: route tests asserted the log line was emitted.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:12-36`.
- Contract/behavior: `CreateOrg` does not log, does not call `IEventRepository.AppendAsync`, does not emit a DCB event. Same gap on `RemoveMember`, `AcceptInvite`, `TransferOwnership`, `DeleteOrg`, `CreateInvite`, `UpdateMemberRole`.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs (current) L12-L36
public static async Task<IResult> CreateOrg(
    CreateOrgRequest req,
    ITenantRepository tenantRepo,
    ITenantMembershipRepository membershipRepo,
    IUserRepository userRepo,
    ClaimsPrincipal principal)
{
    var userId = Guid.Parse(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    var existing = await tenantRepo.GetBySlugAsync(req.Slug);
    if (existing is not null)
        return Results.Conflict(new { error = "Slug already taken" });

    var tenant = await tenantRepo.CreateAsync(new Tenant
    {
        Name = req.Name,
        Slug = req.Slug.ToLowerInvariant(),
        Type = "org",
        OwnerId = userId
    });

    await membershipRepo.AddAsync(tenant.Id, userId, "owner");
    return Results.Created($"/api/v1/orgs/{tenant.Id}",
        new OrgResponse(tenant.Id, tenant.Name, tenant.Slug, tenant.Type, tenant.OwnerId, tenant.Settings, tenant.CreatedAt));
}
```

- Dependencies: `IEventRepository` exists at `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` (DCB event store) and `DomainEvents` DbSet is configured in `TammaDbContext.cs:46, 388-404`. Not used here.
- Tests: none.

## 3. The gap

Concrete behavioral difference — what the audit trail sees.

- TS did: every org-lifecycle mutation produced a structured log record with `event: 'TENANT.CREATED.SUCCESS'` (and similar). Operators could grep or filter logs by event type for audit or debugging.
- C# does: tenant creates are invisible in logs; `domain_events` has no row; the audit trail for "who created which org" has to be reconstructed from the `Tenants.CreatedAt` column alone (no requester context).
- For an operator investigating "why does this user own this org?", TS produced a correlatable log record with `tenantId`, `userId`, `event` in one JSON line. C# produces nothing beyond a row in `tenants` with `OwnerId`, with no indication of when the membership was granted.
- In production, this also violates the project-wide DCB event sourcing requirement from `CLAUDE.md` ("All system actions are captured as immutable events in a single PostgreSQL stream") and Tamma's SOC2 stance on audit trails.

Error paths:
- n/a — this is an observability/audit gap on the happy path.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story's acceptance criteria for this behavior:
  - AC 14: "**Event emission**: `TENANT.CREATED.SUCCESS`, `TENANT.MEMBER_INVITED.SUCCESS`, `TENANT.MEMBER_JOINED.SUCCESS`, `TENANT.MEMBER_REMOVED.SUCCESS` events".
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

Also aligns with `CLAUDE.md` Architecture Principles §1 (Event Sourcing — DCB Pattern).

## 5. Status

- **Classification**: Not-yet-implemented.
- **What's needed to finish**:
  1. Inject `IEventRepository` (or a higher-level `IDomainEventEmitter`) into `CreateOrg` and the other mutating handlers.
  2. After successful persistence, call `await events.AppendAsync(new DomainEvent { Type = "TENANT.CREATED.SUCCESS", Tags = { { "tenantId", tenant.Id.ToString() }, { "userId", userId.ToString() } }, … })`.
  3. Mirror for `TENANT.MEMBER_REMOVED.SUCCESS`, `TENANT.MEMBER_INVITED.SUCCESS`, `TENANT.MEMBER_JOINED.SUCCESS`, `TENANT.OWNERSHIP_TRANSFERRED.SUCCESS`, `TENANT.DELETED.SUCCESS`, `TENANT.PURGED.SUCCESS`.
  4. Assert event emission in xUnit tests.
- **Is it "just a stub" or is scope missing?** Scope defined in Story 18-3 AC 14; port skipped it. The infrastructure (`IEventRepository`, `DomainEvents` DbSet) exists; just needs wiring.
- **Blockers**: depends on a confirmed event schema for the DCB store (tags shape). The `DomainEvent` entity already has `Tags`/`Metadata`/`Data` jsonb columns.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` (all 7 mutating handlers), `apps/tamma-elsa/src/Tamma.Api/Program.cs` (if `IEventRepository` needs to flow via DI into handlers).
- Files to create: `apps/tamma-elsa/tests/Tamma.Api.Tests/Orgs/EventEmissionTests.cs`.
- Tests to add:
  - `CreateOrg_Emits_TenantCreatedSuccess_Event`
  - `RemoveMember_Emits_TenantMemberRemovedSuccess_Event`
  - `CreateInvite_Emits_TenantMemberInvitedSuccess_Event`
  - `AcceptInvite_Emits_TenantMemberJoinedSuccess_Event`
  - `TransferOwnership_Emits_TenantOwnershipTransferredSuccess_Event`
  - `DeleteOrg_Emits_TenantDeletedSuccess_Event`
- Estimated effort: 0.5h broken down as:
  - Wiring emission into each handler: 0.25h
  - Tests: 0.25h

## References

- TS source: `packages/api/src/routes/orgs/index.ts:139-144, 389-394, 458-463, 592-597, 746-751, 819-823, 843-847` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (AC 14)
- Related findings: `013-delete-member-hierarchy-missing.md`, `017-accept-invite-no-active-tenant.md`, `020-transfer-ownership-non-atomic.md`, `021-delete-org-one-phase.md`
- CLAUDE.md section: "Architecture Principles §1: Event Sourcing (DCB Pattern)"
