# Story 43-5: Storage, Principal Resolution, the Resolver, and Audit

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **tenant admin** (in SaaS) or **the sole user** (self-hosted),
I want the autonomy assignment I make for an action or a group to be durably stored, resolvable from every plane the system runs on — HTTP request, Elsa engine, background sweeper — and to survive a restart,
So that "deploy needs a person below level 90" is a fact the system can answer at the moment it matters, from a principal it can actually identify, with an audit row proving what was decided.

## Priority

P0 — This is the load-bearing middle of the epic. Story 43-6 (admin API) writes through this storage;
Story 43-9 (the five seams) reads through this resolver. Nothing above it can be built or tested first.

## Architectural Context (READ FIRST)

### Control-plane residency is FORCED, not preferred

Three independently fatal facts, each verified:

1. **Background actors and `PlatformTaskWorker` have no ambient tenant context.** The shipped fail-loud
   posture for a tenant-resident read is `AcceptanceRulesRepository.RequireTenantId()` (`:21-25`) — it
   throws. A gate consulted from a sweeper would throw on every tick.
2. **The engine plane may carry no tenant at all.** `ServiceAuthPrincipal`
   (`apps/tamma-elsa/src/Tamma.Api/Auth/AuthPrincipal.cs:30-39`) is declared with `Guid? TenantId`, and its
   own doc comment says it "is populated from the `X-Tenant-Id` header when present; **otherwise null**
   (the request is platform-level)". The 17 mutating engine mediation routes execute under it.
3. **Decisively: a new tenant migration never reaches already-provisioned tenants.** `ITenantDbMigrator`
   has exactly two production call sites, both creation-only —
   `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/TenantProvisioningService.cs` (the provisioning
   path) and `apps/tamma-elsa/src/Tamma.Activities/TenantLifecycle/MigrateTenantDatabaseActivity.cs:52`
   (the explicit migrate activity). **There is no startup sweep.** A table added under `Migrations/Tenant/`
   would simply not exist for any tenant provisioned before it, and every gate read would return
   Postgres `42P01 undefined_table`.

So: **both tables are control-plane resident in both modes.** The precedent is
`apps/tamma-elsa/src/Tamma.Data/Entities/TenantAgentEnablement.cs:14-21`, whose doc comment states the rule
verbatim — CP-resident in both modes because it gates a CP-resident catalog. Here the catalog lives in the
binary, which is stronger still.

### The one place this story deviates from that precedent — and it is the point

`TenantAgentEnablement`'s doc comment continues: "Hence it joins the `Program.cs` startup-reset DROP list
and the `ControlPlaneDbContextModelTests` strict entity list." **This story takes the second half and
deliberately refuses the first.**

`apps/tamma-elsa/src/Tamma.Api/Program.cs:3234-3276` runs
`DROP TABLE IF EXISTS … CASCADE` over ~55 tables **on every startup** unless `TAMMA_PRESERVE_DB=1`. Every
other table on that list is operational data — events, outbox rows, webhook deliveries, workflow instances.
`action_assignments` and `action_authorizations` are not operational data: **they are the only thing between
an agent and a production deploy.** A CP-resident safety table on that list would silently revert every
admin tightening on the next restart — a governance surface that lies. The exclusion is a deliberate,
tested exception, and the test reads the actual `ExecuteSqlRaw` string so a future "add it for consistency"
edit fails the build.

### Naming: `AutonomyGate*`, never `ActionGate*`

