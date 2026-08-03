# Implementation Plan — Story 43-13: Caller-Kind Predicate — the Dial Governs the LLM Only

Written 2026-08-02 against the working tree. Every file:line below was re-verified on that date;
where the story document's citation drifted, the verified line is given and the drift noted.

## Scope & Deliverable

One enum (`CallerKind { Human, Machinery, Llm }`), one resolver function that turns auth state
into a caller kind, one defaulted field on `AutonomyQuery`, and two evaluator short-circuits:

- **Human** — never gated. Short-circuits to `Automated` with new reason `ReasonCallerHuman`
  before any policy input is consulted. RBAC still applies (it ran before the gate did).
- **Machinery** — never dial-gated. The 42 machinery-inventory rows (5 plumbing effects + 29
  `automation:*` + 8 `platform-task:*`) get an `IsMachinery` descriptor flag; the evaluator
  replaces the dial comparison for them (and for any caller declared `Machinery`) with a terminal
  `Automated` / `ReasonMachineryNotDialGoverned`. The `enabled` off-switch, role restrictions and
  the F6 fail-closed degradation still apply — only the threshold/dial machinery is bypassed.
- **Llm** — everything else, including **every engine-token call**, fail-closed by a defaulted
  `Caller = CallerKind.Llm` on `AutonomyQuery`. The dial path is unchanged for this caller.

Plus: threshold writes on machinery targets become a 400 naming the classification; every gate
decision event gains a `callerKind` tag; fixtures pin the 42 machinery rows (identical decisions
at dial 1 and dial 100) and the 7 dormant HUMAN rows. No count pin moves.

## Pre-Reading

