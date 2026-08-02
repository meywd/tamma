# Story 43-12: Per-Target Merge/Deploy Keys and the Ladder Gaps

Status: drafted

Implements: Story 43-11 **Amendment 3** (zones, per-target actions), the **Caller-kind re-audit — Missing actions** section, and the count-pin consequences both name.

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As a **platform operator setting the dial between 55 and 90**,
I want merge gated by the PR's target branch and deploy gated by the target environment — separate catalog keys per target, at the zone levels the product owner set,
So that "merge to dev is fine, merge to main needs a person" is expressible, instead of one coarse merge key and one coarse deploy key that must carry the worst case.

## Priority

P1 — Amendment 3's ladder has five named slots (55, 60, 65, 70, 85 for merges/deploys) that are **empty** until these keys exist. Without them, dial positions 55–70 do nothing for the merge/deploy family and the zone model is prose.

## Architectural Context (READ FIRST)

- **The ladder** (43-11 Amendment 3): merge splits by PR base branch — `git.merge.dev` 55 / `git.merge.qa` 60 / `git.merge.main` 65; deploy splits by environment — `deploy.dev` 70 / `deploy.qa` 75 / `deploy.uat` 80 / `deploy.staging` 85 / `deploy.prod` 90; `git.checks.bypass` sits at 50 with **no action in the tree performing it** — the key is reserved before anything does.
- **The coarse keys today**: `effect:git.pull-request.merge` is one catalog row (`ActionCatalog.Descriptors.cs`, source-control-write group) bound at the merge route — `app.MapPut("/api/v1/git/{owner}/{repo}/pull-requests/{n:int}/merge", GitEndpoints.MergePullRequest)` at `apps/tamma-elsa/src/Tamma.Api/Program.cs:3409`, handler at `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitEndpoints.cs:48`. `effect:deploy.promote-prod` is bound at Seam E (`DeploymentPipelineWorkflow.cs`, third `OR` term). Both are retired by this story.
- **The merge handler does not know the base branch.** `GitEndpoints.MergePullRequest` (`GitEndpoints.cs:48-52`) takes `(owner, repo, n, body)` and calls `MergePullRequestAsync` — nothing reads the PR's base. Resolving the per-target key requires a PR-details read before the gate decision. Unknown/unreadable base **fails closed to `git.merge.main`** (the highest of the three).
- **The deploy pipeline has three stages, not five.** `DeploymentPipelineWorkflow.cs:113` describes the shipped pipeline: "Deploy through **QA -> UAT -> Prod**". Amendment 3's claim that "the pipeline already has the stages" is **wrong for dev and staging** — no dev or staging stage exists in the workflow. `deploy.dev` and `deploy.staging` are therefore minted as **reserved keys** (like `git.checks.bypass`): real catalog rows at their zone levels, no enforcement site until a pipeline stage exists. This story records that correction; it does not add pipeline stages.
- **The two UNDECIDED keys from the re-audit**, resolved here:
  - **`effect:engine.command` — DELETE the endpoint, mint nothing.** `EngineEndpoints.SendCommand` (`apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:31-32`) returns `{ message = "Command accepted", command = req.Command }` and does nothing else; it is mapped at `Program.cs:3101` under `WorkflowsManage` and no caller was found. The argument: a live route that accepts an arbitrary "command", answers 200 "accepted", and performs nothing is worse than a missing feature — it is a false affordance (callers believe a command was queued), an audit hole (a 200 with no event row), and a standing invitation to grow ungoverned behavior later. Cataloguing a no-op would pin governance vocabulary to a lie. Delete the route, the handler, and `SendCommandRequest`; if a real engine-command surface is ever built, it arrives with its own catalog key and enforcement in the same PR.
  - **`effect:git.webhook.register` — mint it, reserved at 85, classified DUAL-dormant.** `IGitPlatformClient.RegisterWebhookAsync` (`apps/tamma-elsa/src/Tamma.Platforms.Abstractions/IGitPlatformClient.cs:101`) is implemented by drivers with **no caller** in the tree. It is a repo-settings write that mints standing infrastructure (a webhook is a durable ingress path), which is the 85 "create infrastructure" zone. Minting now reserves the slot so the first caller cannot ship ungoverned; classification is DUAL (admin setup by hand, or an LLM onboarding flow), and per Story 43-13 the level binds only an LLM path. If the first real caller turns out to be provisioning plumbing, the row moves to the machinery inventory in the wiring PR — the re-audit's "classify when wired" is honored by making the reclassification a named, reviewable move instead of leaving the key unminted.
