# Role/Action Taxonomy & Resolution

Tamma keys **prompts** and **coding conventions** off one shared, code-defined
taxonomy: `AgentRole` × per-role specific `AgentAction`. Resolution is an exact
`(role, action)` lookup (tenant override → system default), performed at the
prompt-pull step of the [LLM Call workflow](Workflow-LLM-Call.md).

## Why not keywords?

A bare action like `plan` is ambiguous — *plan what?* The **role** answers it:
architect + `plan` → plan a system design; developer + `plan` → plan an
implementation/fix. The meaningful unit is the `(role, action)` pair, so
resolution is a keyed fetch, not keyword matching.

## Taxonomy

8 roles: developer, tester, security, devops, architect, product_owner,
senior_developer, tech_writer. Each role has its own specific action set
(~80 jagged cells total). Shared tokens (`context-scan`, `code-review`,
`plan-review`) repeat across roles; the role half of the key disambiguates.

Epic 39 added the split **`(developer, triage-context-scan)`** cell — a
triage-focused context scan distinct from the general `(developer, context-scan)`
— so the [Triage Context Gathering](Workflow-Triage-Context-Gathering.md) binding
produces a `Findings` document under its own producer cell (rather than sharing
research's cell).

See the canonical list and rationale in the design spec:
`docs/superpowers/specs/2026-05-18-role-action-taxonomy-and-resolution-design.md`.

## Guarantees

- **Strong-typed:** `AgentRole`/`AgentAction` enums; wire format is a plain
  string (`PlanSystemDesign` → `"plan-system-design"`).
- **No drift:** prompt + convention seeds are generated from the taxonomy; a
  build test rejects any workflow dispatching a pair outside it.
- **Code-defined:** roles/actions are not in the database; tenant
  customization is per-`(role, action)` convention/prompt overrides only.
- **Document-type bound (Epic 39):** every document-producing `(role, action)`
  cell is now bound to a document-type validator — a universal invariant enforced
  by `ContractBindingTests`, so a producer cell always type-checks against the
  [document type](Document-Lifecycle) it emits (Decomposition, Findings, Plan,
  Design, Review, TriageDecision, Diagnosis, TestSpec, …). Only genuine
  **prose/code** cells remain unbound: tech-writer prose outputs (changelog, ADR,
  postmortem, release notes — markdown with an audience tag, no forced schema) and
  code-producing cells (code's validator is the build/test/gate stack, not a
  document schema).

## Related

- [LLM Call Workflow](Workflow-LLM-Call.md)
- [Agent Dispatch](Agent-Dispatch.md)
- [Epics](Epics.md) — Epic 27 stories 27-8..27-19
