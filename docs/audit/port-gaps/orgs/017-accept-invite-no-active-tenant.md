# Finding 017: `POST /orgs/invites/accept` — 500 on Re-Accept, No Active-Tenant Update, No Event

**Scope**: orgs
**Severity**: P1 (feature broken)
**Status**: Behavioral drift (semantics diverged)
**Estimated port effort**: 2h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/orgs/index.ts`.

- File: `packages/api/src/routes/orgs/index.ts:537-605`.
- Contract/behavior: hash incoming token, look up the invite by hash, reject if missing/expired/already-accepted with distinct 400 messages. If the user is already a member of the target tenant, mark the invite accepted (idempotent) and return a friendly message. Otherwise call `acceptInvite`, add the membership, set the user's `active tenant` if they don't have one, and emit `TENANT.MEMBER_JOINED.SUCCESS`.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/orgs/index.ts (9e9a57c~1) L553-L605
const tokenHash = createHash('sha256').update(token).digest('hex');
const invite = await membershipStore.getInviteByTokenHash(tokenHash);

if (!invite) {
  return reply.status(400).send({ error: 'Invalid or expired invite token' });
}

// Check if already accepted
if (invite.acceptedAt !== null) {
  return reply.status(400).send({ error: 'Invite has already been accepted' });
}

// Check expiry
if (new Date(invite.expiresAt) < new Date()) {
  return reply.status(400).send({ error: 'Invite has expired' });
}

// Check if already a member
const existingMembership = await membershipStore.getMembership(invite.tenantId, jwt.sub);
if (existingMembership) {
  // Mark invite as accepted anyway
  await membershipStore.acceptInvite(invite.id);
  return reply.send({ message: 'You are already a member of this organization' });
}

// Accept invite
await membershipStore.acceptInvite(invite.id);

// Add as member
await membershipStore.addMember(invite.tenantId, jwt.sub, invite.role);

// Set as active tenant if user doesn't have one
const user = await userStore.getUser(jwt.sub);
if (user && !user.tenantId) {
  await userStore.updateActiveTenant(jwt.sub, invite.tenantId);
}

request.log.info({
  event: 'TENANT.MEMBER_JOINED.SUCCESS',
  tenantId: invite.tenantId,
  userId: jwt.sub,
  role: invite.role,
}, 'User joined organization via invite');

return reply.send({
  tenantId: invite.tenantId,
  role: invite.role,
  message: 'You have joined the organization',
});
```

- Dependencies: `ITenantMembershipStore.{getInviteByTokenHash, acceptInvite, getMembership, addMember}`, `IUserStore.{getUser, updateActiveTenant}`, Pino logger.
- Tests: covered `invalid token`, `already accepted`, `expired`, `already a member` and `new member` paths.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:125-143`, `apps/tamma-elsa/src/Tamma.Api/Program.cs:366`.
- Contract/behavior: compound-check `invite is null || invite.AcceptedAt is not null || invite.ExpiresAt < DateTime.UtcNow` → 400 with a single generic message. If passes, call `AddAsync` (which throws on duplicate PK due to unique index `(TenantId, UserId)` — finding 026) then `AcceptAsync`. No idempotency for already-member case. No active-tenant update. No event emission.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs (current) L125-L143
public static async Task<IResult> AcceptInvite(
    AcceptInviteRequest req,
    IInviteRepository inviteRepo,
    ITenantMembershipRepository membershipRepo,
    ClaimsPrincipal principal)
{
    var userId = Guid.Parse(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    var tokenHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(req.Token))).ToLowerInvariant();

    var invite = await inviteRepo.GetByTokenHashAsync(tokenHash);
    if (invite is null || invite.AcceptedAt is not null || invite.ExpiresAt < DateTime.UtcNow)
        return Results.BadRequest(new { error = "Invalid or expired invite" });

    await membershipRepo.AddAsync(invite.TenantId, userId, invite.Role);
    await inviteRepo.AcceptAsync(invite.Id);

    return Results.Ok(new { message = "Invite accepted", tenantId = invite.TenantId });
}
```

`TenantMembershipRepository.AddAsync` doesn't check for pre-existing membership; the unique index `(TenantId, UserId)` on `tenant_memberships` (TammaDbContext.cs:152) raises `DbUpdateException` (Postgres unique violation) → unhandled → 500.