- **The coarse `agent-action:deploy` and `agent-action:rollback` stay.** They are prompt-taxonomy cells (`RolePhaseMap`, prompt registry), not effect seams. `agent-action:deploy` is pinned at 90 (= `deploy.prod`, the worst target) and enforcement keys on the per-env effect keys; retiring the agent-action would break the role/action taxonomy for no governance gain.
- **Count pins move.** `ActionVocabularyCountTests.TotalCatalogMembers_is_197` (`tests/Tamma.Core.Tests/Actions/ActionVocabularyCountTests.cs:132-149`) and the effect-plane count: −2 retired (`git.pull-request.merge`, `deploy.promote-prod`), +10 minted (3 merge + 5 deploy + `git.checks.bypass` + `git.webhook.register`) → **effects 39 → 47, total 197 → 205**. The test's history comment gains this story's line, per its own convention. `ActionEnforcementSitesTests` (21 bound rows) also moves: the merge route now binds three keys and Seam E binds `deploy.prod` (plus `deploy.qa`/`deploy.uat` when bound at their stages) — the new bound count is pinned explicitly, not left to drift.
- **Not minted here**: `effect:secret.read` (Story 42-10 owns it), the issue/PR operation keys (`git.issue.create` etc. — Story 31-13 owns them). Double-minting a key is a build failure by the catalog's duplicate-key guard; the ownership split is deliberate.

## Acceptance Criteria

1. **Nine new descriptors ship at their zone levels**: `effect:git.merge.dev` 55, `effect:git.merge.qa` 60, `effect:git.merge.main` 65, `effect:deploy.dev` 70, `effect:deploy.qa` 75, `effect:deploy.uat` 80, `effect:deploy.staging` 85, `effect:deploy.prod` 90, `effect:git.checks.bypass` 50, plus `effect:git.webhook.register` 85. Each carries group, risk, reversibility and (where live) site key consistent with its zone row in 43-11.
2. **The coarse keys are retired**: `effect:git.pull-request.merge` and `effect:deploy.promote-prod` are removed from the catalog; every code and test reference is re-pointed at the per-target keys. A grep for either wire key over `src/` returns zero hits.
3. **The merge route resolves the key from the PR base branch.** The gate decision at the merge route uses `git.merge.dev|qa|main` chosen by the PR's base; a test drives one merge per target and asserts the resolved key. Base branches other than the configured dev/qa/main trunk names, and an unreadable base, resolve to `git.merge.main` — pinned by test (fail-closed).
4. **Seam E gates `deploy.prod`** where it gated `deploy.promote-prod`, behavior-identical at every dial position — pinned by a before/after test at dial 89 (gated) and 90 (automated). The QA and UAT stages gain gate calls on `deploy.qa` / `deploy.uat` at their stage entries.
5. **`deploy.dev`, `deploy.staging`, `git.checks.bypass`, `git.webhook.register` are reserved rows**: a test asserts each has an empty `enforcementSites` array and the policy view renders them as declarative (no live seam). The docs correction about the three-stage pipeline is recorded in this story and in 43-11's changelog.
6. **`POST /api/engine/command` is gone**: route, `SendCommand`, and `SendCommandRequest` deleted; a test (or the route-inventory drift sweep) asserts the path is unmapped. No catalog key exists for it.
7. **`agent-action:deploy` pins at 90 and `agent-action:rollback` at 95**, with a descriptor comment naming the per-env effect keys as the enforcement surface.
8. **Count pins move deliberately**: `TotalCatalogMembers` asserts 205 with a history line; the effect-plane count asserts 47; `ActionEnforcementSitesTests` pins the new bound-row count with a comment naming which rows this story bound.
9. **`dotnet test` is green; no schema change** (`dotnet ef migrations has-pending-model-changes` clean).

## Dependencies

- **Story 43-11** — the zone model and level table these keys land in. Blocking.
- **Story 43-13 (caller-kind predicate)** — `git.webhook.register`'s DUAL-dormant classification only means something once the gate distinguishes callers. Not blocking; the key is minted regardless.
- **Story 43-14 (grant minting)** — the merge-composite grant covers the per-target merge keys; land 43-14 with or after this story so the grant names real keys.
- **Story 31-13** — owns the issue/PR-operation keys; no overlap by construction.
- **Verified in tree**: `Program.cs:3409` (merge route), `:3101` (command route); `GitEndpoints.cs:48-52`; `EngineEndpoints.cs:31-32`; `DeploymentPipelineWorkflow.cs:113`; `IGitPlatformClient.cs:101`; `ActionVocabularyCountTests.cs:132-149`.

## Out of Scope

- Adding dev/staging stages to `DeploymentPipelineWorkflow` — a pipeline feature, not a vocabulary story.
- Building anything that performs `git.checks.bypass` — the key is a reservation.
- The issue/PR-operation keys (31-13) and `secret.read` (42-10).
- Per-repo trunk-name configuration UI; this story reads whatever branch-name config exists and otherwise uses `dev`/`qa`/`main` literals, fail-closed per AC3.

## Estimated Effort

3 days — 1 for descriptors + retirements + count pins, 1 for the merge-route base-branch resolution and Seam E rebinding with tests, 1 for the endpoint deletion, webhook key, and drift-sweep updates.

## Change Log

| Date       | Version | Changes                                                          | Author |
| ---------- | ------- | ---------------------------------------------------------------- | ------ |
| 2026-08-02 | 1.0.0   | Initial story — per-target merge/deploy keys, checks-bypass + webhook.register minted, engine.command deleted, count-pin moves | Claude |
