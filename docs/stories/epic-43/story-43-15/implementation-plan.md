# Implementation Plan — Story 43-15: Toggle Encoding and the Dial as Detents With a Diff Preview

Written 2026-08-02 against the working tree. Every file:line below was re-verified on that date;
where a story citation was stale, this plan gives the corrected number and says so.

## Scope & Deliverable

When this story is done:

- A per-action toggle is stored as `min_autonomy = AutonomyDial.Min` — "automated, period" — never
  the dial value at mint time. The mint-time dial goes into the audit event, not the arithmetic.
- The 409 (`ACTION_POLICY.LEVEL_OWNED`) and the `levelOwned`/`editable` view fields key on **what
  the ladder resolves WITHOUT the principal's action row** (group rows, platform ceiling and the
  legacy floor all included) — closing the group-row bypass Amendment 2-E verified.
- `DELETE` on an action row answers with what now applies and names the surviving source
  (`group` | `shipped` | `ceiling`).
- `GET /api/actions/policy/diff?from=L1&to=L2` returns the automated-set delta between two dial
  positions, with last-30-day fire count and approve rate **where a source exists** and `null`
  (rendered "no data") where none does.
- The base-dial UI is detents (the distinct shipped levels) with the diff as a confirm step, and
  lowering the dial enumerates surviving toggles with a bulk revoke offer.
- Zero schema change. Zero C# count-pin movement — by design (see D5).

## Pre-Reading