`apps/tamma-elsa/src/Tamma.Activities/Security/ActionGate.cs:17` is a shipped, DI-registered
(`Program.cs:750`), constructor-injected type holding the 20-regex shell denylist. `Tamma.Api` references
`Tamma.Activities`, so `ActionGateService` inside `Tamma.Api` collides in every file that uses both. Every
type this story adds is `AutonomyGate*` / `ActionAssignment*` / `GovernancePrincipal*`.
(One exception, deliberate: the **audit** type is `ActionGateEventsService` emitting `ACTION.GATE.*` event
strings — those are wire values consumed by dashboards, and `AUTONOMY.GATE.*` would be a second name for
the same thing. The C# type name is qualified enough not to collide.)

### The Core/Api split, copied exactly

`Tamma.Core` has **zero** `ProjectReference`s — it cannot touch a database. The shipped pattern is
`apps/tamma-elsa/src/Tamma.Core/Documents/Policy/IAcceptanceRulesResolver.cs`: interface + model in Core,
EF-backed `AcceptanceRulesService` in `Tamma.Api` (its own doc comment records this as Story 39-5 D1). This
story repeats it precisely: `IAutonomyGate` + the **pure** `AutonomyGateEvaluator` in
`Tamma.Core/Actions/`; `AutonomyGateService` in `Tamma.Api/Services/Actions/`.

Note the same file's naming rationale, which this story inherits: `ForTenant`-suffixed method names rather
than `Guid`/`Guid?` overloads, because a non-null `Guid` binds to both and the non-nullable always wins —
silently routing single-user callers onto the SaaS path.

### The always-escalate bridge

`apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceGuardrails.TryPreGate` (`:45`) has **zero
production call sites** — it is referenced only from `tests/Tamma.Core.Tests/Documents/Policy/AcceptanceGuardrailsTests.cs`.
This story gives it its first, by taking **only** its always-escalate contribution as a floor. `TryPreGate`
also implements an unrelated rounds-exhausted short-circuit (`AcceptanceGuardrailsTests.cs:58,66` exercise
it); the evaluator must ignore that outcome — the document lifecycle keeps owning rounds.

### Shape to follow, verbatim

`TammaModelConfiguration.cs:1621-1654` (the `AcceptanceRulesOverride` block) is the template for the EF
config — CHECK constraint on `ToTable`, `HasKey`, `gen_random_uuid()` default, `HasDefaultValueSql("now()")`,
`HasIndex(...).IsUnique().AreNullsDistinct(false)` with an explicit `HasDatabaseName`. **Two things there
must NOT be copied**: `ApplyTenantFilter` and `omitTenantIdColumn` — both are tenant-residency machinery,
and `ApplyTenantFilter` would break the platform-ceiling read path outright.

## Acceptance Criteria

1. **`action_assignments` exists, CP-resident, with THREE scopes.**
   `apps/tamma-elsa/src/Tamma.Data/Entities/ActionAssignment.cs` + EF config beside the
   `AcceptanceRulesOverride` block, + a migration under `Migrations/ControlPlane/`. Columns:
   `id`, `user_id` (null), `tenant_id` (null), `target_kind` (`action` | `group` | `mode`), `target_key`,
   `min_autonomy` (**NULLABLE**), `enforce` (**NULLABLE**), `enabled` (**NULLABLE**), `allowed_roles`
   (`text[]`, **NULLABLE**), `note`, `version`, `created_by`, `updated_by`, `created_at`, `updated_at`.
   The principal CHECK is **`ck_action_assignments_principal_scope`** and admits three cases — user-only,
   tenant-only, **and neither** (the platform ceiling). It is deliberately NOT named `_principal_xor`, so a
   reader who pattern-matches the six shipped XOR stores is stopped by the name. A second CHECK pins
   `(target_kind = 'mode') = (min_autonomy IS NULL)`. Unique index over
   `(user_id, tenant_id, target_kind, target_key)` with `AreNullsDistinct(false)`.

2. **All three policy columns are nullable, so "unset" is representable.** A threshold-only write must not
   silently re-enable a group-disabled action; a non-nullable `enabled DEFAULT TRUE` would do exactly that.
   Tested: writing `min_autonomy` alone leaves `enforce`/`enabled`/`allowed_roles` NULL and resolution
   continues to inherit them from the next tier.

3. **NO database CHECK on `min_autonomy`.** A CHECK is frozen into a migration snapshot and would be a
   second permanent hardcoding of the dial bound, defeating Story 43-1. The single source is
   `AutonomyDial`, validated in the domain — the same posture the acceptance-rules body already takes (no
   `[Range]`, no CHECK, an opaque `jsonb` body). A test asserts the migration text contains no numeric
   constraint on the column.

4. **`action_authorizations` exists** with the same three-scope CHECK: `id`, `tenant_id?`/`user_id?`,
   `correlation_id`, `target_kind`/`target_key` (the **granted scope** — an action or a whole group),
   `state ∈ {pending, granted, denied, expired}`, `requested_at_utc` **NOT NULL from day one**,
   `decided_at_utc`, `decided_by_user_id`, `expires_at_utc` (default +24h, config
   `Tamma:Governance:AuthorizationTtlHours`), `consumed_at_utc`, `reason`, `autonomy_level_at_request`.
   A partial unique index over `(tenant_id, user_id, correlation_id, target_kind, target_key)`
   `NULLS NOT DISTINCT WHERE state IN ('pending','granted')`.

5. **Both tables are on the strict CP entity list and NOT on the destructive DROP list.**
   `tests/Tamma.Api.Tests/Epic28/ControlPlaneDbContextModelTests.cs`'s `BeEquivalentTo` set gains both
   names (an unlisted table fails there). `Program.cs:3234-3276` is **not** modified. A dedicated test,
   `ActionAssignmentResidencyTests.Tables_AreNotInTheDestructiveDropList`, reads the `ExecuteSqlRaw` string
   from source and fails if either name appears — with a message explaining why, citing this AC.

6. **Repository surfaces are parallel and never joined, and reads do not use the tenant factory.**
   `apps/tamma-elsa/src/Tamma.Data/Repositories/IActionAssignmentRepository.cs` carries the
   parallel-surfaces invariant in its doc comment (the `IAcceptanceRulesRepository.cs:8-12` wording,
   extended to **three** planes). The implementation reads `ControlPlaneDbContext` **directly** — not
   `ITenantDbContextFactory` + `IgnoreQueryFilters()`, which is the tenant-resident idiom — and carries
   explicit other-key-null predicates (`AcceptanceRulesRepository.cs:34,108` idiom). No `ApplyTenantFilter`.
   Tested: `Platform_rows_are_never_returned_by_a_principal_query` and
   `Reads_DoNotUseTenantDbContextFactory`.

7. **Principal resolution is mandatory and has one documented rule per plane.**
   `IGovernancePrincipalResolver` (Tamma.Api) resolves a `GovernancePrincipal` with these branches, each
   individually tested:
   - **SaaS**: tenant id from `ITenantContext`. If absent → resolve against the **platform scope only** and
     emit `ACTION.GATE.PRINCIPAL_UNRESOLVED`. It **never** falls through to a user row.
   - **single-user, human plane**: user id from the authenticated `ClaimsPrincipal`.
   - **single-user, engine / service / background plane**: user id from a new `ISoleUserProvider` — returns
     `Tamma:SingleUser:OwnerUserId` when configured, else the earliest-created row in `users`; cached with
     invalidation on user create; **fail-loud** `GOVERNANCE.PRINCIPAL.NO_SOLE_USER` when `users` is empty.
   A test pins `EnginePlane_NeverReadsPrincipalFromTheWireBody` — the principal is never taken from
   caller-supplied payload.

8. **`AutonomyGateEvaluator` is pure, lives in `Tamma.Core/Actions/`, and implements the `max()` ladder.**
   No I/O, no DI, static, unit-testable without a database:

   ```
   effectiveMinAutonomy(action, principal) =
       max( platformCeiling(action),            // platform-scope rows: action → group → no ceiling
            legacyAlwaysEscalateFloor(action),  // AcceptanceGuardrails.TryPreGate, always-escalate only
            principalLadder(action) )           // first present of: action row → group row → shipped default
   ```

   Inside the principal ladder an **action override beats its group override outright** (`??`, not `max()`)
   — that is what "individual actions override their group" means, and it is what
   `AcceptanceRulesService.cs:52-64` already does. `Enforce`, `Enabled` and `AllowedRoles` resolve **per
   field, independently**, on the same ladder. Every resolved row carries provenance:
   `platform-ceiling` | `always-escalate-legacy` | `action-override` | `group-override` | `system-default`.

9. **The legacy always-escalate floor cannot be lowered from the new surface.** Because it composes with
   `max()`, an `AlwaysEscalate` entry contributes `AlwaysHuman` and only deleting it in the acceptance-rules
   UI removes it. Tested: `LegacyAlwaysEscalate_CannotBeLoweredByAnActionRow`. Separately,
   `RoundsExhausted_DoesNotAffectActionThreshold` — the rounds short-circuit inside `TryPreGate` is ignored.

10. **An empty table resolves every catalog member to its shipped default.** `EmptyTable_ResolvesEveryMemberToShippedDefault`
    iterates the whole catalog against zero rows and asserts each result equals the Story 43-3 default with
    source `system-default`. This is the zero-blast property: a fresh deployment writes no rows and behaves
    exactly as it does today.

11. **`IAcceptanceRulesResolver` gains base-row resolution, once.**
    `ResolveBaseAsync(Guid? userId, ct)` / `ResolveBaseForTenantAsync(Guid tenantId, ct)`, lifted from the
    concrete `AcceptanceRulesService.cs:91-108` (which already computes it). The evaluator needs the base
    row for the current dial level; today the interface cannot reach it. Method naming follows the
    `ForTenant` rationale documented at `IAcceptanceRulesResolver.cs:9-15`.

12. **Snapshot caching is scoped, and proven so.** `IGovernancePolicySnapshotProvider` is registered
    **scoped** and loads lazily once per HTTP request (one CP read pair per request, not per gate call — a
    tool loop gating 40 calls must issue one read). Background actors get a per-tick scope. Cross-process
    invalidation rides the already-present Redis connection when `ConnectionStrings:Redis` is set, in-process
    otherwise with a 30 s ceiling. Tested: `Registration_IsScoped`,
    `TwoGateCallsInOneRequest_IssueOneRepositoryRead`.

13. **One audit event family, and denials are not swallowed.** `ActionGateEventsService`
    (`AcceptanceRulesEventsService.cs:16-18,54-93` template — `const` type strings, tags,
    `{workflowVersion, eventSource}` metadata) emits `ACTION.GATE.ALLOWED` / `.REQUIRES_HUMAN` / `.DENIED` /
    `.AUTHORIZED` / `.AUTHORIZATION_DENIED` / `.PRINCIPAL_UNRESOLVED` / `.EVALUATION_FAILED`.
    Tags: `{actionKey, actionGroup, risk, autonomyLevel, effectiveMinAutonomy, assignmentSource, outcome,
    enforced, role, correlationId, issueId, tenantId, userId}`.
    Emission is wrapped in the template's swallowing try/catch **with one deliberate exception: `.DENIED`
    and `.REQUIRES_HUMAN` under enforcement are NOT swallowed** — a block with no audit row is a compliance
    hole. Events are appended **directly via `IEventRepository`** from Tamma.Api, because `TammaEventEmitter`
    structurally requires an `ActivityExecutionContext` and the tool loop runs inside a blocking HTTP
    request. Volume control: `.ALLOWED` fires only when `Source != system-default` or `Enforced`.

14. **`dotnet ef migrations has-pending-model-changes` is clean** after the migration lands, and the full
    `dotnet test` suite passes including the amended `ControlPlaneDbContextModelTests`.

## Dependencies

- **Story 43-1 (`AutonomyDial`)** — `Min`, `Max`, `Default`, `AlwaysHuman = Max + 1`, `IsValidThreshold`.
  Blocking: AC3 and AC8 both dereference it.
- **Story 43-3 (Groups + shipped defaults)** — the evaluator's `system-default` tier is 43-3's
  `DefaultMinAutonomy` per descriptor; AC10 iterates 43-3's completed partition. **Blocking.**
- **Story 43-4 (Tool-vocabulary reconciliation)** — the resolver resolves an emitted tool name through
  `ToolNameAliases` before it can look up an assignment. Blocking for the `tool:*` plane only.
- **Existing, verified:** `ControlPlaneDbContext`; `TammaModelConfiguration.cs:1621-1654` (the shape);
  `ControlPlaneDbContextModelTests.cs:33-48`; `Program.cs:3234-3276` (the DROP list, untouched);
  `AcceptanceRulesRepository.cs:21-25,34,108`; `AuthPrincipal.cs:30-39`;
  `IAcceptanceRulesResolver` + `AcceptanceRulesService.cs:52-64,91-108`;
  `AcceptanceGuardrails.TryPreGate:45`; `AcceptanceRulesEventsService.cs:16-18,54-93`; `IEventRepository`;
  `TenantAgentEnablement.cs:14-21` (the CP-in-both-modes precedent).
- **Feeds:** Story 43-6 (writes through the repository, reads the resolved shape),
  Story 43-9 (all five seams call `IAutonomyGate`; the ledger's `TryConsumeAsync` collapses one human
  decision across two gate points).

## Out of Scope

- **The enforcement seams themselves.** No call site of `IAutonomyGate` is added here — Story 43-9 owns all
  five. This story ships the component and its tests; nothing invokes it in production yet.
- **The admin API and its DTOs.** Story 43-6.
- **Writing authorization rows from human surfaces.** The ledger table and `TryConsumeAsync` ship here;
  the decision endpoint and the resume-endpoint wiring are Story 43-9.
- **A new suspend activity or bookmark prefix.** `CanonicalSuspendActivities` is keyed by activity `Type`,
  so a new prefix requires a new activity; v1 grants arrive through the 11 landed resume endpoints.
- **Any change to `Program.cs:3234-3276`.** Deliberately untouched (AC5).
- **Migrating `AcceptorRequirement` into the catalog.** It ships `design=Human` with zero consumers and
  stays a separate concept; folding it in means touching the document-lifecycle acceptance path.
- **Payload-predicate policy.** The gate matches on identity, not argument values — same limitation
  `EscalationClassKind` has today. A richer gate is a 39-5 change this epic does not attempt.

## Estimated Effort

5 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |
