# Story 43-9: The Five Seams, Enforcement Live, and the Authorization Ledger

Status: drafted — **partially superseded by shipped code; read the 2026-08-01 amendment before starting**

---

## AMENDMENT 2026-08-01 — READ THIS FIRST

This story was written on 2026-07-25 against a tree in which none of it existed. Since then
Stories 43-4, 43-5 and 43-8 shipped, and **roughly half of this story is already in the tree**.
Nothing below was silently rewritten: every original claim is kept, and each correction says what
the story used to assert, that the assertion is now wrong, and what is true, with file:line
evidence.

Four problems were found and are resolved here as decisions, not open questions.

### A. HALF OF THIS STORY HAS ALREADY SHIPPED

**What the story used to imply:** that an implementer starts from nothing. The Implementation
Plan's 14 steps say `CREATE` for `IAutonomyGate`, `AutonomyGateEvaluator`, `AutonomyGateService`,
`ActionGateEventsService`, the ledger, and the whole Seam B block.

**That is wrong.** All six exist. An implementer following the plan literally would rebuild them.
Verified against the tree on 2026-08-01, per AC:

| AC | Status 2026-08-01 | Primary evidence |
|---|---|---|
| 1 — Core/Api split, naming | **DONE** (43-5), with one falsified sub-clause | `Tamma.Core/Actions/AutonomyGovernance.cs:314` (`IAutonomyGate`), `:279` (`AutonomyQuery`), `:292` (`AutonomyDecision`), `:143` (`AutonomyOutcome`); `Tamma.Core/Actions/AutonomyGateEvaluator.cs` (636 lines, pure static); `Tamma.Api/Services/Actions/AutonomyGateService.cs:49` |
| 2 — live enforcement, behaviour-preserving defaults | **PARTIAL** — proved for Seam B only; one deliberate exception (see §C) | `Tamma.Api.Tests/Agents/ToolLoopAutonomyGateSeamTests.cs:96` + `:116` (the anti-no-op pair, Seam B); `Tamma.Core.Tests/Actions/ActionCatalogDefaultsTests.ShippedDefaults_ReproduceTodaysGatingBehaviour` |
| 3 — Seam A observe-only | **NOT DONE** — and unsatisfiable as written (see §B) | `Tamma.Api/Endpoints/LlmCallEndpoints.cs` contains no gate call; `Program.cs:3318` maps the route, `:3321` binds `.Governs(effect:llm.call)` |
| 4 — Seam B siting + required dependency | **DONE** (43-4/43-5) | `InlineToolLoopRunner.cs:73` (required `IToolLoopAutonomyGate` ctor param), `:94` (null-throw), `:332-390` (the seam); tests `ToolLoopAutonomyGateSeamTests.cs:235,142,73` |
| 5 — `Denied` via existing machinery | **DONE** (43-4) | `InlineToolLoopRunner.cs:381` writes `rejectedToolCalls[tc.Id]`; `:411-433` is the pre-existing rejected→tool-result path; test `ToolLoopAutonomyGateSeamTests.cs:36` |
| 6 — additive over fail-open allowlists | **DONE by behaviour** (43-4); the named test does not exist | `ToolExecutorRegistry.cs:56-62` unchanged; `ToolLoopAutonomyGateSeamTests.cs:36` runs with `tools: null` and no validator (`:417-419`) and the denial still holds |
| 7 — Seam C endpoint filter | **NOT DONE** | no `AutonomyGateEndpointFilter`/`ActionGateFilter` anywhere; `GovernsExtensions.cs:28-29` attaches metadata only |
| 8 — 409, not 202 | **NOT DONE** | same |
| 9 — Seam D deny-only | **PARTIAL** — the admin-API half is DONE, the helper and call sites are not | DONE: `ActionPolicyEndpoints.cs:614-623` rejects a mid-range threshold on a non-escalatable target with `ACTION_POLICY.INVALID`; test `ActionPolicyEndpointsTests.cs:295`. NOT DONE: no `BackgroundActionGate`, no `MayRunAsync`, no hosted-service call site |
| 10 — Seam E over HTTP | **NOT DONE** | `POST /api/v1/governance/evaluate` does not exist (`KnownUngovernedEndpoints.cs:88` and `GovernedEndpointCoverageSweepTests.cs:317` both say so); no `CheckActionGateActivity`; no `TammaApiClient.EvaluateGovernanceAsync` |
| 11 — deployment pipeline OR term | **NOT DONE** | `DeploymentPipelineWorkflow.cs:242-245` still has exactly two terms |
| 12 — the ledger | **MOSTLY DONE** (43-5); three pieces remain | DONE: `Tamma.Data/Repositories/IActionAuthorizationLedger.cs`, `ActionAuthorizationLedger.cs:132` (`TryConsumeAsync`), migration `20260729070256_AddActionGovernance.cs:75-104` (columns + `ux_action_authorizations_open`), tests `ActionAssignmentStorageTests.cs:296,345,370,405,444,474,501`. REMAINING: (a) no production call site consults `TryConsumeAsync`; (b) `AutonomyDecision` has no `CoveredBy`/`AuthorizationId` field; (c) nothing reads `Tamma:Governance:AuthorizationTtlHours` |
| 13 — decide + pending endpoints | **NOT DONE** | `DecideAsync` exists on the ledger (`IActionAuthorizationLedger.cs:64`) but no route maps to it; no `/api/actions/authorizations` route in `Program.cs` |
| 14 — one audit family | **DONE** (43-5), with two recorded deviations from this AC's text | `Tamma.Api/Services/Actions/ActionGateEventsService.cs:35-52`; tests `Tamma.Activities.Tests/Actions/ActionGateEventsServiceTests.cs:63,76,106,123,134,150,161` |
| 15 — live reads, scoped resolver widening | **DONE** (43-5) | `IAcceptanceRulesResolver.cs:40,48`; `AcceptanceRulesService.cs:114,123`; consumed at `AutonomyGateService.cs:159,164` |
| 16 — `TryPreGate`'s first production call site | **DONE** (43-5) | `AutonomyGateEvaluator.cs:595` calls `AcceptanceGuardrails.TryPreGate`; floor semantics documented `:549-560`; provenance `ActionAssignmentSource.AlwaysEscalateLegacy` (`AutonomyGovernance.cs:167`) |

**The scoping pass that produced this amendment claimed ACs 1, 4, 5, 6, 14, 15, 16 and "most of
12" were done. Checked one by one, that list is right on 1, 4, 5, 14, 15, 16 and 12, and
overstated on 6** (the behaviour ships; the named pin does not). **It also missed two things it
should have caught: AC9 is half-done** (`ActionPolicyEndpoints.cs:614-623`), **and AC2 is
half-done** — the anti-no-op pair AC2 demands exists, but only for Seam B.

**THE REMAINING WORK IS THEREFORE:** AC3 (Seam A), AC7/AC8 (Seam C filter + opt-in), AC9's helper
and its call sites, AC10/AC11 (Seam E), AC13 (decide + pending endpoints), the ledger consult
inside the gate and the two `AutonomyDecision` fields it needs, and AC2's per-seam defaults tests
for every seam other than B. **Do not re-create anything marked DONE above.**

### B. DECISION 1 — `.Governs()` stays METADATA-ONLY; enforcement is an explicit per-route opt-in

**The conflict, stated as fact.** AC3/D2 require Seam A (`POST /api/v1/llm/call`) to be incapable
of blocking, in every version. But that route already carries `.Governs(effect:llm.call)`
(`Program.cs:3321`), `effect:llm.call` ships `Enforceable = true` (the `Effect(...)` helper
defaults `enforceable: true`, `ActionCatalog.Descriptors.cs:55-60`; the only `enforceable: false`
member is `effect:secret.reveal`, `:395`), and the plan's step 7 attaches the enforcement filter
**inside `Governs()`**. The moment that lands, an admin who sets `effect:llm.call` to
`AlwaysHuman` gets a hard 409 at Seam A — the exact outcome D2 exists to forbid. It also
double-gates deploy, because the deployment pipeline reaches the model through that same route
(`DeploymentPipelineWorkflow.cs:588` → `StageDeployDispatch` → `llm-call`) while Seam E gates the
prod-approval decision.

**RESOLVED: `.Governs(action)` remains metadata-only. Enforcement becomes a separate, visible,
per-route opt-in** — a distinct call at each route that should be gated. The exact spelling is the
implementer's choice; the required semantic is that **binding and enforcing are two different
lines in the diff**, and that a route is gated only if someone wrote the second one.

Reasoning, recorded so it is not re-litigated:

1. **Blast radius must not be a side effect of a helper.** 21 routes are bound today — 17
   minimal-API `.Governs(...)` calls (`Program.cs:3126,3132,3144,3147,3156,3321,3344,3348,3352,3359,3375,3383,3390,3404,3417,3441,3451`) plus 4 `[Governs]` controller
   actions (`MentorshipController.cs:62,151,175,199`). A one-line
   `.AddEndpointFilter<…>()` inside `GovernsExtensions.Governs` would convert 17 of them into
   live 409 gates simultaneously, with no per-route review. Explicit opt-in makes each route's
   enforcement a deliberate line a reviewer can see.
2. **Structural beats keyed.** Seam A's route simply never opts in. That makes D2's "never
   blocks" a property of the wiring rather than a carve-out keyed on the string `llm.call` inside
   a filter — a carve-out a future refactor could delete without anything going red.
3. **The rejected alternative and why it lost.** *Special-case `effect:llm.call` inside the
   filter.* Rejected: it puts the safety property in the one place a "clean up the special cases"
   commit deletes it; it leaves the other 20 routes still flipped on wholesale by the same
   one-line change; and it does not stop a future author from binding a second never-block route
   without knowing the carve-out list exists.

**Honesty about what this reverses.** This is **not** a continuation of 43-8's stated design; it
overturns it. 43-8 says four separate times that 43-9 attaches the filter inside `Governs()`
"so annotating and enforcing stay one call" — `GovernsExtensions.cs:11-13`,
`ActionGateMetadata.cs:8`, `43-8-…md:89` and `43-8-…md:296`. Those notes are hereby superseded by
this story; 43-8's *factual* claim (a binding is metadata today) is unchanged and still true.