| File:line (verified 2026-08-02) | Why |
|---|---|
| `docs/stories/epic-43/story-43-15/43-15-toggles-and-dial-ui.md` | The ACs — source of truth. |
| `docs/stories/epic-43/story-43-11/43-11-automation-level-model-and-per-action-levels.md` — Amendment 2 §E/§H, Amendment 3, Amendment 4, the caller-kind re-audit and its Dial-governed / Machinery tables | The ruling model: zones at 5-point steps; the dial governs the LLM only; acceptance is always a step, the dial picks the approver; the four approval scopes. §E is this story's encoding + predicate; §H is the detents + diff and the honest list of which telemetry exists. |
| `apps/tamma-elsa/src/Tamma.Data/Entities/ActionAssignment.cs:60-75` | The toggle row: `TargetKind` `:60-62`, `TargetKey` `:64-68`, nullable `MinAutonomy` with no DB CHECK `:70-75`. Story citation correct. |
| `apps/tamma-elsa/src/Tamma.Core/Actions/AutonomyGateEvaluator.cs:11-17` (composition doc), `:517-557` (`ResolveEffectiveMinAutonomy`: principal ladder `:542-547`, ceiling `:549-554`), `:571-610` (`LegacyAlwaysEscalates`, internal) | The ladder the without-the-row predicate must mirror. `ResolveEffectiveMinAutonomy(descriptor, snapshot)` already exists and is public; the floor helper is `internal`, which is why the new predicate lives in this file (D2). |
| `apps/tamma-elsa/src/Tamma.Core/Actions/AutonomyGovernance.cs:57-100` (`GovernancePolicySnapshot`, `FromSuccessfulRead`) | The snapshot the predicate reads; the endpoint builds a fresh one for writes (the F4 pattern). |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/ActionPolicyEndpoints.cs` — `:56-66` (`GetDial`; the story's "`:57`" is the section comment), `:98-181` (`GetPolicy`), `:145-148` (`editable = true` + S3 comment — the line AC8 deletes), `:187-200` (`PutActionThreshold`), `:247-275` (`DeleteAction` — story 43-11 said `:246-271`, now `:247-275`), `:336-355` (`ResetPolicy`), `:501-546` (`UpsertNonThresholdFieldAsync` + `PinnedEffectiveThreshold` — the fresh-read-through-the-evaluator template the 409 predicate reuses), `:600-627` (`ValidateThresholdForAction`) | Every write/read this story changes, and the house pattern for fresh-read validation. |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs:2914-2951` | The `/api/actions` route block (literal-before-parameterized convention) where the diff route registers. |
| `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AutonomyDial.cs:27,30,38,41,48` | `Min = 70` **today** — 43-11's `Min = 1` has NOT landed. Constrains what is testable now (see Blocked #1). |
| `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs:85-86` | The literal `< 70 or > 100` bound — still in the tree; a dial of 60 is unstorable until 43-11 AC2 lands. |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AcceptanceRulesEndpoints.cs:128-189` (`Upsert`; the literal `base` key is the dial write) | Where the dial-lower enumeration hooks (step 7). |
| `apps/tamma-elsa/src/Tamma.Api/Services/Actions/ActionGateEventsService.cs:42` (`ACTION.GATE.ASSIGNMENT_CHANGED`), `:215-230` (`EmitAssignmentChangedAsync`) | The audit family the mint-time dial tag and the dial-lower event join. |
| `apps/tamma-elsa/src/Tamma.Data/Entities/ActionAuthorization.cs:44-56` (`TargetKind/TargetKey/State/RequestedAtUtc/DecidedAtUtc`) + `Tamma.Data/Repositories/IActionAuthorizationLedger.cs:13-85` | Approve-rate source. The ledger has **no read/aggregate method** — step 6 adds one. |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs:5-46` | `QueryAsync` is exact-type, `ListByTenantAsync` is prefix + pagination, **neither takes a since-date** — step 6 adds a count-by-prefix-since method. |
| `apps/tamma-elsa/src/Tamma.Api/Services/Git/GitEventTypes.cs:32-57` | The mediation event families that DO exist: `GIT.BRANCH_CREATED.*`, `GIT.PR_OPENED.*`, `GIT.PR_MERGED.SUCCESS` **but `GIT.PR_MERGE.FAILED`** (two different prefixes for one action — the telemetry map must carry both), `GIT.ISSUE_UPDATED.*`, `GIT.BRANCH_DELETED.*`, `GIT.RELEASE_CREATED.*`. Plus `AgentDispatchEventTypes.cs:20-21` (`AGENT_DISPATCH.RUN_TRIGGERED.*`). |
| `apps/tamma-elsa/src/Tamma.Api/Services/Actions/ActionGateEventsService.cs:18,35,67` | `.ALLOWED` is volume-gated (SystemDefault suppressed) — Amendment 2-H's "drops exactly the count needed". Agent-action fire counts therefore have **no source**; do not promise them. |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/KnownUngovernedEndpoints.cs:221` (`PinnedCount = 216`), `:235` (`PinHistory = [237, 216]`, strictly decreasing), `:250` (`PinnedInScopeCount = 239` — note: 43-9's plan said 237; it has moved twice since), `:317-335` (`ReviewedUngovernedExceptions`, `ExceptionPinHistory = [2]`, ALSO shrink-only), `:352,354,448,715-724` (the `/api/actions` policy writes, baselined "human-operated") | Why AC7 cannot ship as a NEW mutating route (D4): the baseline may only shrink and the exception set may only shrink. |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/GovernedEndpointCoverageSweepTests.cs:85-100` (`InScopeEndpoints`: mutating verbs + method-less + named governed GETs) | Why the diff GET moves **no** pin: GETs are out of scope unless added to `GovernedGetEndpoints` (`:74`). |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/ActionPolicyEndpointsTests.cs` — `:207` (`PutThreshold_DoesNotResetEnforceEnabledOrRoles`), `:295` (`AutomationTarget_RejectsMidRangeThreshold`), `:333`/`:369` (the fresh-read F4 pins), `:467` (`Policy_ReflectsWrites_AndTheCeilingWins_WithProvenance`), `:505` (`Delete_FallsBackToTheNextTier`) | Existing fixtures this story extends; several assert the exact behaviour this story changes and will need re-vectoring named in the Test Plan. |
| `packages/dashboard/src/components/acceptance-rules/RulesEditDialog.tsx:35-36` (`MIN_AUTONOMY = 70` / `MAX_AUTONOMY = 100`), `:188-189` (slider min/max) + `__tests__/RulesEditDialog.test.tsx` | The free-form dial input AC6 removes. |
| `packages/dashboard/src/pages/admin/acceptance-rules/AcceptanceRulesAdminPage.tsx` + `__tests__/AcceptanceRulesAdminPage.test.tsx:121` (`expect(slider.min).toBe('70')`) | The dashboard pin this story moves. Story 43-11 cited `:116-123`; the assertion is at `:121`. |
| **Verified ABSENT**: no `ActionCatalogAdminPage`, no `/admin/actions` page, no dashboard caller of `/api/actions/*` (grep over `packages/dashboard/src` — zero hits) | Confirms the story's coordination note: 43-7 is unbuilt; UI ACs land in the acceptance-rules surface (D6). |

## Design Decisions

- **D1 — The toggle stores `AutonomyDial.Min`, validated as "must equal `AutonomyDial.Min`", and
  the mint-time dial is an audit tag.** Amendment 2-E's state-machine result is the reasoning: a
  row at dial-at-mint is an inequality against a moving value — a later dial drop below the mint
  value silently kills the toggle while the UI badge (row presence) says ON, and a dial return
  silently resurrects it, path-dependently. A row at `Min` is a constant function of the dial:
  `dial >= Min` at every legal position, so the row means "automated, period" forever.
  *Rejected:* keeping 43-11 AC8's "only legal value is the caller's current dial" — that is the
  encoding that fails; 43-11's AC text gets a pointer here (step 9), not a silent contradiction.
  *Rejected:* a new `is_toggle` column — zero-schema-change is an AC, and `MinAutonomy = Min`
  already resolves correctly through the untouched `max()`/`??` ladder.
  *Consequence to sequence around:* today `Min = 70`; after 43-11 lands, `Min = 1`. A toggle row
  minted before 43-11 stores 70 and stops meaning "period" once the dial can go below 70. Land
  this story's write path **after** 43-11's constant edit (or in the same wave), and the
  validation ("must equal `AutonomyDial.Min`") self-heals: it always demands the current constant.

