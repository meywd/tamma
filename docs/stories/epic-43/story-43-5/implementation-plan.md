# Implementation Plan — Story 43-5: Storage, Principal Resolution, the Resolver, and Audit

## Scope & Deliverable

When this story is done, an autonomy assignment is a durable, resolvable fact. Two control-plane tables
(`action_assignments`, `action_authorizations`) exist in both operating modes, carry three scopes
(platform / tenant / user), keep all three policy columns nullable so "unset" is representable, and are
**deliberately absent from the destructive startup DROP list** with a test that reads the SQL string to
prove it. `IGovernancePrincipalResolver` + `ISoleUserProvider` answer "who is this?" on every plane
including the engine plane, where `ServiceAuthPrincipal` carries no user id and a nullable tenant id.
`AutonomyGateEvaluator` — pure, in `Tamma.Core`, zero I/O — composes the platform ceiling, the legacy
always-escalate floor and the principal ladder with `max()`, giving `AcceptanceGuardrails.TryPreGate` its
first production call site. `AutonomyGateService` in `Tamma.Api` wires it to EF behind a scoped snapshot
that issues one read per request. `ActionGateEventsService` emits one event family, swallowing everything
except denials under enforcement.

Nothing calls the gate yet. Story 43-9 owns every seam.

## Pre-Reading

- `docs/stories/epic-43/README.md` — "Storage" and "Enforcement"; **D1: v1 enforces, with defaults
  reproducing today's behaviour** (there is no observe-only phase and no soak precondition)
- `docs/stories/epic-43/story-43-1/` — `AutonomyDial` (`Min`/`Max`/`Default`/`AlwaysHuman`/`IsValidThreshold`)
- `docs/stories/epic-43/story-43-3/` — the 15-group partition and every descriptor's `DefaultMinAutonomy`;
  the evaluator's `system-default` tier is literally this
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/IAcceptanceRulesResolver.cs` — **the Core/Api split
  being copied**, including the `ForTenant`-vs-overload naming rationale at `:9-15` (a non-null `Guid` binds
  to both a nullable and non-nullable overload and the non-nullable wins, silently routing single-user
  callers onto the SaaS path)
- `apps/tamma-elsa/src/Tamma.Api/Services/AcceptanceRules/AcceptanceRulesService.cs:52-64` (the
  override-beats-base ladder), `:91-108` (the base-row resolution AC11 lifts into the interface)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceGuardrails.cs:45` (`TryPreGate`) +
  `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Policy/AcceptanceGuardrailsTests.cs:38,50,58,66` — note
  `:58`/`:66` exercise the **rounds-exhausted** short-circuit the evaluator must ignore; `:50` is the
  always-escalate branch it consumes. Zero production call sites today.
- `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs:1621-1654` — the EF-config shape (CHECK on
  `ToTable`, `gen_random_uuid()`, `now()`, `.IsUnique().AreNullsDistinct(false)`, explicit
  `HasDatabaseName`). **Do not copy** its `ApplyTenantFilter` / `omitTenantIdColumn` lines.
- `apps/tamma-elsa/src/Tamma.Data/Repositories/AcceptanceRulesRepository.cs:21-25` (`RequireTenantId`, the
  fail-loud tenant-resident posture), `:34` and `:108` (the explicit other-key-null predicates
  `p.UserId == userId && p.TenantId == default(Guid?)` / the mirror)
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IAcceptanceRulesRepository.cs:8-12` — the parallel-surfaces
  invariant wording to extend to three planes
- `apps/tamma-elsa/src/Tamma.Data/Entities/TenantAgentEnablement.cs:14-21` — CP-resident-in-both-modes
  precedent; note it explicitly says it **joins** the DROP list, which this story refuses (see D3)
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:3234-3276` — the `DROP TABLE … CASCADE` block, ~55 tables,
  every restart without `TAMMA_PRESERVE_DB=1`. **Read it; do not edit it.**
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Epic28/ControlPlaneDbContextModelTests.cs:33-48` — the strict
  `BeEquivalentTo` entity set (an unlisted table fails)
- `apps/tamma-elsa/src/Tamma.Api/Auth/AuthPrincipal.cs:30-39` — `ServiceAuthPrincipal(KeyId, ServiceName,
  Permissions, TenantId?)`: **no UserId at all**, tenant from the `X-Tenant-Id` header, nullable by design
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/TenantProvisioningService.cs` +
  `apps/tamma-elsa/src/Tamma.Activities/TenantLifecycle/MigrateTenantDatabaseActivity.cs:52` — the **only
  two** `ITenantDbMigrator` production call sites, both creation-only. No startup sweep exists.