**A fourth fact 43-8's design did not account for, and which independently sinks it:** the two
authoring shapes do not share a mechanism. `GovernsExtensions.Governs` is a `RouteHandlerBuilder`
extension (`GovernsExtensions.cs:28`); the four `MentorshipController` actions are governed by an
**attribute** (`ActionGateMetadata.cs:47-52`) that never passes through that method. A filter
added inside `Governs()` would therefore have enforced 17 routes and silently skipped 4, while
reading as "all bindings are now enforced". An explicit opt-in must cover **both** planes and say
which mechanism it uses for each.

### C. DECISION 2 — AC2's "byte-identical to today" is false for `effect:mcp.tool.invoke`

**What AC2 used to say:** "every descriptor's `DefaultMinAutonomy` is set so that day one control
flow is byte-identical to today". **That is now false, deliberately.**
`effect:mcp.tool.invoke` ships `min: AutonomyDial.AlwaysHuman`
(`ActionCatalog.Descriptors.cs:386-388`) with `Enforceable = true`, reversed on **2026-07-30**
from its original `Min`. The reasoning is on the descriptor at `:342-372` and in the epic README
Decision D2 (`docs/stories/epic-43/README.md:548`): epic D2 tolerates an unclassified action at
runtime only because the drift harnesses make it unmergeable in CI, and **no CI harness can
enumerate a remote MCP server's tools**, so for MCP the CI half of that bargain does not exist and
never will. The runtime tolerance has nothing backing it, so the member ships requiring a person.

**RESOLVED — amend AC2 to state exactly one exception, and to scope the guarantee to the routes
that actually opt in.** The MCP route does not opt into enforcement in this story:
`POST /api/kb/mcp/tools/invoke` carries no binding at all (`Program.cs:3491` is
`.RequireAuthorization("SettingsManage")` only) and sits in the ungoverned baseline
(`KnownUngovernedEndpoints.cs:393`). So the precise, testable statement is:

> Day-one control flow is byte-identical to today **at every seam site that opts into enforcement
> in this story**. `effect:mcp.tool.invoke` ships `AlwaysHuman` by the 2026-07-30 decision and is
> the single catalogued exception to "shipped defaults change nothing"; it has no enforcing seam
> in this story, so it changes no control flow here either. Any future opt-in of the MCP route is
> a behaviour change and must be argued as one.

The blast radius is empty today for a second, independently verifiable reason recorded on the
descriptor (`:363-367`): no MCP tool executor is registered, so an `mcp__*` name emitted into the
tool loop already terminates as an unknown tool.

**BUT — this decision OVERRIDES a written commitment in the tree, and that must be honoured
explicitly, not left to collide.** `KnownUngovernedEndpoints.cs:393-394` baselines
`POST /api/kb/mcp/tools/invoke` with the justification *"binding-owned-by Story 43-9: … **43-9
attaches the `.Governs` binding plus the enforcement filter**"*. That sentence and this decision
cannot both stand. **Resolution: the decision wins, and the justification is amended in the same
change** to say that 43-9 deliberately does **not** bind or enforce this route, naming the
2026-07-30 `AlwaysHuman` default as the reason a binding here would be a behaviour change rather
than a behaviour-preserving one. The entry **stays** in the baseline (the route stays unbound), so
no count pin moves and the `binding-owned-by` classifier keyword still applies once the owning
story is renamed. If a future story does bind it, that story owns the behaviour change.

### C-bis. NEWLY FOUND (not in the scoping brief) — 43-9 is on record owing FIVE route bindings this story never mentions

`KnownUngovernedEndpoints` names Story 43-9 as the binding owner for **five** routes, none of
which appears anywhere in this story's ACs:

| Route | Baseline entry |
|---|---|
| `POST /api/kb/mcp/tools/invoke` | `KnownUngovernedEndpoints.cs:393` |
| `POST /api/admin/scheduled-triggers/` | same file, `binding-owned-by Story 43-9` |
| `PUT /api/admin/scheduled-triggers/{id:guid}` | same |
| `DELETE /api/admin/scheduled-triggers/{id:guid}` | same |
| `POST /api/admin/scheduled-triggers/{id:guid}/run-now` | same |

AC7/AC8 speak only of "the 17 mutating `EngineServiceOnly` routes", which are **already bound**
(2026-07-30, 43-8 §A3 step 2). So the story's Seam C scope and the tree's recorded expectation
disagree by five routes.

**LEFT OPEN, deliberately, with the reason.** Resolving it is a scoping decision this amendment is
not authorised to take, and it is not a mechanical one:

- The MCP route is settled by Decision 2 above — **not bound here**.
- The four `scheduled-triggers` routes are `/api/admin/...` surfaces. The epic's own general rule
  for that shape is `human-operated` — "reached by a person, never by an agent, so gating it would
  gate a human on themselves" (the justification family used throughout
  `KnownUngovernedEndpoints`) — which argues they should never have carried `binding-owned-by`.
  Their catalogued effects (`ExternalEffect.ScheduleCreate` / `ScheduleUpdate` / `ScheduleDelete`)
  are classified `RouteOnly` in `MediationClientEffectSweepTests.cs:159-164` precisely because
  they are "reached from the dashboard, not the engine".
- `run-now` is arguably a different risk class from the other three — it *executes* rather than
  configures — and may deserve a binding even if the CRUD trio does not.

**Whoever implements this story must decide all five explicitly and amend each justification to
match**, so that after this story no baseline entry names 43-9 as an owner that never came. Do not
silently leave five entries pointing at a finished story.

### D. DECISION 3 — the mediation-client ratchet is unpassable as written; add a named exception

**What the plan used to say** (step 10): `TammaApiClient.EvaluateGovernanceAsync` is "read-only ⇒
goes in `KnownReadOnlyClientMethods`, bumping 43-8's pin by one."

**Two things are wrong with that.** First, there is no `KnownReadOnlyClientMethods`; the real
baseline is `MediationClientEffectSweepTests.KnownNonEffectClientMethods`
(`Tamma.Activities.Tests/Actions/MediationClientEffectSweepTests.cs:231`). Second, **the pin
cannot be bumped.** Adding one read-only method takes the baseline from 19 to 20, and the pin is
the last element of `NonEffectPinHistory = [19]` (`:543`) under
`TheRatchetPin_IsMechanicallyShrinkOnly` (`:546-562`), which asserts the last element equals 19
**and** that the history strictly decreases. The meta-test
`RatchetDisciplineTests.EveryRatchet_hasACountPin_thatIsMechanicallyShrinkOnly`
(`Tamma.Activities.Tests/Actions/RatchetDisciplineTests.cs:200-235`) asserts the same properties
again from the registry. Appending `20` is red by design; editing `19` to `20` in place is the
undeclared re-widening the ratchet exists to catch.