- **D2 — The without-the-row predicate is a new pure function in `AutonomyGateEvaluator`, next to
  the ladder it mirrors.** `ResolveLadderWithoutActionRow(descriptor, snapshot, baseRules)` =
  principal **group** row `??` shipped default, then platform ceiling (action **and** group rows —
  the ceiling is not a toggle and stays in) by `max()`, then the legacy always-escalate floor by
  `max()`. `levelOwned = dial >= thatResolution`. This closes the verified group-row bypass in
  both directions: a group row at `Min` covering an above-dial action makes it level-owned (409);
  a group/ceiling row holding a below-dial action shut makes it editable.
  *Why in Core:* `LegacyAlwaysEscalates` is `internal` to `Tamma.Core` (`:571`), and the greying
  rule must be computed "with the same comparison the gate uses" — same file, shared `Row`/floor
  helpers, so it cannot drift. *Rejected:* computing it in the endpoint by cloning the snapshot
  minus one key — spreads ladder knowledge into `Tamma.Api` and misses the floor.
  *Write-path input is FRESH rows, not the snapshot* — the endpoint already has the exact pattern
  (`PinnedEffectiveThreshold`, `ActionPolicyEndpoints.cs:526-546`, review F4): fresh
  `ListForPrincipalAsync` + `ListPlatformAsync` → `GovernancePolicySnapshot.FromSuccessfulRead`.
  The read-path (`GetPolicy`) uses the cached snapshot, same as today.

- **D3 — The diff is computed over the principal's EFFECTIVE ladder; the detents are the distinct
  SHIPPED levels.** The diff answers "what changes for THIS deployment", so
  `Automated(L) = { dial-governed d : L >= effectiveMin(d) }` with the principal's rows included —
  a toggle at `Min` is automated at both `from` and `to` and correctly never appears in the delta;
  for `from > to` the surviving toggles (action rows at `Min` whose without-the-row resolution
  exceeds `to`) are listed separately, which is AC5's symmetric clause. The detents answer "where
  can the set change at all", which is a catalog fact — the distinct `DefaultMinAutonomy` values
  over dial-governed rows, served by the API so the UI hardcodes nothing.
  *Rejected:* detents from effective levels — they would shift whenever an admin writes a group
  row, making the control's positions unstable under the admin's own edits.

