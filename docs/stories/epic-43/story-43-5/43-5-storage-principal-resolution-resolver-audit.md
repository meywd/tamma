# Story 43-5: Storage, Principal Resolution, the Resolver, and Audit

Status: done — conformance-reviewed 2026-07-29; both control-plane tables, the principal resolver, the pure evaluator, the ledger and the audit family ship; AC12 superseded by F7 (singleton 60 s TTL store, no Redis — `Registration_IsScoped` / `TwoGateCallsInOneRequest_IssueOneRepositoryRead` do not exist) and AC13's `.ALLOWED` "or Enforced" arm dropped per F9; `IAutonomyGate` has no production caller, but Seam B enforcement is live and resolver-backed. **F6 and F10 CLOSED 2026-07-30**: the gate fails CLOSED on a degraded read — a failed rules read is represented as `null` rather than substituted with the shipped defaults, so concluding "no legacy floor" from a failure is unrepresentable at the signature; degraded decisions force AlwaysHuman/Enforced with their own provenance (`policy-unavailable`) and distinct reasons for the two causes, and are never suppressed by the `.ALLOWED` volume gate. Deliberate carve-outs — non-enforceable members and uncatalogued keys stay **Automated** (epic OQ2/D2) — but both now carry `Unavailable` provenance so they are still audited (review 2.1, 2026-07-30). F10's cross-plane composition is monotone by construction — `Enforce` ORs, `AllowedRoles` intersects — so a row on either plane can only restrict. F7 (≤60 s cross-instance staleness) and F9 remain as recorded: F6 was about ignorance, F7 is about lag. **F11 CLOSED 2026-07-30 — Story 43-9 is no longer blocked by it**: a config-sourced, mandatorily-expiring, per-decision-audited break-glass override that suspends the fail-closed substitution for an UNREADABLE input and nothing else (a denial from a successfully-read policy row is still a denial) — see "F11 — CLOSED" below. **F12 remains OPEN** (the live seam hard-denies rather than escalating; break-glass changes whether work continues, not who is asked). Also landed here: **`effect:mcp.tool.invoke` ships `AlwaysHuman`** and `mcp__*` names resolve to it rather than passing as uncatalogued — see "MCP tool invocation — governed by default".

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

`apps/tamma-elsa/src/Tamma.Api/Program.cs`'s `ExecuteSqlRaw` block (line numbers deliberately unpinned —
they drift; the residency test locates the literal dynamically) runs
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
   `Tamma:Governance:AuthorizationTtlHours` — *note 2026-07-29: this key has **no reader** in the tree yet;
   it is named in the entity/ledger doc comments as the intended knob, and 43-9's decision endpoint is the
   caller that will resolve it. Do not read it as shipped configuration*), `consumed_at_utc`, `reason`,
   `autonomy_level_at_request`.
   A partial unique index over `(tenant_id, user_id, correlation_id, target_kind, target_key)`
   `NULLS NOT DISTINCT WHERE state IN ('pending','granted')`.

5. **Both tables are on the strict CP entity list and NOT on the destructive DROP list.**
   `tests/Tamma.Api.Tests/Epic28/ControlPlaneDbContextModelTests.cs`'s `BeEquivalentTo` set gains both
   names (an unlisted table fails there). `Program.cs`'s DROP list is **not** modified. A dedicated test,
   `ActionGovernanceResidencyTests.Tables_AreNotInTheDestructiveDropList`, reads the `ExecuteSqlRaw` string
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

12. **[Superseded 2026-07-29 — see Follow-ups F7.]** The shipped provider is a **singleton TTL store**
    (`GovernancePolicySnapshotStore`, 60 s lazy-refresh TTL, a startup priming service, monotonic
    version-gated installs, invalidate-on-write via `RefreshAsync`, **no Redis**). The scoped registration,
    the Redis clause and both named tests below (`Registration_IsScoped`,
    `TwoGateCallsInOneRequest_IssueOneRepositoryRead`) do **not** exist in the tree. The AC text is kept
    verbatim below for provenance; F7 is the governing statement.
    ~~**Snapshot caching is scoped, and proven so.**~~ `IGovernancePolicySnapshotProvider` is registered
    **scoped** and loads lazily once per HTTP request (one CP read pair per request, not per gate call — a
    tool loop gating 40 calls must issue one read). Background actors get a per-tick scope. Cross-process
    invalidation rides the already-present Redis connection when `ConnectionStrings:Redis` is set, in-process
    otherwise with a 30 s ceiling. Tested: `Registration_IsScoped`,
    `TwoGateCallsInOneRequest_IssueOneRepositoryRead`.

13. **[Partially superseded 2026-07-29 — see Follow-ups F9: the `.ALLOWED` volume gate's "or `Enforced`"
    arm was deliberately DROPPED.]** `.ALLOWED` emits only when the resolution's provenance is not
    `system-default`; the "or `Enforced`" clause at the end of this AC is not implemented, because under
    epic D1 enforce defaults to TRUE and that arm would have defeated the volume gate entirely.
    **One audit event family, and denials are not swallowed.** `ActionGateEventsService`
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
  `ControlPlaneDbContextModelTests.cs:33-48`; `Program.cs`'s DROP list (untouched);
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
  *[Clarified 2026-07-29 — literally true of `IAutonomyGate` (zero production callers), but do not read it
  as "no enforcement is live": **Seam B enforcement IS live and is now resolver-backed**.
  `CatalogDefaultToolLoopAutonomyGate`'s production constructor consumes this story's
  `IGovernancePolicySnapshotProvider`, and that gate is a REQUIRED constructor dependency of
  `InlineToolLoopRunner` — wired at
  `apps/tamma-elsa/src/Tamma.Api/Extensions/ActionCatalogGovernanceServiceCollectionExtensions.cs:79-82`.
  So this story's assignment ladder already decides live tool calls; the four remaining seams are 43-9's.]*
- **The admin API and its DTOs.** Story 43-6.
- **Writing authorization rows from human surfaces.** The ledger table and `TryConsumeAsync` ship here;
  the decision endpoint and the resume-endpoint wiring are Story 43-9.
- **A new suspend activity or bookmark prefix.** `CanonicalSuspendActivities` is keyed by activity `Type`,
  so a new prefix requires a new activity; v1 grants arrive through the 11 landed resume endpoints.
