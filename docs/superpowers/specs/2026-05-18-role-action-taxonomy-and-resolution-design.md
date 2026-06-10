# Role/Action Taxonomy & Prompt+Convention Resolution — Design

**Date:** 2026-05-18
**Status:** Design — pending user spec review
**Supersedes (in part):** the resolution model of Epic 27 stories 27-8, 27-9, 27-13
**Owner:** Platform Engineering

---

## 1. Problem

An audit of the Convention Store work (Epic 27 stories 27-8/27-9/27-13) and its
interaction with the Elsa workflows surfaced three verified defects and a deeper
modelling problem.

### 1.1 Verified findings (evidence-backed)

1. **Role never reaches convention resolution.** `LlmCallContext`
   (`(Action, Tools, SearchableText, RepoLanguages)`) has no `Role` field, and
   `LlmCallWorkflow.cs:185-186` passes `Role` and `Action` to the prompt
   registry as *separate* inputs with no composite. Story 27-13's intended
   `agentRole + "/" + taskAction` composite is **unimplemented**. Result: every
   `role-*` convention template can never fire via keyword tokenisation.
2. **Three conflicting definitions of the action term.** Story 27-9 (camelCase
   single token), story 27-13 (`role/action` composite), and the actual code
   (`context.GetInput<string>("action")`, bare kebab string, no transform) all
   disagree.
3. **Two contradictory matchers in story 27-9.** `WHERE keyword IN (@terms)`
   set-membership (line 102, the B-tree hot path) vs
   `Regex.IsMatch(corpus, \b{keyword}\b)` (line 221). `\b` treats `-` as a word
   boundary and would re-shatter `plan-review`; the two cannot coexist.

Scope of the audit (deterministic, not sampled): 64 workflow files, 21
`llm-call` dispatch sites, 9 distinct `["action"]` literals
(`context-scan, create-tasks, deploy, plan, plan-review, summarize,
task-review, triage, write-tests`). `plan-review`, `create-tasks`, and
`task-review` had no keyword in the seed.

### 1.2 The deeper problem

Keyword matching is the wrong model. A flat action like `plan` is meaningless
on its own — *plan what?* The answer is carried by the **role**: architect +
`plan` = plan a system design; developer + `plan` = plan an implementation/fix;
product_owner + `plan` = plan scope. The meaningful unit is the
**`(role, action)` pair**, translated at prompt-pull time — which is exactly
how the Prompt Store already works (`SystemPrompts.cs`: 8 roles × 10 actions =
80 `RoleActionTemplates`, resolved by `ResolvePromptFromRegistryActivity`).
Conventions must use the *same* model, not a parallel keyword system.

### 1.3 Greenfield status

`ResolveConventionsActivity`, `IConventionStore`/`ConventionStore`, and
migration `018` **do not exist**. Stories 27-8/27-9/27-13 are 100%
unimplemented. The only shipped code is `ReadRepoConventionsActivity.cs` (the
repo-config fallback) and the 46 templates (served only as starter data via
`GET /api/convention-templates`, used in zero resolution path). Per
CLAUDE.md ("no migration anxiety"), we fix the design before building.

---

## 2. Decision Model (model 1)

Confirmed via brainstorming:

- **The workflow always knows the specific action. There is no "unknown" and
  no classification-at-pull-time step.** The action is always fully specific
  (`plan-system-design`, `plan-implementation`, …); the workflow emits exactly
  that. (This is the *target*. Until initiative (2) specialises every dispatch
  site, a few sites still emit a generic action as an explicit transitional
  state — see §3.5 and §6. Generic is never a runtime fallback tier.)
- **One shared typed `(role → specific-action)` taxonomy**, owned by
  `RolePhaseMap` (rebuilt on enums), single source of truth.
- **Consumed identically by prompts, conventions, agent resolution, provider
  routing, and the workflow dispatch sites.** Same roles, same per-role action
  sets, everywhere.
- **Roles/actions stay code-defined** (no `roles`/`actions` DB tables). Only
  conventions and prompts are two-tier (system default + tenant override).
  Dynamism = tenant overrides keyed by `(role, action)`.

---

## 3. Locked Resolution Architecture

### 3.1 Strong types

```csharp
public enum AgentRole {
    Developer, Tester, Security, Devops,
    Architect, ProductOwner, SeniorDeveloper, TechWriter
}

// One enum = the UNION of all distinct action tokens across roles (§4).
// Per-role membership (which actions a role may perform) is defined by
// RolePhaseMap (§3.2), not by partitioning the enum. Shared tokens
// (context-scan, code-review, plan-review, write-tests) are single enum
// values reused across roles; the (role, action) key disambiguates them.
public enum AgentAction { /* union of all §4 tokens */ }
```