**RESOLVED: do NOT edit the assertion, and do NOT mint a second client for one method. Add a
named, dated, reviewed exception.** Why an exception rather than either alternative: a
strictly-decreasing pin that forbids *any* new non-effect method would eventually force one of
two dishonest moves — classify a genuinely-read-only method as an effect, or split
`TammaApiClient` so the new method lands somewhere the sweep does not look
(`MediationClientEffectSweepTests.cs:66-73` already names "a method on a base class the client
might grow" as a hole). Both are worse than an exception a reviewer can see.

**Required shape — specified tightly so it cannot become a blanket escape hatch:**

1. It is a **separate, per-method collection**, not a count bump. Keyed by the exact method name
   (`EvaluateGovernanceAsync`), so a *different* new method still goes red. A count-level
   exception — the `TemplateExampleConformanceTests` "name the index that may rise" precedent
   (`Tamma.Activities.Tests/Workflows/TemplateExampleConformanceTests.cs:208-224,614-632`, cited
   by 43-8 §A3 step 3) — is rejected here **because it is anonymous**: any future method could
   occupy the widened slot.
2. Every entry carries **method name + ISO date + the reviewing story id + a justification that
   passes the existing classifier** (`MediationClientEffectSweepTests.RatchetClassifies`,
   `:626-628`; today's keywords are `read-only` and
   `internal-session-lifecycle-no-external-effect`).
3. The exception set is **itself count-pinned at 1** and **itself shrink-only**, and it is
   registered in `RatchetDisciplineTests.Ratchets()` (`:59-93`) so all three AC8 properties are
   asserted against it. That registry's own pin is a review-gated bump, not a ratchet — its
   failure message says so verbatim (`:123-128`) — so 3 → 4 is a legitimate, reviewed change.
4. `KnownNonEffectClientMethods` **stays at 19** and its history stays `[19]`. The exception set
   is unioned into the classifier's "is this method accounted for" check and **excluded from the
   count pin**, so unreviewed growth is still impossible.
5. **Staleness both ways still applies:** an exception entry whose method no longer exists, or
   which becomes mapped to an `ExternalEffect`, fails until deleted.

**The same problem exists a second time, and the plan does not mention it.** AC10 adds
`POST /api/v1/governance/evaluate` to `KnownUngovernedEndpoints`, whose `PinnedCount = 216` is the
last element of `PinHistory = [237, 216]` (`Tamma.Api.Tests/Actions/KnownUngovernedEndpoints.cs:128,142`) under
the identical strictly-decreasing rule. 216 → 217 is red by design. 43-8 §A3 step 3 already
re-derived the arithmetic (`PinnedInScopeCount` 237 → 238, `PinnedCount` 216 → 217) and states the
resolution must be "a reviewed decision recorded at the history … not a quiet edit of the
assertion". Apply the **same** exception shape as above: a named, dated route-level exception,
count-pinned and shrink-only, leaving `PinnedCount` at 216. `PinnedInScopeCount` has no direction
rule and is a plain literal bump 237 → 238.

### E. EVERY LINE NUMBER IN THE PLAN'S PRE-READING IS STALE EXCEPT THE DEPLOYMENT-PIPELINE ONES

Verified 2026-08-01. Refreshed table lives in the Implementation Plan's Pre-Reading section. The
`DeploymentPipelineWorkflow.cs:242 / :248 / :588` and `WaitForDeploymentApprovalActivity.cs:52`
references are still correct; `LlmCallModels.cs:500`, `ToolExecutorRegistry.cs:56-62` and
`NotificationEndpoints.cs:116` also still hold. Everything else moved.

---

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **tenant admin who has set `effect:deploy.promote-prod` to human-only**,
I want that setting to actually stop the system from promoting to production by itself — at the one place where a real human wait exists, with one human decision covering the whole correlation rather than one per retry,
So that the autonomy dial is a safety control and not a label, and so that a denial always leaves an audit row.

## Priority

P0 — Without this story the whole epic is a declaration. `AcceptanceRules.AutonomyLevel` has 11 production references and **not one of them branches on it**; `AcceptanceGuardrails.TryPreGate` has **zero production call sites**. This story is the consuming layer.

## Architectural Context (READ FIRST)

> **AMENDMENT NOTE 2026-08-01 — this whole section was written on 2026-07-25 and its line
> references have decayed.** The reasoning is preserved because it is still the reasoning; the
> coordinates are not. Refreshed values, verified 2026-08-01:
>
> | Claim as written | Verified 2026-08-01 |
> |---|---|
> | `ActionGate.cs:17`, DI-registered `Program.cs:750` | `ActionGate.cs:18`; registered `Program.cs:731` |
> | Seam A site `Program.cs:3026` | route mapped `Program.cs:3318`, bound `.Governs` at `:3321` |
> | `InlineToolLoopRunner` ctor `:45-55`; validator block `:260`–`:281`; `ArgumentsJson` rewrite `:271`; rejected-call machinery `:299-325`; `executableToolCalls` `:330`; parallel fork `:335` | ctor `:63-107`; validator block `:308-330`; rewrite `:319`; **Seam B already exists at `:332-390`**; assistant message `:393`; rejected-call machinery `:411-433`; `executableToolCalls` `:439`; parallel fork `:444` |
> | Development blanket re-registers "all 22 named policies" `Program.cs:1698-1730` | **26** policies, `Program.cs:1791-1808` |
> | middleware order `Program.cs:1788-1803` | `Program.cs:1874-1889` |
> | `PermissionHandler.cs:41` / `:26,106`; `SelfOrPermissionRequirement.cs:65` | `PermissionHandler.cs:48` (platform_admin), `:33`, `:113`, `:160` (`"*"`); `SelfOrPermissionRequirement.cs:69` |
> | `POST /api/v1/notifications/slack` `Program.cs:3136` | `Program.cs:3438` (`NotificationEndpoints.cs:116` unchanged and still correct) |
> | "the 11 landed resume endpoints (`Program.cs:2919-2957`)" | **6** endpoints, `Program.cs:3211,3215,3222,3228,3235,3248` |
> | `Tamma.ElsaServer` mediation comment `Program.cs:2849-2851` | not re-located in this pass; treat as unverified |
> | `DeploymentPipelineWorkflow.cs:242 / :248 / :588`; `WaitForDeploymentApprovalActivity.cs:52`; `LlmCallModels.cs:500`; `ToolExecutorRegistry.cs:56-62`; `AcceptanceGuardrails.TryPreGate` | **all still correct** |
>
> Three substantive corrections to this section's *claims*, not just its coordinates:
> 1. **"Seam B: one call site …" is written in the future tense and describes work that is
>    already done.** The seam shipped under Story 43-4/43-5 exactly as specified — required ctor
>    parameter (`InlineToolLoopRunner.cs:73,94`), sited after the validator block and before the
>    fork, not nested inside `if (_toolCallValidator != null)`. One naming difference the reader
>    must know: the seam takes `IToolLoopAutonomyGate` (a **synchronous, non-DB** gate,
>    `Tamma.Api/Services/Agents/IToolLoopAutonomyGate.cs:21`), **not** `IAutonomyGate` — 43-5 AC12
>    forbids blocking the per-tool-call path on a database. That is why Seam B cannot consult the
>    ledger and why AC12's `SecondSeam_RecordsCoveredBy` must name a *different* pair of seams.
> 2. **"Seam C is an endpoint filter, attached by `.Governs`" is superseded** — see the 2026-08-01
>    amendment §B. `.Governs` stays metadata-only; enforcement is a separate per-route opt-in.
> 3. **`WOULD_BLOCK` does not exist as an event type.** The "shadow signal" language below
>    describes a type that was never minted (see AC14's amendment).

### BINDING: enforcement is live in v1

**There is no enforcement-flip story and no soak precondition.** Every action ships assigned so that day one reproduces today's behaviour exactly (Story 43-3's `ShippedDefaults_ReproduceTodaysGatingBehaviour`); the admin opts into gating and it bites immediately.

Shipping the mechanism switched off behind a "≥30 days of `WOULD_BLOCK` telemetry" gate was considered and **rejected**: under it, an admin who sets deploy to human-only gets *nothing*, and an epic whose entire product requirement is "the admin can set what the system may do by itself" would ship not doing that. `WOULD_BLOCK` remains as a **shadow signal for actions the admin has not yet tightened**, not as a precondition for the mechanism working.

### The gate: split Core/Api exactly like `IAcceptanceRulesResolver`

`Tamma.Core` has **zero** `ProjectReference`s and cannot touch a database. So: `IAutonomyGate` + the pure static `AutonomyGateEvaluator` live in `Tamma.Core/Actions/`; `AutonomyGateService : IAutonomyGate` (the DB-touching impl) lives in `Tamma.Api/Services/Actions/`.

**Named `AutonomyGate*`, never `ActionGate*`.** `Tamma.Activities.Security.ActionGate` (`ActionGate.cs:17`) is a shipped, DI-registered (`Program.cs:750`), constructor-injected type, and the name collides inside `Tamma.Api`.

### The five seams, and why each is shaped the way it is

| Seam | Site | Enforces | Denial shape |
|---|---|---|---|
| **A — llm-call** | `Program.cs:3026` → `LlmCallEndpoints.CallLlm` | **Never, in any version** | — (observe only) |
| **B — tool dispatch** | `InlineToolLoopRunner.cs`, one site | Yes | `rejectedToolCalls` entry → message back to the model |
| **C — mutating routes** | endpoint filter via `.Governs` | Yes | **409**, never 202 |
| **D — background actors** | one call per tick per actor | **Deny only** | tick skipped, audit row |
| **E — Elsa graphs** | `CheckActionGateActivity`, over HTTP | Yes | `RequiresHuman` `[FlowNode]` into an existing wait |

**Seam A never blocks, in every version.** A `RequiresHuman` returned at the llm-call endpoint reaches a `DispatchWorkflow` whose **calling workflow has no human route in 44 of 45 cases** — escalation into a void, a workflow that suspends with nobody able to resume it. And blocking there *and* at Seam E would double-gate deploy: the deployment pipeline dispatches `llm-call` for the deploy stage *and* has a prod-approval decision. Agent-action enforcement lives **only at Seam E**, where a real human wait exists. This is pinned by a test, not by a comment.

**Seam B: one call site, and its position is load-bearing three ways.** In `InlineToolLoopRunner.cs`:
- **After** the `if (_toolCallValidator != null)` block **closes** (`:260` opens, closes at `:281`) — the validator **rewrites `tc.ArgumentsJson` at `:271`**, so gating before it would gate un-sanitized arguments.
- **Before** `executableToolCalls` is computed (`:330`) and therefore before the `EnableParallelTools` fork (`:335`). `EnableParallelTools` defaults **`false`** (`LlmCallModels.cs:500`), so a gate on the parallel branch alone would govern **nothing** in the default configuration.
- **NOT nested inside `if (_toolCallValidator != null)`.** Every `InlineToolLoopRunner` constructor dependency is optional-nullable (`:45-55`: `logger`, `httpClientFactory`, `configuration`, `sanitizer`, `toolRegistry = null`, `toolCallValidator = null`, `contextCompactor = null`, `eventEmitter = null`, `parallelExecutor = null`, `credentialResolver = null`). Nesting the gate inside that block would make the gate **absent exactly whenever the validator is absent**. `IAutonomyGate` is therefore a **required** constructor parameter.

A denial becomes a `rejectedToolCalls[tc.Id]` entry, which the machinery at `:299-325` **already** turns into a tool-result message fed back to the LLM. **Zero new plumbing, no exception, no new failure code.** The outcome is named **`Denied`, not `RequiresHuman`** — there is no human on this path, and calling it escalation would be a lie.

The two existing **fail-open** allowlists stay: `ToolCallValidator.Validate` and `ToolExecutorRegistry.IsAllowed` (`:56-62` — `if (allowlist is null || allowlist.Length == 0) return true;`). The gate is **additive** and cannot be defeated by a null allowlist.

**Seam C is an endpoint filter, not an `IAuthorizationHandler`.** Middleware order is authentication → `ProxyHeaderAuthMiddleware` → authorization → rate limiter → impersonation → tenant context (`Program.cs:1788-1803`), so **`ITenantContext.TenantId` is unset during policy evaluation**, and there is no `IAuthorizationPolicyProvider` for dynamic per-action policies. Two security properties follow and are worth keeping:
- The gate does **not inherit the two unconditional superuser bypasses** — `platformRole == "platform_admin"` succeeds every `PermissionRequirement` (`PermissionHandler.cs:41`, duplicated `SelfOrPermissionRequirement.cs:65`) and an api-key `permission` claim of `"*"` (`PermissionHandler.cs:26,106`; `OrchestratorChannelHandler.cs:46-50`). **A platform admin can edit assignments but cannot bypass a governed effect.**
- It is unaffected by the Development-without-JWT blanket that re-registers **all 22 named policies** with `AllowAnonymousRequirement` (`Program.cs:1698-1730`).

**Denial returns `409 Conflict`, never `202`.** Verified: `TammaApiClient` branches **solely** on `IsSuccessStatusCode` — 11 sites, every mutating method (`:228,502,551,593,626,680,729,761,804,854,890`) — and **`202` is already a success code on that client**: `QueueSlackNotificationAsync:386` calls `POST /api/v1/notifications/slack` (`Program.cs:3136`) which returns `Results.Accepted` (`NotificationEndpoints.cs:116`). A 202 "escalated" response would be **indistinguishable from success**, and the engine would proceed as if the effect had happened. `409` not `403`: the caller **is** authorized; the *system* is not yet permitted to act autonomously.

**Seam D can only deny.** A sweeper cannot suspend for a person — there is no `ActivityExecutionContext`, no bookmark, and nobody watching. Every `automation:*` descriptor is `EscalatableToHuman = false`, the admin API rejects a mid-range `MinAutonomy` on such a target, and the UI renders a two-state control. Exceptions are **caught inside the helper**: `BackgroundServiceExceptionBehavior` defaults to `StopHost`, and a governance evaluation failure must never take down the host.

**Seam E reaches the gate over HTTP, not by DI.** `Tamma.ElsaServer` registers **no repository** and mediates everything through `TammaApiClient` (`Program.cs:2849-2851`; `ElsaServer.csproj` references only `Tamma.Activities` + the analyzer). So this story adds a mediation route `POST /api/v1/governance/evaluate` (`EngineServiceOnly`). It mints **no `ExternalEffect` member** (it is a read) and goes on `KnownUngovernedEndpoints` with the justification **`gate-evaluation-endpoint-cannot-gate-itself`**.

**v1 adopts Seam E in exactly one place, by OR, never by replacement** — `DeploymentPipelineWorkflow.cs:242-246`:

```csharp
var prodApprovalNeeded = new FlowDecision(ctx =>
       string.Equals(mode.Get(ctx)?.Trim(), "business", StringComparison.OrdinalIgnoreCase)
    || requireProdApproval.Get(ctx)
    || gateOutcome.Get(ctx) == "requires-human")   // NEW — additive only
```
routed into the **existing** `WaitForDeploymentApprovalActivity` (`Tamma.Activities/ADL/WaitForDeploymentApprovalActivity.cs:52`), reached at `DeploymentPipelineWorkflow.cs:248`. A threshold-only *replacement* would be **strictly weaker** for business-mode tenants, whose gate is currently unconditional.

The gate is evaluated on **`effect:deploy.promote-prod`, not `agent-action:deploy`**, because `StageDeployDispatch` (`DeploymentPipelineWorkflow.cs:588`) is **shared across staging / uat / prod** and one `agent-action:deploy` member cannot distinguish stage.

### The ledger

One human decision must cover one deploy, not one decision per retry and not one per seam. `action_authorizations` is scoped by `(principal, correlationId, target)` where the target may be an **action or a whole group**; `TryConsumeAsync` lets a group-scoped grant satisfy every member within the correlation, and the second seam's `ACTION.GATE.ALLOWED` records `CoveredBy`.

**The ledger is written by the EXISTING human surfaces.** No new suspend activity and no new bookmark prefix: `LifecycleBookmarks.CanonicalSuspendActivities` is keyed by activity `Type`, so a prefix without an activity is not representable. Grants arrive via the landed resume endpoints (`Program.cs:2919-2957`) and via `POST /api/actions/authorizations/{id}/decide`.

## Acceptance Criteria

1. **`IAutonomyGate` + pure evaluator in Core; DB-backed service in Api.**
   `Tamma.Core/Actions/IAutonomyGate.cs` (`AutonomyQuery`, `AutonomyDecision`, `AutonomyOutcome { Automated, RequiresHuman, Denied }`) and `Tamma.Core/Actions/AutonomyGateEvaluator.cs` — **pure, static, zero I/O**, taking the policy snapshot and the base acceptance rules as arguments. `Tamma.Api/Services/Actions/AutonomyGateService : IAutonomyGate` performs the reads. Named `AutonomyGate*` throughout; a test asserts no type named `ActionGate*` is added to `Tamma.Api`.

   > **AMENDED 2026-08-01 — DONE by Story 43-5, and one sub-clause is falsified.**
   > **Shipped:** `IAutonomyGate` at `Tamma.Core/Actions/AutonomyGovernance.cs:314`,
   > `AutonomyQuery` `:279`, `AutonomyDecision` `:292`, `AutonomyOutcome` `:143`;
   > `Tamma.Core/Actions/AutonomyGateEvaluator.cs` (pure static, 636 lines);
   > `Tamma.Api/Services/Actions/AutonomyGateService.cs:49`. Tests:
   > `Tamma.Core.Tests/Actions/AutonomyGateEvaluatorTests.cs`,
   > `AutonomyGateEvaluatorBreakGlassTests.cs`,
   > `Tamma.Activities.Tests/Actions/AutonomyGateServiceFailurePostureTests.cs`.
   > **Correction 1 — the filename.** This AC said `Tamma.Core/Actions/IAutonomyGate.cs`. No such
   > file exists; the four types live together in `AutonomyGovernance.cs`. Cite that path.
   > **Correction 2 — the naming test is FALSIFIED and must not be written as stated.** This AC
   > required "a test asserts no type named `ActionGate*` is added to `Tamma.Api`". Two such types
   > now exist there, both deliberately: `ActionGateEventsService`
   > (`Tamma.Api/Services/Actions/ActionGateEventsService.cs:33`, whose own doc-comment at
   > `:25-31` records it as "the one deliberate exception to the `AutonomyGate*` naming rule",
   > because `ACTION.GATE.*` are wire strings consumed by dashboards) and `ActionGateMetadata` /
   > `IActionGateMetadata` / `GovernsAttribute` (`Tamma.Api/Infrastructure/ActionGateMetadata.cs:22,32,47`,
   > shipped by 43-8). A test as written would be red on day one.
   > **Replacement (testable, and it can still fail):** the naming pin asserts that the set of
   > `ActionGate*`-named types in `Tamma.Api` is **exactly** `{ActionGateEventsService,
   > ActionGateMetadata, IActionGateMetadata}` — an allowlist with a count pin, so a *new*
   > `ActionGate*` type still goes red and still has to argue for itself against the
   > `Tamma.Activities.Security.ActionGate` collision (`ActionGate.cs:18`, DI-registered at
   > `Program.cs:731` — **not** `:750` as this story's Architectural Context says).
   > **Remaining:** `AutonomyDecision` (`AutonomyGovernance.cs:292-303`) carries no `CoveredBy` or
   > `AuthorizationId`; it has `Enabled` and `AllowedRoles` instead. AC12's ledger consult needs
   > those two fields added here.

2. **Enforcement is live, and the shipped defaults are behaviour-preserving.**
   `Enforce` resolves per the Story 43-5 ladder; the **shipped default enforces**, and every descriptor's `DefaultMinAutonomy` is set so that day one control flow is byte-identical to today. A test per seam asserts `ShippedDefaults_DoNotAlterControlFlow`, and a second test asserts that **tightening one action does change control flow at its seam** — otherwise the first test is satisfiable by a gate that does nothing.

   > **AMENDED 2026-08-01 — one clause was false; scope corrected; PARTIAL.**
   > **What this AC used to say and why it is wrong:** "every descriptor's `DefaultMinAutonomy` is
   > set so that day one control flow is byte-identical to today". Since **2026-07-30** that is
   > false for exactly one member: `effect:mcp.tool.invoke` ships
   > `min: AutonomyDial.AlwaysHuman` (`ActionCatalog.Descriptors.cs:386-388`) with
   > `Enforceable = true`, reversed from `Min` by the epic decision recorded on the descriptor at
   > `:342-372` and at `docs/stories/epic-43/README.md:548` — MCP is the one action family where
   > epic D2's bargain cannot close, because no CI harness can enumerate a remote server's tools,
   > so the runtime tolerance D2 buys with an unmergeable-in-CI guarantee has nothing backing it.
   > **The corrected, testable claim:** day-one control flow is byte-identical to today **at every
   > seam site that opts into enforcement in this story** (see the 2026-08-01 amendment §B —
   > enforcement is a per-route opt-in). `effect:mcp.tool.invoke` is the single catalogued
   > exception to "shipped defaults change nothing"; its route
   > (`POST /api/kb/mcp/tools/invoke`, `Program.cs:3491`) carries no binding, sits in
   > `KnownUngovernedEndpoints.cs:393`, and does **not** opt in here, so AC2 holds unqualified for
   > every route that does. A future opt-in of that route is a behaviour change and must be argued
   > as one.
   > **Shipped:** the anti-no-op pair exists **for Seam B only** —
   > `ToolLoopAutonomyGateSeamTests.The_real_gate_with_shipped_defaults_changes_nothing` (`:96`)
   > and `An_always_human_threshold_denies_through_the_real_gate` (`:116`); catalog-wide,
   > `Tamma.Core.Tests/Actions/ActionCatalogDefaultsTests.ShippedDefaults_ReproduceTodaysGatingBehaviour`.
   > **Remaining:** the same pair for every seam this story adds (A, C, D, E). Both halves are
   > required per seam; without the second, the first is satisfiable by a gate that never fires.

3. **Seam A is observe-only, permanently.**
   `LlmCallEndpoints.CallLlm` evaluates `ActionKey(AgentAction, request.Action)` when `LlmCallRequest.Action` is non-null, emits the audit event, and **always proceeds**. Pinned by `LlmCallSeam_NeverBlocks_EvenUnderEnforce` — including with the action set to `AlwaysHuman` at every scope. The test's doc-comment states the reason (44-of-45 no-human-route; double-gating deploy against Seam E).

   > **AMENDED 2026-08-01 — NOT DONE, and unsatisfiable alongside AC7 as originally planned.
   > RESOLVED by Decision 1 (amendment §B).**
   > **The conflict, in the tree:** `POST /api/v1/llm/call` (`Program.cs:3318`) already carries
   > `.Governs(new ActionKey(Effect, effect:llm.call))` (`:3321`), and `effect:llm.call` is
   > `Enforceable = true` (`ActionCatalog.Descriptors.cs:313-314`, taking the `Effect(...)` helper's
   > `enforceable: true` default at `:55-60`). The Implementation Plan's step 7 attached the
   > enforcement filter **inside `Governs()`**. Landing that would make this route hard-409 the
   > moment an admin set `effect:llm.call` to `AlwaysHuman` — the exact double-gate D2 forbids, and
   > it double-gates deploy as well, since the deployment pipeline reaches the model through this
   > same route (`DeploymentPipelineWorkflow.cs:588`).
   > **Resolution:** `.Governs()` stays metadata-only; enforcement is an explicit per-route opt-in
   > (amendment §B). **Seam A's route never opts in.** "Never blocks" therefore becomes a
   > structural fact about the wiring, not a carve-out keyed on an action name.
   > **AC3 is amended so that it can still fail.** The pin has two arms, both required:
   > (a) `LlmCallSeam_NeverBlocks_EvenUnderEnforce` — with `effect:llm.call` at `AlwaysHuman` at
   > platform, tenant and user scope, `POST /api/v1/llm/call` returns 200 and the dispatch
   > proceeds; and (b) a **structural** arm asserting that the endpoint for
   > `POST /api/v1/llm/call` carries the gate metadata but **not** the enforcement opt-in — so a
   > future author who adds the opt-in goes red on the wiring, not only on behaviour. Arm (b) is
   > what makes this AC survive a change in how the filter decides.
   > The observe-only evaluation itself (`ActionKey(AgentAction, request.Action)` when
   > `LlmCallRequest.Action` is non-null, emit, always proceed) is still to be written:
   > `Tamma.Api/Endpoints/LlmCallEndpoints.cs` contains no gate call today.

4. **Seam B: one call site, correctly positioned, with the gate as a required dependency.**
   The gate call sits in `InlineToolLoopRunner` **after** the validator block closes (`:281`) and **before** `executableToolCalls` (`:330`) / the `EnableParallelTools` fork (`:335`), and is **not** inside `if (_toolCallValidator != null)`. `IAutonomyGate` is a **required** constructor parameter. Tests: `Constructor_RequiresTheGate`; `SequentialAndParallelBranchesBothGoverned`; `Gate_runs_after_sanitization` (asserts the gate sees the rewritten `ArgumentsJson`); `Gate_evaluates_when_validator_is_null`.

5. **Seam B denials are `Denied`, expressed through existing machinery.**
   A denial writes `rejectedToolCalls[tc.Id]` and nothing else — the code at `:299-325` turns it into a tool-result message back to the model. No exception, no new failure code, no new plumbing. The outcome enum member used is `Denied`. Test: `Denied_tool_call_becomes_a_tool_result_message_not_an_exception`.

6. **Seam B is additive over the fail-open allowlists.**
   `ToolExecutorRegistry.IsAllowed` returns `true` on a null/empty allowlist (`:56-62`); the gate still denies. Pinned by `Gate_denies_even_when_registry_allowlist_is_null`.

   > **AMENDED 2026-08-01 — behaviour DONE (43-4), the named pin does not exist.** The scoping
   > pass called this AC done outright; that overstates it. `ToolExecutorRegistry.IsAllowed`
   > `:56-62` is unchanged and still fail-open, and the gate loop
   > (`InlineToolLoopRunner.cs:346-390`) is evaluated independently of it, so the property holds.
   > It is exercised — `ToolLoopAutonomyGateSeamTests.cs:36` runs with `tools: null` and no
   > validator (`:417-419`, so the derived allowlist is empty and `IsAllowed` returns true) and
   > the denied call is still not executed. **Remaining:** a test named for *this* property, so
   > the guarantee is not incidental to a test about something else. Same for
   > `Gate_runs_after_sanitization` (AC4): the siting is correct in the tree
   > (`InlineToolLoopRunner.cs:319` rewrites `ArgumentsJson`, `:353` reads it) but nothing asserts
   > the gate observes the **rewritten** value, so a future re-order would not go red.

7. **Seam C is an endpoint filter, attached by `.Governs`, and does not inherit the bypasses.**
   `Tamma.Api/Infrastructure/AutonomyGateEndpointFilter.cs`, attached by the Story 43-8 `.Governs(ActionKey)` extension. Tests: `Gate_still_evaluates_when_all_policies_are_AllowAnonymous` (the `Program.cs:1698-1730` Development blanket); `PlatformAdmin_cannot_bypass_a_governed_effect`; `WildcardApiKey_cannot_bypass_a_governed_effect`.

   > **AMENDED 2026-08-01 — NOT DONE. Attachment mechanism changed by Decision 1; three line
   > references refreshed; one count corrected.**
   > **What this AC used to say:** the filter is "attached by the Story 43-8 `.Governs(ActionKey)`
   > extension". Per amendment §B that is now wrong: `.Governs` stays metadata-only and the filter
   > is attached by a **separate, explicit per-route opt-in**. The opt-in must cover both
   > authoring planes — the minimal-API builder (`GovernsExtensions.cs:28`) and the controller
   > attribute (`ActionGateMetadata.cs:47`), which never passes through that builder — and the AC
   > must name which mechanism it uses for each.
   > **Line references, verified 2026-08-01 (all three were stale):** the Development-without-JWT
   > blanket is `Program.cs:1791-1808`, not `:1698-1730`; the middleware order (authentication →
   > `ProxyHeaderAuthMiddleware` → authorization → rate limiter → impersonation → tenant context)
   > is `Program.cs:1874-1889`, not `:1788-1803`; the bypasses are `PermissionHandler.cs:33`
   > (`permission` claim `"*"`), `:48` (`platformRole == "platform_admin"`), `:113`, `:160`, and
   > `SelfOrPermissionRequirement.cs:69`, not `:26,41,106` / `:65`.
   > **Count corrected:** the blanket re-registers **26** named policies (`Program.cs:1795-1806`),
   > not 22 — `TrackerView` and `TrackerManage` were added by Story 44-2, and the array also
   > carries `ActionsManage`, `ScheduleManage`, `AuthenticatedAny` and `OrchestratorChannel`.
   > Assert against `Enum`/array length, not the literal 22.
   > The two security properties (no superuser inheritance; unaffected by the Development
   > blanket) are unchanged and still correct; all three named tests remain required.
   > **Scope gap — five routes this AC does not mention.** `KnownUngovernedEndpoints` names
   > Story 43-9 as the binding owner of five routes that appear nowhere in this story:
   > `POST /api/kb/mcp/tools/invoke` (`:393`) and the four `/api/admin/scheduled-triggers/*`
   > entries. This AC talks only about the 17 mutating `EngineServiceOnly` routes, which are
   > **already bound**. All five must be decided explicitly and their justifications amended —
   > see amendment §C-bis, where the MCP one is settled and the other four are left open with
   > the reasoning.
   > **AC7 is amended to be explicit about scope, so it can fail:** the AC must name the exact
   > set of routes that opt into enforcement in this story, and a test must assert that the
   > enforcement-opted-in set is **exactly** that list — so both an accidental addition and an
   > accidental omission go red. Without a named set, "attach the filter" is not a testable
   > requirement under Decision 1.

8. **Seam C denial is `409`, and `202` is proven unusable.**
   Body: `{ code: "ACTION.GATE.REQUIRES_HUMAN", action, group, effectiveMinAutonomy, autonomyLevel, authorizationId }`. Tests: `Denial_returns_409`; `Client_treats_202_as_success` — a characterization test asserting `TammaApiClient` returns success for a 202 response, so the reason 409 was chosen is *encoded*, not just written down.

   > **AMENDED 2026-08-01 — NOT DONE. The evidence behind "202 is unusable" re-verified and still
   > holds; one supporting line reference refreshed.** `NotificationEndpoints.cs:116`
   > (`Results.Accepted`) is still correct. The route it backs moved:
   > `POST /api/v1/notifications/slack` is `Program.cs:3438`, not `:3136`. The `TammaApiClient`
   > `IsSuccessStatusCode` line list in the Architectural Context
   > (`:228,502,551,593,626,680,729,761,804,854,890`) was not re-verified line-by-line in this
   > pass and should be treated as unconfirmed; the *claim* it supports — that the client
   > discriminates on nothing but `IsSuccessStatusCode`, so 202 is indistinguishable from success
   > — is what `Client_treats_202_as_success` must encode, and that test does not need the line
   > list to be right.
   > **Note on the body shape:** `authorizationId` presupposes the ledger consult (AC12
   > remaining). If the consult is not wired at the same time, the field is always null and the
   > 409 tells a caller nothing it can act on — land them together or drop the field.

9. **Seam D denies only, never escalates, and never takes down the host.**
   `Tamma.Api/Services/Actions/BackgroundActionGate.cs` — one call per tick per actor, principal from `IGovernancePrincipalResolver` (per-tenant for tenant-scoped sweeps, platform scope for cross-tenant). Every `automation:*` descriptor is `EscalatableToHuman = false`; the admin API rejects a non-sentinel `MinAutonomy` on an `automation:*` target with `ACTION_POLICY.INVALID`. Exceptions inside the helper are caught and emitted as `ACTION.GATE.EVALUATION_FAILED`. Tests: `MidRangeThreshold_OnAutomation_Is400`; `Evaluation_failure_does_not_propagate_out_of_the_helper`; `Denied_tick_is_skipped_and_audited`.

   > **AMENDED 2026-08-01 — PARTIAL. The admin-API half is DONE; the helper and its call sites are
   > not.** The scoping pass missed this.
   > **DONE:** every `automation:*` descriptor is `EscalatableToHuman = false` by construction —
   > the `Automation(...)` factory hard-codes it (`ActionCatalog.Descriptors.cs:62-69`); the admin
   > API rejects a mid-range threshold on a non-escalatable target with `ACTION_POLICY.INVALID`
   > (`ActionPolicyEndpoints.cs:614-623`), pinned by
   > `ActionPolicyEndpointsTests.AutomationTarget_RejectsMidRangeThreshold` (`:295`). That is
   > AC9's `MidRangeThreshold_OnAutomation_Is400` under a different name — **do not rebuild it.**
   > **NOT DONE:** `BackgroundActionGate` / `MayRunAsync` do not exist, and no hosted service
   > calls anything.
   > **Correction — the count.** The Implementation Plan says "MODIFY each of the **25** hosted
   > services". The `automation:*` plane has **29** members
   > (`Tamma.Core.Tests/Actions/ActionVocabularyCountTests.BackgroundActor_has_29_members`,
   > `:84,101`; 29 `Automation(...)` descriptors in `ActionCatalog.Descriptors.cs`). Drive the
   > call-site list from `Enum.GetValues<BackgroundActor>()`, not from a literal.
   > **New implementation constraint, from the shipped DI.** `IAutonomyGate` is registered
   > **scoped** (`ActionCatalogGovernanceServiceCollectionExtensions.cs:84`) and
   > `IGovernancePrincipalResolver` is scoped (`:67`, it reads the scoped `ITenantContext`). A
   > singleton `IHostedService` cannot inject either — the helper must create a scope per tick.
   > State this in the AC, because getting it wrong is a startup crash, not a test failure.

10. **Seam E reaches the gate over HTTP, and the route cannot gate itself.**
    `[Activity] CheckActionGateActivity` in `Tamma.Activities/Policy/` with `Automated` / `RequiresHuman` `[FlowNode]` outcomes, calling a new `TammaApiClient` method against `POST /api/v1/governance/evaluate` (`EngineServiceOnly`). The route mints **no** `ExternalEffect` member and is added to `KnownUngovernedEndpoints` with the justification `gate-evaluation-endpoint-cannot-gate-itself` (and the Story 43-8 count pin is bumped in the same commit). Test: `GovernanceEvaluateRoute_IsJustifiedUngoverned`.

    > **AMENDED 2026-08-01 — NOT DONE. "the count pin is bumped" is impossible as written;
    > RESOLVED by Decision 3 (amendment §D).**
    > **What this AC used to say:** "the Story 43-8 count pin is bumped in the same commit". It
    > cannot be. `KnownUngovernedEndpoints.PinnedCount = 216` is the last element of
    > `PinHistory = [237, 216]` (`Tamma.Api.Tests/Actions/KnownUngovernedEndpoints.cs:128,142`),
    > and the shrink-only rule (asserted twice — in the owning fixture and again by
    > `Tamma.Api.Tests/Actions/RatchetDisciplineTests`) makes appending `217` red **by design**.
    > 43-8 §A3 step 3 already says the resolution must be "a reviewed decision recorded at the
    > history … not a quiet edit of the assertion".
    > **Resolution:** add a **named, dated, per-route exception entry** for
    > `POST /api/v1/governance/evaluate` with the justification
    > `gate-evaluation-endpoint-cannot-gate-itself`, shaped exactly as amendment §D specifies
    > (per-route key, ISO date, story id, classifier-passing justification, itself count-pinned at
    > 1, itself shrink-only, registered in the ratchet-discipline registry). `PinnedCount` stays
    > **216**. `PinnedInScopeCount` has **no** direction rule and is a plain literal bump
    > **237 → 238** (`:157`).
    > **Also required and not previously stated:** delete
    > `GovernedEndpointCoverageSweepTests.PreProvisionedJustificationKeyword_isStillUnused`, which
    > pins that justification arm at 0 uses today (43-8 §A3 step 3).
    > **Second ratchet, same problem:** `TammaApiClient.EvaluateGovernanceAsync` collides with
    > `KnownNonEffectClientMethods` — see amendment §D and AC10's companion note in the
    > Implementation Plan. The plan's `KnownReadOnlyClientMethods` does not exist; the real name is
    > `MediationClientEffectSweepTests.KnownNonEffectClientMethods`
    > (`Tamma.Activities.Tests/Actions/MediationClientEffectSweepTests.cs:231`). Adding the method
    > **also** moves the exactly-pinned discovered-surface count
    > `The_sweep_actually_sees_the_client_surface` 36 → 37 (`:442`); that pin is a legitimate
    > bump-with-review and its own message says so (`:443-445`).

11. **Seam E's one v1 adoption is by OR and on the effect, not the agent-action.**
    `DeploymentPipelineWorkflow.cs:242-246`'s `prodApprovalNeeded` gains a third **OR** term; the existing business-mode and `requireProdApproval` terms are untouched; the true branch routes into the existing `WaitForDeploymentApprovalActivity` (`:248`). The gate is evaluated on `effect:deploy.promote-prod`. Tests: `EnforceMode_NeverWeakensBusinessModeGate` (business mode + gate `Automated` still waits); `GateRequiresHuman_AddsAWaitWhereThereWasNone`; `Gate_is_on_the_effect_not_the_shared_dispatch` (asserts `StageDeployDispatch` at `:588` is **not** individually gated, since it cannot distinguish stage).

12. **The `action_authorizations` ledger: one human decision per correlation.**
    `IActionAuthorizationLedger.TryConsumeAsync(principal, correlationId, actionKey)` — an action-scoped grant covers itself; a **group-scoped grant covers every member**. States `{pending, granted, denied, expired}`; `requested_at_utc` **NOT NULL from day one**; `expires_at_utc` default +24h from `Tamma:Governance:AuthorizationTtlHours`; `consumed_at_utc`; `autonomy_level_at_request`. Unique index on `(tenant_id, user_id, correlation_id, target_kind, target_key) NULLS NOT DISTINCT WHERE state IN ('pending','granted')`. Tests: `GroupGrant_CoversEveryMemberWithinOneCorrelation`; `Grant_does_not_leak_across_correlations`; `ExpiredGrant_IsNotConsumable`; `SecondSeam_RecordsCoveredBy`.

    > **AMENDED 2026-08-01 — MOSTLY DONE by Story 43-5. Three pieces remain.**
    > **DONE, exactly as specified:** `Tamma.Data/Repositories/IActionAuthorizationLedger.cs`
    > (interface, with `RequestAsync` `:25`, `TryConsumeAsync` `:51`, `DecideAsync` `:64`);
    > `ActionAuthorizationLedger.cs:132` (`TryConsumeAsync`, group membership resolved **inside**
    > the ledger from `ActionCatalog`, never from caller input — review F2); migration
    > `20260729070256_AddActionGovernance.cs:75-104` carries every column this AC names
    > (`RequestedAtUtc` NOT NULL DEFAULT now(), `ExpiresAtUtc`, `ConsumedAtUtc`,
    > `AutonomyLevelAtRequest`), the state CHECK `('pending','granted','denied','expired')`, and
    > `ux_action_authorizations_open` — the exact unique index this AC specifies. Tests
    > (Testcontainers) `Tamma.Api.Tests/Actions/ActionAssignmentStorageTests.cs`: `:296`
    > (action-covers-itself, group-covers-members, expired and consumed do not), `:345`
    > (`GroupGrant_CannotBeConsumedForAnActionOutsideTheGroup`), `:370` / `:405` (CAS races),
    > `:444`, `:474` (`TimeExpiredGrant_IsNotConsumable`), `:501`. **Do not rebuild any of it.**
    > **REMAINING — the three pieces this story still owns:**
    > (a) **No production call site consults the ledger.** `TryConsumeAsync` has zero callers in
    > `src/`; `AutonomyGateService.EvaluateAsync` (`:82-144`) does not consult it. Wiring the
    > consult into the gate is this story's work.
    > (b) **`AutonomyDecision` cannot carry the answer.** It has no `CoveredBy` and no
    > `AuthorizationId` (`AutonomyGovernance.cs:292-303`). Both must be added for the second
    > seam's `.ALLOWED` to record `CoveredBy` — and `SecondSeam_RecordsCoveredBy` cannot be
    > written until they are.
    > (c) **The TTL config key is a doc-comment, not code.** `Tamma:Governance:AuthorizationTtlHours`
    > appears only in prose (`ActionAuthorization.cs:62`, `ActionAuthorizationLedger.cs:12`);
    > nothing reads it. The ledger has a hard-coded `DefaultTtl = 24h` (`:28`) and takes an
    > optional `ttl` argument (`:47`). This story's request path must resolve the config value and
    > pass it, or the AC's "+24h **from** `Tamma:Governance:AuthorizationTtlHours`" stays false.

13. **The decide endpoint and the pending-authorizations surface.**
    `POST /api/actions/authorizations/{id}/decide` (`ActionsManage`) with `{ decision: granted|denied, reason? }`, and `GET /api/actions/authorizations?state=pending` for the surface. No new suspend activity and no new bookmark prefix are introduced — `LifecycleBookmarks.CanonicalSuspendActivities` is keyed by activity `Type`, so a prefix without an activity is not representable; grants also arrive through the 11 landed resume endpoints (`Program.cs:2919-2957`). Tests: `Member_Gets403OnDecide`; `Decide_is_idempotent_on_an_already-decided_row`; `NoNewBookmarkPrefix_IsRegistered`.

    > **AMENDED 2026-08-01 — NOT DONE. One count is wrong; one line reference is stale; the
    > `NoNewBookmarkPrefix` pin needs a concrete value.**
    > **The state machine already has an owner:** `IActionAuthorizationLedger.DecideAsync`
    > (`:64`, conditional single-statement UPDATE, `WHERE state = 'pending'` and not past expiry)
    > shipped in 43-5 and is pinned by
    > `ActionAssignmentStorageTests.Decide_RejectsAlreadyDecidedAndExpiredRows` (`:501`) and
    > `ConcurrentGrantAndDeny_ExactlyOneWins_AndTheRowMatchesTheWinner` (`:405`). This story adds
    > the **route**, not the transition — and `Decide_is_idempotent_on_an_already-decided_row` is
    > an endpoint-level restatement of a property the ledger already guarantees.
    > **Correction — "the 11 landed resume endpoints (`Program.cs:2919-2957`)".** Both halves are
    > wrong. There are **6**, at `Program.cs:3211, 3215, 3222, 3228, 3235` (`adl.MapPost(…/resume)`)
    > and `:3248` (`documents.MapPost("/decisions/{sessionId}/resume", …)`). The same "11 landed
    > resume endpoints (`Program.cs:2919-2957`)" claim appears in the Architectural Context §"The
    > ledger" and in the Implementation Plan's Pre-Reading and D11 — all four instances are wrong
    > by the same amount and should read **6, `Program.cs:3211-3248`**.
    > **`NoNewBookmarkPrefix_IsRegistered` — make it able to fail.** As written ("asserts the
    > dictionary is unchanged in count and keys") it has no reference value.
    > `LifecycleBookmarks.CanonicalSuspendActivities`
    > (`Tamma.Activities/Documents/LifecycleBookmarks.cs:98-105`) holds exactly **2** entries:
    > `WaitForDocumentDecisionActivity → "document-decision"` and
    > `WaitForDocumentInputActivity → "document-input"`. Pin both the count (2) and the two
    > key/value pairs.
    > **Route ordering:** `/api/actions/policy/...` literals already exist
    > (`ActionPolicyEndpoints.cs`), so `RouteOrder_LiteralsBeatParameterized` has real inputs.

14. **One audit event family, and denials under enforcement are not swallowed.**
    `Tamma.Api/Services/Actions/ActionGateEventsService.cs` (built on the `AcceptanceRulesEventsService.cs:16-18,54-93` template), appending **directly via `IEventRepository`** from `Tamma.Api` — `TammaEventEmitter` structurally requires an `ActivityExecutionContext` and the tool loop runs inside a blocking HTTP request. Types: `ACTION.GATE.ALLOWED` / `.REQUIRES_HUMAN` / `.DENIED` / `.WOULD_BLOCK` / `.AUTHORIZED` / `.AUTHORIZATION_DENIED` / `.PRINCIPAL_UNRESOLVED` / `.EVALUATION_FAILED`. Tags: `{actionKey, actionGroup, risk, autonomyLevel, effectiveMinAutonomy, assignmentSource, outcome, enforced, role, correlationId, issueId, tenantId, userId}`. Emission rides the template's swallowing try/catch **with one deliberate exception: `.DENIED` and `.REQUIRES_HUMAN` under enforcement are NOT swallowed** — a block with no audit row is a compliance hole. Volume control: `.ALLOWED` fires only when `Source != system-default` or `Enforced`. Test: `DeniedEmissionFailure_Propagates`; `AllowedEmissionFailure_IsSwallowed`.

    > **AMENDED 2026-08-01 — DONE by Story 43-5, with two recorded deviations from this AC's text.
    > Do not rebuild it.**
    > Shipped at `Tamma.Api/Services/Actions/ActionGateEventsService.cs`, direct `IEventRepository`
    > append, non-swallowing on enforced `.DENIED`/`.REQUIRES_HUMAN`. Tests
    > (`Tamma.Activities.Tests/Actions/ActionGateEventsServiceTests.cs`):
    > `TheEightTypeStrings_AreExact` `:63`, `DecisionEvent_CarriesTheTagSet` `:76`,
    > `Allowed_IsSuppressed_ForSystemDefaultResolutions_AndEmittedOtherwise` `:106`,
    > `AppendFailure_OnAllowed_IsSwallowed` `:123`, `AppendFailure_OnAnEnforcedDenial_Rethrows`
    > `:134`, `AppendFailure_OnAnUnenforcedDenial_IsSwallowed` `:150`.
    > **Deviation 1 — `.WOULD_BLOCK` was never minted.** This AC lists it among the eight types.
    > The shipped eight are `ALLOWED`, `REQUIRES_HUMAN`, `DENIED`, `AUTHORIZED`,
    > `AUTHORIZATION_DENIED`, `PRINCIPAL_UNRESOLVED`, `EVALUATION_FAILED`, `ASSIGNMENT_CHANGED`
    > (`:35-42`), plus a ninth added by F11, `BREAK_GLASS_BYPASS` (`:52`). `.WOULD_BLOCK` was
    > replaced by `.ASSIGNMENT_CHANGED`. **Consequence for this story:** the Architectural Context
    > above ("`WOULD_BLOCK` remains as a **shadow signal** for actions the admin has not yet
    > tightened") describes an event type that does not exist. Either mint it as part of a seam
    > that needs it, or delete the shadow-signal language — do not leave the story promising a
    > signal nothing emits.
    > **Deviation 2 — the `.ALLOWED` volume rule dropped its second arm.** This AC says
    > "`.ALLOWED` fires only when `Source != system-default` **or `Enforced`**". The shipped rule
    > drops the `Enforced` arm, with the reason recorded at `:19-22`: under epic D1 `Enforce`
    > defaults to **true**, so that arm would have made the volume gate a no-op. The shipped rule
    > is the correct one; this AC's text is stale.

15. **Gate reads are live, and the Init-time rules cache is fixed for the gate path only.**
    `DocumentLifecycleWorkflow.cs:184` resolves `ResolvedAcceptanceRules` once at Init into serialized state. The **gate path** re-reads at each decision point; the existing `state.Rules` reads (`:433,589,678,1208-1209`) are **untouched**, so in-flight instances stay valid. The one interface widening — `IAcceptanceRulesResolver.ResolveBaseAsync` / `ResolveBaseForTenantAsync`, lifted from `AcceptanceRulesService.cs:91-108` — is done once, here or in Story 43-5, never twice.

    > **AMENDED 2026-08-01 — the resolver widening is DONE by Story 43-5; every line number in
    > this AC is stale; and one claim about the gate path is contradicted by the shipped design.**
    > **DONE:** `IAcceptanceRulesResolver.ResolveBaseAsync` (`Tamma.Core/Documents/Policy/IAcceptanceRulesResolver.cs:40`)
    > and `ResolveBaseForTenantAsync` (`:48`), implemented at
    > `Tamma.Api/Services/AcceptanceRules/AcceptanceRulesService.cs:114,123`, consumed by the gate
    > at `AutonomyGateService.cs:159,164`. The AC's "done once, here or in 43-5, never twice" is
    > satisfied — it was done in 43-5. **Do not widen it again.**
    > **Line numbers, verified 2026-08-01:** the Init-time resolve is
    > `DocumentLifecycleWorkflow.cs:195` (`DocumentLifecycleHelper.ResolveRules(...)`), not `:184`.
    > The `state.Rules` reads are `:445, :601, :690, :1223-1224`, not `:433,589,678,1208-1209`.
    > `AcceptanceRulesService.cs:91-108` is now `:114-130`.
    > **Contradicted claim — "the gate path re-reads at each decision point".** The shipped policy
    > source is a **singleton, 60-second-TTL, whole-snapshot cache**
    > (`Tamma.Api/Services/Actions/GovernancePolicySnapshotStore.cs:59-63`, registered
    > `TryAddSingleton` at `ActionCatalogGovernanceServiceCollectionExtensions.cs:52`), not a
    > per-decision read. Its own doc-comment states the consequence honestly (`:28-32`): a policy
    > change may take **up to 60 s** to be observed, and all gate calls within that window share
    > one read. That is the right design for the hot path, but it means "live" here means
    > "not captured at workflow Init", **not** "reflects an admin edit immediately". Amend the AC
    > to say so, and pin the bound: `Gate_rereads_rules_at_each_decision_point` as named is false
    > and would have to be written to pass vacuously. Replace it with a test that asserts the gate
    > does not read from serialized workflow state, plus one that pins `RefreshTtl` at 60 s so the
    > staleness window is a declared number rather than an accident.
    > **Deleted requirement:** the plan's Risk section claims
    > `IGovernancePolicySnapshotProvider` is "**scoped** — one CP read pair per request … pinned
    > by `TwoGateCallsInOneRequest_IssueOneRepositoryRead`, 43-5". It is singleton, and that test
    > does not exist anywhere in the tree.

16. **The legacy always-escalate list gets its first production call site.**
    The evaluator calls `AcceptanceGuardrails.TryPreGate` and, if it escalates for a class mapping to this `ActionKey`, contributes `AlwaysHuman` as a **floor** composed by `max()` — so a legacy entry cannot be lowered by a catalog row. `TryPreGate`'s unrelated rounds-exhausted short-circuit is **ignored**; the document lifecycle keeps owning rounds. Tests: `LegacyAlwaysEscalate_CannotBeLoweredByAnActionRow`; `RoundsExhausted_DoesNotAffectActionThreshold`.

    > **AMENDED 2026-08-01 — DONE by Story 43-5. Do not rebuild it.**
    > `AutonomyGateEvaluator.cs:595` is the call site
    > (`AcceptanceGuardrails.TryPreGate(ctx, out var escalation) ? … : …`); the "escalation
    > contribution only, rounds-exhausted ignored" rule is documented at `:549-560` and the
    > `max()` ladder at `:15`. The provenance value exists:
    > `ActionAssignmentSource.AlwaysEscalateLegacy` (`AutonomyGovernance.cs:167`). Ladder tests
    > live in `Tamma.Core.Tests/Actions/AutonomyGateEvaluatorTests.cs`.
    > **The only thing left for this story:** nothing. If the two named tests do not exist under
    > those exact names, rename or add them against the shipped evaluator — do not re-implement
    > the bridge.

## Dependencies

> **AMENDED 2026-08-01.** Three of the four "Blocking" dependencies below have **landed**:
> 43-5 (`Tamma.Core/Actions/AutonomyGovernance.cs`, `AutonomyGateEvaluator.cs`,
> `Tamma.Api/Services/Actions/*`, `Tamma.Data/Repositories/IActionAuthorizationLedger.cs`,
> migration `20260729070256_AddActionGovernance.cs`), 43-8
> (`Tamma.Api/Infrastructure/GovernsExtensions.cs` + `ActionGateMetadata.cs`, 21 bound routes,
> the four ratchets), and 43-3 (shipped defaults, pinned by
> `Tamma.Core.Tests/Actions/ActionCatalogDefaultsTests.ShippedDefaults_ReproduceTodaysGatingBehaviour`).
> **F12 below is also resolved by the shipped code and its entry is now misleading** — see the
> note on it.
>
> **Two dependency facts this section does not record and an implementer needs:**
> - **43-8 hands over one open obligation, not zero.** 43-8 §A3 step 3 (`43-8-…md:362-381`) is
>   still open and is 43-9's first task: add `POST /api/v1/governance/evaluate` to the ungoverned
>   baseline and delete
>   `GovernedEndpointCoverageSweepTests.PreProvisionedJustificationKeyword_isStillUnused`. 43-8
>   already derived the arithmetic and already flagged that the pin bump is red by design — see
>   AC10's amendment and amendment §D.
> - **43-8's stated design for the filter is overturned by this story** (amendment §B). 43-8 says
>   in four places that 43-9 attaches the filter inside `Governs()`; it does not. Anyone reading
>   43-8 first must be told, or they will implement the version that breaks AC3.

- **Story 43-5 (storage, principal resolution, resolver, audit)** — `action_assignments` + `action_authorizations` tables/entities/migration, `IGovernancePrincipalResolver` + `ISoleUserProvider`, `IGovernancePolicySnapshotProvider`, `IActionAssignmentRepository`. **Blocking.** This story owns the *ledger semantics* (`TryConsumeAsync`, group-covers-member, TTL/consumption) and its two endpoints; 43-5 owns the table.
- **✅ Story 43-5 follow-up F11 — the break-glass override for the FAIL-CLOSED posture. CLEARED 2026-07-30; NO LONGER BLOCKING.** It shipped as a **config-sourced** lever (`Tamma:Governance:BreakGlass:Enabled` / `:ExpiresAtUtc` / `:Reason`), read once at construction, with **no endpoint and no writer** — engaging requires a config change and a restart, deliberately. It **refuses to engage** without an explicit UTC expiry, with one already past, or with one **more than 24 hours away** (cap added by review MEDIUM-3, 2026-07-31), expires by itself, and logs at ERROR on engage / refusal / expiry / **every bypassed decision**; each bypassed decision also writes an `ACTION.GATE.BREAK_GLASS_BYPASS` row on the **non-swallowing** append path (an unrecordable bypass fails rather than happening quietly), and carries the distinct provenance `ActionAssignmentSource.BreakGlass` (wire `break-glass`). **Precision this entry used to blur (review MEDIUM-1, fixed 2026-07-31):** "every bypassed decision" means the decisions the override **permitted**. A decision blocked while the override was engaged — by a read row, a ceiling, a disable, a role rule or an `AlwaysHuman` shipped default — is not a bypass, gets no bypass row, and keeps the provenance of whatever blocked it; it is audited on the ordinary non-swallowing `.DENIED`/`.REQUIRES_HUMAN` path. **A requirement on every seam this story adds:** do not treat "the override is engaged" as "this decision was bypassed" — read `decision.Source`, and note that `IAutonomyGate` is the path that is 1:1 while Seam B's row is deliberately a superset (allowed *and* denied shapes). **Also relevant to this story:** the disengage direction is not symmetric — setting `Enabled=false` and reloading configuration does NOT turn the override off in a running process, because the state is captured in the constructor; only expiry or a restart ends it. Full write-up: 43-5 → "F11 — CLOSED".

  **The constraint this story MUST honour when it wires seams A/C/D/E.** The override bypasses **degradation only**: it suspends the substitution of `AlwaysHuman` for a policy input that could not be READ, and nothing else. A decision denied by a policy row that WAS read — including a platform ceiling, an `Enabled = false`, an `AllowedRoles` restriction, a read legacy always-escalate entry, or an `AlwaysHuman` **shipped default** — is still denied while it is engaged. That boundary is enforced by construction in `AutonomyGateEvaluator` (the `Enabled`/`AllowedRoles` guards deliberately sit ABOVE the degradation branch; the snapshot bypass is sited inside the `!IsAuthoritative` branch, which provably carries no rows) and pinned by `EngagedButARealPolicyRowDenies_IsStillDenied` and `BreakGlassEngaged_AgainstARealPolicyDenial_IsSTILLDenied`. **A seam added here must not re-derive its own degraded behaviour**: call the gate, honour the decision, and emit the bypass row with its own `seam` tag — the gate is the only place that knows the composition is monotone.

  Two properties of the shipped lever this story should note rather than rediscover: it is **per-process, not per-tenant** (the failure it relieves is itself per-process, so a SaaS operator engaging it engages it for every tenant on that host), and it is **inert on a healthy evaluation** (pinned over the whole catalog by `Engaged_ChangesNothing_WhenEveryInputIsReadable`), so leaving it configured after an outage does not quietly change behaviour before the expiry does its job.
- **Story 43-5 follow-up F12 — the degraded outcome is a DENIAL, not an escalation, until this story lands.** `ToolLoopGateOutcome` has no `RequiresHuman` case, so the one live consumer feeds a degraded decision back to the model as a tool rejection and the run burns its turns reaching nobody. Seam work here is the first opportunity to make `AutonomyOutcome.RequiresHuman` mean an actual human wait on a live path; until then, do not describe the posture as "escalates".

  > **AMENDED 2026-08-01 — still open, and confirmed against the tree, but the entry understates
  > why.** `ToolLoopGateOutcome` is `{Allowed, Denied}` and has no `RequiresHuman` case
  > (`Tamma.Api/Services/Agents/IToolLoopAutonomyGate.cs:33-40`) — verified. The entry reads as if
  > that were a temporary gap this story closes at Seam B. **It is not.** It is the deliberate
  > design recorded on the interface itself (`:15-19`): there is no human wait on the
  > tool-dispatch path, so naming the outcome `RequiresHuman` there would be a lie. F12 is closed
  > not by giving Seam B an escalation case but by **Seam E** — the only seam in this story with a
  > real human wait (`WaitForDeploymentApprovalActivity`, reached at
  > `DeploymentPipelineWorkflow.cs:248`). Until AC11 lands, `AutonomyOutcome.RequiresHuman` has no
  > live consumer that can honour it, and the story must not describe the posture as "escalates".
- **Story 43-8 (drift harnesses)** — `.Governs(ActionKey)` and `ActionGateMetadata`. **Blocking for Seam C**: the filter attaches to metadata that must already exist and be swept. This story bumps `KnownUngovernedEndpoints` by one (AC10).
- **Story 43-3 (groups + behaviour-preserving defaults)** — AC2 is meaningless without it. Blocking.
- **Story 43-6 (admin API)** — `POST /api/actions/authorizations/{id}/decide` and the pending list join the `/api/actions` group and reuse `ActionsManage`. Coordinate route ordering (literals before parameterized).
- **Existing, verified:** `InlineToolLoopRunner` (`:45-55` ctor, `:260-281` validator block, `:299-325` rejected-call handling, `:330`/`:335` fork), `ToolExecutorRegistry.IsAllowed:56-62`, `LlmCallModels.cs:500`, `Program.cs:1698-1730`/`1788-1803`/`3026`/`3136`, `PermissionHandler.cs:26,41,106`, `SelfOrPermissionRequirement.cs:65`, `NotificationEndpoints.cs:116`, `DeploymentPipelineWorkflow.cs:242,248,588`, `WaitForDeploymentApprovalActivity.cs:52`, `AcceptanceGuardrails.TryPreGate`, `AcceptanceRulesEventsService.cs:16-18,54-93`.

## Out of Scope

- **A sixth seam.** Elsa's `UseWorkflowsApi()` surface (`ElsaServer/Program.cs:103,403`) runs in another process and is not gated; the TypeScript sidecar is ungoverned past the proxy route. Both recorded, neither closed.
- **Argument-value gating.** The gate matches on **identity, not payload**. "Gate this action *when* the payload looks like X" is not expressible and is deliberately not attempted — where a payload predicate is genuinely needed the answer is one of three things that already work (make the state unrepresentable in the document type's validation, use the landed `BlockingReviewViolation` clamp, or route it as a typed side-effect edge). A payload-predicate policy layer is a 39-5 change.
- **`ManagedAgent.ToResolvedTools` filtering.** Filtering *advertisement* means the model never asks, so the denial never fires and the capability silently vanishes — and the method is `private static` returning `null` on empty, where `null` and empty diverge downstream. Deferred with the rationale recorded.
- **Closing the `file_write` / `shell_execute` bypasses.** `effect:git.pull-request.create` set to human-only is still defeated by `git push` under `tool:git_operations.write`, and every governed route is still reachable by `curl` under `tool:shell_execute`. Needs a protected-path selector and a merged shell denylist; neither exists.
- **Gating the deploy itself.** Production deploy is an **LLM tool loop** (`DeploymentPipelineWorkflow.cs:588` dispatches generic `llm-call` with `enableTools=true`), not a typed activity. Gating `effect:deploy.promote-prod` gates the **stage transition**; the deploy happens inside the loop under `tool:shell_execute`. This must appear in the `deploy-control` group description in the UI, not only here.

## Estimated Effort

7 days

> **AMENDED 2026-08-01.** The 7-day estimate was made against a tree in which none of this
> existed. ACs 1, 4, 5, 14, 15, 16 and most of 12 have since shipped under Stories 43-4 and 43-5,
> and half of AC9 under 43-6's admin surface. The remaining work is AC3, AC7/AC8, AC9's helper +
> 29 call sites, AC10/AC11, AC13, the ledger consult, and AC2's per-seam defaults tests. The
> estimate has **not** been re-derived here — re-estimate before committing to it, and note that
> the two ratchet exceptions (amendment §D) are review-gated, not just code.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |
| 2026-07-30 | 1.0.1   | Dependencies: 43-5 F11 (no break-glass override for the fail-closed posture) recorded as a **blocker**, and 43-5 F12 (the live seam hard-denies rather than escalating) recorded as context. Neither is built — F11's shape is a product decision. | Claude |
| 2026-07-30 | 1.0.2   | **F11 CLEARED — this story is unblocked.** The break-glass override shipped (config-sourced, mandatory expiry, per-decision ERROR log + non-swallowing `ACTION.GATE.BREAK_GLASS_BYPASS` row, `break-glass` provenance). Its scoping constraint — bypasses DEGRADATION only, never a successfully-read denial — is now a requirement on every seam this story adds. F12 unchanged and still open. | Claude |
| 2026-08-01 | 1.1.0   | **Conformance amendment — the story disagreed with the tree in four ways, all now resolved in place.** (1) **Half of this story has shipped** under 43-4/43-5/43-6: ACs 1, 4, 5, 14, 15, 16 and most of 12 are DONE, AC9 is half-done, AC2 is proved for Seam B only. Every AC now carries a dated DONE/PARTIAL/NOT-DONE note with file:line evidence, so an implementer does not rebuild `IAutonomyGate`, `AutonomyGateEvaluator`, `AutonomyGateService`, `ActionGateEventsService`, the ledger or Seam B. (2) **DECISION 1** — AC3 and AC7 were unsatisfiable together, because `POST /api/v1/llm/call` already carries `.Governs(effect:llm.call)` with `Enforceable = true`; `.Governs()` now stays metadata-only and enforcement becomes an explicit per-route opt-in. This **overturns** 43-8's stated design (filter inside `Governs()`), which is recorded rather than glossed. (3) **DECISION 2** — AC2's "byte-identical to today" is false for `effect:mcp.tool.invoke`, which ships `AlwaysHuman` since 2026-07-30; AC2 is rescoped to "every route that opts into enforcement", which the MCP route does not. (4) **DECISION 3** — adding `TammaApiClient.EvaluateGovernanceAsync` (and the `/api/v1/governance/evaluate` baseline entry) is unpassable against two strictly-decreasing ratchets; resolved with a named, dated, reviewed, count-pinned, per-item exception set rather than an edited assertion or a second client. **Newly found and left OPEN (§C-bis):** `KnownUngovernedEndpoints` names 43-9 as the binding owner of **five** routes the story never mentions — the MCP invoke route (settled here: not bound) and four `/api/admin/scheduled-triggers/*` routes (left open, with the `human-operated`-vs-`run-now` reasoning recorded). Also corrected: AC1's `NoTypeNamedActionGate_IsAddedToTammaApi` is unwritable (two such types already exist, both justified) and AC15's `Gate_rereads_rules_at_each_decision_point` is false against the shipped design; `.WOULD_BLOCK` was never minted; the `.ALLOWED` volume rule lost its `Enforced` arm; the policy snapshot is a singleton 60 s cache, not a per-request read, so an admin tightening takes up to 60 s to bite; the resume-endpoint count is 6, not 11; `BackgroundActor` has 29 members, not 25; the Dev policy blanket covers 26 policies, not 22; and every pre-reading line number except the deployment-pipeline ones is refreshed. | Claude |