- **Any change to `Program.cs`'s DROP list.** Deliberately untouched (AC5).
- **Migrating `AcceptorRequirement` into the catalog.** It ships `design=Human` with zero consumers and
  stays a separate concept; folding it in means touching the document-lifecycle acceptance path.
- **Payload-predicate policy.** The gate matches on identity, not argument values — same limitation
  `EscalationClassKind` has today. A richer gate is a 39-5 change this epic does not attempt.

## Estimated Effort

5 days

## Follow-ups from adversarial review (2026-07-29)

The 43-5 slice passed adversarial review **approve-with-conditions**. F1–F5 were fixed in the same
cycle (see below); F6–F10 are recorded here as open follow-ups so they cannot silently vanish.

### Fixed in this cycle (F1–F5)

- **F1 (MAJOR)** — `EfActionAuthorizationLedger.TryConsumeAsync`/`DecideAsync` were
  check-then-write (load → mutate → `SaveChangesAsync`, no concurrency token): two contexts that
  both read before either wrote could double-consume a grant, and a concurrent grant + deny both
  returned non-null with last-write-wins. Both transitions are now conditional single-statement
  `ExecuteUpdate` CAS writes (the `ScheduledTriggerRepository.TryClaimManualFireForDispatchAsync`
  pattern): consume CASes on `state='granted' AND ConsumedAtUtc IS NULL` (and not past expiry),
  decide CASes on `state='pending'` (and not past expiry); affected-rows 1 wins, the loser gets
  null. Pinned by `ConcurrentConsume_OfOneGrant_HasExactlyOneWinner` and
  `ConcurrentGrantAndDeny_ExactlyOneWins_AndTheRowMatchesTheWinner` (real Postgres).
- **F2** — a group grant was consumable for an action OUTSIDE the group: `TryConsumeAsync` trusted
  a caller-supplied `groupWire`. The parameter is removed; the ledger resolves the covering group
  from `ActionCatalog` itself (Tamma.Data already references Tamma.Core). An uncatalogued action
  key can only be covered by an exact action-scoped grant. Pinned by
  `GroupGrant_CannotBeConsumedForAnActionOutsideTheGroup` (deploy-control grant vs
  `tool:shell_execute`).
- **F3** — a time-expired open (pending/granted) row deadlocked its
  (principal, correlation, target) key forever: nothing transitioned `state→'expired'`, the
  partial unique open-row index blocked a fresh row, `RequestAsync` idempotently returned the
  stale row, and `DecideAsync` refused it. `RequestAsync` now CAS-transitions a past-expiry open
  row to `'expired'` (removing it from the partial index — the index `WHERE` clause is unchanged)
  and mints a fresh pending row; the decide/consume predicates exclude expired-by-time rows in
  SQL. Pinned by `ExpiredPendingRow_DoesNotDeadlockTheKey_AFreshRequestSucceeds` and
  `TimeExpiredGrant_IsNotConsumable`.
- **F4 (staleness half)** — `ActionPolicyEndpoints` derived the threshold it materialized into
  enforce/enabled/roles-first writes from the ≤60 s-stale snapshot and re-supplied it on EXISTING
  rows, so an enforce write on pod B within the TTL of a threshold tightening on pod A silently
  reverted the tightening. Fixed: the row's existence is decided by a FRESH repository read; an
  existing row gets a null threshold (per-field independence preserves the stored value), and a
  genuinely-new row's pin is computed from fresh repository rows through
  `AutonomyGateEvaluator.ResolveEffectiveMinAutonomy`, never the snapshot. Pinned by
  `EnforceOnlyWrite_OnAnExistingRow_PreservesItsStoredThreshold_EvenWhenTheSnapshotIsStale`.
  **Materialize-and-pin semantics for genuinely-new rows (design consequence, deliberate):** a
  first enforce/enabled/roles write materializes an action row whose threshold is pinned at the
  CURRENT effective value. From then on that action row beats group rows (`??` inside the
  principal ladder): a LATER group-scope tightening no longer reaches this member, and the pin
  survives deletion of the group row. Provenance resolves as `action-override`, which the 43-6 UI
  surfaces so the pin is visible. Pinned by
  `EnforceFirstWrite_MaterializesAndPinsTheCurrentEffective_SoALaterGroupTighteningDoesNotReachThisAction`.
- **F5** — group-level threshold writes (principal AND ceiling routes) bypassed the per-action
  validation: a mid-range threshold on a group containing `automation:*` (non-escalatable)
  members silently behaved as Deny for them — the exact value the action route 400s. Group writes
  now run the member check and reject with `ACTION_POLICY.INVALID` naming every offending member.
  Non-enforceable members (`effect:secret.reveal`) are exempt — the evaluator never blocks on
  them, and the secrets group must stay writable. Pinned by
  `GroupWrite_MidRangeOnAGroupWithNonEscalatableMembers_Is400NamingThem`.

### Open follow-ups (record only — NOT implemented)

- **F6 — fail-open on cold snapshot / CP outage. MUST be revisited before 43-9 wires the
  enforcing seams A/C/D/E.** If the snapshot store is cold (priming failed and the lazy refresh
  has not landed) the gate evaluates against zero rows — shipped defaults — so every admin
  tightening is silently not applied for that window. Worse, if the base-rules read degrades
  (`ResolveBaseAsync` throwing → shipped `AcceptanceDefaults.Rules`), the legacy
  **AlwaysEscalate floor vanishes**: an action a user pinned to always-human via acceptance rules
  evaluates as automated. Dial degrade (falling back to the default dial level) is safe — it can
  only be more conservative than a raised dial. Before any seam ENFORCES, decide: fail-closed on
  cold snapshot for enforce-marked rows, or a bounded startup gate.
  **→ CLOSED 2026-07-30. See "F6 — CLOSED" below; the diagnosis above is kept verbatim as the
  statement of the bug.**
- **F7 — AC12 deviations are now formal.** The shipped snapshot provider deviates from AC12's
  sketch: it is a **singleton** (not scoped), with a **60 s TTL** and **no Redis cross-instance
  invalidation** (the story's Redis clause is unimplemented). Consequence: enforcement changes
  have a **≤60 s cross-instance staleness bound** (the writing instance is consistent immediately
  via invalidate-on-write). This amendment supersedes the AC text rather than leaving it
  contradicted; the scoped/Redis design remains available if the bound ever becomes unacceptable.
- **F9 — audit-event gaps, by design, until 43-9.** The shipped `.ALLOWED` volume gate DROPS
  AC13's "or `Enforced`" arm (deliberately: under epic D1 enforce defaults to TRUE, so that arm
  would have defeated the volume gate entirely) — `.ALLOWED` emits only when the resolution's
  provenance is not `system-default`. Consequence: Automated decisions whose resolution stays
  system-default are suppressed even where an enforce opinion exists, and live tool-loop denials
  emit **no event at all** until 43-9 wires the seam emitters. Anyone reading the audit stream
  before 43-9 lands must not conclude "no events = no gated activity". (Post-F4 note: an
  enforce-only WRITE now materializes a pinned action row, so those specific resolutions carry
  `action-override` provenance and do emit.)
- **F10 — cross-plane Enforce/AllowedRoles composition is non-monotone (trap for 43-6).** The
  evaluator composes `Enforce` as platform-wins-when-present and `AllowedRoles` as
  principal-wins-when-present — neither is `max()`-monotone like the threshold. Today the ceiling
  routes only write thresholds, so this is latent. If 43-6 (or later) adds ceiling
  enforce/roles endpoints, a platform `Enforce=false` would OVERRIDE a tenant's `Enforce=true`
  (and a tenant roles list already overrides a platform one) — decide the intended lattice before
  exposing those endpoints.
  **→ CLOSED 2026-07-30 by FIXING the composition, not by recording it. See "F10 — CLOSED" below.**

- **F11 — there is NO BREAK-GLASS OVERRIDE for the fail-closed posture. ⛔ WAS A BLOCKER ON
  STORY 43-9 — CLOSED 2026-07-30, see "F11 — CLOSED" below; 43-9 is unblocked.**
  Recorded 2026-07-30 (adversarial review of the F6 close). The diagnosis below is kept verbatim
  as the statement of the gap. The product questions it left open were answered by the coordinator
  and are recorded, with what shipped, in the F11 close section.

  *The proven behaviour.* When the governance policy snapshot is non-authoritative — priming
  failed and no lazy TTL refresh has landed — the live Seam B tool-loop gate DENIES **every
  catalogued tool**. This is exhaustive, not a majority: `ActionCatalog`'s `Tool(...)` factory
  never passes `enforceable: false`, so every shipped `tool:*` descriptor is enforceable, and the
  one non-enforceable catalog member (`effect:secret.reveal`) is not a tool. Every DI-registered
  tool executor — `file_read`, `file_write`, `search_code`, `shell_execute`, `run_tests`,
  `git_operations` (which splits read/write by subcommand) — resolves to a catalogued, enforceable
  key and is therefore refused. **Every agent tool loop is effectively inert** for the duration;
  only names the catalog does not know (e.g. `mcp__*`) still pass, by epic D2. This holds in
  **BOTH** SaaS **and** single-user-with-a-control-plane deployments: the posture keys on the
  snapshot store, not on the tenancy mode. A deployment with no control-plane repository at all is
  unaffected (that store is authoritative from birth — no rows to miss).

  *What already works, and is not the gap.* It **self-heals automatically** within `RefreshTtl`
  (60 s) of the control plane coming back — no restart, no operator action. The logging is loud
  and correct: `GovernancePolicySnapshotStore.RefreshAsync` logs at ERROR when a refresh fails
  before any successful load, `GovernancePolicySnapshotPrimingService` logs at ERROR on failed
  priming, and each denial logs at ERROR naming `policy-snapshot-unavailable` rather than a policy
  reason. Diagnosis is not the problem.

  *The gap.* There is **no config flag, no environment variable and no admin endpoint** that can
  force the gate open. An operator who has diagnosed the outage correctly, understands that the
  control plane is unreachable and has accepted the risk has no supported way to keep the fleet
  working — the only levers are "wait for the control plane" or "edit code". For 43-5 that is
  arguably acceptable: the blast radius is one seam and the failure direction is the safe one.

  *Why it blocks 43-9.* Story 43-9 carries this same posture into **four more seams** (A/C/D/E),
  each with a larger blast radius than the tool loop. At that point a control-plane outage stops
  an agent from doing anything at all, across every seam, with no supported recovery lever short
  of a code change. An explicit break-glass knob should exist **before** 43-9 lands.

  *Left open deliberately — the product questions to answer first.* Who may set it (platform owner
  only? per-tenant? deploy-time only, so it cannot be flipped by a compromised admin session?);
  whether it is time-bounded and auto-expiring or sticky until cleared; how each decision made
  under it is audited (a distinct `ACTION.GATE.*` type? a `breakGlass` tag alongside `degraded`?);
  whether it opens the gate wholesale or only for a named risk tier. Whatever it is, it must be
  **loudly logged at every use** — a quiet break-glass is the fail-open with extra steps.
  **→ ANSWERED and CLOSED 2026-07-30. See "F11 — CLOSED" below.**

- **F12 — the live seam HARD-DENIES rather than escalating.** Recorded 2026-07-30, same review.
  This is a precision correction to how the F6 posture is described, not a new defect: it is the
  correct failure *direction*, but calling it "escalation" would be inaccurate.

  `IAutonomyGate`'s decision type can return `RequiresHuman`, and the evaluator does return it for
  escalatable members. But `IAutonomyGate` **has no production caller yet** (43-5 D12 — 43-9 owns
  all five seams). The only LIVE consumer of the governance posture is the tool loop, via
  `CatalogDefaultToolLoopAutonomyGate` → `ToolLoopGateDecision`, whose `ToolLoopGateOutcome` has
  **no `RequiresHuman` case** — only `Allowed` and `Denied`. So on the one path that actually
  enforces today, a degraded decision is a flat denial: it is fed back to the model as a tool
  rejection, and the run burns its remaining turns retrying tools that will all be refused,
  accomplishing nothing and reaching nobody. No human is notified, no approval is requested, no
  authorization row is created.

  The story should say this plainly rather than let "fails closed to requires-human" imply that a
  person is waiting somewhere. 43-9's seam work — or the break-glass knob of F11 — is where a
  degraded decision could become a real escalation.

See also `.dev/findings/2026-07-29-governance-wipe-orphans-policy-rows.md` (review F8: the
dev-mode wipe orphans `action_assignments` principal keys).

## F6 — CLOSED 2026-07-30: the gate FAILS CLOSED on a degraded read, and degradation is visible

### The posture chosen, and why

Three options were on the table (propagate the failure to the caller; cache last-known-good and
serve it with a warning; fail the gate closed). **Chosen: fail CLOSED, with the two degraded
causes named separately and both surfaced in the audit stream.** The argument:

1. **Every input this gate composes can only TIGHTEN.** The platform ceiling `max()`es upward, the
   legacy always-escalate floor `max()`es upward, `Enabled` ANDs downward, a role allowlist only
   restricts. There is no input whose absence is *more* restrictive than its presence. So "I could
   not read X" can never be answered with "then there is no X" — **ignorance is not absence**. The
   pre-fix code did exactly that: it substituted `AcceptanceDefaults.Rules`, whose `AlwaysEscalate`
   list is EMPTY, and thereby concluded from a failed read that the principal had pinned nothing.
   `agent-action:triage-intake` — which ships at `AutonomyDial.Min` and gets its human floor
   *only* from the legacy list — became AUTOMATED on a tenant-DB blip.
2. **Last-known-good was rejected.** The snapshot store already keeps a last-known-good for
   `action_assignments` (a refresh failure after a successful load keeps serving the previous
   snapshot — that part is unchanged and correct). A *second*, differently-shaped LKG cache for
   the per-principal acceptance rules would need its own invalidation hook (acceptance-rules
   writes do not notify the gate), and its staleness would be silent in exactly the loosening
   direction — a floor added since the cached read would not apply. One cache with a clear
   invalidation story beats two with a fuzzy one.
3. **Propagate-and-let-the-caller-decide was rejected** because it pushes the safety decision onto
   five seams that do not exist yet (43-9), each free to get it wrong differently. The gate is the
   place that knows the composition is monotone; it is the place that should conclude.
4. **The availability cost is bounded and mostly illusory.** `action_assignments` and the base
   acceptance rules are read against databases the request needed anyway (control plane for
   auth/tenancy, tenant DB for the rules). A deployment that cannot read them is not otherwise
   healthy. Fail-closed converts "silently ungoverned" into "visibly waiting for a person".

### What the failure posture actually is

| Input | Read succeeded, nothing found | Read FAILED / never happened |
|---|---|---|
| `action_assignments` snapshot | shipped defaults, `system-default` provenance, automatable | `AlwaysHuman`, `Unavailable` provenance, reason `policy-snapshot-unavailable` |
| principal base acceptance rules | shipped defaults, dial + empty always-escalate, automatable | `AlwaysHuman`, `Unavailable` provenance, reason `acceptance-rules-unavailable` |

A degraded decision is `RequiresHuman` where a human wait exists and `Denied` where none does
(the `EscalatableToHuman` split, unchanged), and **`Enforced` is forced TRUE** — a degraded
decision a seam is free to ignore would be the same fail-open wearing a warning label.

**Two deliberate carve-outs from fail-closed**, both pre-existing epic answers that degradation
must not silently reverse:

- `Enforceable = false` members (`effect:secret.reveal`) stay `Automated`. Epic open question 2
  answered that reading a secret never requires a human; turning every credential fetch into an
  approval during a blip amplifies an outage rather than containing it.
- An **uncatalogued** key stays `Automated` (epic D2 — unclassified is allowed at runtime,
  unmergeable in CI). An unread policy table does not create a catalog entry.

**A carve-out from fail-closed is NOT a carve-out from AUDIT (review 2.1, corrected 2026-07-30).**
Both carve-outs keep their outcome and carry `Unavailable` provenance, so both still emit. This
was originally true only of the non-enforceable one: the uncatalogued short-circuit returned
*before* the degradation check and hard-coded `SystemDefault` provenance, `Enforced: false`,
`Outcome: Automated` — and the `.ALLOWED` volume gate (which suppresses system-default allows)
then suppressed the row entirely, with the `degraded` tag reading `false` even if it had not.
The fix moved the degradation computation above the short-circuit and stamps `Unavailable` on it.
**The outcome is provably unchanged** — still `Automated`, still `Enforced: false`, still
`Enabled: true`, still `EffectiveMinAutonomy = Min`, still reason `uncatalogued`; only the
provenance moved, which is asserted field-by-field against the healthy answer in
`UncataloguedKey_UnderDegradation_StaysAutomated_ButCarriesDegradedProvenance`. During a
control-plane outage the uncatalogued surface is precisely the surface that STAYS OPEN, which is
precisely why an auditor needs a record of it.

### The mechanism (why the two states could not be told apart before)

`GovernancePolicySnapshot` gains **`IsAuthoritative`** (defaults TRUE, so every hand-built
snapshot and the 43-6 fresh-read pin keep meaning "these ARE the rows"). `GovernancePolicySnapshotStore`
tracks a `_everLoaded` flag set only on a SUCCESSFUL load and stamps it onto every projection;
a store with **no** repository is authoritative from birth (no control-plane DB ⇒ no rows to
miss ⇒ the empty snapshot IS the truth). `GovernancePolicySnapshot.Unavailable` is the named
degraded value. Before this, both states were literally `GovernancePolicySnapshot.Empty`.

`AutonomyGateEvaluator.Evaluate`'s `baseRules` parameter is now **nullable, and null means the
read failed** — the shipped defaults are no longer a legal substitute for a failure, which makes
the fail-open unrepresentable at the signature rather than forbidden by a comment.
`AutonomyGateService.ResolveBaseRulesAsync` returns null on exception and logs at **ERROR**
(it logged a WARNING and returned defaults). A platform-only principal has no rules row to read
at all — that is a successful "nothing to read" and still returns the shipped base, not null.

`ActionAssignmentSource` gains **`Unavailable`** (wire `policy-unavailable`), and the decision
event gains a **`degraded`** tag. Both matter: a degraded decision is never `system-default`, so
the `.ALLOWED` volume gate cannot suppress it.

**The audit guarantee, stated precisely** (the first wording of this paragraph over-claimed and
was corrected on review, 2026-07-30):

- A degraded decision that BLOCKS (`RequiresHuman`/`Denied`) is `Enforced`, so its append rides
  the non-swallowing path — **guaranteed an audit row or an exception**, never silence.
- A degraded decision that ALLOWS (the two carve-outs) is **guaranteed an emission ATTEMPT**
  carrying `assignmentSource=policy-unavailable` and `degraded=true`. It is not enforced-blocking,
  so an append failure is logged and swallowed like every other best-effort `.ALLOWED` — the
  volume gate can no longer suppress it, but the event repository being down is still tolerated.

The distinction is deliberate: the compliance hole the non-swallowing path exists to close is *a
block with no record of it*. Rethrowing on a failed audit append for an allow would convert an
event-store blip into a second outage on the surface that is deliberately staying open.

`GovernancePolicySnapshot` has **no public constructor** (review 2.2, 2026-07-30): it is minted by
`FromSuccessfulRead(...)` or by the `Unavailable` singleton, so a caller must state which it is.
`IsAuthoritative` was previously an `init` property defaulting to TRUE. Every construction site
was correct at the time, but a default-true *safety* bit means the next hand-built snapshot
silently claims an authority it may not have — a test-convenience default governing a production
safety property. Making it structural costs nothing: `GovernancePolicySnapshotStore.Project`
returns `Unavailable` when the store has never loaded (provably lossless — `_snapshot` is only
assigned inside `_installLock` together with `_everLoaded = 1`, so a non-authoritative store's
`FullSnapshot` is necessarily `Empty`), `ActionPolicyEndpoints.PinnedEffectiveThreshold` says
`FromSuccessfulRead` because both row sets were just read successfully on that request, and the
two test helpers say the same. No call site changed meaning.

### Seam B (the one gate that already enforces) honours it

`CatalogDefaultToolLoopAutonomyGate` now reads the provenance out of
`ResolveEffectiveMinAutonomy` and denies with reason `policy-snapshot-unavailable` + an ERROR
log when the snapshot has never loaded — deliberately NOT `below-min-autonomy`, because this is
an outage of the governance surface, not a policy decision. This is the behaviour change with the
largest blast radius in the fix: a host that comes up while the control plane is unreachable
withholds tool calls instead of running them ungoverned.

### Tests (the "prove they are distinguishable" obligation)

- `AutonomyGateEvaluatorTests.UnavailableSnapshot_FailsClosed_WhileALoadedEmptyTableAutomates` —
  THE pair, same action, opposite answers, different provenance.
- `AutonomyGateEvaluatorTests.UnreadableBaseRules_CannotConcludeThereIsNoLegacyFloor` — the
  concrete `triage-intake` loss, both directions.
- `AutonomyGateEvaluatorTests.TheTwoDegradedCauses_CarryDistinctReasons` (including the
  deterministic order when both degrade at once).
- `AutonomyGateEvaluatorTests.DegradedDecision_IsAlwaysEnforced_EvenAgainstAStoredEnforceFalse`,
  `Degraded_DeniesRatherThanEscalates_WhereNoHumanWaitExists`,
  `NonEnforceableMember_StaysAutomated_WhenPolicyIsUnavailable`,
  `UncataloguedKey_StaysAllowed_EvenWhenPolicyIsUnavailable`,
  `ResolveEffectiveMinAutonomy_OnAnUnavailableSnapshot_IsAlwaysHuman`.
- `GovernancePolicySnapshotStoreTests.AStoreThatHasNeverLoaded_ServesANonAuthoritativeSnapshot`,
  `AFailedLoad_LeavesTheStoreNonAuthoritative_SoGatesFailClosed`,
  `AFailedRefreshAfterASuccessfulLoad_KeepsTheLastGoodSnapshot_AndStaysAuthoritative`,
  `FailedPriming_LeavesTheStoreNonAuthoritative_ButDoesNotCrashStartup`,
  and `NoRepository_ServesEmptySnapshots_ShippedDefaultsApply` (extended: no repository ⇒ still
  authoritative).
- `AutonomyGateEvaluatorTests.UncataloguedKey_UnderDegradation_StaysAutomated_ButCarriesDegradedProvenance`
  (review 2.1) — every other field asserted equal to the healthy answer, so the fix is pinned as
  provenance-only.
- `AutonomyGateServiceFailurePostureTests` (new fixture) — the composed service end to end,
  including `ADegradedDecision_EmitsAnEventTaggedDegraded_WithPolicyUnavailableSource`,
  `BaseRulesReadSucceedsWithNoOverrides_Automates_AndEmitsNoDegradedTag`, and (review 2.1)
  `AnUncataloguedKey_UnderDegradation_StaysAutomated_AndStillEmitsADegradedRow` with its control
  `AnUncataloguedKey_WithReadablePolicy_EmitsNothing` — the second proves the new row is a
  degradation signal rather than new noise on the healthy path.
- `ResolverBackedToolLoopGateTests.AnUnavailableSnapshot_DeniesEveryCatalogedTool_WithItsOwnReason`
  and `AnUnavailableSnapshot_StillAllowsUncataloguedNames_EpicD2`.

### What F6 does NOT close

The **staleness** bound is untouched: a snapshot that loaded 59 seconds ago is authoritative even
if an admin tightened policy 58 seconds ago on another instance. That is F7's ≤60 s cross-instance
bound, deliberately separate — F6 is about *ignorance*, F7 about *lag*.

## F10 — CLOSED 2026-07-30: Enforce and AllowedRoles compose monotonely

Fixed rather than recorded, because the fix is small, is unreachable from today's endpoints (the
ceiling routes author thresholds only, so **no shipped behaviour changes**), and encoding the
invariant in the evaluator is strictly better than leaving prose for whoever adds the endpoints.

**The invariant, now enforced by construction:** *adding a row on either plane can only make the
resolution more restrictive, never less. The only value a plane may lower is the SHIPPED default,
and only when no other plane has an opinion.*

- **`Enforce` composes by `OR`** over the planes' present opinions; the v1 default (epic D1: TRUE)
  applies only when NEITHER plane has one. So a platform `Enforce=false` can no longer override a
  principal's `Enforce=true` — nor the reverse. A single plane saying observe-only with nothing
  opposing it is still honoured: that is a *default* being lowered, not a plane overriding a plane.