- `ToWire()` → canonical kebab/snake string (`PlanSystemDesign` →
  `"plan-system-design"`, `ProductOwner` → `"product_owner"`). One mapping
  table, one place.
- `Parse(string)` → enum; applies `RolePhaseMap.LegacyRoleAliases` first
  (`"implementer"` → `Developer`, `"analyst"` → `ProductOwner`), then exact
  match; **throws `TammaError` on unknown** (the fail-fast boundary).
- Round-trip invariant: `Parse(x.ToWire()) == x` for every enum value
  (enforced by test).
- The wire format remains a primitive string, so Elsa's serialized
  cross-workflow dispatch boundary and persisted workflow state are unchanged
  and backward-compatible — no `JsonConverter`, no durable-payload contract.

### 3.2 `RolePhaseMap` becomes the authority, rebuilt on enums

- `ValidRoles`/`ValidActions` derive from `Enum.GetValues<>()`.
- The per-role action set (§4) replaces the flat phase→roles eligibility map.
  `IsRoleEligibleForPhase(role, action)` becomes "is `action` in
  `role`'s action set".
- `GetPrimaryActionForRole`, normalisation, legacy aliases keep current
  behaviour, keyed off enums.
- The four existing cross-cutting consumers (`AgentResolverService`,
  `ProviderChainResolver`, `AgentEndpoints`, `DefaultAgentConfig`) are
  behaviourally unaffected — they call the same `RolePhaseMap` surface.

### 3.3 Resolution = exact `(role, action)` lookup

- Convention store and prompt store both keyed by `(tenant_id, role, action)`.
- Resolution: tenant-override row → system-default row. **No keyword matching,
  no tokenizer, no composite delimiter, no specificity fallback tiers.**
- The following are **deleted from the Epic 27 design**:
  - `convention_keywords` table, B-tree keyword index, `tokenize`,
    `match_mode`, `always_apply` (story 27-8)
  - both matchers in story 27-9; replaced by a single keyed fetch
  - the composite-action construction in story 27-13

### 3.4 Anti-drift

- A single typed registry (the §4 taxonomy in `RolePhaseMap`) is the source.
- **Codegen** generates *both* the prompt seed and the convention seed from it.
  They share keys → cannot drift.
- **Build test**: every `(role, action)` literal emitted by a compiled
  workflow dispatch site ∈ the taxonomy (drift = build failure).

### 3.5 Generic actions are a migration state, not a tier

Until initiative (2) (see §6) structurally specialises every dispatch site, a
small number of workflows still emit a generic action (e.g. `plan`). These
generic cells exist **only as transitional seed rows** and are deleted as each
dispatch is specialised. They are not a permanent resolution fallback.

---

## 4. Per-Role Action Taxonomy

Naming rule: specific action = `{verb}-{object}`. Atomic actions
(`context-scan`, `write-tests`, `debug`, `deploy`, `rollback`) stay atomic;
planning/design/review actions specialise by object. Shared tokens repeat
across roles intentionally — the **role half of the `(role, action)` key**
differentiates them.

### product_owner — intake, requirements, prioritisation, acceptance
`context-scan`, `triage-intake`, `clarify-requirements`, `plan-scope`,
`define-acceptance-criteria`, `prioritize-backlog`, `plan-roadmap`,
`summarize-stakeholder`, `review-acceptance`, `review-scope`

### architect — system design, technical strategy
`context-scan`, `triage-technical`, `plan-system-design`,
`design-api-contract`, `design-data-model`, `design-integration`,
`plan-migration-strategy`, `write-adr`, `plan-review`,
`code-review-architecture`, `assess-technical-risk`

### senior_developer — tech lead: decomposition, review, mentorship
`context-scan`, `create-tasks`, `plan-implementation`, `plan-review`,
`code-review`, `plan-refactor`, `debug-rootcause`, `triage-technical`,
`summarize-technical`, `resolve-blocker`, `mentor-feedback`

### developer — implementation
`context-scan`, `plan-implementation`, `plan-fix`, `plan-debugging`,
`implement-feature`, `implement-fix`, `write-tests`, `refactor`, `debug`,
`code-review`, `address-review-comments`, `self-review`, `review-feasibility`,
`triage-defect`

### tester — QA, test engineering
`context-scan`, `plan-test-strategy`, `write-test-cases`, `write-tests`,
`write-regression-test`, `exploratory-test`, `verify-acceptance`,
`code-review-coverage`, `triage-defect`, `review-testability`

### security — security review, threat modelling
`context-scan`, `threat-model`, `plan-review-security`,
`code-review-security`, `assess-vulnerability`, `audit-dependencies`,
`audit-secrets`, `review-compliance`, `analyze-security-incident`

