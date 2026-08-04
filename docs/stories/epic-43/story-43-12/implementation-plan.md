# Implementation Plan — Story 43-12: Per-Target Merge/Deploy Keys and the Ladder Gaps

Written 2026-08-02 against the working tree. Every file:line below was re-verified on that
date; where the story's own citations were checked they all held (noted per row in
Pre-Reading).

## Scope & Deliverable

When this story is done the merge/deploy family of the zone ladder (43-11 Amendment 3) has
real catalog keys at its slots, and the two coarse keys are gone:

- **Minted (10)**: `effect:git.merge.dev` 55 / `effect:git.merge.qa` 60 /
  `effect:git.merge.main` 65 / `effect:deploy.dev` 70 / `effect:deploy.qa` 75 /
  `effect:deploy.uat` 80 / `effect:deploy.staging` 85 / `effect:deploy.prod` 90 /
  `effect:git.checks.bypass` 50 / `effect:git.webhook.register` 85.
- **Retired (2)**: `effect:git.pull-request.merge`, `effect:deploy.promote-prod` — every
  code, comment and test reference re-pointed; grep over `src/` for either wire is zero.
- **Live seams**: the merge route resolves `git.merge.dev|qa|main` from the PR's base
  branch (fail-closed to `git.merge.main`); Seam E gates `deploy.prod` where it gated
  `deploy.promote-prod`, and the QA/UAT stages gain gate calls on `deploy.qa`/`deploy.uat`
  with their own resumable human waits.
- **Reserved rows (4)**: `deploy.dev`, `deploy.staging` (the shipped pipeline is
  QA→UAT→Prod only — verified, `DeploymentPipelineWorkflow.cs:113`), `git.checks.bypass`
  (nothing performs it), `git.webhook.register` (drivers implement, no caller). Each is a
  real catalog row with an empty `enforcementSites` array.
- **Deleted**: `POST /api/engine/command` — route, `SendCommand`, `SendCommandRequest`,
  and its `KnownUngovernedEndpoints` baseline entry.
- All count pins moved deliberately (table below), `dotnet test` green, no schema change.

The dial governs the LLM only (Amendment 4); all ten keys are LLM-class
(`git.webhook.register` DUAL-dormant). Humans and deterministic machinery are never gated
by these rows; acceptance is always a step and the dial picks the approver.

## Pre-Reading