- **`AllowedRoles` composes by INTERSECTION.** A principal allowlist could previously ADD roles a
  platform restriction excluded. Now both restrictions apply. Two subtleties, both tested: a
  **stored empty array keeps its historical "no restriction" reading** (so no stored row changes
  meaning), while an **intersection that comes out empty is a restriction that allows nobody** —
  which is the honest answer to "developers only" AND "testers only". The guard therefore tests
  `allowedRoles is not null` rather than `Count > 0`.
- `MinAutonomy` (`max()`) and `Enabled` (`AND`) were already monotone and are unchanged.

Pinned by `PlatformEnforceFalse_CannotUnEnforceAPrincipalEnforceTrue`,
`PrincipalEnforceFalse_CannotUnEnforceAPlatformEnforceTrue`,
`SinglePlaneEnforceOpinion_StillApplies_AndTheDefaultStaysTrue`,
`PrincipalRoles_CannotWidenAPlatformRoleRestriction`, `DisjointRoleRestrictions_AllowNobody`,
`AnEmptyStoredRolesArray_StillMeansUnrestricted`.

**What a future ceiling endpoint must not do:** re-introduce a "this plane wins outright" rule for
any field, and in particular must not let a ceiling write express "un-enforce" or "widen the
allowed roles" — the evaluator will now refuse to honour it, so an endpoint that offers it would
be lying to its caller. If a platform-level *kill switch* for enforcement is ever genuinely
wanted, it needs a separate, explicitly non-monotone field with its own story, not a reinterpretation
of `Enforce`.

## F11 — CLOSED 2026-07-30: a config-sourced, expiring, audited break-glass that bypasses DEGRADATION ONLY

### The product answers (they were the blocker, not the code)

| Question F11 left open | Answer | Why |
|---|---|---|
| Who may set it? | **Nobody at runtime. It is CONFIGURATION** (`Tamma:Governance:BreakGlass:*`), read ONCE at construction of a singleton. Engaging requires a config change **and a restart**. | An endpoint that can switch off a governance posture is itself a governance surface — it would need its own permission, its own ceiling and its own audit, and a compromised admin session would reach it. Configuration friction is the feature. There is deliberately **no writer** on the type, pinned by `TheOverrideHasNoWriter`. |
| Time-bounded or sticky? | **Time-bounded, mandatory.** It **refuses to engage** with a missing, unparseable or already-past `ExpiresAtUtc`, logging at ERROR each time. | A break-glass that can be left on forever stops being a break-glass and becomes the permanent configuration — the fail-open F6 removed, re-introduced by an operator who forgot. |
| Wholesale, or per risk tier? | **Neither — scoped by CAUSE, not by target.** It suspends exactly one thing: the substitution of `AlwaysHuman` for an input that could **not be read**. | A risk-tier switch would be a second policy language on top of the catalog. Scoping by cause is narrower *and* needs no new vocabulary: it can only ever restore the answer the system would have given had the unreadable input said "nothing". |
| How is each use audited? | A **distinct event type `ACTION.GATE.BREAK_GLASS_BYPASS`**, one row per bypassed decision, carrying the operator's reason, the expiry, the seam and the degraded cause — on the **non-swallowing** append path. Plus a `break-glass` `assignmentSource` wire value and a `breakGlass` tag on the ordinary decision row. | A tag alone would sit inside the allow stream and could be suppressed by the `.ALLOWED` volume gate. A reviewer after the outage needs to *select* the bypassed decisions. |
| Loud? | ERROR on engage, on refusal to engage, on expiry, and on **every** bypassed decision at both live gates. | Recorded requirement: a quiet break-glass is the fail-open with extra steps. |

### THE boundary — bypasses degradation, never a real denial

This is the whole design, and it is enforced by **construction**, not by comment:

```
break-glass suspends:   "I could not READ policy X, therefore AlwaysHuman"
break-glass never suspends:
    a threshold row that WAS read        (incl. the platform ceiling)
    Enabled = false that WAS read
    an AllowedRoles restriction that WAS read
    a legacy always-escalate entry that WAS read
    the SHIPPED catalog default          (document-type:design, effect:mcp.tool.invoke, …)
```

Two structural facts make that hold rather than merely intending it:

1. **The snapshot half cannot discard a row, because there are none to discard.** The bypass is
   sited *inside* `ResolveEffectiveMinAutonomy`'s `!IsAuthoritative` branch, and
   `GovernancePolicySnapshot.Unavailable` carries no rows by construction (the store's own
   `Project` comment proves `!IsAuthoritative ⇒ FullSnapshot.Empty`). The fallback is
   `descriptor.DefaultMinAutonomy` — **not "allow"** — so an `AlwaysHuman` shipped default still
   blocks. Pinned by `ResolveEffectiveMinAutonomy_Engaged_NeverDiscardsAReadRow` and
   `EngagedAndSnapshotUnavailable_AnAlwaysHumanShippedDefault_StillBlocks`.
2. **The acceptance-rules half keeps the real ladder result.** Degradation causes are evaluated in
   a fixed order (snapshot first), so reaching the `baseRules is null` branch means the assignment
   rows *were* read. That branch therefore leaves `effectiveMin`/the ladder untouched and only
   skips the unreadable legacy floor — it re-stamps provenance and nothing else. Pinned by
   `EngagedButARealPolicyRowDenies_IsStillDenied` (the reason comes back `always-human`, naming the
   POLICY, not the bypass) and `BreakGlassEngaged_AgainstARealPolicyDenial_IsSTILLDenied`.

Also load-bearing and easy to lose in a refactor: `Enabled` and `AllowedRoles` are checked
**above** the degradation branch in `AutonomyGateEvaluator.Evaluate`, which is why they survive an
engaged override for free. Moving the break-glass fall-through above them would silently turn the
lever into a backdoor.

And: **an engaged override is inert on a healthy evaluation.** It is a fallback for an unreadable
input, not a mode the system runs in — proved over the WHOLE catalog by
`Engaged_ChangesNothing_WhenEveryInputIsReadable`, so a member cannot quietly acquire
break-glass-only behaviour later.

### What shipped