- Dependencies: `IInviteRepository`, `ITenantMembershipRepository`.
- Tests: none.

## 3. The gap

Concrete behavioral difference.

- TS did: distinct 400 messages for invalid/expired/already-accepted; idempotent success for already-a-member; updated `users.tenant_id` if unset; emitted event.
- C# does: single 400 message lumps three failures together; second-time accept crashes with 500 due to unique violation; never updates `users.tenant_id` so the new member's active tenant stays on whatever prior value (often NULL → causes `TenantContextMiddleware` to skip setting tid on subsequent requests, which cascades into other bugs per finding 023).
- For a user clicking the emailed accept-invite link twice: TS returns 400 with clear "already accepted" message on the second click; C# returns 500 on the first duplicate attempt.
- For a user who accepts their first-ever invite: TS sets `users.tenant_id = invite.tenantId` so the dashboard knows to show that tenant as active; C# leaves `users.tenant_id = null`, user lands on a blank dashboard with no org context.
- In production: the end-to-end invite UX is broken after first accept. The feature also cannot emit the `TENANT.MEMBER_JOINED.SUCCESS` event, breaking the audit trail for onboarding.

Error paths:
- TS error paths: `400 { "error": "Invalid or expired invite token" }`, `400 { "error": "Invite has already been accepted" }`, `400 { "error": "Invite has expired" }`, `200 { "message": "You are already a member of this organization" }` (idempotent success).
- C# error paths: `400 { "error": "Invalid or expired invite" }` (conflated), `500` on retry (unhandled `DbUpdateException`).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story's acceptance criteria for this behavior:
  - AC 7: "**Accept invite** endpoint `POST /api/v1/orgs/invites/accept` accepts `{ token }`, adds user to tenant with invited role".
  - AC 14: "`TENANT.MEMBER_JOINED.SUCCESS` events".
  - Implementation notes L143-L149: "Users switch tenants via `POST /api/v1/auth/switch-org`, which: … Updates `users.tenant_id` to the new active tenant" — same shape applies to first-time membership via accept.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Behavioral drift.
- **What's needed to finish**:
  1. Split the combined check into three distinct branches (null / already-accepted / expired), returning distinct 400 messages.
  2. Before `AddAsync`, call `GetRoleAsync(invite.TenantId, userId)` — if already a member, call `AcceptAsync` (idempotent), return 200 with "already a member" message.
  3. After `AddAsync`, load user; if `users.tenant_id` is null, call `UpdateActiveTenantAsync(userId, invite.TenantId)`.
  4. Emit `TENANT.MEMBER_JOINED.SUCCESS`.
  5. Consider the response shape: the TS returned `{ tenantId, role, message }`; match that.
- **Is it "just a stub" or is scope missing?** Scope defined in AC 7; port cut corners on error branches and side-effects.
- **Blockers**: depends on finding 008 (event emission). Finding 023 improves downstream behavior once `users.tenant_id` is set.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` (AcceptInvite).
- Files to create: `apps/tamma-elsa/tests/Tamma.Api.Tests/Orgs/AcceptInviteTests.cs`.
- Tests to add:
  - `AcceptInvite_Returns400_WhenTokenUnknown`
  - `AcceptInvite_Returns400_WhenAlreadyAccepted`
  - `AcceptInvite_Returns400_WhenExpired`
  - `AcceptInvite_ReturnsOk_WhenUserIsAlreadyMember_Idempotent`
  - `AcceptInvite_UpdatesActiveTenant_WhenUserHasNone`
  - `AcceptInvite_DoesNotUpdateActiveTenant_WhenUserAlreadyHasOne`
  - `AcceptInvite_EmitsTenantMemberJoinedSuccess_Event`
  - `AcceptInvite_DoesNotThrow_WhenCalledTwice` (second returns 400, not 500)
- Estimated effort: 2h broken down as:
  - Branch refactor: 0.5h
  - Active-tenant + event: 0.5h
  - Tests: 1h

## References

- TS source: `packages/api/src/routes/orgs/index.ts:537-605` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:125-143`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (ACs 7, 14)
- Related findings: `008-post-orgs-no-event-emission.md`, `014-create-invite-weak-token-no-email.md`, `023-tenant-context-middleware-shallow.md`, `026-tenant-memberships-pk-change.md`