- `apps/tamma-elsa/src/Tamma.Api/Services/AcceptanceRules/AcceptanceRulesEventsService.cs:16-18,54-93` —
  the `<Feature>EventsService` template (const type strings, tags, metadata, swallowing try/catch)
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` — the direct append surface
- `apps/tamma-elsa/src/Tamma.Activities/Security/ActionGate.cs:17` + `Tamma.Api/Program.cs:750` — **the name
  collision**; everything here is `AutonomyGate*`
- **NOT FOUND (prerequisites, no code yet):** `apps/tamma-elsa/src/Tamma.Core/Actions/*` (43-2/43-3),
  `apps/tamma-elsa/src/Tamma.Api/Services/Actions/ToolNameAliases.cs` (43-4),
  `Tamma.Core/Documents/Policy/AutonomyDial.cs` (43-1).

## Design Decisions

- **D1 — CP residency is a forced consequence, and the plan records all three reasons so nobody reopens
  it.** (i) sweepers have no ambient `ITenantContext` and the shipped posture is to throw
  (`AcceptanceRulesRepository.cs:21-25`); (ii) `ServiceAuthPrincipal.TenantId` is nullable by design
  (`AuthPrincipal.cs:30-39`); (iii) **a `Migrations/Tenant/` migration never reaches an existing tenant** —
  two creation-only call sites, no sweep — so the table would simply not exist and every gate read would be
  `42P01`. (iii) alone is fatal. The invariant is written into the entity doc comment so the next reader
  gets the reasoning, not just the choice.

- **D2 — Three scopes, and the CHECK is named `_principal_scope` on purpose.** Six shipped stores use
  `ck_<table>_principal_xor` with exactly two admissible cases. This table admits a third — **neither key
  set** — which is the platform ceiling. Naming it `_xor` would be false and would invite a "fix" that
  deletes the ceiling. The name is the documentation. Pinned by
  `Platform_rows_are_never_returned_by_a_principal_query`, which is the behavioural half: a principal query
  must never see a ceiling row (the ceiling is applied by the evaluator via `max()`, not by union).

- **D3 — DROP-list exclusion, tested by reading the SQL string.** `ActionAssignmentResidencyTests` locates
  `Program.cs`, extracts the `ExecuteSqlRaw` literal, and asserts neither table name appears — with a
  failure message that states *why* (safety policy, not operational data) and points at AC5. Reading source
  text in a test is unusual; it is justified because the DROP list is a string literal with no other
  reflectable surface, and the failure mode it guards (every admin tightening silently reverted on restart)
  is catastrophic and invisible. The same test asserts both names **are** present in
  `ControlPlaneDbContextModelTests`' set, so the pair of obligations is one test file.

- **D4 — All three policy columns nullable; "no opinion" is the absence of a row.** Two distinct concepts,
  both needed: a *row that exists but says nothing about `enabled`* (NULL column → inherit), and *no row at
  all* (the next tier owns everything). A non-nullable `enabled DEFAULT TRUE` would make a threshold-only
  write silently re-enable a group-disabled action — the same bug class as 43-0's `acceptorRequirement`
  reset, one layer down. DELETE removes the row and the next tier takes over, mirroring
  `AcceptanceRulesService.DeleteAsync`.

- **D5 — No DB CHECK on `min_autonomy`, and a test enforces the absence.** A CHECK lives in a migration
  snapshot; changing `AutonomyDial.Min` later would then require a migration, which is precisely the second
  hardcoding Story 43-1 exists to eliminate. Validation is domain-side (`AutonomyDial.IsValidThreshold`,
  called by 43-6's endpoints). The existing acceptance-rules body takes the identical posture — opaque
  `jsonb`, no `[Range]`, no CHECK. `Migration_HasNoNumericConstraintOnMinAutonomy` scans the generated
  migration.

- **D6 — The evaluator is pure and takes a snapshot, not a repository.** Signature:
  `AutonomyDecision Evaluate(AutonomyQuery q, GovernancePolicySnapshot snapshot, ResolvedAcceptanceRules baseRules)`.
  Every ladder test then runs with no database and no mocks — the ladder is the part most likely to be
  subtly wrong, so it must be the cheapest to test exhaustively. `Tamma.Core` could not hold it otherwise
  (zero project references).

- **D7 — `max()` composes the three sources; `??` resolves inside the principal ladder.** These are
  different operators for different reasons and the distinction is load-bearing:
  - `max(platformCeiling, legacyFloor, principalLadder)` — monotone encoding (higher = more human), so a
    platform can only tighten and a tenant admin can never lower a platform gate.
  - inside the principal ladder, `actionRow ?? groupRow ?? shippedDefault` — an action override **beats**
    its group outright, which is what "individual actions override their group" means and what
    `AcceptanceRulesService.cs:52-64` already does. The consequence (an admin can lower one action below its
    group) is recorded as a risk, not designed away; mitigations are provenance badges (43-6/43-7), the audit
    event, and 43-7's confirm dialog on lowering a `Destructive` action.
  - `Enforce`, `Enabled`, `AllowedRoles` each run the ladder **independently** — a row that sets only a
    threshold must not carry its NULLs down as decisions.

- **D8 — The `TryPreGate` bridge takes the always-escalate contribution and nothing else.** The evaluator
  calls it, and consumes the result **only** when the escalation's cause is the always-escalate class
  matching this `ActionKey`; the rounds-exhausted outcome (`AcceptanceGuardrailsTests.cs:58,66`) is
  discarded. Contribution is `AutonomyDial.AlwaysHuman`, entering the `max()`, so a legacy entry is a floor
  the new surface cannot lower — only deleting it in the acceptance-rules UI removes it. Pinned by two
  tests (`LegacyAlwaysEscalate_CannotBeLoweredByAnActionRow`, `RoundsExhausted_DoesNotAffectActionThreshold`).

- **D9 — `ISoleUserProvider` is a new singleton and fails loud.** Single-user mode's engine/service/
  background planes have no `ClaimsPrincipal` at all, so `user_id` must come from somewhere: config
  `Tamma:SingleUser:OwnerUserId` first, else the earliest-created `users` row, cached with invalidation on
  user create. Empty `users` throws `GOVERNANCE.PRINCIPAL.NO_SOLE_USER` — guessing here would silently apply
  the wrong principal's policy. In SaaS with no tenant context the resolver falls back to the **platform
  scope only** and emits `PRINCIPAL_UNRESOLVED`; it never reaches for a user row, because in SaaS a user row
  is not a legal principal at all.

- **D10 — The snapshot provider is scoped, not singleton, and the lifetime is pinned.** Singleton would
  need explicit invalidation on every write and would serve stale policy across tenants; per-call would put
  a CP read on every one of a 40-call tool loop. Scoped gives bounded staleness of exactly one request/tick
  with one read pair. Redis invalidation rides the connection already present in `Tamma.Api.csproj` for
  `RateLimitService` when `ConnectionStrings:Redis` is set; otherwise in-process with a 30 s ceiling.
  `Registration_IsScoped` and `TwoGateCallsInOneRequest_IssueOneRepositoryRead` are the pins.

- **D11 — Audit appends directly through `IEventRepository`, not `TammaEventEmitter`.** The emitter
  structurally requires an `ActivityExecutionContext`; the tool loop and the endpoint filter both run inside
  a blocking HTTP request with no such context. The swallowing try/catch of the `<Feature>EventsService`
  template is kept **except** for `.DENIED` / `.REQUIRES_HUMAN` under enforcement: a block with no audit row
  is a compliance hole, so those rethrow. `.ALLOWED` is volume-controlled — emitted only when
  `Source != system-default` or `Enforced` — otherwise a 40-call tool loop writes 40 rows saying "nothing
  happened".

- **D12 — `IAutonomyGate` ships with no production caller, deliberately.** Story 43-9 adds all five seams.
  Landing the component alone keeps this story's diff reviewable and means a bug here cannot change any
  runtime behaviour before its own tests are green. Note this is *not* an observe-mode phase: per epic D1,
  **v1 enforces** — enforcement arrives with the seams, with defaults that reproduce today's behaviour.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Data/Entities/ActionAssignment.cs` and `ActionAuthorization.cs`**
   (AC1/AC4). Doc comments carry D1's three residency reasons verbatim and the D2 naming note. Nullable
   `int? MinAutonomy`, `bool? Enforce`, `bool? Enabled`, `string[]? AllowedRoles`.

2. **MODIFY `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs`** — two entity blocks immediately
   after the `AcceptanceRulesOverride` block (`:1621-1654`), following its shape exactly **minus**
   `ApplyTenantFilter` and `omitTenantIdColumn`:
   - `ck_action_assignments_principal_scope` (three-case), `ck_action_assignments_mode_row`
     (`(target_kind = 'mode') = (min_autonomy IS NULL)`);
   - unique index `(UserId, TenantId, TargetKind, TargetKey)` `.IsUnique().AreNullsDistinct(false)` with an
     explicit `HasDatabaseName`;
   - `action_authorizations`: the same three-case CHECK, `RequestedAtUtc` required, the partial unique index
     over `(TenantId, UserId, CorrelationId, TargetKind, TargetKey)` with
     `.HasFilter("state IN ('pending','granted')")` and `AreNullsDistinct(false)`.
   **No CHECK on `min_autonomy`** (D5).

3. **CREATE `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddGovernedActionCatalog.cs`**
   via `dotnet ef migrations add`, then verify `dotnet ef migrations has-pending-model-changes` is clean
   (AC14).

4. **MODIFY `apps/tamma-elsa/tests/Tamma.Api.Tests/Epic28/ControlPlaneDbContextModelTests.cs:33-48`** — add
   both table names to the `BeEquivalentTo` set. **DO NOT MODIFY `Program.cs:3234-3276`** (AC5).

5. **CREATE `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/ActionAssignmentResidencyTests.cs`** (D3) — the
   DROP-list absence test (source-string read) plus the CP-list presence test, in one file so the two
   obligations are read together.

6. **CREATE `apps/tamma-elsa/src/Tamma.Data/Repositories/IActionAssignmentRepository.cs` +
   `ActionAssignmentRepository.cs` + `IActionAuthorizationLedger.cs` + `ActionAuthorizationLedger.cs`**
   (AC6). Interface doc comment: the `IAcceptanceRulesRepository.cs:8-12` parallel-surfaces invariant
   extended to three planes. Implementation: `ControlPlaneDbContext` injected directly; explicit
   other-key-null predicates on every query; **no** `ApplyTenantFilter`, **no** `ITenantDbContextFactory`,
   **no** `IgnoreQueryFilters()`. Ledger exposes `TryConsumeAsync(principal, correlationId, actionKey)` —
   an `action`-scoped grant covers itself, a `group`-scoped grant covers every member.

7. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Actions/IGovernancePrincipalResolver.cs` +
   `GovernancePrincipalResolver.cs` + `ISoleUserProvider.cs` + `SoleUserProvider.cs`** (AC7, D9). One
   documented rule per plane; the SaaS-without-tenant branch emits `ACTION.GATE.PRINCIPAL_UNRESOLVED` and
   resolves platform-scope only; `SoleUserProvider` reads config → earliest `users` row → throws
   `GOVERNANCE.PRINCIPAL.NO_SOLE_USER`.

8. **CREATE `apps/tamma-elsa/src/Tamma.Core/Actions/IAutonomyGate.cs`, `AutonomyModels.cs`,
   `AutonomyGateEvaluator.cs`** (AC8, D6/D7/D8):

   ```csharp
   public interface IAutonomyGate { Task<AutonomyDecision> EvaluateAsync(AutonomyQuery q, CancellationToken ct = default); }
   public sealed record AutonomyQuery(ActionKey Action, GovernancePrincipal Principal, AgentRole? Role,
                                      string? Operation, string? Target, string? CorrelationId);
   public sealed record AutonomyDecision(AutonomyOutcome Outcome, ActionKey Action, ActionGroup Group,
                                         ActionRisk Risk, int AutonomyLevel, int EffectiveMinAutonomy,
                                         ActionAssignmentSource Source, bool Enforced, ActionKey? CoveredBy,
                                         Guid? AuthorizationId, string? Reason);
   public enum AutonomyOutcome { Automated, RequiresHuman, Denied }
   public enum ActionAssignmentSource { PlatformCeiling, AlwaysEscalateLegacy, ActionOverride, GroupOverride, SystemDefault }
   public static class AutonomyGateEvaluator {
       public static AutonomyDecision Evaluate(AutonomyQuery q, GovernancePolicySnapshot snapshot,
                                               ResolvedAcceptanceRules baseRules);
   }
   ```

   `Denied` vs `RequiresHuman` is not cosmetic: `Denied` is the outcome where **no human route exists**
   (the tool loop, a sweeper). Calling that "escalation" would be a lie, and 43-9 depends on the
   distinction.

9. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/IAcceptanceRulesResolver.cs` and
   `apps/tamma-elsa/src/Tamma.Api/Services/AcceptanceRules/AcceptanceRulesService.cs`** (AC11) — add
   `ResolveBaseAsync(Guid? userId, ct)` / `ResolveBaseForTenantAsync(Guid tenantId, ct)`, lifting the logic
   already at `:91-108`. Names follow the `ForTenant` rationale at `IAcceptanceRulesResolver.cs:9-15`. Every
   existing implementation/mock of the interface must be updated — grep before starting.

10. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Actions/AutonomyGateService.cs`,
    `IGovernancePolicySnapshotProvider.cs` + `GovernancePolicySnapshotProvider.cs`** (AC12, D10). The
    service composes: principal resolver → snapshot provider → `ResolveBase*Async` → pure evaluator →
    events. Register the snapshot provider **scoped** in `Tamma.Api/Program.cs`, beside the acceptance-rules
    registrations at `:414-422`, with a comment naming `Registration_IsScoped`.

11. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Actions/ActionGateEventsService.cs`** (AC13, D11) —
    the `AcceptanceRulesEventsService` template with the eight `const` type strings and the 13-tag set;
    swallowing try/catch except `.DENIED`/`.REQUIRES_HUMAN` under enforcement; `.ALLOWED` volume gate.

12. **CREATE the test suites** — `tests/Tamma.Core.Tests/Actions/AutonomyGateEvaluatorTests.cs`,
    `tests/Tamma.Api.Tests/Actions/{ActionAssignmentRepositoryTests, GovernancePrincipalResolverTests,
    GovernancePolicySnapshotLifetimeTests, ActionGateEventsServiceTests}.cs`. See Test Plan. Finish with
    `dotnet ef migrations has-pending-model-changes` (clean) and full `dotnet test`.

## Test Plan

NUnit + FluentAssertions + Moq; Testcontainers Postgres for the repository and residency suites (the CHECK
constraints and `NULLS NOT DISTINCT` semantics are Postgres behaviour and cannot be proven in-memory).

- **`AutonomyGateEvaluatorTests`** (pure, no DB — the largest suite, deliberately) — the ladder matrix:
  no rows → `system-default` for every member (AC10, iterating the whole 43-3 catalog);
  action row beats group row (`??`, provenance `action-override`); group row applies where no action row
  exists; a platform-ceiling row **raises** but never lowers a principal value; a tenant row attempting to
  go below the ceiling resolves to the ceiling with provenance `platform-ceiling`;
  `LegacyAlwaysEscalate_CannotBeLoweredByAnActionRow`; `RoundsExhausted_DoesNotAffectActionThreshold`;
  per-field independence (`ThresholdOnlyRow_LeavesEnabledInherited`,
  `EnabledFalseAtGroup_SurvivesAnActionThresholdRow` — the D4 bug class);
  `AlwaysHuman_IsNeverAutomatedAtAnyLevelInRange`; `Outcome_IsDeniedNotRequiresHuman_ForNonEscalatableTargets`.
  **Covers AC8, AC9, AC10.**
- **`ActionAssignmentRepositoryTests`** (Testcontainers) — `Platform_rows_are_never_returned_by_a_principal_query`;
  `Reads_DoNotUseTenantDbContextFactory` (the repository's constructor takes `ControlPlaneDbContext`;
  assembly/type-shape assertion plus a source scan for `ITenantDbContextFactory`);
  the three-scope CHECK rejects a row with **both** keys set; the mode-row CHECK rejects
  `target_kind='mode'` with a non-null threshold and rejects `target_kind='action'` with a null one;
  the unique index dedupes `(null, tid, kind, key)` and keeps `(uid, null, …)` disjoint;
  a threshold-only upsert leaves the other three columns NULL (AC2);
  `min_autonomy` accepts a value outside `[70,100]` at the DB layer (proving AC3 — validation is domain-side).
  **Covers AC1, AC2, AC3, AC6.**
- **`ActionAssignmentResidencyTests`** — `Tables_AreNotInTheDestructiveDropList` (reads the `ExecuteSqlRaw`
  literal from `Program.cs`; failure message states the reason) and `Tables_AreOnTheStrictControlPlaneList`.
  Plus `Migration_HasNoNumericConstraintOnMinAutonomy`. **Covers AC5, AC3.**
- **`ActionAuthorizationLedgerTests`** (Testcontainers) — the partial unique index permits a second row once
  the first is `denied`/`expired` but not while `pending`/`granted`; `TryConsumeAsync` — an action grant
  covers itself, a **group** grant covers every member of that group, an expired grant does not,
  a consumed grant does not; `requested_at_utc` is NOT NULL at the DB layer. **Covers AC4.**
- **`GovernancePrincipalResolverTests`** — one test per branch: SaaS-with-tenant → tenant principal;
  SaaS-without-tenant → platform scope only, `PRINCIPAL_UNRESOLVED` emitted, **no user row consulted**;
  single-user human plane → `ClaimsPrincipal` user id; single-user engine plane → `ISoleUserProvider`;
  `SoleUserProvider` prefers config over earliest-user; empty `users` throws `GOVERNANCE.PRINCIPAL.NO_SOLE_USER`;
  `EnginePlane_NeverReadsPrincipalFromTheWireBody` (a caller-supplied `userId`/`tenantId` in the request body
  is ignored). **Covers AC7.**
- **`GovernancePolicySnapshotLifetimeTests`** — `Registration_IsScoped` (interrogates the built
  `IServiceCollection` descriptor); `TwoGateCallsInOneRequest_IssueOneRepositoryRead` (counting fake
  repository, two `EvaluateAsync` calls in one scope → one read pair);
  `ANewScope_IssuesAFreshRead`. **Covers AC12.**
- **`ActionGateEventsServiceTests`** — the eight type strings are exact; the 13-tag set is present and
  correctly populated; a repository failure on `.ALLOWED` is swallowed; a repository failure on `.DENIED`
  **under enforcement** rethrows; `.ALLOWED` is suppressed for `system-default` + not-enforced and emitted
  otherwise. **Covers AC13.**
- **Regression:** the existing `AcceptanceGuardrailsTests`, `AcceptanceRulesService` tests and every
  `IAcceptanceRulesResolver` mock still compile and pass after AC11's widening.

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — `action_assignments`, three scopes, named `_principal_scope` | 1, 2, 3 | `ActionAssignmentRepositoryTests` (CHECK cases) |
| 2 — all policy columns nullable | 1, 2 | `ActionAssignmentRepositoryTests` (threshold-only upsert) + evaluator per-field tests |
| 3 — no DB CHECK on the threshold | 2, 3 | `Migration_HasNoNumericConstraintOnMinAutonomy`; out-of-range insert succeeds |
| 4 — `action_authorizations` + partial unique index | 1, 2, 3 | `ActionAuthorizationLedgerTests` |
| 5 — on the CP list, NOT on the DROP list | 4, 5 | `ActionAssignmentResidencyTests` (both halves) |
| 6 — parallel surfaces, CP context direct | 6 | `Platform_rows_are_never_returned_by_a_principal_query`, `Reads_DoNotUseTenantDbContextFactory` |
| 7 — principal resolution per plane, fail-loud | 7 | `GovernancePrincipalResolverTests` (one per branch) |
| 8 — pure evaluator, `max()` ladder, provenance | 8 | `AutonomyGateEvaluatorTests` (ladder matrix) |
| 9 — legacy floor cannot be lowered; rounds ignored | 8 | the two named guardrail-bridge tests |
| 10 — empty table → shipped defaults everywhere | 8 | `EmptyTable_ResolvesEveryMemberToShippedDefault` |
| 11 — resolver widened once | 9 | Compiles + existing acceptance-rules suites green |
| 12 — scoped snapshot, one read per request | 10 | `GovernancePolicySnapshotLifetimeTests` |
| 13 — one event family; denials not swallowed | 11 | `ActionGateEventsServiceTests` |
| 14 — migrations clean, suite green | 3, 12 | `dotnet ef migrations has-pending-model-changes`, `dotnet test` |

## Risks & Mitigations

- **CP residency puts a control-plane read on the hottest path.** The scoped snapshot reduces it to one read
  pair per request, but a cold cache during a burst, or a CP blip, degrades every agent run. Mitigation:
  the scoped lifetime pin, the Redis-when-configured invalidation, and a load test before 43-9 wires the
  seams. Tenant residency is not an available alternative (D1 (iii)).
- **Someone "fixes" the DROP-list omission.** The single most dangerous edit in this story's blast radius —
  it would silently revert every admin tightening on the next restart. Mitigation: D3's source-reading test
  with an explanatory failure message, plus the entity doc comment.
- **The `??`-inside-`max()` asymmetry reads as an inconsistency.** An admin who sets `deploy-control` to
  `AlwaysHuman` and later sets one member to `Min` has lowered that member without touching the group.
  Mitigation: it is the shipped `AcceptanceRulesService.cs:52-64` semantic, it is what the requirement asks
  for, and it is documented in the evaluator's doc comment, surfaced as provenance in 43-6, and fronted by
  43-7's confirm dialog for `Destructive` members. Recorded as a risk, not designed away.
- **`AlwaysHuman = Max + 1` is derived from `Max`.** Widening the dial *downward* is one edit; **raising
  `Max` would silently reinterpret every stored `101` as an ordinary threshold.** Mitigation: a comment at
  the constant and a test asserting no stored value equals the old sentinel after a `Max` change would be
  needed — out of scope here, flagged for 43-1's constant.
- **AC11's interface widening touches every mock.** Mitigation: grep for `IAcceptanceRulesResolver`
  implementations before step 9; it is mechanical but must not be discovered late.
- **The evaluator ships with no caller, so integration risk is deferred, not removed.** Mitigation: the
  evaluator is pure and exhaustively tested; 43-9's seam tests are where composition risk lands, and the
  `AutonomyDecision` shape is frozen here so 43-9 cannot drift it.
- **43-3's partition is the evaluator's `system-default` tier.** A wrong group assignment produces a
  wrong-but-consistent policy that no test here can catch. Mitigation: stated as 43-3's own risk; AC10 at
  least proves every member resolves to *its declared* default.

## Blocks / Blocked by

- **Blocked by:** 43-1 (`AutonomyDial` — hard), 43-3 (groups + shipped defaults — hard; the `system-default`
  tier is literally 43-3's data), 43-4 (`ToolNameAliases` — hard for the `tool:*` plane only; the rest of
  the evaluator can be built and tested without it).
- **Blocks:** 43-6 (the admin API writes through `IActionAssignmentRepository` and returns the resolved
  shape), 43-9 (every seam calls `IAutonomyGate`; the ledger's `TryConsumeAsync` is what makes one human
  decision cover one deploy across two gate points), 43-7 transitively.
- **Parallel-safe:** 43-8 (drift harnesses) — it depends on 43-2 only and touches no file here.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1–3 | Entities, EF config (two CHECKs, two indexes), migration | 0.8 |
| 4, 5 | CP-list amendment + residency tests (incl. the DROP-list source read) | 0.4 |
| 6 | Two repositories + the ledger's `TryConsumeAsync` | 0.8 |
| 7 | Principal resolver + `ISoleUserProvider` (4 branches, fail-loud) | 0.6 |
| 8 | Pure evaluator: `max()` ladder, per-field resolution, `TryPreGate` bridge | 0.9 |
| 9 | `IAcceptanceRulesResolver` widening + mock fallout | 0.3 |
| 10 | `AutonomyGateService` + scoped snapshot provider + Redis invalidation | 0.6 |
| 11 | `ActionGateEventsService` (8 types, 13 tags, selective non-swallow) | 0.3 |
| 12 | Test suites (7 files; Testcontainers for 3) | 1.3 |
| **Total** | | **6.0** (story estimate: 5 days — the overrun is the evaluator matrix and the Testcontainers suites; flag early if the ladder tests expand) |