| File:line (verified 2026-08-02) | Why |
|---|---|
| `docs/stories/epic-43/story-43-13/43-13-caller-kind-predicate.md` | The ACs — source of truth. |
| `docs/stories/epic-43/story-43-11/…md` — Amendment 4 (`:1458-1512`), caller-kind re-audit (`:991-1031`), machinery inventory (`:1320-1393`), dial table (`:1032-1318`) | The ruling model and this story's fixture content: 120 LLM / 7 HUMAN / 28 DUAL / 42 MACHINERY. |
| `apps/tamma-elsa/src/Tamma.Api/Infrastructure/GovernanceEnforcement.cs:224-294` | Seam C. Binding/gate/principal resolved `:233-244`; `principals.ResolveAsync` `:281`; `AutonomyQuery` built `:285-293`. (Story cites `:242-287`; the query construction actually runs to `:294`.) This is where Seam C's caller kind is read. |
| `apps/tamma-elsa/src/Tamma.Api/Services/Actions/GovernancePrincipalResolver.cs:29-98` | Proof the existing principal is a *scope*, not a *kind* — `ResolveAsync` (`:65-97`) returns tenant/user/platform and cannot tell a human JWT from the engine token. Class at `:39` as cited. |
| `apps/tamma-elsa/src/Tamma.Api/Auth/AuthPrincipal.cs:15-50` | The typed union the resolver keys on: `UserAuthPrincipal` `:18`, `InstallationAuthPrincipal` `:25`, `ServiceAuthPrincipal` `:35`, `GetAuthPrincipal` `:48`. |
| `apps/tamma-elsa/src/Tamma.Api/Auth/ApiKeyAuthHandler.cs:549-666` | How each key scope mints its principal (`"service"` case `:586-617`, claims incl. `"scope"` `:648-662`). `Context.SetAuthPrincipal` `:639`. |
| `apps/tamma-elsa/src/Tamma.Api/Auth/ClaimsPrincipalExtensions.cs:40-56` | `GetUserId` reads JWT `sub` then `NameIdentifier`. A service key's `NameIdentifier` is a service *name* (not a Guid) so `GetUserId` is null for it — but do NOT build the predicate on that accident; use the typed principal. |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs:1705-1719` | `EngineServiceOnly` — only a `ServiceAuthPrincipal` passes; a user JWT is 403. Explains why no human reaches today's 16 enforced routes (all EngineServiceOnly, `:3157-3520`). |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaEngineAuthHandler.cs:56-90` | The engine's Bearer stamp (`Tamma:ApiToken`) — what makes every mediation call a service-scope call at the API. |
| `apps/tamma-elsa/src/Tamma.Core/Actions/AutonomyGateEvaluator.cs:124-246, 258-442` | The pure evaluator. `Evaluate` signature `:183-187` (as cited); uncatalogued short-circuit `:220-246`; ladder `:258`; per-field ladders `:310-341`; not-enforceable `:359-366`; disabled `:368-377`; roles `:379-385`; degraded `:387-400`; **the dial comparison `:413`** — the machinery short-circuit goes immediately before it. |
| `apps/tamma-elsa/src/Tamma.Core/Actions/AutonomyGovernance.cs:299-306, 338-…` | `AutonomyQuery` (gains the `Caller` field), `AutonomyDecision`. |
| `apps/tamma-elsa/src/Tamma.Core/Actions/ActionDescriptor.cs:58-69` | Descriptor record — gains `IsMachinery`. |
| `apps/tamma-elsa/src/Tamma.Core/Actions/ActionCatalog.Descriptors.cs:38-75, 296-309, 393` | The six helpers (`Agent :38`, `Doc :44`, `Tool :49`, `Effect :55`, `Automation :62`, `Task :71`) and the five plumbing-effect declaration sites: `EngineEventsAppend :296`, `EnginePlatformEventsAppend :298`, `EngineDocumentPersist :300`, `EngineDocumentSetStatus :309`, `SecretReveal :393`. |
| `apps/tamma-elsa/src/Tamma.Api/Services/Actions/AutonomyGateService.cs:137-205, 254-320` | The DB-backed gate; `ConsultLedgerAsync` `:254` — only `RequiresHuman` consults the ledger, so Human/Machinery short-circuits make the ledger structurally unreachable for those callers with zero changes here (43-14 coordination point). |
| `apps/tamma-elsa/src/Tamma.Api/Services/Actions/BackgroundActionGate.cs:90-160, 232-259` | Seam D. The query it builds `:117-131` gains `Caller: Machinery`. Call sites (accessor + `MayRunTickAsync` continuation): `RevealTokenSweeper.cs:64-65`, `ChannelOutboxSweeper.cs:77-78`, `OutboxSlackSender.cs:133-134`, `TaskQueueProcessor.cs:94-95`, `OutboxSmtpSender.cs:153-154` (story's lines are the accessor line; the invocation continues one line below — same sites). |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/GovernanceEvaluateEndpoints.cs:54-137` | Seam E's server half. Query built `:88-100`; no auth-state read today. |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/LlmCallEndpoints.cs:232-260` | Seam A's observe-only evaluation (`new AutonomyQuery` `:251`). |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/ActionPolicyEndpoints.cs:126, 569-598, 600-627` | The policy-view evaluator call (`:126`, stays LLM-view); `InvalidGroupThreshold` `:569-598`; `ValidateThresholdForAction` `:600-627` with the two-state automation rule at `:614-624`. (Story cites `:600-625`; the method closes at `:627`.) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Actions/ActionGateEventsService.cs:70-132` | `EmitDecisionAsync` — the tag set `:90-117`, volume gate `:84-87`. Gains `callerKind`. |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/CatalogDefaultToolLoopAutonomyGate.cs:118-231` | Seam B. It calls `ResolveEffectiveMinAutonomy` (`:222`) — **not** `Evaluate` — and its input is a model-emitted tool call, so its caller kind is structural (Llm), not computed. |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/AutonomyGateEndpointFilterTests.cs:37-161` | The Seam C harness (DefaultHttpContext + scripted gate) the new caller-kind seam tests extend. |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Actions/BackgroundActionGateTests.cs:86-215` | Seam D helper tests — mock the inner gate, so they pass unmodified; the machinery declaration is one added assertion. |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/KnownUngovernedEndpoints.cs:221, 235, 250, 335, 368-369, 393-447, 476-479, 739` | The pins that must NOT move, and the two recorded decisions that shape AC2 (see Blocked/contradictions): scheduled-trigger routes deliberately unbound (`:368-369, :476-479, :739`); tracker routes binding-owned-by Story 44-2 (`:393, :400, :424, :436, :446`). |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/ActionEnforcementSitesTests.cs:159-220` | The 21-bound-rows pin (`:169`, `:200`) that must not move, and `IActionEnforcementSites` — the mechanism the dormant-HUMAN fixture uses to detect a future LLM path (a `method:` site appearing). |
| `apps/tamma-elsa/tests/Tamma.Core.Tests/Actions/ActionVocabularyCountTests.cs:131-149` | The 197 pin — untouched. |
| `apps/tamma-elsa/tests/Tamma.Core.Tests/Actions/ActionDescriptorMetadataTests.cs:44-53` | `Every_automation_member_is_non_escalatable` — its comment ("the 43-6 API will reject mid-range thresholds") is corrected by step 7. |
| `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs:83-85` | `Validate()` still literal `< 70 or > 100` (43-11 AC2 unlanded). Why the dial-1 fixture must construct `ResolvedAcceptanceRules` directly — `Validate()` is not on the pure-evaluator path. |

## Design Decisions

- **D1 — `CallerKind` rides `AutonomyQuery` as a defaulted field, not a new `Evaluate` parameter.**
  `AutonomyQuery` gains `CallerKind Caller = CallerKind.Llm` after `SeamCanBlock`
  (`AutonomyGovernance.cs:299-306`). Every existing construction site (five in `src/`, verified:
  `GovernanceEnforcement.cs:285`, `BackgroundActionGate.cs:118`, `GovernanceEvaluateEndpoints.cs:89`,
  `LlmCallEndpoints.cs:251`, `ActionPolicyEndpoints.cs:126`) compiles unchanged and lands on the
  fail-closed default. *Rejected:* a separate `Evaluate(query, snapshot, rules, callerKind)`
  parameter — breaks the signature 43-14 is coordinating against and churns every caller for no
  semantic gain. *Rejected:* ambient context (AsyncLocal / HttpContext.Items) — invisible at call
  sites, un-greppable, and exactly how a second computation site sneaks in.

- **D2 — The default is `Llm`, and the default IS the acceptance criterion.** AC3 demands that an
  engine-token call with no declaration hits the dial. A defaulted field makes "forgot to declare"
  resolve to the gated path; the construction-site pin (D8) makes a *new* site a reviewed decision.
  *Rejected:* a required, non-defaulted parameter — stronger at compile time, but it forces the
  policy-view call (`ActionPolicyEndpoints.cs:126`, which genuinely wants the LLM-path view) and
  Seam A to state the obvious, and it re-breaks 43-14's staging. The fail-closed direction is
  identical either way; the pin covers the difference.

- **D3 — One resolver, typed-principal first, and `Machinery` is NEVER claimable from the wire.**
  `CallerKindResolver.Resolve(HttpContext)` (new file, Tamma.Api) is the single function that
  computes a caller kind from auth state:
  1. `http.GetAuthPrincipal()` is `ServiceAuthPrincipal` or `InstallationAuthPrincipal` → **Llm**
     (the engine token, any service key, any GitHub App installation key — fail-closed);
  2. `UserAuthPrincipal` → **Human** (a user-scope key is a user credential);
  3. no typed principal but the `"scope"` claim says `service`/`installation` → **Llm**
     (belt-and-braces if `Items` is lost across a context copy);
  4. JWT plane: `Identity.IsAuthenticated` and `GetUserId()` non-null → **Human**;
  5. anything else (anonymous, malformed) → **Llm**.
  `Machinery` has no wire spelling at all: it exists only as the in-process declaration Seam D's
  helper makes. *Rejected:* a `machinery` key scope or header — a credential can be exfiltrated
  into an LLM path (the shell-curl bypass), and a wire-claimable "never gate me" kind is a
  self-service bypass. *Rejected:* deciding Human from `GetUserId() != null` alone — it works only
  by the accident that service-key OwnerIds are non-Guid strings (`ApiKeyAuthHandler.cs:650`);
  the typed principal is the honest source.

- **D4 — The machinery short-circuit sits AT the dial comparison, not before the policy checks.**
  In `Evaluate`, compute `machineryPath = descriptor.IsMachinery || query.Caller == Machinery`,
  and immediately before `if (dial >= effectiveMin)` (`AutonomyGateEvaluator.cs:413`) return
  `Automated` / `ReasonMachineryNotDialGoverned` (with `Enforced = false`,
  `EffectiveMinAutonomy = AutonomyDial.Min`, `Source = SystemDefault`) when `machineryPath`.
  What still applies to machinery, deliberately: `enabled = false` still denies (`:368-377` — the
  admin's only off-switch once thresholds are gone, and 43-11 M3 rule 3 says it is orthogonal to
  the level); role restrictions still deny (`:379-385`); F6 degradation still fails closed
  (`:387-400` — an unreadable table cannot testify that no disable row exists). What is bypassed:
  the threshold ladder's *outcome* — dial, action/group rows, platform ceiling, the AlwaysHuman
  sentinel. That is exactly "identical decisions at dial 1 and dial 100", which the fixture proves.
  *Rejected:* short-circuiting at the top of `Evaluate` — kills `enabled=false` and the fail-closed
  posture, i.e. removes Seam D's remaining deny levers and contradicts AC6.
  *Rejected:* keying the short-circuit on the caller alone (no descriptor flag) — AC4 requires the
  42 *rows* to be unreachable through the dial resolver whoever asks; the policy view and 43-15's
  detent math also need the row-level fact.

- **D5 — The Human short-circuit sits BEFORE the policy checks.** Right after the uncatalogued
  branch (`:220-246`, so the decision still carries the descriptor's group/risk): if
  `query.Caller == Human`, return `Automated` / `ReasonCallerHuman` (`Enforced = false`,
  `EffectiveMinAutonomy = AutonomyDial.Min`, `Source = SystemDefault`). A person is subject to
  ordinary RBAC only (story, Architectural Context): `enabled`, roles, degradation and the dial
  are all controls on the *system's* autonomy, and Amendment 4's test ("gating a person on
  themselves is absurd") applies to each. A human passes even during a control-plane outage.
  *Rejected:* applying the `enabled` disable to humans — "this deployment never sends email" is
  about autonomous sending; the human dashboard path has its own RBAC, and a governance row that
  can block a person cancelling their own mentorship session is the exact failure this story
  exists to remove.

- **D6 — AC2/AC3/AC7's route-level proofs run in harness hosts over the REAL filter, gate and
  resolver — production routes are not touched.** Two recorded decisions in the tree forbid the
  literal reading of AC2 (see Blocked/contradictions): the scheduled-trigger routes are
  *deliberately unbound* (KnownUngovernedEndpoints.cs:368-369, decided 2026-08-01 by 43-9 §C-bis)
  and the tracker routes' bindings are owned by Story 44-2 (`:393` et al.). Binding either here
  would also move the 21-bound pin and the 216 baseline — AC9 forbids both. So the two-direction
  test maps the real route *shapes* (`PUT /api/admin/scheduled-triggers/{id}` bound to
  `effect:schedule.update`; `PATCH /api/work-items/{id}` bound to
  `effect:tracker.work-item.update`) in a test host carrying `.Governs(...).EnforcesGovernance()`,
  with the real `AutonomyGateEndpointFilter` → real `AutonomyGateService` → real evaluator and a
  snapshot forcing the level above the dial; AC3 additionally drives a real production-shaped
  engine credential at the same seam. When 44-2 (tracker) or a future story (schedule) binds the
  production routes, these tests become redundant with the production path, not wrong.

- **D7 — Threshold writes on machinery targets: 400 `ACTION_POLICY.MACHINERY_NOT_DIAL_GOVERNED`,
  checked before the enforceability check; the group rule stops policing machinery members.**
  In `ValidateThresholdForAction` (`ActionPolicyEndpoints.cs:600-627`): `descriptor.IsMachinery` →
  400 naming the classification and this story, for ANY value (today `Min` is accepted on
  automation targets — that acceptance is the red state). The old two-state branch (`:614-624`) is
  deleted as moot: every `EscalatableToHuman = false` row is an `automation:*` row and every one is
  machinery (subsumption verified against `ActionCatalog.Descriptors.cs:62-69`). Machinery wins
  over `NOT_ENFORCEABLE` for `effect:secret.reveal` — the classification is the stronger, newer
  fact. `InvalidGroupThreshold` (`:569-598`): the machinery-member rejection is removed — a group
  threshold is provably inert for machinery members (the evaluator never reads it, D4), so
  mid-range group writes become legal; a group whose enforceable members are ALL machinery
  (`platform-automation`) is 400, because the write could govern nothing. `PUT …/enabled` is
  untouched at every step. *Rejected:* silently accepting-and-ignoring machinery thresholds — a
  200 that does nothing is the false affordance this epic keeps hunting down.

- **D8 — AC1's "grep" is a residency test, not a grep.** `CallerKindResidencyTests`: (a) a
  source-scan over `src/Tamma.Api` asserting the `AuthPrincipal`/`"scope"`-claim inspection that
  produces a `CallerKind` exists only in `CallerKindResolver.cs`; (b) a pinned list of the
  `new AutonomyQuery(` sites in `src/` (five today + none added by this story), so a new
  construction site — the only place a caller kind can be declared — fails until classified.
  This is the mechanical form of "no second site computes caller kind from auth state".

- **D9 — Audit: `callerKind` tag on every emitted decision; human passes are exempt from the
  `.ALLOWED` volume gate, machinery passes are not.** `EmitDecisionAsync` adds
  `tags["callerKind"] = query.Caller` (wire: `human` / `machinery` / `llm`). The volume gate
  (`ActionGateEventsService.cs:84-87`) gains one carve-out: an allow with reason
  `ReasonCallerHuman` is emitted even at `SystemDefault` source — it is precisely the new
  predicate's work product, and human traffic on enforced routes is zero today (all 16 are
  `EngineServiceOnly`) so the volume risk is bounded. Machinery short-circuit allows keep
  `SystemDefault` source and stay suppressed — Seam D would otherwise emit one row per actor per
  tick. *Rejected:* emitting machinery allows — per-tick flooding for a row that says "nothing is
  ever going to happen here".

- **D10 — Seam B is structurally Llm; no signature change.** The story's AC1 sentence "Seam B/D
  callers pass a CallerKind into `AutonomyGateEvaluator.Evaluate`" is imprecise for B: the tool
  loop gate never calls `Evaluate` — it calls `ResolveEffectiveMinAutonomy`
  (`CatalogDefaultToolLoopAutonomyGate.cs:222`) on a sync path. Its input is a model-emitted tool
  call, so the caller is the LLM *by construction*, which is stronger than a passed flag. The
  fact is recorded in `IToolLoopAutonomyGate`'s doc-comment and pinned by a comment-anchored
  assertion in the residency test, not by plumbing a constant through a sync interface that could
  then be mis-set. Recorded as a deviation from AC1's letter, in the story's favor.

### Where each seam reads Human vs Machinery vs Llm (the question this plan must answer exactly)

| Seam | Where the kind is read | Result |
|---|---|---|
| **B** (tool loop) | Nowhere — structural. The input is a model tool call (`InlineToolLoopRunner` → `IToolLoopAutonomyGate.Evaluate`); doc-pinned Llm. | always **Llm** |
| **C** (endpoint filter) | `CallerKindResolver.Resolve(http)` called from `AutonomyGateEnforcement.EvaluateAsync` (`GovernanceEnforcement.cs:224-294`), reading `http.GetAuthPrincipal()` (`AuthPrincipal.cs:48`) then the `"scope"` claim then JWT `sub`. Passed as `Caller:` in the query at `:285-293`. | `UserAuthPrincipal`/user JWT → **Human**; `ServiceAuthPrincipal` (engine token) / `InstallationAuthPrincipal` / anonymous → **Llm** (fail-closed) |
| **D** (background helper) | Declared, not read: `BackgroundActionGate.MayRunAsync` stamps `Caller: CallerKind.Machinery` in the query it builds (`BackgroundActionGate.cs:117-131`). Only the five opted-in actors (call sites listed in Pre-Reading) reach it; future opt-ins inherit the declaration by using the same helper. | always **Machinery** |
| **E** (engine mediation, `POST /api/v1/governance/evaluate`) | `CallerKindResolver.Resolve(http)` in `GovernanceEvaluateEndpoints.Evaluate` (handler gains an `HttpContext` parameter). The route is `EngineServiceOnly`, so the only principal that can reach it is a `ServiceAuthPrincipal`. | **Llm** (fail-closed — deterministic workflow steps share `TammaApiClient` with LLM steps and cannot be told apart) |
| **A** (llm-call observe) | Same resolver, same result as E (`LlmCallEndpoints.cs:250-260`); observe-only posture unchanged. | **Llm** |

## Blocked / contradictions

1. **AC2 cannot be run against the production routes it names, and this is the tree's explicit
   decision, not an accident.** The `PUT /api/admin/scheduled-triggers/{id}` route is unbound and
   carries a reviewed 2026-08-01 justification saying so (`KnownUngovernedEndpoints.cs:368-369`,
   `:476-479`, `:739` — "DECIDED … it is NOT bound"); every tracker write is baselined
   "binding-owned-by Story 44-2" (`:393, :400, :424, :436, :446`). Binding either here would move
   `PinnedCount` (216) and the 21-bound-rows pin — which AC9 forbids — and would overturn a
   recorded decision outside this story's scope. **Resolution (D6):** the two-direction pin runs
   in a harness host over the real filter/gate/resolver with the real route shapes and real
   credential shapes. Recorded here rather than planned around silently; the story author should
   amend AC2's wording, and the production bindings stay with 44-2 / a future schedule story —
   both of which become *safe to take* once this predicate exists (the "would gate a human on
   themselves" objection in those justifications dissolves).
2. **AC6's "deny when executing an un-gated upstream LLM decision" has no lever left in this
   story once AC4/AC5 land.** Today that deny is expressed as an `AlwaysHuman` threshold on the
   `automation:*` row; AC5 removes threshold writes on machinery targets and AC4 makes stored ones
   inert. What remains at Seam D after this story: `enabled = false` (denies) and the F6
   fail-closed degradation (denies). The literal "was the upstream enqueue gated?" check is a
   ledger consult at send time — 43-14's grant machinery, not this story's. AC6 is satisfiable as
   written only because its tests script the inner gate (`BackgroundActionGateTests` mock
   `IAutonomyGate`), so "still deny, never escalate" holds for any decision the gate hands them.
   Recorded so nobody reads this story as having *implemented* the upstream-gating check.
3. **AC1's letter vs Seam B** — see D10. Seam B never calls `Evaluate`; its caller kind is
   structural. Deviation recorded, semantics preserved.
4. **Not blocked, but worth stating: the dial-1 leg of AC4 does not wait for 43-11's widen.**
   `AutonomyDial.Min` is still 70 and `AcceptanceRules.Validate()` still rejects `< 70`
   (`AcceptanceRules.cs:83-85`) — but `Validate()` is not on the pure-evaluator path, so the
   fixture constructs `ResolvedAcceptanceRules` with `AutonomyLevel = 1` directly. The fixture is
   correct before and after 43-11 lands.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Core/Actions/CallerKind.cs`** — `enum CallerKind { Human,
   Machinery, Llm }` + `ToWire()` (lowercase), XML doc quoting Amendment 4's three-kinds rule and
   the fail-closed default. *(0.25 d with step 2)*

2. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Actions/AutonomyGovernance.cs`** — `AutonomyQuery`
   (`:299-306`) gains `CallerKind Caller = CallerKind.Llm` as the last parameter, doc-comment
   stating the default is the fail-closed design (AC3), and that `Machinery` may only be set by
   in-process declaration.

3. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Actions/ActionDescriptor.cs` +
   `ActionCatalog.Descriptors.cs`** — `ActionDescriptor` gains `bool IsMachinery = false` with a
   doc naming the re-audit. In the helpers (`:38-75`): `Automation(...)` and `Task(...)` hard-code
   `IsMachinery: true` (29 + 8 rows); `Effect(...)` gains `bool machinery = false`; the five
   plumbing effects pass `machinery: true` (`:296, :298, :300, :309, :393`). No key, group, risk
   or count changes — the 197 pin must stay green after this step alone. *(0.25 d)*

4. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Actions/AutonomyGateEvaluator.cs`** — two new reason
   constants (`ReasonCallerHuman`, `ReasonMachineryNotDialGoverned`) beside the existing ones
   (`:127-133`); the Human short-circuit after the uncatalogued branch (D5); `machineryPath`
   computed after descriptor resolution and the terminal machinery return immediately before the
   dial comparison at `:413` (D4); class doc gains an Amendment-4 paragraph. *(0.5 d)*

5. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Actions/CallerKindResolver.cs`; MODIFY the
   three HTTP seams** — the D3 resolver (static, testable). `GovernanceEnforcement.cs:285-293`
   passes `Caller: CallerKindResolver.Resolve(http)`; `GovernanceEvaluateEndpoints.Evaluate` gains
   an `HttpContext` parameter and does the same (`:88-100`); `LlmCallEndpoints.cs:250-260`
   likewise (observe posture untouched). `ActionPolicyEndpoints.cs:126` stays on the default with
   a comment ("the policy view is the LLM-path view by definition"). *(0.5 d)*

6. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Services/Actions/BackgroundActionGate.cs`** — the query
   at `:117-131` gains `Caller: CallerKind.Machinery` with a comment naming AC6 ("the declaration,
   not a bypass: enabled=false and fail-closed degradation still deny"). No call-site changes —
   the five actors flow through the same helper. *(0.1 d)*

7. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Endpoints/ActionPolicyEndpoints.cs`** — D7: machinery
   400 in `ValidateThresholdForAction` (`:600-627`, old two-state branch deleted), group rule
   rewrite in `InvalidGroupThreshold` (`:569-598`). Correct the stale comment in
   `ActionDescriptorMetadataTests.cs:44-53` in the same commit. *(0.25 d)*

8. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Services/Actions/ActionGateEventsService.cs`** — D9:
   `callerKind` tag in `EmitDecisionAsync` (`:90-117`), volume-gate carve-out for
   `ReasonCallerHuman` (`:84-87`). **MODIFY
   `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IToolLoopAutonomyGate.cs`** — D10 doc-comment
   (Seam B structurally Llm). *(0.15 d)*

9. **CREATE the test suites** (Test Plan below): `MachineryInventoryTests` (Core.Tests),
   `CallerKindEvaluatorTests` (Core.Tests), `CallerKindResolverTests` (Api.Tests),
   `CallerKindSeamTests` (Api.Tests — AC2/AC3 harness hosts), `DormantHumanRowsTests` (Api.Tests),
   `CallerKindResidencyTests` (Api.Tests); edits to `ActionGateEventsServiceTests`,
   `BackgroundActionGateTests`, `ActionPolicyEndpointsTests`. *(1 d)*

10. **Run `dotnet test` + `dotnet ef migrations has-pending-model-changes`** (trivially clean — no
    entities touched) and verify every count pin still reads its pre-story value (list below).
    *(0.25 d, includes doc sweep)*

Total: 3 days, matching the story estimate.

## Test Plan — with each test's red state against today's tree

Tests are written first; a test that cannot go red is listed with the exact mutation that reddens it.

- **`MachineryInventoryTests` (NEW, `tests/Tamma.Core.Tests/Actions/`)** — AC4.
  - `TheFixture_isExactlyTheMachineryFlaggedRows` — an explicit 42-key array (5 effects, 29
    automation, 8 platform-task, transcribed from 43-11's inventory) compared by symmetric
    difference to `ActionCatalog.All.Where(d => d.IsMachinery)`. **Red today:** `IsMachinery` does
    not exist — does not compile; after step 3 it is the drift pin (a row moving between dial and
    machinery without editing the fixture fails with the key named).
  - `EveryMachineryRow_DecidesIdenticallyAtDial1AndDial100` — for each of the 42, run the pure
    `Evaluate` with directly-constructed `ResolvedAcceptanceRules` at `AutonomyLevel` 1 and 100:
    outcome and reason must be equal at both extremes and the reason must be
    `ReasonMachineryNotDialGoverned`. **Red today:** at dial 1 the rows resolve below-threshold
    (Denied/RequiresHuman), at 100 Automated — unequal, and the reason constant doesn't exist.
  - `AHostileLadder_CannotReachAMachineryRow` — same evaluation with a snapshot carrying an
    `AlwaysHuman` action row AND an `AlwaysHuman` platform ceiling on the target: still
    `Automated` / machinery reason at dial 100 and dial 1. **Red today:** AlwaysHuman blocks at
    every dial. This is the "unreachable through the dial resolver" proof.
  - `EnabledFalse_StillDeniesAMachineryRow` and `Degradation_StillFailsClosedForMachinery` — the
    two levers D4 keeps. **Red state:** implement the short-circuit at the top of `Evaluate`
    instead of at the dial comparison and both go red — they pin the placement.
- **`CallerKindEvaluatorTests` (NEW, Core.Tests)** — D5 semantics on the pure evaluator.
  - `AHumanCaller_PassesBelowTheThreshold_WithReasonCallerHuman` (threshold AlwaysHuman, dial Min,
    Caller Human → Automated). **Red today:** no `Caller` field; once the field exists but before
    step 4, the outcome is RequiresHuman.
  - `AHumanCaller_PassesEvenWhenDisabled_AndUnderDegradation` — pins D5's placement.
  - `TheDefaultCaller_IsLlm_AndIsGated` (same query without `Caller:` → RequiresHuman). **Red
    state:** flip the default to Human — red; this is AC3's evaluator half.
  - `AMachineryCaller_OnADialRow_IsNotDialGated` (Caller Machinery on `effect:notify.email.send`
    above the dial → Automated / machinery reason) — pins the caller half of D4.
- **`CallerKindResolverTests` (NEW, `tests/Tamma.Api.Tests/Actions/`)** — D3 table, one test per
  row: `ServiceAuthPrincipal` → Llm, `InstallationAuthPrincipal` → Llm, `UserAuthPrincipal` →
  Human, JWT-`sub` principal → Human, `"scope":"service"` claim without typed principal → Llm,
  anonymous → Llm. **Red today:** the type does not exist.
- **`CallerKindSeamTests` (NEW, Api.Tests)** — AC2 + AC3 end-to-end at Seam C, on the
  `AutonomyGateEndpointFilterTests` harness pattern (`:37-161`) extended with the real
  `AutonomyGateService` + stub snapshot/rules providers so the dial comparison is real:
  - `TheSameRequest_HumanPasses_EngineTokenIsGated` — ONE test class, both directions (AC2's
    drift-proofing requirement), run for the `effect:schedule.update` route shape
    (`PUT /api/admin/scheduled-triggers/{id}`) and the `effect:tracker.work-item.update` shape
    (`PATCH /api/work-items/{id}`), each mapped in the harness host with
    `.Governs(...).EnforcesGovernance()` and the level forced above the dial via a platform-ceiling
    row. Human-shaped principal (JWT `sub` claim) → 200, decision reason `ReasonCallerHuman`,
    event tag `callerKind=human`; `ServiceAuthPrincipal` → 409. **Red today:** the human direction
    409s (no exemption exists). See D6/Blocked #1 for why this is a harness host.
  - `AnEngineToken_WithNoDeclaration_ConsultsTheDial` (AC3) — engine-shaped credential, no
    `Caller` anywhere, level above dial → 409 AND the emitted event carries `callerKind=llm`.
    **Red today:** the tag does not exist (the 409 half alone would pass vacuously — the tag
    assertion is what makes this fail-first). **Red state after landing:** change the
    `AutonomyQuery.Caller` default or make the resolver return Human for service principals — red.
- **`DormantHumanRowsTests` (NEW, Api.Tests)** — AC7.
  - `TheSevenDormantHumanRows_PassForAHumanAtDialMin` — explicit fixture of exactly
    `effect:schedule.create|update|delete` + `effect:mentorship.session.start|pause|resume|cancel`;
    each evaluated with Caller Human at dial Min → Automated / `ReasonCallerHuman`. **Red today:**
    no Human path.
  - `NoDormantHumanRow_HasAMediationMethodSite` — for each of the 7,
    `IActionEnforcementSites.For(key)` contains no `method:`-prefixed site (the mentorship rows
    keep exactly their controller `route:` site, the schedule rows none). **This is the intended
    failure mode:** adding a `TammaApiClient` `[PerformsEffect]` method (an LLM path) for one of
    these keys turns it red until the fixture is consciously updated. **Red state:** add such a
    method — red; green today by the current truth, which is what a dormancy pin is.
- **`CallerKindResidencyTests` (NEW, Api.Tests)** — AC1/D8: source scan (single computation site)
  + the pinned `new AutonomyQuery(` site list (5 entries). **Red state:** add a sixth construction
  site or a second `GetAuthPrincipal`-reading kind computation — red.
- **EDIT `tests/Tamma.Activities.Tests/Actions/ActionGateEventsServiceTests.cs`** — the tag-union
  test gains `callerKind`; new `AHumanAllow_IsNotVolumeGated` / `AMachineryAllow_IsVolumeGated`
  pair (D9). **Red today:** tag absent; carve-out absent.
- **EDIT `tests/Tamma.Activities.Tests/Actions/BackgroundActionGateTests.cs`** — one added
  assertion on the captured query: `Caller == CallerKind.Machinery` (AC6's "explicit machinery
  declaration"); every existing test passes unmodified. **Red today:** field absent; after step 2
  but before step 6, the default Llm fails it.
- **EDIT `tests/Tamma.Api.Tests/Actions/ActionPolicyEndpointsTests.cs`** — AC5:
  `MachineryTarget_RejectsAnyThreshold_NamingTheClassification` replaces
  `AutomationTarget_RejectsMidRangeThreshold` (`:295`); plus
  `AnEngineAppendEffect_ThresholdIsAlsoRejected` (**red today: accepted** — `effect:engine.events.append`
  is escalatable and enforceable, so a mid-range write currently succeeds; the cleanest
  fail-first case in the story) and `AMachineryMin_IsAlsoRejected` (**red today: accepted** — the
  two-state rule allows `Min` on automation targets); `SecretReveal_ReportsMachineryNotUnenforceable`
  pins the D7 precedence; `AMidRangeGroupWrite_IsLegal_WhenTheGroupHasDialMembers` +
  `AnAllMachineryGroup_TakesNoThreshold` pin the group rewrite. `PUT …/enabled` on a machinery
  target stays green throughout (pinned).

## Count pins — current values, all UNCHANGED by this story (AC9)

| Pin | Where | Value before → after |
|---|---|---|
| Total catalog members | `ActionVocabularyCountTests.cs:147-148` | 197 → **197** |
| Bound catalog rows | `ActionEnforcementSitesTests.cs:169` and `:200` | 21 → **21** |
| Ungoverned baseline | `KnownUngovernedEndpoints.cs:221` (`PinnedCount`), history `[237, 216]` `:235` | 216 → **216** |
| In-scope surface | `KnownUngovernedEndpoints.cs:250` | 239 → **239** |
| Reviewed ungoverned exceptions | `KnownUngovernedEndpoints.cs:335` (`ExceptionPinHistory`) | `[2]` → **`[2]`** |
| Non-effect client methods | `MediationClientEffectSweepTests.cs:768` (`NonEffectPinHistory`) | `[19]` → **`[19]`** |
| Enforcement opted-in routes | `GovernedEndpointEnforcementSweepTests.cs:70` | 16 routes, 0 controller → **unchanged** |

Two *content* pins change without count movement: the events tag-union set gains `callerKind`,
and `ActionPolicyEndpointsTests.cs:295` is renamed/re-targeted per AC5. Both are named edits in
step 9, not silent drifts.

## Dependencies on the other stories in this batch

- **43-11 (ruling model)** — the classification tables (120/7/28/42, machinery inventory, 7
  dormant HUMAN keys) are this story's fixture *content* and are already frozen in the story
  document. 43-11's *implementation* (dial widen to 1, zone levels) is **not** blocking: every
  test here forces thresholds via assignment rows / constructed rules, and the dial-1 fixture
  bypasses `Validate()` (Blocked #4). Land in either order.
- **43-12 (per-target keys)** — **not blocking either way; same-file contention.** Both stories
  edit `ActionCatalog.Descriptors.cs` (43-12 mints 10 / retires 2 and moves the 197 pin to 205;
  this story changes helper signatures + 5 flags and moves nothing). All 43-12 keys are dial rows,
  so the 42-row fixture is unaffected; my AC2/AC3 tests use `git.branch.create` /
  `schedule.update` / `tracker.work-item.update`, none retired by 43-12. Whoever lands second
  rebases the helper-signature diff. Prefer this story first (smaller descriptor diff).
- **43-14 (approval scopes / grant minting)** — **this story lands first.** 43-14's AC8 ("grants
  are LLM-scoped; a human caller never needs one") reads `AutonomyQuery.Caller`. Coordination
  honored by D1: the kind rides the query record, so 43-14 needs no signature renegotiation; and
  this story leaves `ConsultLedgerAsync` (`AutonomyGateService.cs:254`) untouched — Human and
  Machinery short-circuits return `Automated`, so a non-LLM caller structurally never reaches the
  ledger already.
- **43-15 (toggles / dial UI)** — **depends on this story** for `IsMachinery` (its policy view
  excludes machinery from detent math — 156 dial rows, not 197 — and renders "no toggle"). This
  story deliberately does NOT touch the policy-view payload beyond what exists; exposing
  `machinery` on `GET /api/actions/policy` is 43-15's first step, reading the flag shipped here.
- **43-16 (acceptance unification)** — no overlap: document-type rows are DUAL dial rows; the
  acceptance resolver path is untouched here. Independent.
- **42-10 (shell sandbox / secret.read)** — **its AC5 and AC8 are blocked on this story** (it says
  so): the reveal-route human/LLM split needs the predicate, and its `secret.reveal`-stays-machinery
  pin extends this story's 42-row fixture. Its `effect:secret.read` mint (+1 catalog) does not
  touch the machinery fixture. This story first.
- **39-25 (ambiguity threading)** — explicitly "not blocked by 43-13" in its own text; no shared
  files. Independent.
- **40-8 (triage dead-ends / create-issues)** — no shared files with this story; the engine
  routes it touches are Llm-side and fail-closed by default here. Independent.
- **31-13 (full PR operations)** — mints LLM-class issue/PR keys (count pins move there); no
  machinery or HUMAN-fixture overlap. Independent; watch only the shared
  `ActionCatalog.Descriptors.cs` merge order, same as 43-12.

Suggested wave placement: this story runs in the first wave alongside 39-25 / 40-8 / 31-13
(disjoint lanes), before 43-14, 43-15 and 42-10's AC5/AC8, with 43-12 sequenced against it only
for the descriptor-file rebase.

## Risks

- **Stored `AlwaysHuman` threshold rows on machinery targets become inert.** Today an admin can
  switch a sweeper off with a threshold; after this story only `enabled = false` does that, and an
  existing threshold row is ignored by the evaluator and can no longer be re-written (400). No
  production users exist (CLAUDE.md: no migration anxiety), and the 400's message names the
  `enabled` lever — but this is a real semantic change and is stated here, in the 400 body, and in
  the evaluator doc.
- **A user-scope API key wielded by a script or an LLM reads as Human.** D3 classifies user
  credentials as Human; an exfiltrated user key in an agent's hands bypasses the dial. Same class
  as the recorded shell-curl bypass; mitigation (sandbox/env-strip) is 42-10's, not here.
  Recorded, not solved.
- **Installation-key calls classify as Llm.** If a GitHub-App-installation credential ever calls
  an enforced route, it gates. Today impossible (all enforced routes are `EngineServiceOnly` and
  an installation principal fails that policy before the filter runs), and fail-closed is the
  stated design for anything not provably human — a wrongly-gated deterministic caller is a
  visible nuisance, not a safety failure.
- **The Human short-circuit ignores `enabled = false` and role restrictions** (D5, deliberate). If
  a future admin expectation is "disable means nobody, not even people", this design says no —
  that is RBAC's job. The decision is pinned by `AHumanCaller_PassesEvenWhenDisabled…` so changing
  it is a conscious red test, not a drift.
- **Harness-host tests (D6) prove the seam, not the production wiring of the two AC2 routes** —
  because those routes are deliberately unenforced today. When 44-2 binds the tracker routes, its
  tests must repeat the two-direction pin against production wiring; noted in Blocked #1 so the
  handover is explicit.
- **Descriptor-file merge contention** with 43-12 / 42-10 / 31-13 — coordination risk only;
  the helper-signature change here is mechanical to rebase.

## Definition of Done

| AC | Steps | Verified by |
|---|---|---|
| 1 — one predicate, single-sourced | 1, 2, 5 | `CallerKindResolverTests`, `CallerKindResidencyTests` |
| 2 — human passes / LLM gated, both pinned | 4, 5 | `TheSameRequest_HumanPasses_EngineTokenIsGated` (one class, both directions, two route shapes) |
| 3 — engine token defaults to Llm | 2, 5 | `AnEngineToken_WithNoDeclaration_ConsultsTheDial`, `TheDefaultCaller_IsLlm_AndIsGated` |
| 4 — 42 machinery rows never consult the dial | 3, 4 | `MachineryInventoryTests` (fixture + dial-1/100 + hostile ladder) |
| 5 — machinery threshold writes 400; two-state rule removed | 7 | `ActionPolicyEndpointsTests` edits (incl. the two red-today acceptance cases) |
| 6 — Seam D semantics unchanged; explicit declaration | 6 | `BackgroundActionGateTests` unmodified + one added assertion; Blocked #2 records the scope boundary |
| 7 — 7 dormant HUMAN rows pinned | 4 | `DormantHumanRowsTests` (pass-for-human + no-method-site drift pin) |
| 8 — audit rows carry callerKind | 8 | `ActionGateEventsServiceTests` tag-union + volume-gate pair |
| 9 — green, no count pins move | 10 | the pin table above, re-read after `dotnet test` |

## Change Log

| Date | Version | Changes | Author |
|---|---|---|---|
| 2026-08-02 | 1.0.0 | Initial plan; all citations re-verified against the tree; AC2 route contradiction and AC6 scope boundary recorded in Blocked/contradictions | Claude |
