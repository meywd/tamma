# Implementation Plan — Story 43-6: Admin API + RBAC for the Action Catalog

## Scope & Deliverable

When this story is done, `/api/actions` is a working admin surface: reads (`AuthenticatedAny`) publish the
dial, the catalog, and the effective policy *rendered at a level the caller chooses to view*; writes
(`ActionsManage`) change exactly one field at a time, per group or per action, keyed on the right principal
for the operating mode. The new `actions:manage` permission is registered in all three required places —
including the Development-permissive literal array that silently breaks dev-without-JWT when missed — and
the permission matrix gains the key-set pin it has never had. Every write DTO is single-field and
non-defaulted, structurally preventing the `acceptorRequirement` silent-reset bug class from recurring on a
safety surface. `automation:*` targets are validated down to two states, because a mid-range threshold there
would silently behave as Deny.

Nothing enforces yet — Story 43-9 owns the seams. This story makes policy authorable and readable.

## Pre-Reading

- `docs/stories/epic-43/README.md` — the model (§3 one integer per (target, principal); §4 resolution and
  provenance), and **D1: v1 enforces with defaults reproducing today's behaviour**
- `docs/stories/epic-43/story-43-5/` — `IActionAssignmentRepository`, `IActionAuthorizationLedger`,
  `IGovernancePrincipalResolver`, `AutonomyDecision` / `ActionAssignmentSource`, `ActionGateEventsService`
- `apps/tamma-elsa/src/Tamma.Api/Auth/Permissions.cs:12` (`Matrix`), `:70-80` (the `pricing:manage` /
  `acceptance-rules:manage` pair and the comment explaining why owner-only `settings:manage` 403s every
  tenant_admin) — **18 keys today**
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Auth/PermissionsMatrixTests.cs` — **read the whole file**: it is
  `[TestCase]`-driven with no count and no key-set assertion (see Corrections)
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:1518-1696` — the 22 `AddPolicy` registrations;
  `AcceptanceRulesManage` at `:1618` is the template
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:1719-1730` — the Development-without-JWT branch; the literal
  22-name array is at `:1724-1726` and `AddPolicy(name, …AllowAnonymousRequirement)` at `:1728`
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:2714-2734` — the acceptance-rules group: the `AuthenticatedAny`
  read-gate rationale (`:2716-2727`) and the **literals-before-parameterized** warning (`:2728-2733`).
  The new group is registered immediately after `:2734`.
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/AcceptanceRulesEndpoints.cs:37,77,121,152` — the inline mode
  split repeated in every handler; `:77` shows the discard form (`is Guid` with no capture) used where the
  id is not needed
- `apps/tamma-elsa/src/Tamma.Api/Dtos/AcceptanceRules/AcceptanceRulesDtos.cs:12-25` — the full-object PUT
  with the **defaulted trailing `AcceptorRequirement = AcceptorRequirement.Any`** at `:22-25`. This is the
  exact shape to not repeat.
- `apps/tamma-elsa/tests/Tamma.Api.Tests/**/AcceptanceRulesEndpointsTests.cs:192-197` — the
  `ContainKey("acceptance-rules:manage")` assertion that must keep passing
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminApiKeysEndpoints.cs:63` — api-key permissions accepted
  free-form, zero validation against the matrix (why `acceptance-rules:manage` cannot be deleted, and why
  `actions:manage` is self-grantable)
- `apps/tamma-elsa/src/Tamma.Core/Actions/EnumWire.cs` (43-2) — `:39` case-sensitive ordinal parse; reject,
  never coerce
- **NOT FOUND (prerequisites):** `Tamma.Api/Services/Actions/*` (43-4/43-5), `Tamma.Core/Actions/*`
  (43-2/43-3), `Tamma.Core/Documents/Policy/AutonomyDial.cs` (43-1),
  `Tamma.Data/Repositories/IActionAssignmentRepository.cs` (43-5).

## Corrections to the design

1. **"`PermissionsMatrixTests` count goes 18 → 19" describes a pin that does not exist.**
   `PermissionsMatrixTests.cs` is entirely `[TestCase("<permission>")]`-driven — `Member_HasBasicReadPermissions`,
   `Member_DeniedAdminPermissions`, `Admin_HasAdminPermissions`, `Owner_HasOwnerOnlyPermissions` — and makes
   **no assertion about `Permissions.Matrix.Count` or its key set**. Adding a permission and forgetting the
   test file is invisible today. The plan therefore *adds* the key-set pin (step 2) rather than
   incrementing a number, and says so in the commit message so a reviewer looking for "18 → 19" finds the
   reason it is not there.
2. **`Permissions.cs` has 18 matrix keys, and `acceptance-rules:manage` is the last, at `:80`** — verified
   by enumerating the `["…:…"]` keys. The design's "immediately after `:80`" is correct.
3. **The Development-permissive array is at `Program.cs:1724-1726`** (the design says `:1726-1728`) and the
   `AddPolicy` line is at `:1728`. The array holds exactly 22 names today, matching the 22 `AddPolicy`
   registrations in the production branch — so the invariant to preserve is "these two lists have the same
   membership", which step 2's test asserts directly rather than pinning a number.

## Design Decisions

- **D1 — One endpoint per field, never a full-object PUT.** The 43-0 bug is structural, not a typo: a
  defaulted trailing field on a full-object DTO makes an omitted field indistinguishable from an explicit
  reset. Twelve narrow endpoints cost more route registrations and are worth it here, because the failure
  mode on a safety catalog is "the admin's deploy gate quietly went back to automated". As a corollary the
  UI needs **no draft/dirty state at all** (43-7 D-side benefit): every control PUTs its one field and
  refreshes.

- **D2 — Records with positional required parameters, no defaults, and explicit `[JsonPropertyName]`.**
  `public sealed record SetThresholdRequest([property: JsonPropertyName("minAutonomy")] int MinAutonomy);`
  A missing `minAutonomy` binds `default(int)` = 0 in System.Text.Json, which is **not** a 400 for a value
  type — so each handler additionally validates against `AutonomyDial` and 0 is out of range, producing the
  400. For `SetEnabledRequest(bool Enabled)` there is no invalid value, so those two DTOs use
  `bool?` with an explicit null check and a 400 on null. Stated here because "non-defaulted record ⇒ 400"
  is true for reference types and *not* for `int`/`bool`, and getting that wrong reintroduces the exact bug.

- **D3 — Level is a query parameter on a read, never persisted.** `?level=NN` parameterizes the *display*;
  storage stays level-independent (S3). The endpoint computes `automatedAtLevel` by calling the same
  `ActionPolicyValue.IsAutomatedAt` the gate calls — not by re-implementing `min <= level` — so the greyed
  rule and the enforcement rule are one method. `?level` omitted defaults to the principal's current dial.

- **D4 — `editable` is always `true`, and it is still in the payload.** It looks redundant. It is there so
  the UI has a single field to bind and so a future policy ("platform-ceilinged rows are read-only") is a
  server change, not a UI change. Pinned by a test asserting it is true for every row today, so the
  constant-ness is deliberate and visible rather than accidental.

- **D5 — `enforcementSites` is computed from the catalog descriptor's binding count, and shipping `0` is the
  honest answer.** On day one only ~17 of ~205 mutating routes carry a gate binding; every other member's
  `enforcementSites` is `0`. Without this field the UI would render a fully-populated policy table that
  implies protection the system does not have. It is the single most important field for not lying to the
  admin.

- **D6 — `automation:*` validation lives in the endpoint, not in the DB or the evaluator.** A sweeper cannot
  suspend for a human, so `EscalatableToHuman = false` on those descriptors and the only enforcing outcome is
  `Denied`. A mid-range threshold would render as "human below level N" and behave as Deny. Rejecting at the
  API is the earliest point where the admin gets a message; the evaluator still handles the value correctly
  if one arrives by another path (defence in depth, not duplication).

- **D7 — The mode split is repeated inline in every handler.** `AcceptanceRulesEndpoints.cs:37,77,121,152`
  and `PromptEndpoints.cs:252` do this; six stores do this; there is no helper. Introducing the repo's first
  one inside a safety story is a half-migration that leaves seven call sites on the old idiom. Mitigated by a
  per-endpoint `SaaS_caller_never_reads_a_user_scoped_row` test rather than by abstraction.

- **D8 — Writes are principal-scoped only; the platform ceiling has no write path in v1.** The evaluator
  honours ceiling rows (43-5), and they can be inserted operationally, but exposing a ceiling write here
  would need a platform-owner-only sub-surface and a second RBAC story. Out of scope, stated in the story.