| Piece | Where |
|---|---|
| `BreakGlassState` (engaged / expiry / reason) | `Tamma.Core/Actions/AutonomyGovernance.cs` |
| `ActionAssignmentSource.BreakGlass` (wire `break-glass`) | same file — a THIRD value, distinct from `system-default` (nothing was wrong) and `policy-unavailable` (the gate refused) |
| The bypass rules + `ReasonBreakGlassBypass` | `Tamma.Core/Actions/AutonomyGateEvaluator.cs` — new optional `breakGlass` parameter on `Evaluate` and on `ResolveEffectiveMinAutonomy` (both default null ⇒ every existing caller keeps the F6 posture) |
| `IGovernanceBreakGlass` + `ConfigurationGovernanceBreakGlass` | `Tamma.Api/Services/Actions/GovernanceBreakGlass.cs` |
| `ACTION.GATE.BREAK_GLASS_BYPASS` + `EmitBreakGlassBypassAsync` (non-swallowing) + the `breakGlass` tag | `Tamma.Api/Services/Actions/ActionGateEventsService.cs` |
| Per-decision ERROR log + bypass event on the `IAutonomyGate` path | `Tamma.Api/Services/Actions/AutonomyGateService.cs` |
| Seam B: bypass handling + ERROR log; the decision carries `BreakGlassState` | `Tamma.Api/Services/Agents/CatalogDefaultToolLoopAutonomyGate.cs`, `IToolLoopAutonomyGate.cs` |
| Seam B: the durable audit row | `Tamma.Api/Services/Agents/InlineToolLoopRunner.cs` — the gate is **sync by design** (AC12: the per-tool-call path must never block on a database), so the append rides the loop's async path via a new optional `ActionGateEventsService` collaborator |
| Singleton DI registration | `Tamma.Api/Extensions/ActionCatalogGovernanceServiceCollectionExtensions.cs` |

Configuration:

```
Tamma:Governance:BreakGlass:Enabled       = true
Tamma:Governance:BreakGlass:ExpiresAtUtc  = 2026-07-31T06:00:00Z   # MANDATORY; bare instants read as UTC
Tamma:Governance:BreakGlass:Reason        = control plane unreachable, INC-4412
```

### One deliberate tension with the F6 close, recorded rather than glossed

The F6 close reasoned that rethrowing on a failed audit append for an **allow** would convert an
event-store blip into a second outage on the surface that is deliberately staying open — and that
reasoning still governs `.ALLOWED` (pinned by the new control test
`AnOrdinaryDegradedAllow_StillSwallowsItsEmissionFailure`). **Break-glass is the one exception:**
its audit row is not commentary on the decision, it *is* the justification for having a bypass at
all, and an unrecorded bypass is indistinguishable from an unauthorised one. So the bypass append
is non-swallowing and a bypass that cannot be recorded fails instead of happening quietly
(`ABypassThatCannotBeAudited_FailsRatherThanHappeningQuietly`,
`A_break_glass_bypass_that_cannot_be_audited_does_not_happen_quietly`).

### What F11 does NOT close

- **F12 still stands.** `ToolLoopGateOutcome` still has no `RequiresHuman` case, so a degraded (or
  break-glass-blocked) decision at Seam B still reaches the model as a tool rejection and reaches
  no person. Break-glass changes *whether* work continues, not *who is asked*.
- **The staleness bound (F7) is untouched.** Break-glass is about ignorance, not lag.
- **It is per-process, not per-tenant.** A SaaS operator engaging it engages it for every tenant on
  that host. That is deliberate for v1 — the failure it relieves (an unreadable control plane) is
  itself per-process — but it means the lever is a platform-owner action in every deployment mode,
  and a per-tenant variant would need its own story.
- **Nothing verifies that the override was *needed*.** Configuring it while the control plane is
  healthy is legal and inert; the expiry, the logging and the per-decision audit are what bound the
  risk, not a liveness check.
- **One bypassed decision does NOT get a `BREAK_GLASS_BYPASS` row — named here rather than left
  implicit.** "An ERROR log and an audit row on every decision it bypasses" is true with one
  exception. When the snapshot is unreadable, break-glass is engaged, the base rules *were* read, and
  a live legacy always-escalate entry covers the action, `AutonomyGateEvaluator` takes the third
  branch and **overwrites the `BreakGlass` provenance with `AlwaysEscalateLegacy`** — the more
  specific answer, because the legacy floor really is what decided it. But
  `AutonomyGateService` gates the bypass audit on `decision.Source == ActionAssignmentSource.BreakGlass`,
  so that decision emits no `ACTION.GATE.BREAK_GLASS_BYPASS` row and misses the per-decision ERROR
  log. Reachable only on the agent-action / document-type planes (the only planes
  `EscalationClassKind` covers). **Why it is acceptable rather than a defect:** the outcome is a
  *block*, so it still rides the non-swallowing `.REQUIRES_HUMAN` audit path and is never silent —
  what is lost is the "this was bypassed" label on a decision that was not, in the end, permitted by
  the bypass. A future change that makes provenance a set rather than a single value would close it;
  keeping one value and choosing the more specific one is the deliberate trade.

### Tests

- `Tamma.Core.Tests/Actions/AutonomyGateEvaluatorBreakGlassTests.cs` — the pure rules, paired
  bypass/anti-backdoor: `EngagedAndSnapshotUnavailable_Proceeds_WithBreakGlassProvenance`,
  `EngagedAndBaseRulesUnreadable_Proceeds_WithBreakGlassProvenance`,
  **`EngagedButARealPolicyRowDenies_IsStillDenied`**, `EngagedButAPlatformCeilingDenies_IsStillDenied`,
  `EngagedAndSnapshotUnavailable_AnAlwaysHumanShippedDefault_StillBlocks`,
  `EngagedButTheActionIsDisabled_IsStillDenied`, `EngagedButTheRoleIsNotAllowed_IsStillDenied`,
  `EngagedButALegacyAlwaysEscalateEntryWasRead_StillFloors`,
  `NotEngaged_AndDegraded_StillDeniesExactlyAsBefore`,
  `Engaged_ChangesNothing_WhenEveryInputIsReadable`,
  `ResolveEffectiveMinAutonomy_Engaged_FallsBackToTheShippedDefault`,
  `ResolveEffectiveMinAutonomy_Engaged_NeverDiscardsAReadRow`.