### devops — infra, CI/CD, deployment, ops
`context-scan`, `plan-deployment`, `implement-infrastructure`,
`configure-cicd`, `deploy`, `rollback`, `monitor-health`,
`diagnose-incident`, `plan-incident-response`, `write-postmortem`,
`assess-capacity`, `review-operability`

### tech_writer — documentation
`context-scan`, `summarize-changes`, `write-user-docs`, `write-api-docs`,
`write-release-notes`, `write-runbook`, `update-changelog`, `review-docs`

**Story 27-19 additions:** `review-feasibility` (developer), `review-testability` (tester), `review-operability` (devops), and `review-scope` (product_owner) were added to fully-specialise the cross-role review/triage panels — no generic actions. The developer set was also widened to include `triage-defect` (previously tester-only) so developers can triage defects directly.

~84 meaningful jagged cells (vs the old flat 8×10). Mapping to existing
workflow reality:

| Existing workflow | Maps to |
|---|---|
| ReviewFix (`implementer`) | developer/`address-review-comments` |
| Debugging (regression test) | developer/`debug` + tester/`write-regression-test` |
| BlockerDiagnosis | senior_developer/`resolve-blocker` or devops/`diagnose-incident` |
| Mentorship | senior_developer/`mentor-feedback` |
| TaskReview | senior_developer/`plan-review` (task-level) |
| TaskCreation | senior_developer/`create-tasks` |
| Triage panel/PO | product_owner/`triage-intake` + architect/`triage-technical` |

---

## 5. Components Affected

| Component | Change |
|---|---|
| `AgentRole`/`AgentAction` enums (new) | Closed enums + `ToWire()`/`Parse()`, alias-aware |
| `RolePhaseMap.cs` | Rebuilt on enums; per-role action sets replace eligibility map |
| `SystemPrompts.cs` / prompt seed | Reshaped from flat 8×10 to the §4 jagged taxonomy; codegen'd |
| Convention store schema (story 27-8) | `conventions(tenant_id, role, action, body, …)`, `UNIQUE(tenant_id, role, action)`; **no** keyword table |
| Convention store service (story 27-9) | Single keyed-fetch resolver; both matchers deleted |
| `ResolveConventionsActivity` (story 27-13) | New: exact `(role, action)` fetch at the prompt-pull boundary |
| ~21 cross-workflow dispatch sites | Emit `AgentAction.X.ToWire()` instead of raw string literals |
| Codegen tool | Generates prompt seed + convention seed from the taxonomy |
| Build test | Asserts dispatch literals ∈ taxonomy + `Parse/ToWire` round-trip |
| `ReadRepoConventionsActivity.cs` | Retained as fallback source only |

---

## 6. Scope Boundary & The (1)↔(2) Seam

This spec is **initiative (1): the shared typed taxonomy + exact-lookup
resolution model for prompts and conventions.** It is independently valuable
(fixes the verified defects, unifies prompts+conventions) and is a hard
prerequisite for (2).

**Initiative (2) — out of scope here, separate brainstorm/epic:**
restructuring `SingleIssueCycleWorkflow` into a "roundabout" — a state machine
where any step is reachable from any step — providing the structural
classification/routing that makes the workflow *always know* the specific
`(role, action)`. (2) *consumes* this model unchanged; a dynamically-chosen
`developer/plan-debugging` resolves through the exact same `(role, action)`
lookup as a hardcoded one.

Until (2) lands, Model 1 is only partially realised: some dispatch sites still
emit generic actions (transitional seed cells, §3.5), removed as (2)
specialises each.

---

## 7. Testing Strategy

- **Round-trip:** `Parse(x.ToWire()) == x` for every `AgentRole`/`AgentAction`.
- **Alias:** `AgentRole.Parse("implementer") == Developer`,
  `Parse("analyst") == ProductOwner`; `ToWire()` always emits canonical.
- **Taxonomy coverage:** every `(role, action)` in §4 has a seeded prompt row
  and a seeded convention row (codegen output asserted).
- **Drift build test:** every `(role, action)` literal in the 21 compiled
  dispatch sites ∈ the taxonomy; failure breaks the build.
- **Resolution:** `(tenant, role, action)` returns tenant override when
  present, else system default, else `TammaError` (no silent empty for a
  taxonomy-valid pair).
- **Backward compatibility:** existing suspended Elsa workflow instances
  (string-valued dispatch payloads) still deserialize and resolve.

---

## 8. Out of Scope

- `roles`/`actions` DB tables; DB-dynamic role/action vocabulary.
- Changes to agent-resolution/provider-routing/API-validation behaviour beyond
  the enum refactor.
- Initiative (2): the `SingleIssueCycleWorkflow` roundabout / structural
  routing (separate epic).
- The keyword-matching convention model (deleted, not migrated).