- **D9 — `POST /policy/reset` is a bulk DELETE of the calling principal's rows, not a "restore defaults"
  write.** "No opinion is the absence of a row" (epic §3). Writing 153 rows of shipped defaults would make
  the table 100% noise and destroy the empty-table zero-blast property.

## Implementation Steps

1. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Auth/Permissions.cs`** — add `["actions:manage"] = ["admin", "owner"]`
   immediately after `:80`, with a comment mirroring the `acceptance-rules:manage` one (owner-only
   `settings:manage` would 403 every tenant_admin; single-user is unaffected because every signed-up user is
   auto-owner of their personal tenant).

2. **MODIFY `apps/tamma-elsa/tests/Tamma.Api.Tests/Auth/PermissionsMatrixTests.cs`** (AC4, Correction 1) —
   add `Matrix_KeySet_IsExact` (19 literal names, symmetric-diff failure message naming what to add/remove)
   and `[TestCase("actions:manage")]` rows to the member-denied / admin-has / owner-has fixtures. Leave every
   existing case untouched.

3. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Program.cs`** (AC3):
   - `options.AddPolicy("ActionsManage", …)` beside `AcceptanceRulesManage` at `:1618`, same requirement shape;
   - **add `"ActionsManage"` to the Development-permissive literal array at `:1724-1726`.**
   **CREATE `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/ActionsPolicyRegistrationTests.cs`** —
   `ActionsManage_IsInDevelopmentPermissiveArray` plus `DevelopmentPermissiveArray_MatchesRegisteredPolicyNames`
   (the general invariant, which would have caught this class of miss for all 23).

4. **CREATE `apps/tamma-elsa/src/Tamma.Api/Dtos/Actions/ActionCatalogDtos.cs`** (AC2, AC5, AC6, D2) — the
   four single-field write DTOs and the read shapes:

   ```csharp
   public sealed record SetThresholdRequest([property: JsonPropertyName("minAutonomy")] int MinAutonomy);
   public sealed record SetEnforceRequest ([property: JsonPropertyName("enforce")]  bool? Enforce);
   public sealed record SetEnabledRequest ([property: JsonPropertyName("enabled")]  bool? Enabled);
   public sealed record SetRolesRequest   ([property: JsonPropertyName("allowedRoles")] string[]? AllowedRoles);

   public sealed record DialDto(int Min, int Max, int AlwaysHuman, int Default, int Current);
   public sealed record ActionPolicyRowDto(string Key, string Ns, string Title, string Summary, string Risk,
       bool Reversible, string? SiteKey, int MinAutonomy, bool? Enforce, bool? Enabled, string[]? AllowedRoles,
       string Source, bool AutomatedAtLevel, bool Editable, bool EscalatableToHuman, int EnforcementSites,
       string? WhyGreyed);
   public sealed record ActionGroupDto(string Group, string Title, string Description,
       GroupAssignmentDto Assignment, bool AutomatedAtLevel, int AutomatedCount, int Total,
       IReadOnlyList<ActionPolicyRowDto> Actions);
   public sealed record EffectivePolicyResponse(DialDto Dial, int Level, IReadOnlyList<ActionGroupDto> Groups);
   ```

   No property carries a default value. `bool?` on the two boolean DTOs is deliberate (D2) — it is the only
   way a missing field is distinguishable from `false`.

5. **CREATE `apps/tamma-elsa/src/Tamma.Api/Endpoints/ActionCatalogEndpoints.cs`** (AC1, AC5–AC12) — a static
   endpoint class in the house shape. Handlers:
   `GetDial`, `GetCatalog`, `ListEffective` (`?level`), `GetAction`, `SetGroupThreshold`, `SetGroupEnforce`,
   `DeleteGroup`, `SetActionThreshold`, `SetActionEnforce`, `SetActionEnabled`, `SetActionRoles`,
   `DeleteAction`, `ResetAll`, `ListAuthorizations`, `Decide`.
   Each write handler: parse target → validate (D6 + `AutonomyDial`) → resolve principal (inline mode split,
   D7) → repository upsert of the **single column** → emit audit → return the refreshed resolved row.

6. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Program.cs`** — register the group immediately after `:2734`,
   with a comment block mirroring `:2716-2733` (the `AuthenticatedAny` read-gate rationale and the
   literals-before-parameterized warning). Literal routes (`/dial`, `/catalog`, `/policy`, `/policy/reset`,
   `/authorizations`) precede `/policy/groups/{group}/…` and `/policy/actions/{ns}/{key}/…`.

7. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Actions/ActionAssignmentWriteService.cs`** — the thin
   service the endpoints call: validation, principal resolution, single-column upsert through
   `IActionAssignmentRepository`, audit emission through `ActionGateEventsService`, and the
   read-back-resolved response. Keeps the endpoint class to parsing + status codes, matching
   `AcceptanceRulesEndpoints` → `AcceptanceRulesService`.

8. **CREATE the test suites** — `tests/Tamma.Api.Tests/Actions/{ActionCatalogEndpointsTests,
   ActionCatalogValidationTests, ActionCatalogRbacTests, ActionsPolicyRegistrationTests}.cs`. See Test Plan.

## Test Plan

NUnit + FluentAssertions; `WebApplicationFactory` for the endpoint suites (the house pattern for
`AcceptanceRulesEndpointsTests`), Testcontainers Postgres where a real upsert must be read back.

- **`ActionCatalogEndpointsTests`** —
  `PutThreshold_DoesNotResetEnforceEnabledOrRoles` (**the load-bearing test**: set all four, PUT only the
  threshold, assert the other three unchanged in storage);
  `PutThreshold_MissingField_400` (empty body and `{}` both), same for enforce/enabled/roles;
  `ListEffective_ComputesAutomatedAtLevel_ViaTheGateMethod` (a member with `minAutonomy = 85` is
  `automatedAtLevel` at `?level=85` and not at `84`);
  `ListEffective_IncludesDialSoTheClientNeverHardcodesBounds`;
  `ListEffective_ReportsZeroEnforcementSitesForUngovernedMembers` (D5);
  `Editable_IsTrueForEveryRow` (D4);
  `Delete_FallsBackToNextTier`; `Reset_DeletesEveryRowForThePrincipal_AndWritesNone` (D9);
  `Decide_TransitionsPendingToGranted`, `Decide_OnDecidedRow_409`, `Decide_OnExpiredRow_409`.
  **Covers AC2, AC5, AC10, AC11, AC12.**
- **`ActionCatalogValidationTests`** — `UnknownWire_400`; `WrongCasing_400` (`Agent-Action:Deploy` rejected,
  not coerced); `ThresholdBelowMin_400`, `ThresholdAboveAlwaysHuman_400`, `AlwaysHuman_IsAccepted`;
  `AutomationTarget_RejectsMidRangeThreshold` (and accepts `Min` and `AlwaysHuman`);
  `UnknownGroup_400`. **Covers AC7.**
- **`ActionCatalogRbacTests`** — `Member_Gets403OnWrite` (parameterized over all nine write routes);
  `Member_CanRead` (over all five read routes); `RouteOrder_LiteralsBeatParameterized` (walks
  `EndpointDataSource` and asserts the literal routes are matched, not captured by a sibling parameter);
  `SingleUser_KeysOnUserId`, `SaaS_KeysOnTenantId`, and per-endpoint
  `SaaS_caller_never_reads_a_user_scoped_row` (seed a user-scoped row, call as a SaaS tenant, assert it is
  not returned). **Covers AC1, AC8, AC9.**
- **`ActionsPolicyRegistrationTests`** — `ActionsManage_IsInDevelopmentPermissiveArray`;
  `DevelopmentPermissiveArray_MatchesRegisteredPolicyNames` (the general invariant);
  `ActionsManage_PolicyResolves_InProductionBranch`. **Covers AC3.**
- **`PermissionsMatrixTests` (amended)** — `Matrix_KeySet_IsExact` (19 names) plus the three new
  `[TestCase("actions:manage")]` rows; `acceptance-rules:manage` cases and
  `AcceptanceRulesEndpointsTests.cs:192-197` still green. **Covers AC4.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — route group, literals first | 5, 6 | `RouteOrder_LiteralsBeatParameterized` |