- `Tamma.Activities.Tests/Actions/GovernanceBreakGlassTests.cs` — the config source, including the
  three refusals (`EnabledWithNoExpiry_REFUSES_ToEngage`,
  `EnabledWithAnUnparseableExpiry_REFUSES_ToEngage`,
  `EnabledWithAnAlreadyPastExpiry_REFUSES_ToEngage`),
  `AnEngagedOverride_StopsBeingEngaged_TheInstantItExpires`,
  `ABareTimestamp_IsReadAsUtc_NotAsServerLocalTime`, `TheOverrideHasNoWriter`.
- `Tamma.Activities.Tests/Actions/AutonomyGateServiceFailurePostureTests.cs` (extended) —
  `BreakGlassEngaged_OverADegradedRead_Proceeds_AndWritesItsOwnAuditRow`,
  **`BreakGlassEngaged_AgainstARealPolicyDenial_IsSTILLDenied`**,
  `BreakGlassNotEngaged_LeavesTheFailClosedPostureUntouched`,
  `BreakGlassEngaged_ChangesNothing_WhenEveryReadSucceeds`,
  `ABypassThatCannotBeAudited_FailsRatherThanHappeningQuietly`,
  `AnOrdinaryDegradedAllow_StillSwallowsItsEmissionFailure`.
- `Tamma.Activities.Tests/Actions/ResolverBackedToolLoopGateTests.cs` (extended) — Seam B:
  `BreakGlassEngaged_OverADegradedSnapshot_AllowsAndCarriesTheBypassState`,
  `BreakGlassEngaged_DoesNotOpenAnAlwaysHumanShippedDefault`,
  `BreakGlassEngaged_ChangesNothing_WhenTheSnapshotWasRead`,
  `BreakGlassNotEngaged_LeavesTheF6PostureExactlyAsItWas`.
- `Tamma.Api.Tests/Agents/ToolLoopAutonomyGateSeamTests.cs` (extended) — the durable row through
  the REAL loop: `A_break_glass_bypass_writes_an_audit_row_per_call`,
  `A_break_glass_bypass_that_cannot_be_audited_does_not_happen_quietly`,
  `An_ordinary_decision_writes_no_break_glass_row`.

## MCP tool invocation — governed by default (2026-07-30)

Not a 43-5 follow-up, but it lands in the same change and it touches this story's resolver, so it
is recorded here as well as in the epic README and Story 43-4.

**`effect:mcp.tool.invoke` now ships `AutonomyDial.AlwaysHuman`**, and `ToolNameAliases` resolves
the `mcp__<server>__<tool>` prefix family onto it instead of leaving those names uncatalogued.
The reason is specific to MCP and does not generalise: epic **D2** allows an unclassified action at
runtime *because* the drift harnesses make it unmergeable in CI, and for MCP that second half
cannot exist — no harness can enumerate a remote server's tools. See the epic README's
"MCP: the one family where the CI half cannot exist".

Consequence for this story's ladder: an `mcp__*` call at Seam B now resolves through the normal
assignment ladder (action row → group row → shipped default, then platform ceiling by `max()`), so
an admin re-opens the family with one action-scoped row and a platform ceiling can still hold it
shut. Pinned by `ResolverBackedToolLoopGateTests.AnMcpToolName_IsDeniedByDefault_AndCarriesTheMcpEffectKey`,
`AnActionRowAtTheFloor_ReOpensMcp`, `APlatformCeiling_StillBinds_WhenAPrincipalRowReOpensMcp`.

## Change Log

| Date       | Version | Changes                                                                | Author |
| ---------- | ------- | ---------------------------------------------------------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation                                                  | Claude |
| 2026-07-29 | 1.1.0   | Adversarial-review amendments: F1–F5 fixed (ledger CAS, catalog-derived group coverage, expired-row unblocking, fresh-read threshold preservation + documented materialize-and-pin, group-write member validation); F6/F7/F9/F10 recorded as follow-ups | Claude |
| 2026-07-30 | 1.2.0   | **F6 CLOSED** — fail-CLOSED posture on a degraded read: `GovernancePolicySnapshot.IsAuthoritative`, nullable `baseRules` meaning "read failed", `ActionAssignmentSource.Unavailable` + `degraded` audit tag, ERROR logging, Seam B honours it. **F10 CLOSED** — `Enforce` composes by OR and `AllowedRoles` by intersection, so every cross-plane field is monotone. F7/F9 remain open as recorded. | Claude |
| 2026-07-30 | 1.3.0   | **F11 CLOSED — 43-9 is unblocked.** A config-sourced (`Tamma:Governance:BreakGlass:*`), mandatorily-expiring, per-decision-audited break-glass override that suspends the fail-closed substitution for an UNREADABLE policy input and **nothing else**: `BreakGlassState`, `ActionAssignmentSource.BreakGlass`, `ReasonBreakGlassBypass`, `IGovernanceBreakGlass`/`ConfigurationGovernanceBreakGlass`, `ACTION.GATE.BREAK_GLASS_BYPASS` on the non-swallowing append path, ERROR logging at engage/refuse/expire/every-bypass, and Seam B support. Anti-backdoor boundary pinned in four places (a read policy row, a platform ceiling, a disable, a role restriction, and an AlwaysHuman shipped default all still deny). **Also in this change: `effect:mcp.tool.invoke` ships `AlwaysHuman` and `mcp__*` names resolve to it** — see the MCP section. F7/F9/F12 remain open. | Claude |
| 2026-07-30 | 1.2.1   | Adversarial review of the F6 close. **2.1 fixed** — the degradation check now runs BEFORE the uncatalogued short-circuit, so a degraded uncatalogued decision carries `Unavailable` provenance and therefore emits; outcome provably unchanged (still Automated/observe-only). The "guaranteed an audit row" claim is narrowed to state the blocking and allowing cases separately. **2.2 fixed** — `GovernancePolicySnapshot` has no public constructor; `FromSuccessfulRead(...)` / `Unavailable` make the authority bit a required, stated input rather than a default-true safety property. **F11 recorded as a BLOCKER ON 43-9** (no break-glass override for the fail-closed posture) and **F12 recorded** (the live seam hard-denies rather than escalating — `ToolLoopGateOutcome` has no `RequiresHuman` case). | Claude |