| Reference | Verified 2026-08-02 | Why it matters |
|---|---|---|
| `docs/stories/epic-43/story-43-12/43-12-per-target-merge-deploy-keys-and-ladder-gaps.md` | — | The ACs; source of truth. All six of its Dependencies "verified in tree" citations re-checked below and CONFIRMED. |
| `docs/stories/epic-43/story-43-11/…md` — Amendment 3 (`:936-989`), Caller-kind re-audit (`:991-1030`), dial table Levels 50-100 (`:1232-1318`), Missing actions (`:1395-1450`), Amendment 4 (`:1458-1508`) | — | The ruling model: zones at 5-point steps; ladder slots; `git.checks.bypass` reserved; per-target split; the LLM-only rule. |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs:3409-3413` | CONFIRMED — merge route; `.Governs(GitPullRequestMerge)` at `:3412`, `.EnforcesGovernance()` at `:3413` | The one route that must carry three keys after the split. |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs:3101` | CONFIRMED — `engine.MapPost("/command", EngineEndpoints.SendCommand)` | The route to delete. |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitEndpoints.cs:48-54` | CONFIRMED (story said `:48-52`; the method closes at `:54`) | `MergePullRequest` does not know the base branch — nothing reads the PR before merging. |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:31-32` | CONFIRMED — returns `{ message = "Command accepted" }` and does nothing | The stub to delete. `SendCommandRequest` is `Dtos/Engine/EngineDtos.cs:5`. |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DeploymentPipelineWorkflow.cs:113` | CONFIRMED — "Deploy through QA -> UAT -> Prod" | Amendment 3's "the pipeline already has the stages" is FALSE for dev/staging. |
| `DeploymentPipelineWorkflow.cs:188-240` (QA stage `:190-213`, UAT `:218-240`), `:298-331` (prod gate: `CheckActionGateActivity` `:300-318`, key literal `:303`, `prodApprovalNeeded` OR-term `:320-330`), `:337-361` (denied terminal + `waitProdApproval`), `:926` (`IsBlockingGateOutcome`) | all CONFIRMED | Where the rebind and the two new stage gates go. Note the OR-term comment block `:242-297` (F1 fix: denied ≠ requires-human) — the QA/UAT gates copy that shape. |
| `apps/tamma-elsa/src/Tamma.Activities/ADL/WaitForDeploymentApprovalActivity.cs` (bookmark name doc `:22-25`) | CONFIRMED — bookmark is `adl-deploy-prod-approval-{tenant}-{repo}-{issue}-{mergeSha}`, NO stage segment | Reusing this activity at QA/UAT as-is collides on the bookmark name. Needs a Stage input (design D6). |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdlEndpoints.cs:158-201` + `Program.cs:3251` | CONFIRMED — `POST /api/v1/adl/deploy-approval/resume`, DTO has no stage field | Resume plumbing the QA/UAT waits need. |
| `apps/tamma-elsa/src/Tamma.Platforms.Abstractions/IGitPlatformClient.cs:101` | CONFIRMED — `RegisterWebhookAsync`; drivers implement it (GitHub `:212` returns ServiceUnavailable, GitLab `:430`, Gitea `:303`), zero production callers | Basis for minting `git.webhook.register` reserved. Also `:65-66` `GetPullRequestAsync` and `Models/PullRequest.cs:31` `TargetBranch` — the base-branch read exists at the driver layer. |
| `apps/tamma-elsa/src/Tamma.Api/Services/Git/IGitMediationService.cs:13-39` + `GitMediationService.cs:80-101` | CONFIRMED — no PR-details read; `ExecuteGuardedAsync` shape at `:109` | The selector needs a new `GetPullRequestAsync` mediation read. |
| `apps/tamma-elsa/src/Tamma.Core/Actions/ExternalEffect.cs:71,134,139` | CONFIRMED — `GitPullRequestMerge`, `DeployPromoteProd`, `DeployRollback` wires | The enum this story edits (−2 +10). |
| `apps/tamma-elsa/src/Tamma.Core/Actions/ActionCatalog.Descriptors.cs:55-60` (Effect helper), `:179-180` (agent deploy/rollback), `:323-324` (merge descriptor), `:402-405` (promote-prod / rollback) | CONFIRMED | Descriptor rows to retire/mint; AC7's comment lands on `:179-180`. |
| `apps/tamma-elsa/src/Tamma.Core/Actions/ActionCatalog.cs:181-183` (`INVALID_DEFAULT`), `:187-189` (`DUPLICATE_KEY`), `:195-201` (`DUPLICATE_SITE_KEY` — effect plane SiteKeys must be unique) | CONFIRMED | Three constraints that shape the mint: levels below `AutonomyDial.Min` refuse to boot (the 43-11 hard order); double-minting fails; the three merge keys need three DISTINCT SiteKey strings. |
| `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AutonomyDial.cs:27` | CONFIRMED — `Min = 70` TODAY | Until 43-11 edits this to 1, minting at 50/55/60/65 is a boot failure. Blocking. |
| `apps/tamma-elsa/src/Tamma.Core/Actions/PerformsEffectAttribute.cs:29` | CONFIRMED — `AllowMultiple = false` | `MergePullRequestAsync` must carry three effects; the attribute and BOTH its readers change (D3). |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs:305` (`[PerformsEffect(GitPullRequestMerge)]`), `:1111` (comment) | CONFIRMED | Method-plane rebind + comment sweep. |
| `apps/tamma-elsa/src/Tamma.Api/Infrastructure/GovernanceEnforcement.cs:233` (`GetMetadata<IActionGateMetadata>` — SINGLE), `:224-294` (evaluate flow), `GovernsExtensions.cs:39-40` | CONFIRMED | Seam C reads exactly one binding per route today. Multi-key + selector support goes here (D2). |
| `apps/tamma-elsa/src/Tamma.Api/Services/Actions/ActionEnforcementSites.cs:144,168` | CONFIRMED — single-metadata / single-attribute reads | Both must read ALL bindings/attributes after the split. |
| `tests/Tamma.Api.Tests/Actions/GovernanceHostFixture.cs:139-152` (`EndpointFact.Action` — singular), `:199-205` | CONFIRMED | The harness's one-binding assumption; grows to a list (D2). |
| `tests/Tamma.Core.Tests/Actions/ActionVocabularyCountTests.cs:53-81` (`ExternalEffect_has_39_members`), `:132-149` (`TotalCatalogMembers_is_197`) | CONFIRMED — story's citation `:132-149` exact | The two headline pins: 39→47, 197→205, each with a history line. |
| `tests/Tamma.Api.Tests/Actions/ActionEnforcementSitesTests.cs:42-53` (MediationEffects list incl. merge), `:169` (`withSites == 21`), `:200` (`bound.Count == 21`), `:264` (`HaveCount(17)` method-plane) | CONFIRMED — "21 bound rows" as the story says | AC8's bound-row pin: 21→23 rows; route count stays 21. |
| `tests/Tamma.Api.Tests/Actions/KnownUngovernedEndpoints.cs:221` (`PinnedCount = 216`), `:235` (`PinHistory = [237, 216]`), `:250` (`PinnedInScopeCount = 239`), `:538-539` (the `/api/engine/command` entry) | CONFIRMED | The engine.command deletion moves all three, via the recorded-history mechanism (append, never edit). |
| `tests/Tamma.Activities.Tests/Actions/MediationClientEffectSweepTests.cs:111-215` (effect→site map; merge `:124-125`, promote-prod/rollback `:209-214` InProcess), `:714,727` (`GetCustomAttribute` — throws on multiples), `:717` (attributed == 17), `:751-758` (`MediationClientSites_countIsPinned` == 17) | CONFIRMED | Map totality over the enum forces 10 new entries; the count pin moves 17→19; the readers must not throw. |
| `tests/Tamma.Core.Tests/Actions/ActionWirePinTests.cs:39-66` | CONFIRMED — the effect wire list (39 strings) | Wire pin edit: −2 +10. |
| `tests/Tamma.Core.Tests/Actions/ActionGroupMembershipTests.cs:150-158` (SourceControlWrite = 6), `:182-190` (DeployControl = 6), `:283,:285` (count table) | CONFIRMED | Group pins: both 6→10. |
| `tests/Tamma.Activities.Tests/Workflows/DeploymentPipelineGateTests.cs:138` (pins the `effect:deploy.promote-prod` literal) + `tests/Tamma.Activities.Tests/Actions/SeamEMediationTests.cs:53,86,99,127,202,288` | CONFIRMED | Existing tests that re-point; the wire pin at `:138` is the AC4 before/after anchor. |
| `tests/Tamma.Api.Tests/Actions/GovernedEndpointEnforcementSweepTests.cs:70` (`EnforcementOptedInRoutes` — exact set) | CONFIRMED | The merge route stays one opted-in route; the set does NOT change. Deleting engine.command doesn't touch it (never opted in). |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/GovernanceEvaluateEndpoints.cs:75`, `Tamma.Activities/Policy/GovernanceEvaluateModels.cs:20`, `CheckActionGateActivity.cs:60,109` | CONFIRMED — doc-comment mentions of promote-prod | AC2's zero-grep includes comments; sweep them. |
| `packages/orchestrator/src/transports/remote.ts:72-80` | CONFIRMED — legacy TS client still POSTs `/api/engine/command` | Dead code in the superseded TS package; the deletion turns it into a 404. Noted, not fixed here (C# scope). |

## Design Decisions

- **D1 — Land AFTER 43-11's `Min` widening, and do not restate levels it owns.**
  `ActionCatalog.BuildIndex` refuses any `DefaultMinAutonomy` outside
  `[Min,Max] ∪ {101}` (`ActionCatalog.cs:181-183`) and `Min` is `70` today
  (`AutonomyDial.cs:27`). Minting `git.checks.bypass` at 50 or `git.merge.dev` at 55
  before 43-11 lands is a refuse-to-boot, not a red test. Rejected alternative: mint the
  low-zone keys at `Min` temporarily and re-level later — that ships the ladder's slots at
  the wrong numbers, which is precisely the "prose zone model" defect this story exists to
  fix, and it would need a second reviewed pin move. Consequence: 43-11 is a hard
  predecessor; this plan also assumes 43-11 has rewritten
  `ActionCatalogDefaultsTests.EveryOtherMember_DefaultsToMin` / `Deploy_ShipsAtMin_…`
  into its per-row level table — 43-12 then ADDS its ten rows (and the
  `agent-action:deploy` 90 / `rollback` 95 pins, AC7) to that table rather than fighting
  the old invariant.

- **D2 — The merge route keeps ONE route with THREE bindings plus a per-request key
  selector; enforcement machinery stays in Seam C.** The gate must decide on
  `git.merge.dev|qa|main` chosen by the PR base, which no static metadata can express.
  Mechanism: the route carries three `ActionGateMetadata` entries (repeated `.Governs`,
  or a `params` overload), plus one new metadata item — `IActionKeySelector` — that the
  Seam C filter invokes to pick the key for THIS request. `AutonomyGateEnforcement`
  changes from `GetMetadata` (`GovernanceEnforcement.cs:233`) to `GetOrderedMetadata`:
  exactly one binding → today's path, unchanged; multiple bindings **without** a selector
  → the fail-closed `ACTION.GATE.MISCONFIGURED` 409 arm (static wiring fault, same
  posture as a missing binding); multiple with a selector → selector resolves the key,
  and a selector that cannot read the PR returns `git.merge.main` — that is a DECISION
  (fail-closed per AC3), not an evaluation error, so it does not ride the fail-open arm.
  Rejected alternatives: (a) gate inside `GitEndpoints.MergePullRequest` — scatters
  governance into a handler, and the enforcement sweep (`EnforcementOptedInRoutes`) plus
  the 409/grant/correlation plumbing all live in the filter; (b) three routes
  (`…/merge-dev` etc.) — the engine picks its own governance key, which is the caller
  choosing its gate; (c) one binding on `git.merge.main` with dev/qa as declarative rows —
  renders two live-gated keys as "not enforced anywhere", lying in exactly the surface
  AC5 makes honest.

- **D3 — `PerformsEffectAttribute` becomes `AllowMultiple = true`; both readers move to
  `GetCustomAttributes` in the same commit.** `MergePullRequestAsync` performs one of
  three effects after the split; the method plane must say so or
  `ActionEnforcementSitesTests.EveryMediationEffect_hasBothARouteSiteAndAMethodSite`
  becomes unsatisfiable for dev/qa. `GetCustomAttribute<T>` THROWS
  (`AmbiguousMatchException`) on a multiply-attributed method, so the two readers —
  `ActionEnforcementSites.cs:168` and `MediationClientEffectSweepTests.cs:714,727` —
  change with the attribute or the suite dies noisily. Rejected: attribute only
  `GitMergeMain` and carry dev/qa as route-only — the method demonstrably performs all
  three; a half-true method plane defeats the sweep's purpose.

- **D4 — Base-branch mapping is literal and fail-closed, in one place.**
  `MergeTargetActionKeySelector` maps `PullRequest.TargetBranch` ordinally:
  `"dev"` → `git.merge.dev`, `"qa"` → `git.merge.qa`, `"main"` → `git.merge.main`,
  anything else (including `master`, `feature/*`) and any unreadable PR →
  `git.merge.main`. Per the story's Out of Scope there is no trunk-name config today
  (the ADL `BaseBranch` default at `AdlModels.cs:103` is per-run, not a trunk registry);
  literals with the highest key as the floor is the honest v1. The PR read goes through a
  new `IGitMediationService.GetPullRequestAsync` (guard → per-tenant token → platform
  `GetPullRequestAsync` → one read event), NOT a direct driver call — the mediation
  service is where cross-tenant guarding lives. No `TammaApiClient` method is added
  (the selector runs inside the API host), so the D17 non-effect-method ratchet
  (`KnownNonEffectClientMethods`, pinned 19) does not move.

- **D5 — Seam E rebind is a one-literal change; the before/after pin is at dial 89/90.**
  `DeploymentPipelineWorkflow.cs:303` changes
  `"effect:deploy.promote-prod"` → `"effect:deploy.prod"`. Everything else at the prod
  gate — the OR-term (`:320-330`), the denied refusal terminal (`:337-348`), fail-open on
  transport error — is untouched, which is what makes AC4's "behaviour-identical at every
  dial position" checkable: with `deploy.prod` at 90, dial 89 waits and dial 90
  automates, exactly as `deploy.promote-prod` at level 90 would have.

- **D6 — QA/UAT stage gates copy the prod pattern, and their waits are stage-scoped.**
  AC4's second sentence ("the QA and UAT stages gain gate calls … at their stage
  entries") is under-specified: a blocking outcome must have somewhere to go, and chain
  rule 1 (43-11 Amendment 2-C1) forbids a gated link above the chain's entry approval
  with no resumable human wait — so "route a block to the stage-failed terminal" is not
  an option for requires-human. Each stage gets: `CheckActionGateActivity` on
  `deploy.qa` / `deploy.uat` at stage entry (before `qaDeployCall` `:194` /
  `uatDeployCall` `:222`) → a `FlowDecision` on `IsBlockingGateOutcome` (`:926`; no
  business-mode term — that is prod-only semantics) → a `WaitForDeploymentApprovalActivity`
  on approve/continue, reject/stage-failed; a `denied` outcome routes to the stage-failed
  terminal with the gate's reason (the F1 rule: a denial is not an escalation).
  **Bookmark collision forces one activity change**: the bookmark name
  (`adl-deploy-prod-approval-{tenant}-{repo}-{issue}-{sha}`) has no stage segment, so a QA
  wait and a prod wait in one run would mint the same bookmark. `WaitForDeploymentApprovalActivity`
  gains an optional `Stage` input folded into the bookmark name **only when set**; prod
  passes nothing and keeps the byte-identical legacy name (in-flight instances stay
  resumable, the existing resume path untouched). `DeployApprovalDecisionRequest` and
  `IElsaWorkflowService.ResumeDeploymentApprovalAsync` gain an optional `stage`
  (default → legacy prod name). Rejected alternatives: (a) a new wait activity type — a
  third approval surface for the same decision shape; (b) gate calls with no wait
  (fail-closed terminal) — wedges every run at dial < 75 with no escalation path, the
  exact "gating is theatre" failure Amendment 2-A names.

- **D7 — Reserved rows are ordinary descriptors with reserved SiteKeys; "declarative" is
  proven through the existing `enforcementSites` surface, no new machinery.** The
  effect plane requires UNIQUE SiteKeys (`ActionCatalog.cs:195-201`), so each of the four
  reserved rows carries its own string, e.g.
  `"RESERVED (Story 43-12) — no performer in the tree: deploy.dev stage does not exist in DeploymentPipelineWorkflow"`.
  A reserved SiteKey matches no route (`RoutePartOf` finds no em-dash-prefixed route
  pattern), so the binding sweep ignores it and `IActionEnforcementSites.For(key)` is
  empty — which is exactly what `GET /api/actions/policy` serialises
  (`ActionPolicyEndpoints.cs:91,154`) and what AC5's test asserts. Groups and grading:
  merge trio → `SourceControlWrite`, `Mutating`, `reversible: false` (the coarse key's
  grading, carried); `deploy.prod` → `DeployControl`, `Destructive`, irreversible
  (promote-prod's grading, carried); `deploy.dev|qa|uat|staging` → `DeployControl`,
  `Command`, `reversible: true` (a non-prod environment is redeployable — flagged as a
  judgement in the descriptor comment); `git.checks.bypass` → `SourceControlWrite`,
  `Mutating`, `reversible: false` (a merge that rode the bypass cannot be un-ridden);
  `git.webhook.register` → `SourceControlWrite`, `Mutating`, `reversible: true` (a
  webhook can be deleted), with the DUAL-dormant note and the 43-13 pointer in its
  summary, and the "if the first caller is provisioning plumbing, this row moves to the
  machinery inventory in the wiring PR" sentence from the story recorded verbatim.

- **D8 — engine.command is deleted through the ratchet's recorded-history mechanism, not
  around it.** Deleting the route makes its baseline entry
  (`KnownUngovernedEndpoints.cs:538-539`) stale (the staleness arm fails on an entry whose
  route is gone), so the entry is deleted in the same commit and the pins move the way the
  fixture's own rules demand: `PinnedCount` 216→215 with history `[237, 216, 215]`
  (strictly decreasing — this is the direction the ratchet celebrates), and
  `PinnedInScopeCount` 239→238 (one fewer mutating endpoint in scope). Nothing else
  asserts the route's absence, so AC6 gets an explicit test (see Test Plan). The legacy
  TS caller (`packages/orchestrator/src/transports/remote.ts:72-80`) is dead code in the
  superseded package; recorded here, not touched.

- **D9 — `agent-action:deploy`/`rollback` stay, pinned at 90/95 with the enforcement
  pointer in the descriptor.** Per the story's Architectural Context: they are prompt-
  taxonomy cells (RolePhaseMap), not effect seams. The descriptor summaries at
  `ActionCatalog.Descriptors.cs:179-180` gain "enforcement keys on the per-environment
  `effect:deploy.*` keys" and `min: 90` / `min: 95`. If 43-11's per-row table already set
  these numbers, this story only adds the comment — coordinate, don't double-write.

## Corrections to the story (verified against the tree)

1. **`GitEndpoints.cs:48-52`** — the method actually spans `:48-54`; the story's range
   stops before the closing brace. Substance unaffected.
2. **"`ActionEnforcementSitesTests` (21 bound rows) also moves: … Seam E binds
   `deploy.prod`"** — Seam E is invisible to that fixture: it computes sites from routes
   (`.Governs`) and `TammaApiClient` attributes only; `deploy.promote-prod` is NOT among
   today's 21 and `deploy.prod` will not be among the new 23. The pin that moves is the
   rows-with-sites count (21→23, the merge trio replacing the coarse merge row); the
   bound ROUTE count stays 21. The Seam E binding is pinned by
   `DeploymentPipelineGateTests` instead (the `:138` wire literal).
3. **The effect-plane count test is named `ExternalEffect_has_39_members`**
   (`ActionVocabularyCountTests.cs:53-81`), not a separate "effect-plane count" fixture.

## Implementation Steps

1. **Vocabulary edit** — `src/Tamma.Core/Actions/ExternalEffect.cs`: delete
   `GitPullRequestMerge` (`:71`) and `DeployPromoteProd` (`:134`); add ten members with
   wires `git.merge.dev|qa|main`, `deploy.dev|qa|uat|staging|prod`, `git.checks.bypass`,
   `git.webhook.register` (doc-comment each with performer or RESERVED note; update the
   header derivation comment `:7-23`). `src/Tamma.Core/Actions/ActionCatalog.Descriptors.cs`:
   delete the two retired descriptors (`:323-324`, `:402-403`); mint ten per D7 at their
   zone levels; AC7 comment + pins on `:179-180`. Compile-fixing every enum reference is
   part of this step (`Program.cs:3412`, `TammaApiClient.cs:305`, test lists).
   *Effort: 0.5 day.*

2. **Method plane** — `src/Tamma.Core/Actions/PerformsEffectAttribute.cs:29`
   `AllowMultiple = true`; `src/Tamma.Activities/LlmCall/TammaApiClient.cs:305` carries
   `[PerformsEffect]` for all three merge effects (comment sweep at `:1111`);
   `src/Tamma.Api/Services/Actions/ActionEnforcementSites.cs:168` reads
   `GetCustomAttributes`. *Effort: 0.25 day.*

3. **Mediation PR read** — `src/Tamma.Api/Services/Git/IGitMediationService.cs` +
   `GitMediationService.cs`: `GetPullRequestAsync(tenantId, repo, prNumber,
   correlationId, ct)` via `ExecuteGuardedAsync` (new read-operation constants in
   `GitEventTypes`); projection of `PullRequest` (needs `TargetBranch`) into
   `GitMediationResult`. *Effort: 0.5 day.*

4. **Multi-binding + selector (Seam C)** —
   `src/Tamma.Api/Infrastructure/GovernsExtensions.cs`: allow repeated/`params` binding;
   new `IActionKeySelector` metadata contract;
   `src/Tamma.Api/Services/Git/MergeTargetActionKeySelector.cs` (D4) + DI registration;
   `src/Tamma.Api/Infrastructure/GovernanceEnforcement.cs:233ff`: `GetOrderedMetadata`,
   the single/multi/no-selector arms per D2, selector outcome logged with the resolved
   key so the audit row names `git.merge.dev|qa|main`, never a coarse label;
   `src/Tamma.Api/Program.cs:3409-3413`: bind the three keys + selector metadata; the
   route keeps `.EnforcesGovernance()` (the `EnforcementOptedInRoutes` set is UNCHANGED).
   *Effort: 0.75 day.*

5. **Harness multi-binding support** —
   `tests/Tamma.Api.Tests/Actions/GovernanceHostFixture.cs`: `EndpointFact.Action` →
   `Actions` (list) + compile-fix the four consumers
   (`GovernedEndpointBindingSweepTests.cs:124-126,154`, `ActionEnforcementSitesTests.cs:78-80`,
   `GovernedEndpointCoverageSweepTests` `IsGoverned`, `GovernedEndpointEnforcementSweepTests`);
   `ActionEnforcementSites.cs:144`: `GetOrderedMetadata`. *Effort: 0.5 day.*

6. **Seam E** — `src/Tamma.ElsaServer/Workflows/DeploymentPipelineWorkflow.cs`: `:303`
   key literal → `effect:deploy.prod`; comment sweep (`:252`, `:274`, `:911`); QA/UAT
   stage gates per D6 (new `CheckActionGateActivity` + `FlowDecision` + stage-scoped
   `WaitForDeploymentApprovalActivity` + denied → stage-failed, wired before `:194` and
   `:222`); `src/Tamma.Activities/ADL/WaitForDeploymentApprovalActivity.cs`: optional
   `Stage` input, bookmark suffix only-when-set; `src/Tamma.Api/Endpoints/AdlEndpoints.cs`
   + the `DeployApprovalDecisionRequest` DTO + `IElsaWorkflowService`/`ElsaWorkflowService`
   `ResumeDeploymentApprovalAsync`: optional `stage`. Doc-comment sweep:
   `src/Tamma.Api/Endpoints/GovernanceEvaluateEndpoints.cs:75`,
   `src/Tamma.Activities/Policy/GovernanceEvaluateModels.cs:20`,
   `src/Tamma.Activities/Policy/CheckActionGateActivity.cs:60,109`. *Effort: 1 day.*

7. **engine.command deletion** — `src/Tamma.Api/Program.cs:3101` (route),
   `src/Tamma.Api/Endpoints/EngineEndpoints.cs:31-32` (`SendCommand`),
   `src/Tamma.Api/Dtos/Engine/EngineDtos.cs:5` (`SendCommandRequest`);
   `tests/Tamma.Api.Tests/Actions/KnownUngovernedEndpoints.cs`: delete `:538-539`,
   `PinnedCount` 216→215, `PinHistory` append 215, `PinnedInScopeCount` 239→238, with a
   dated decrement comment naming this story. *Effort: 0.25 day.*

8. **Count/wire/group/map pins** (the full table below) —
   `tests/Tamma.Core.Tests/Actions/ActionVocabularyCountTests.cs` (39→47 with derivation
   line; 197→205 with history line, test renamed `TotalCatalogMembers_is_205`),
   `ActionWirePinTests.cs:39-66`, `ActionGroupMembershipTests.cs:150-158,182-190,283,285`,
   `tests/Tamma.Activities.Tests/Actions/MediationClientEffectSweepTests.cs` (map −2 +10:
   merge trio → `MediationClient`/`MergePullRequestAsync`; `deploy.prod|qa|uat` →
   `InProcess` naming the workflow stage; four reserved → a new `SiteKind.Reserved` with
   per-key justification; `MediationClientSites_countIsPinned` 17→19; `:714,727` →
   `GetCustomAttributes`; attributed-method count stays 17 with an updated message),
   `tests/Tamma.Api.Tests/Actions/ActionEnforcementSitesTests.cs` (MediationEffects list
   17→19 effects; `:169` 21→23 with a comment naming the rows this story bound; `:200`
   stays 21; `:264` 17→19). *Effort: 0.5 day.*

9. **Re-point existing tests** —
   `tests/Tamma.Activities.Tests/Workflows/DeploymentPipelineGateTests.cs:138` →
   `effect:deploy.prod`; `tests/Tamma.Activities.Tests/Actions/SeamEMediationTests.cs`
   `:53,202,288` → a per-target merge key (use the fail-closed `git.merge.main` where the
   fixture has no readable PR — that doubles as an AC3 pin), `:86,99,127` →
   `effect:deploy.prod`; `tests/Tamma.Api.Tests/Actions/ActionAssignmentStorageTests.cs`
   / `ActionPolicyEndpointsTests.cs:180-200` — these use the wire only as an arbitrary
   valid key; re-point to `effect:deploy.prod`. *Effort: 0.25 day.*

10. **New tests** (Test Plan below), **docs**: record the three-stage-pipeline correction
    in this story's directory and add the changelog line to
    `docs/stories/epic-43/story-43-11/43-11-automation-level-model-and-per-action-levels.md`
    (AC5); run `dotnet test` and `dotnet ef migrations has-pending-model-changes` (no
    entity changed — must be clean). *Effort: 1 day.*

**Total: ~5.5 days.** The story's 3-day estimate predates D6's discovery that the QA/UAT
waits need stage-scoped bookmarks + resume plumbing, and D2/D5's discovery that
multi-binding ripples through five harness files. Saying so here rather than discovering
it mid-wave.

Suggested order: 1 → 2 → 8 (compile-stable vocabulary first), then 3 → 4 → 5 (merge
lane), 6 (deploy lane), 7 (deletion — independent, any time), 9 → 10.

## Test Plan (fail-first: what red looks like today)

New fixtures/tests; each is red against today's tree for the stated reason, not
vacuously:

- **`MergeTargetKeyResolutionTests`** (`Tamma.Api.Tests`) — drive the merge route once
  per target with a fake mediation service returning `TargetBranch` = `dev`/`qa`/`main`,
  assert the gate evaluated `effect:git.merge.dev|qa|main` respectively (via the decision
  audit/fake gate). RED TODAY: the keys don't exist and the route binds the coarse key —
  the asserted key is never seen.
  - `UnknownBase_ResolvesToMergeMain` — base `feature/x` → `git.merge.main`. RED: same.
  - `UnreadablePr_ResolvesToMergeMain` — mediation read fails → `git.merge.main`, request
    still gated (never fail-open). RED: same.
  - `TwoBindingsWithoutASelector_Is409Misconfigured` — discrimination proof on a fixture
    route. RED TODAY: multi-binding support doesn't exist; the filter reads one metadata
    and proceeds — the 409 never comes.
- **`DeploymentPipelineGateTests` additions** —
  `ProdGate_QueriesDeployProd_NotPromoteProd` (graph walk on the `:303` literal). RED:
  literal is `effect:deploy.promote-prod`.
  `Dial89_Waits_Dial90_Automates` (AC4's before/after, run against the real evaluator
  with the shipped `deploy.prod` descriptor). RED: no `deploy.prod` descriptor → gate
  can't resolve the key.
  `QaStage_GatesDeployQa_AtStageEntry` / `UatStage_GatesDeployUat_AtStageEntry` (graph
  walk: gate node before the stage dispatch; blocking outcome edges into a wait whose
  bookmark name carries the stage; denied edges into the stage-failed terminal). RED: no
  such nodes exist.
  `QaWait_And_ProdWait_MintDistinctBookmarks` — same run, both waits, names differ. RED
  (once the QA wait exists but Stage doesn't): identical names. Until then it fails
  because the QA wait doesn't exist. Either way it cannot pass against today's code.
- **`ReservedKeyTests`** (`Tamma.Api.Tests`) — for each of `deploy.dev`,
  `deploy.staging`, `git.checks.bypass`, `git.webhook.register`:
  `IActionEnforcementSites.For(key)` is empty AND `GET /api/actions/policy` serialises
  `enforcementSites: []` for the row (AC5's "declarative"). RED TODAY: the keys aren't in
  the catalog — the policy list has no such row (assertion on row presence fails first,
  which is the correct red).
- **`EngineCommandRouteIsGone`** (`Tamma.Api.Tests`, AC6) —
  `GovernanceHostFixture.Endpoints` contains no fact with SiteKey
  `POST /api/engine/command`, and no catalog key contains `engine.command`. RED TODAY:
  the route is mapped at `Program.cs:3101`, so the fixture sees it.
- **`RetiredWires_AreGone`** (`Tamma.Core.Tests`, AC2) — no descriptor and no
  `ExternalEffect` wire equals `git.pull-request.merge` or `deploy.promote-prod`. RED
  TODAY: both exist. (The AC's grep-over-src clause is enforced by review; the test pins
  the catalog half mechanically.)
- **`AgentDeployRollback_PinTheirLevels`** (AC7) — `agent-action:deploy` = 90,
  `agent-action:rollback` = 95. RED TODAY: both ship `AutonomyDial.Min` (70). (If 43-11's
  table already pins them, this test lands there and this row becomes a no-op —
  coordinate, don't duplicate.)
- **Moved pins as failing tests**: every count in the table below is an existing
  assertion that goes RED the moment step 1 lands (e.g. `ExternalEffect_has_39_members`
  fails at 47, `TotalCatalogMembers_is_197` at 205, the mediation-sweep totality fails
  listing the ten unmapped members, `SourceControlWrite_has_the_6_expected_members` fails
  on the trio). That is the drift machinery working; the pin edits in step 8 are the
  reviewed answer, each with its dated comment line.

Existing suites that must stay green untouched: `GovernedEndpointEnforcementSweepTests`
(opt-in set unchanged), `KnownNonEffectClientMethods` ratchet (19, unmoved),
`RatchetDisciplineTests` (histories append-only, no new ratchet).

## Count pins moved (all values read from the tree, 2026-08-02)

| Pin | File:line | Before | After |
|---|---|---|---|
| Total catalog members | `tests/Tamma.Core.Tests/Actions/ActionVocabularyCountTests.cs:147-148` | 197 | **205** (+ history line, test renamed) |
| Effect plane members | `ActionVocabularyCountTests.cs:80` | 39 | **47** (+ derivation line) |
| Effect wire list | `tests/Tamma.Core.Tests/Actions/ActionWirePinTests.cs:39-66` | 39 strings | **47** (−2 +10) |
| `source-control-write` members | `tests/Tamma.Core.Tests/Actions/ActionGroupMembershipTests.cs:150-158` | 6 | **10** |
| `deploy-control` members | `ActionGroupMembershipTests.cs:182-190` | 6 | **10** |
| Group count table | `ActionGroupMembershipTests.cs:283,285` | 6 / 6 | **10 / 10** |
| Catalog rows with ≥1 enforcement site | `tests/Tamma.Api.Tests/Actions/ActionEnforcementSitesTests.cs:169` | 21 | **23** (comment names the merge trio) |
| Bound route count | `ActionEnforcementSitesTests.cs:200` | 21 | **21** (unchanged — same one merge route) |
| MediationEffects fixture list | `ActionEnforcementSitesTests.cs:42-53` | 17 effects | **19** |
| Method-plane site count | `ActionEnforcementSitesTests.cs:264` | 17 | **19** |
| Mediation-client site pin | `tests/Tamma.Activities.Tests/Actions/MediationClientEffectSweepTests.cs:751-758` | 17 | **19** |
| Attributed client methods | `MediationClientEffectSweepTests.cs:717` | 17 | **17** (unchanged; message updated) |
| Ungoverned baseline | `tests/Tamma.Api.Tests/Actions/KnownUngovernedEndpoints.cs:221` | 216 | **215** |
| Baseline history | `KnownUngovernedEndpoints.cs:235` | `[237, 216]` | **`[237, 216, 215]`** |
| In-scope mutating surface | `KnownUngovernedEndpoints.cs:250` | 239 | **238** |
| Binding-sweep lower bound | `tests/Tamma.Api.Tests/Actions/GovernedEndpointBindingSweepTests.cs:156` | ≥21 | **unchanged** (facts rise to 23; lower bound not edited) |

Partition arithmetic check (`ActionEnforcementSitesTests.cs:195-198`): bound 21 +
baselined (215 + 2 exceptions) = 238 = new `PinnedInScopeCount`. Consistent.

## Dependencies on the batch (explicit)

- **43-11 (level model)** — **must land first. Hard.** Two independent reasons:
  `AutonomyDial.Min` is 70 (`AutonomyDial.cs:27`) and `ActionCatalog.BuildIndex` refuses
  sub-70 defaults (`ActionCatalog.cs:181-183`) — minting at 50-65 is a boot failure; and
  `ActionCatalogDefaultsTests.EveryOtherMember_DefaultsToMin` /
  `Deploy_ShipsAtMin_PerEpicDecisionD1` contradict every level this story assigns and are
  43-11's to rewrite. AC5 also edits 43-11's changelog (the three-stage correction).
- **43-14 (grant minting)** — land **with or after** this story; its own Dependencies say
  so (`43-14 …:55`): the merge-composite grant must name the per-target keys, and its
  approval-mints-grant rule is what reduces the new QA/UAT gates to one ask per run.
- **43-13 (caller-kind predicate)** — **not blocking**; `git.webhook.register`'s
  DUAL-dormant classification only bites once the gate distinguishes callers. The key is
  minted regardless.
- **43-15 (toggles & dial UI)** — not blocking; consumes the new keys and the reserved
  rows' empty `enforcementSites` for its "declarative" rendering. Coordinate copy only.
- **43-16 (acceptance unification)** — no overlap by construction (document-type plane).
- **42-10 (secret.read + sandbox)** — **no key overlap** (it owns `effect:secret.read`),
  but it edits the SAME pin files (`ExternalEffect.cs`, `ActionWirePinTests`,
  `ActionVocabularyCountTests`, the mediation-sweep map, descriptors). This plan's
  numbers (197→205, 39→47) assume 43-12 applies to today's tree; whichever of
  42-10/43-12/31-13 lands second rebases its counts (+1 per key already landed).
  **Serialize the pin-file commits across these three stories — they are one shared
  mutable surface.**
- **31-13 (PR operations)** — owns `git.issue.*`/PR-op keys; no overlap by construction
  (the duplicate-key guard makes a collision a build failure). Same pin-file contention
  as 42-10, and it also edits `Program.cs`'s git route block and `GitEndpoints.cs` —
  merge-order coordination on those two files.
- **39-25 (ambiguity threading)** — no file overlap. Independent.
- **40-8 (triage dead-ends / create-issues workflow)** — touches `EngineEndpoints.cs`
  (the `CreateIssue` area) while this story deletes `SendCommand` in the same file;
  disjoint regions, trivial merge, but same-file — order the commits.

## Risks

- **The QA/UAT gates are a real behaviour change at the default dial.** Once 43-11 puts
  `deploy.qa` at 75 and `deploy.uat` at 80, a pipeline run at the default dial (70)
  suspends at QA entry and again at UAT until a human approves — twice per run until
  43-14's entry-approval grants land. That is the zone model doing what it says, stated
  plainly: do not ship 43-12 and 43-11 to a live deployment without 43-14 close behind,
  or expect two extra approval clicks per deploy.
- **The merge gate now does a platform read before deciding.** Added latency and a new
  failure path on the main merge path; failure degrades to the STRICTER key
  (`git.merge.main`), never to fail-open. Merge fires once per issue-run, so the cost is
  bounded.
- **Multi-binding ripples through five harness files.** The risk is quietly weakening the
  one-binding assumption somewhere; the `TwoBindingsWithoutASelector_Is409Misconfigured`
  discrimination test plus the unchanged `EnforcementOptedInRoutes` pin are the guards.
- **Bookmark back-compat.** Prod's wait keeps the legacy bookmark name (Stage unset);
  only new QA/UAT waits get the suffix. An in-flight production approval survives the
  deploy of this story. A test pins the prod name byte-identical.
- **Pin-file contention across the batch** (42-10, 31-13). Mitigation above: serialize;
  whoever lands second rebases counts. The history arrays make a mis-rebased count a red
  test, not a silent lie.
- **`deploy.dev`/`deploy.staging` are reserved rows that look like features.** An
  operator setting the dial to 70 "to allow dev deploys" gets nothing — there is no dev
  stage. The descriptor summaries say "RESERVED — no pipeline stage exists" and the
  policy view shows zero sites; the 43-15 UI should surface that string, noted in
  Dependencies.
- **Legacy TS client 404s.** `packages/orchestrator/src/transports/remote.ts:72-80`
  still posts to the deleted route; the package is superseded and not in production.
  Recorded, deliberately untouched.

## Blocked / contradictions

- **Nothing unpassable was found**, given the hard order on 43-11. Every AC has a
  concrete implementation and a fail-first test above.
- **Amendment 3's premise "the pipeline already has the stages" is false for dev and
  staging** — `DeploymentPipelineWorkflow.cs:113` ships QA→UAT→Prod only. The story
  itself already records this correction and turns `deploy.dev`/`deploy.staging` into
  reserved keys; this plan implements that reading. The AC5 changelog edit to 43-11 is
  the remaining bookkeeping.
- **AC4 is under-specified for QA/UAT** (where does a blocking outcome go?). Resolved by
  D6 under chain rule 1 (a gated link above the entry approval must have its own
  resumable human wait) — but the resolution costs a `WaitForDeploymentApprovalActivity`
  Stage input and resume-DTO plumbing the story's 3-day estimate does not include.
  Recorded as scope growth, not silently absorbed: total estimate here is ~5.5 days.
- **The story's AC8 sentence about Seam E moving the `ActionEnforcementSitesTests` pin is
  wrong in detail** (Seam E is invisible to that fixture — Corrections #2). The pin that
  moves is rows-with-sites 21→23; the Seam E rebind is pinned in
  `DeploymentPipelineGateTests` instead. No AC becomes unpassable; the wording of AC8's
  evidence just lands in a different fixture than the story guessed.

## Change Log

| Date       | Version | Changes | Author |
| ---------- | ------- | ------- | ------ |
| 2026-08-02 | 1.0.0   | Initial plan. All story citations re-verified; D2 (multi-binding + selector), D3 (AllowMultiple), D6 (stage-scoped waits) added where the story was silent; count-pin table read from the tree; hard order on 43-11 and pin-file serialization with 42-10/31-13 recorded. | Claude |