| 2 — single-field, non-defaulted DTOs | 4, 5 | `PutThreshold_DoesNotResetEnforceEnabledOrRoles`, the `_400` family |
| 3 — permission in all three places | 1, 3 | `ActionsPolicyRegistrationTests` (both tests) |
| 4 — matrix key-set pin + new cases | 2 | `Matrix_KeySet_IsExact` + the `actions:manage` cases |
| 5 — level-parameterized, server-computed view | 4, 5 | `ListEffective_*` family |
| 6 — dial + catalog published | 5 | `ActionCatalogEndpointsTests` (GET shape assertions) |
| 7 — exact validation, reject not coerce | 5 | `ActionCatalogValidationTests` |
| 8 — correct principal per mode | 5, 7 | `SingleUser_KeysOnUserId`, `SaaS_KeysOnTenantId`, the per-endpoint isolation tests |
| 9 — 403 on writes for members | 6 | `Member_Gets403OnWrite` (all nine routes) |
| 10 — DELETE falls to next tier | 5, 7 | `Delete_FallsBackToNextTier`, `Reset_…_AndWritesNone` |
| 11 — audit on every write, best-effort | 7 | write tests assert an event row; an audit failure does not fail the write |
| 12 — decide endpoint transitions the ledger | 5 | `Decide_*` family |

## Risks & Mitigations

- **The Development-permissive array is a literal list that must stay in lockstep with 23 `AddPolicy`
  calls.** Missing it produces a failure only in dev-without-JWT, which CI does not exercise. Mitigation:
  `DevelopmentPermissiveArray_MatchesRegisteredPolicyNames` generalizes the check to all names, so the next
  permission author is covered too.
- **Twelve narrow endpoints is a lot of surface for one story.** Mitigation: they share one write service
  (step 7) and one validation path; the endpoint class is parsing and status codes only. If the estimate
  slips, the `roles` and `authorizations` endpoints are the safest to defer — but note 43-7 renders roles
  and the pending-authorizations panel, so deferring costs a UI change too.
- **`actions:manage` is self-grantable.** `api_keys.Permissions` is accepted free-form with no validation
  (`AdminApiKeysEndpoints.cs:63`), and a `"*"` permission claim succeeds unconditionally. A platform admin
  or a wildcard key can rewrite every threshold unchallenged. Not fixable here; recorded as an epic open
  question (two-person control) and a risk.
- **The whole admin surface is unauthenticated in Development-without-JWT** (`Program.cs:1719-1730`).
  Pre-existing, but the action catalog is a safety artifact, so the exposure is worse in kind. Must never be
  reachable from a shared environment; call it out in review.
- **`enforcementSites: 0` for most members will read as a bug to a reviewer.** Mitigation: it is the honest
  value on day one and is exactly what stops the UI implying protection; the test name says so, and 43-7
  renders it as a visible "not enforced" badge rather than hiding it.
- **A group PUT can lower every unoverridden member at once.** The API does not confirm; 43-7's confirm
  dialog does. If 43-7 slips, a scripted group write is unguarded — mitigated only by the audit event.

## Blocks / Blocked by

- **Blocked by:** 43-5 (repository, ledger, principal resolver, events — hard), 43-1 (`AutonomyDial` — hard),
  43-2/43-3 (catalog, groups, descriptors — hard; `GET /catalog` is a projection of them).
- **Blocks:** 43-7 (the UI binds to these exact shapes; the DTO keyset is pinned on both sides),
  43-9 (the `decide` endpoint is how a human grant reaches the ledger the seams consume).
- **Parallel-safe:** 43-8 (drift harnesses). Note one interaction: 43-8's `GovernedEndpointCoverageTests`
  sweeps **all** mutating endpoints, so the nine write routes added here will need either a `.Governs(…)`
  binding or `KnownUngovernedEndpoints` entries. Coordinate: these are policy-administration routes, not
  governed effects, so they belong on the allowlist with the justification
  `policy-administration-surface-not-a-governed-effect`.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1–3 | Permission in three places + registration tests + matrix key-set pin | 0.4 |
| 4 | DTOs (write + read shapes, `[JsonPropertyName]`, no defaults) | 0.3 |
| 5 | Endpoint class: 15 handlers, parsing, status codes | 0.7 |
| 6 | Route group registration + ordering + rationale comment | 0.2 |
| 7 | Write service: validation, principal, single-column upsert, audit | 0.5 |
| 8 | Four test suites (incl. the nine-route RBAC parameterization) | 0.9 |
| **Total** | | **3.0** (story estimate: 3 days) |