- **D4 — Bulk revoke reuses `POST /api/actions/policy/reset` with an optional `targets` body; NO
  new mutating route.** The endpoint-coverage ratchet forbids the obvious design: `PinnedCount`
  (216) sits under a strictly-decreasing `PinHistory` (`KnownUngovernedEndpoints.cs:221,235`), so
  a new baselined route is red by design, and the D17 exception set is itself shrink-only
  (`ExceptionPinHistory = [2]`, `:335`) **and** requires a circularity argument a bulk-revoke
  cannot honestly make. The remaining honest options were: bind a new route with `.Governs` (no
  catalog key exists for "edit autonomy policy" — the whole family is baselined "human-operated";
  minting one is a vocabulary decision this story does not own), or reuse an existing baselined
  route. Reset-with-targets IS revoke semantics: "remove these overrides, fall back to the
  ladder". Absent body keeps today's delete-all behaviour byte-identical; present body deletes
  only the named `action`-scope rows, each emitting its own `ASSIGNMENT_CHANGED` event ("audited
  individually", AC7). The route's baseline entry (`:448`) is untouched; no pin moves.
  *This is a deliberate reading of AC7's "one call", recorded here rather than planned around
  silently — if review insists on a dedicated route, the story blocks on a ratchet-widening
  decision that belongs to the harness owners, not this story.*

- **D5 — Zero C# count pins move, and that is a design input, not luck.** The diff endpoint is a
  GET — out of the coverage sweep's scope (`GovernedEndpointCoverageSweepTests.cs:94-100`) unless
  named in `GovernedGetEndpoints`, and it is not governance-bound, so `PinnedInScopeCount` (239)
  and `PinnedCount` (216) stand. D4 keeps the mutating surface unchanged. The catalog is untouched
  (197 / 21-bound stay; 43-12 owns those moves). The only pins that move are dashboard test pins
  (see Count Pins).

- **D6 — UI lands in the acceptance-rules surface, because 43-7's page does not exist.** Verified:
  no `ActionCatalogAdminPage`, no dashboard caller of `/api/actions/*`. The dial is edited today
  through `RulesEditDialog` (base row) under `AcceptanceRulesAdminPage`. So AC6/AC7 land there:
  the base-dial control becomes a detent select fed by a new `GET /api/actions/policy/diff` call
  on selection, with confirm; the free 70–100 slider for the BASE row is removed (per-type
  `autonomyLevel` overrides in the same dialog are 43-11 AC13's problem and are not touched here
  beyond what the shared component forces). This also absorbs the base-row half of 43-11 AC13's
  dashboard work (`MIN_AUTONOMY`/`MAX_AUTONOMY` deletion) — coordinate so it is done once.
  *Rejected:* building the 43-7 page here — two days of scaffolding the 43-7 story owns, and the
  toggle-badge UI (`toggleAboveDial`) has no host until that page exists; the API field ships now,
  the badge ships with 43-7.

- **D7 — The dial-lower enumeration is SERVER-authored.** When the base-row `Upsert` lowers
  `AutonomyLevel`, the handler enumerates surviving toggles (principal action rows at `Min` whose
  without-the-row resolution exceeds the new dial) and emits one
  `ACTION.GATE.TOGGLES_SURVIVED_DIAL_LOWER` event naming them. The UI shows the same list
  pre-confirm (from the diff endpoint) and offers bulk revoke (D4). "Declining records the
  choice" is then structural: the audit trail shows the dial-lower event with the surviving list,
  followed — or not — by the revoke deletions. *Rejected:* a client-posted "user declined" flag —
  the server should not depend on the client to make the audit trail honest.

- **D8 — Telemetry honesty is a pinned TABLE, not best effort.** A declarative map
  (`ActionTelemetrySources`) names exactly which action keys have a fire-count source today:
  the six git mediation families (note merge needs BOTH `GIT.PR_MERGED.` and `GIT.PR_MERGE.`
  prefixes — the success/failure types differ, `GitEventTypes.cs:38-39`) and
  `AGENT_DISPATCH.RUN_TRIGGERED.` — and that approve rates come only from decided
  `action_authorizations` rows. Everything else is `null` on the wire and "no data" in the UI,
  never zero. Amendment 2-H's verified gaps (`.ALLOWED` volume-gated for SystemDefault, no Seam B
  decision events, no actionKey index, structurally-empty grant table) are the reason; the
  emitters are explicitly out of scope (43-9's lane). A test pins the map so a future author who
  adds an emitter must consciously widen it. *Rejected:* counting agent-actions from `.ALLOWED` —
  the volume gate suppresses precisely the rows needed (`ActionGateEventsService.cs:18,67`).

## Blocked / Contradictions

1. **AC2's literal test sequence (toggle at dial 70 → lower dial to 60 → raise back) is
   unpassable against today's tree.** `AutonomyDial.Min = 70` (`AutonomyDial.cs:27`) and
   `AcceptanceRules.Validate` literally rejects `< 70` (`AcceptanceRules.cs:85-86`) — a dial of
   60 cannot be stored until 43-11 AC1+AC2 land. Not planned around silently: the named
   regression test (`Toggle_SurvivesDialMoves_NoSilentKillOrResurrect`) is written in this story
   but **sequenced after 43-11**, and an interim variant exercises the same mechanism inside
   [70,100] using a shipped-`AlwaysHuman` descriptor (e.g. `document-type:design`, 101 > any
   dial): toggle on at 70 → `toggleAboveDial: true` and automated via the row — which proves the
   encoding without a sub-70 dial. The silent-kill/silent-resurrect halves need the real drop.
2. **AC6's detents are degenerate until 43-11's level table lands.** Today's distinct shipped
   levels are {70, 101} — one legal detent. The control, the endpoint and the tests are written
   generically (distinct levels served by the API, no hardcoded count); asserting "13 detents" or
   "156 dial rows" would pin numbers this story does not own (43-11/43-12/43-13 all move them).
3. **AC7's "one call" vs the shrink-only endpoint ratchets** — resolved by D4 (reset-with-targets),
   recorded there as a deliberate reading, with the block condition named if review rejects it.
4. **AC3/AC8's machinery exclusion depends on 43-13.** No caller-kind/machinery marker exists in
   the tree today, so "the policy view excludes machinery from detent math (156 rows)" is
   unimplementable until 43-13's classifier lands. The diff/detent code takes its row filter from
   one predicate function with a default of "all catalogued rows" and a TODO pinned to 43-13; the
   156 number is asserted only in 43-13's lane.

## Implementation Steps

1. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Actions/AutonomyGateEvaluator.cs`** (D2) — add
   `public static (int EffectiveMinAutonomy, ActionAssignmentSource Source)
   ResolveLadderWithoutActionRow(ActionDescriptor, GovernancePolicySnapshot,
   ResolvedAcceptanceRules?)`: principal group `??` shipped, ceiling `max()` (both platform row
   kinds), legacy floor `max()` via the existing internal helper; non-authoritative snapshot fails
   closed to `AlwaysHuman`/`Unavailable` exactly like `:526-531`. Effort: 0.5 day (with tests).

2. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Endpoints/ActionPolicyEndpoints.cs`** (AC1, AC3) —
   in `PutActionThreshold` (`:187-200`) / `ValidateThresholdForAction` (`:600-627`): body
   `minAutonomy != AutonomyDial.Min` → 400 (`ACTION_POLICY.INVALID`, message naming the toggle
   encoding); then fresh-read rows (the `:508-515` pattern), compute the without-the-row
   resolution + the principal dial, and `dial >= resolution` → **409
   `ACTION_POLICY.LEVEL_OWNED`** naming both numbers and the owning source. The write path stores
   `Min`. Extend the `EmitAssignmentChangedAsync` call (`ActionGateEventsService.cs:215`) with a
   `dialAtMint` tag (add an optional parameter; existing callers unchanged). `PUT …/enabled`,
   `…/enforce`, `…/roles` and the ceiling routes are untouched. Effort: 0.5 day.

3. **MODIFY `DeleteAction` (`ActionPolicyEndpoints.cs:247-275`)** (AC4) — after the delete,
   recompute the without-the-row resolution from fresh rows and return
   `{ nowResolvesTo, source: "group"|"shipped"|"ceiling" }` (map `GroupOverride`→`group`,
   `SystemDefault`→`shipped`, `PlatformCeiling`→`ceiling`; `AlwaysEscalateLegacy` reported as
   `shipped` with the floor noted in `reason`). Effort: 0.25 day.

4. **MODIFY `GetPolicy` (`ActionPolicyEndpoints.cs:98-181`)** (AC8, AC2's flag) — per action add
   `shippedLevel` (= `d.DefaultMinAutonomy`), `ladderWithoutRow` (step 1, from the cached
   snapshot), `levelOwned = viewLevel >= ladderWithoutRow`, `editable = !levelOwned`,
   server-authored `reason`, and `toggleAboveDial` (principal action row present at `Min` AND
   `ladderWithoutRow > dial`). **Delete `editable = true` and the S3 comment at `:145-148`.**
   Effort: 0.5 day.

5. **MODIFY `ResetPolicy` (`ActionPolicyEndpoints.cs:336-355`) + `Program.cs:2926`** (AC7, D4) —
   accept an optional body `{ targets: string[] }`: absent → today's delete-all, byte-identical;
   present → delete exactly those `action`-scope rows (validate each wire key), one
   `ASSIGNMENT_CHANGED` emission per row, response lists deleted/missing. No new route; the
   `KnownUngovernedEndpoints.cs:448` entry stands. Effort: 0.5 day.

6. **CREATE the diff endpoint** (AC5, D3, D8):
   - `apps/tamma-elsa/src/Tamma.Api/Services/Actions/ActionTelemetryReader.cs` — the pinned
     source map + reads;
   - **MODIFY `Tamma.Data/Repositories/IEventRepository.cs` + `EventRepository.cs`** — add
     `Task<int> CountByTypePrefixSinceAsync(Guid? tenantId, string typePrefix, DateTime sinceUtc)`;
   - **MODIFY `Tamma.Data/Repositories/IActionAuthorizationLedger.cs` +
     `ActionAuthorizationLedger.cs`** — add
     `Task<IReadOnlyList<ActionAuthorization>> ListDecidedSinceAsync(Guid? tenantId, Guid? userId,
     DateTime sinceUtc, CancellationToken ct)` (approve rate = granted / (granted + denied) per
     `TargetKey`; group grants are NOT attributed to members in v1 — recorded in the reader's doc);
   - **MODIFY `ActionPolicyEndpoints.cs`** — `GetPolicyDiff(from, to, …)`: validate both levels,
     compute the effective-ladder delta, direction, per-action `{ shippedLevel, fireCount30d,
     approveRate30d }` (nullable), `detents` (distinct shipped levels + current), and for
     `from > to` the `survivingToggles` list;
   - **MODIFY `Program.cs`** — `actionsPolicy.MapGet("/policy/diff", …)` registered with the
     literal routes (`:2925` block). No sweep entry needed (GET, D5).
   Effort: 1 day.

7. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Endpoints/AcceptanceRulesEndpoints.cs` (`Upsert`,
   `:128-189`) + `ActionGateEventsService.cs`** (AC7's record, D7) — when the `base` row's
   `AutonomyLevel` decreases, enumerate surviving toggles (fresh rows + step 1) and emit new
   `ACTION.GATE.TOGGLES_SURVIVED_DIAL_LOWER` (constant added beside `:42`) with the row list and
   both dial values. Best-effort emission (assignment rows are the durable fact), same posture as
   `ASSIGNMENT_CHANGED`. Effort: 0.5 day.

8. **Dashboard** (AC6, AC7-UI, D6) —
   - CREATE `packages/dashboard/src/components/acceptance-rules/DialDetentControl.tsx` (detent
     list from the diff endpoint's `detents`, no free-form number input) and
     `DialDiffPreview.tsx` (the delta list; count column renders "no data" for `null`, never 0;
     surviving-toggles panel with "Revoke all" → `POST /api/actions/policy/reset` with `targets`,
     and "Keep them" → proceed);
   - MODIFY `RulesEditDialog.tsx` — the BASE-row `autonomyLevel` slider (`:188-189`) becomes
     `DialDetentControl` + confirm; delete `MIN_AUTONOMY`/`MAX_AUTONOMY` (`:35-36`);
   - MODIFY `packages/dashboard/src/pages/admin/acceptance-rules/AcceptanceRulesAdminPage.tsx` +
     tests to match.
   Effort: 1.5 days.

9. **Doc amendments** (AC1's pointer; the 43-11 AC15 convention) — MODIFY
   `docs/stories/epic-43/story-43-11/43-11-automation-level-model-and-per-action-levels.md`: AC8
   rule 2 and M3 rule 2 ("only legal value is the caller's current dial") gain a superseded-by
   pointer to this story; MODIFY `docs/stories/epic-43/story-43-7/43-7-admin-ui.md`: a note that
   the toggle badge consumes `toggleAboveDial` and the detent control exists in acceptance-rules
   pending the 43-7 page. Effort: 0.25 day.

10. **Run everything** (AC10) — `dotnet test`, `dotnet ef migrations has-pending-model-changes`
    (clean — no entity change; the two repository methods are query-only), dashboard tests via
    pnpm/Vitest. Effort: 0.25 day.

Sequencing: 1 → 2/3/4 (one lane, same file) → 5 → 6 → 7 → 8, with 9 alongside 2 and 10 last.
Steps 1–7 are C#-only; step 8 is TS-only — two disjoint sub-lanes after step 6's response shape
is fixed.

## Test Plan (fail-first: what red looks like today)

NUnit + FluentAssertions; the existing `ActionPolicyEndpointsTests` WebApplicationFactory +
Testcontainers fixture; Vitest for the dashboard.

**Core (`Tamma.Core.Tests/Actions/AutonomyGateEvaluatorLadderWithoutRowTests.cs`, new):**
- `WithoutRow_GroupRowStillCounts` / `WithoutRow_CeilingStillCounts` / `WithoutRow_FloorStillCounts`
  / `WithoutRow_ActionRowIsIgnored` — red today: the function does not exist; the fixture does not
  compile. That is the honest red state for a new pure function; the behavioural red lives in the
  endpoint tests below.

**API (`Tamma.Api.Tests/Actions/ActionPolicyEndpointsTests.cs` + a new
`ActionPolicyDiffEndpointTests.cs`):**
- `ToggleWrite_StoresDialMin_AndRejectsAnyOtherValue` — PUT threshold body `90` on
  `document-type:design` (above-dial via shipped 101) expecting 400, then body `AutonomyDial.Min`
  expecting 200 with the stored row at `Min` and the audit event carrying `dialAtMint`. **Red
  today:** the 90 write returns 200 and stores 90 (`ValidateThresholdForAction` accepts any valid
  threshold).
- `LevelOwned_ViaGroupRow_Rejects409` — seed a principal GROUP row at `Min` covering
  `document-type:design` (whose shipped 101 is above every dial), then PUT the action threshold:
  expect 409 `ACTION_POLICY.LEVEL_OWNED`. **Red today:** returns 200 — this is the group-row
  bypass, inverted into a pin exactly as AC3 demands.
- `HeldShutByCeiling_IsStillEditable` — ceiling row at `AlwaysHuman` on a shipped-`Min` action;
  GET policy expects `levelOwned: false, editable: true` for it. **Red today:** the response has
  no `levelOwned` field at all (deserialization assert fails).
- `PolicyView_MarksLevelOwnedRowsNonEditable` + `PolicyView_GroupRowVariant` (AC8's named pins) —
  **red today:** `editable` is unconditionally `true` (`:148`) and `shippedLevel`/`reason`/
  `toggleAboveDial` are absent.
- `Delete_NamesTheSurvivingSource` — group row + action row, DELETE the action row, expect
  `source: "group"` and the group's value. **Red today:** the response is only
  `{ message: "Assignment deleted; the next tier applies." }` (`:272`).
- `Reset_WithTargets_DeletesOnlyTheNamedRows_AndAuditsEach` — seed three action rows, reset with
  two targets, expect the third to survive and two `ASSIGNMENT_CHANGED` emissions. **Red today:**
  reset ignores any body and deletes all three (`DeleteAllForPrincipalAsync`, `:349`).
- `Reset_WithoutBody_IsByteIdenticalToToday` — the D4 no-regression pin; written green and
  mutation-checked by making the handler require a body.
- `Diff_ReturnsTheDelta_BothDirections` / `Diff_RejectsInvalidLevels` — **red today:** the route
  404s.
- `Diff_RendersNullNotZero_WhenTelemetryIsEmpty` — empty event store + empty ledger → every
  `fireCount30d`/`approveRate30d` is JSON `null`. **Red today:** 404; once the endpoint exists,
  this is the pin that stops a future "default to 0" — its post-landing red is a literal `0` on
  the wire.
- `Diff_FireCounts_ComeFromTheMediationFamilies` — seed `GIT.PR_MERGED.SUCCESS` +
  `GIT.PR_MERGE.FAILED` events, expect both counted for the merge action (the two-prefix trap).
  **Red today:** 404; post-landing red if the map carries only one prefix.
- `TelemetrySourceMap_IsPinned` — asserts the map's exact key set, so adding/removing a source is
  a reviewed diff. Red only when the map drifts — that is its job.
- `DialLower_EmitsSurvivingTogglesEvent` — toggle row + base-row PUT lowering the dial (within
  [70,100] until 43-11; the sub-70 leg joins the blocked test), expect one
  `TOGGLES_SURVIVED_DIAL_LOWER` event naming the row. **Red today:** no such event type exists.
- `Enabled_IsWritableOnALevelOwnedRow` (AC9) — enabled PUT on a row the new predicate marks
  level-owned. **Cannot go red against today's code** (no 409 exists yet anywhere): it is a
  regression pin against step 2 mis-scoping the 409; written before step 2, watched green across
  it, and mutation-checked by temporarily applying the 409 to the enabled route.
- **Sequenced after 43-11:** `Toggle_SurvivesDialMoves_NoSilentKillOrResurrect` — the Amendment
  2-E failure sequence (on at 70 → dial 60 → still automated + `toggleAboveDial: true` → dial 70
  → identical state). Unwritable today (Blocked #1); its red state, once 43-11 lands, is the
  dial-at-mint encoding this story deletes.
- **Re-vectored, not deleted:** `Policy_ReflectsWrites_AndTheCeilingWins_WithProvenance` (`:467`)
  and `Delete_FallsBackToTheNextTier` (`:505`) gain the new response fields;
  `OutOfRangeThreshold_Is400` (`:279`) keeps its cases (all non-`Min` values are now 400 for a
  different reason — the assert message is updated to name the toggle encoding).

**Dashboard (Vitest):**
- `DialDetentControl.test.tsx` — renders exactly the detents from the payload; no free-form
  number/range input. **Red today:** the component does not exist.
- `RulesEditDialog.test.tsx` — base-row dial: selecting a detent fetches the diff and requires
  confirm; "no data" rendered for null counts; revoke-all posts `targets`; decline proceeds
  without a post. **Red today:** the dialog renders a `range` input with `min=70` and no diff
  fetch.
- `AcceptanceRulesAdminPage.test.tsx:121` — `expect(slider.min).toBe('70')` is **deleted with the
  slider** and replaced by the detent assertions (this is the pin movement, named below).

## Count Pins

| Pin | Before (tree, 2026-08-02) | After | Why |
|---|---|---|---|
| `KnownUngovernedEndpoints.PinnedCount` | 216 (`:221`) | **216 — unchanged** | D4/D5: no new mutating route, no baseline entry. |
| `KnownUngovernedEndpoints.PinnedInScopeCount` | 239 (`:250`) | **239 — unchanged** | The diff GET is out of sweep scope. |
| `KnownUngovernedEndpoints.ExceptionPinHistory` | `[2]` (`:335`) | **`[2]` — unchanged** | No exception claimed. |
| `ActionVocabularyCountTests` total | 197 | **197 — unchanged** | Catalog untouched; 43-12 owns the move to 205. |
| `ActionEnforcementSitesTests` bound rows | 21 | **21 — unchanged** | No seam work here. |
| `AcceptanceRulesAdminPage.test.tsx:121` slider bounds pin | `slider.min === '70'` | **deleted** — replaced by detent-control assertions | AC6 removes the free slider for the base dial. |
| `RulesEditDialog.tsx:35-36` `MIN_AUTONOMY`/`MAX_AUTONOMY` | `70`/`100` literals | **deleted** | Absorbed half of 43-11 AC13; coordinate so it is done once. |

This story deliberately moves **no C# count pin**. If implementation finds one moving, something
drifted from D4/D5 — stop and re-check before bumping anything.

## Dependencies on the batch (43-12..16, 42-10, 39-25, 40-8, 31-13)

- **43-11 — must land first (or same wave).** Supplies `Min = 1` (`AutonomyDial.cs:27`), the
  `AcceptanceRules.cs:85-86` rewire, and the zone level table. Until then: AC2's drop-to-60 test
  is unwritable (Blocked #1), the detents are degenerate (Blocked #2), and toggle rows minted
  early store 70 instead of 1 (D1). This story amends 43-11's AC8/M3 text in place (step 9).
- **43-13 — should land before or with this story.** Owns the machinery classifier that excludes
  the 42 machinery rows from detents/diff (Blocked #4) and the machinery threshold-400. Shared
  files: `ActionPolicyEndpoints.cs`, `AutonomyGateEvaluator.cs`, `ActionPolicyEndpointsTests.cs`
  — 43-13 and 43-15 are the SAME LANE for wave planning; do not schedule them concurrently.
- **43-12 — not blocking; order-sensitive for content.** Reshapes the catalog (197 → 205, per-
  target merge/deploy keys) and therefore the detent/diff contents and the merge action's
  telemetry key. The diff code is generic over the catalog, so either order works; if 43-12 lands
  after, the telemetry map's merge entry is re-pointed at `git.merge.*` in 43-12's lane.
- **43-14 — not blocking.** Feeds approve rates (grant rows exist only once something is gated
  and approved); until it lands the rate column is honestly `null`/"no data" (D8). The new
  `ListDecidedSinceAsync` reads columns 43-14's `Scope` migration does not touch.
- **43-16 — no file overlap in the plan's steps** (it lives in `AcceptanceDefaults`/
  `AcceptanceFloors`); watch only step 7's hook in `AcceptanceRulesEndpoints.Upsert` if 43-16
  amends the same handler's validation.
- **42-10** (mints `effect:secret.read`), **31-13** (issue/PR keys), **40-8**, **39-25** — no
  shared files, no ordering constraint; new catalog keys simply appear in the detents/diff once
  minted.

## Risks

- **The 409 predicate consults fresh rows on every threshold PUT** — two extra repository reads
  per write. Writes are rare admin actions; the F4 precedent (`:501-521`) already pays this cost
  for enforce/enabled writes. Not a hot path.
- **`CountByTypePrefixSinceAsync` scans `domain_events` by type prefix with no supporting index**
  (Amendment 2-H verified: no actionKey index; type is a plain column). The diff is an admin
  page, cadence is human-click; if it measures slow, the index is 43-9-lane work (H's other ACs),
  not silent scope growth here. The endpoint caps its window at 30 days by construction.
- **Reset-with-targets overloads an existing route's meaning.** Mitigated by the byte-identical
  no-body pin and by the response naming what was deleted; the alternative (a new route) is
  blocked by the ratchets, and that trade is recorded in D4, in the open.
- **Landing before 43-11 mints toggles at 70.** "No migration anxiety" (CLAUDE.md) makes this
  survivable — rows can be wiped — but the clean path is sequencing (D1). If a pre-43-11 deploy
  happens anyway, a one-line follow-up UPDATE of `Min`-rows is the fix, not a schema change.
- **Same-file contention with 43-13** (`ActionPolicyEndpoints.cs`, the evaluator, the endpoint
  tests). Handled by lane planning, named under Dependencies; merging them blind will conflict in
  `ValidateThresholdForAction` specifically (43-13 deletes the two-state automation rule this
  story's step 2 sits next to).
- **The UI lives in acceptance-rules until 43-7 exists.** When the 43-7 page is built, the detent
  control and preview move/duplicate there; the components are written self-contained (props in,
  callbacks out) so the move is a lift, not a rewrite.

## Definition of Done

| AC | Steps | Verified by |
|---|---|---|
| 1 — toggle stores `Min`, 400 otherwise, audit carries mint dial, 43-11 text amended | 2, 9 | `ToggleWrite_StoresDialMin_AndRejectsAnyOtherValue` |
| 2 — survives dial moves visibly | 2, 4, 7 (+43-11) | interim variant now; `Toggle_SurvivesDialMoves_NoSilentKillOrResurrect` after 43-11 |
| 3 — 409 keys on ladder-without-row, both directions | 1, 2 | `LevelOwned_ViaGroupRow_Rejects409`, `HeldShutByCeiling_IsStillEditable` |
| 4 — DELETE names surviving source | 3 | `Delete_NamesTheSurvivingSource` |
| 5 — diff API | 6 | `Diff_*` fixtures |
| 6 — detents + preview + confirm, no free input | 6, 8 | `DialDetentControl.test.tsx`, `RulesEditDialog.test.tsx` |
| 7 — dial-lower enumeration + bulk revoke, decline recorded | 5, 7, 8 | `Reset_WithTargets_…`, `DialLower_EmitsSurvivingTogglesEvent`, dialog tests |
| 8 — policy-view fields replace `editable = true` | 4 | `PolicyView_MarksLevelOwnedRowsNonEditable` + group variant |
| 9 — enabled orthogonal | 2 (scoping) | `Enabled_IsWritableOnALevelOwnedRow` (+ mutation check) |
| 10 — green, no schema change | 10 | `dotnet test`, ef pending-model-changes, Vitest |

## Estimated Effort

Total ≈ 5.75 days: step 1 — 0.5; step 2 — 0.5; step 3 — 0.25; step 4 — 0.5; step 5 — 0.5;
step 6 — 1; step 7 — 0.5; step 8 — 1.5; step 9 — 0.25; step 10 — 0.25. The story estimated 4;
the overage is step 6's two small data-layer additions (no aggregate reads existed) and the
fail-first re-vectoring of the five existing endpoint tests, both discovered against the tree.

## Change Log

| Date       | Version | Changes | Author |
| ---------- | ------- | ------- | ------ |
| 2026-08-02 | 1.0.0   | Initial plan. Verified all story citations (one drift: `DeleteAction` is `:247-275`, not `:246-271`; `PinnedInScopeCount` is 239, not 43-9-era 237). Recorded: AC2's sub-70 leg blocked on 43-11; AC7 rerouted through reset-with-targets because both endpoint ratchets are shrink-only; machinery exclusion deferred to 43-13's classifier; zero C# count pins move by design. | Claude |
