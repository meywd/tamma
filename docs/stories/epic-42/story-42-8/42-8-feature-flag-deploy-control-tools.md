# Story 42-8: Feature-Flag & Deploy-Control Tools

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As an **agent running a deploy, promotion, or incident workflow**, I want tools to **read/flip feature
flags** and to **trigger, promote, and roll back a deploy** — with prod-impacting operations gated as
`Destructive` and bound to the provider credential — so that a `deployment-pipeline` promotion or a
`41-22` kill-switch/rollback is a first-class governed capability, not a shell script.

## Priority

P2 / Wave 3 — the **release-control family**. Grouped because feature-flag toggling and deploy
control are the same governance shape (release-affecting mutations, prod-gated) and share the descriptor
+ secret + suspend + audit wiring.

## Scope

### Part A — Feature-flag / config toggle tool (`FeatureFlagTool`)

1. **Provider-abstracted flag interface** (`IFeatureFlagProvider`: read / set / list a flag; one
   reference driver + generic seam). One `IToolExecutor` the LLM sees; provider chosen by config/binding.
2. **Permission class by environment.** `read`/`list` = `ReadOnly`; setting a **non-prod** flag =
   `Mutating`; setting a **prod** flag or a **kill-switch** = `Destructive` (routed to orchestrator/human
   by 42-3). The prod-vs-non-prod distinction comes from the flag/env passed in the call, checked at the
   gate — recommend a read/write split as in 42-7 for clean per-tool classing.
3. **Secret:** `SecretRequirement(ApiKey, "flags/<provider>", Required)`, tenant/user-scoped via 42-4.

### Part B — Deploy control tool (`DeployControlTool`)

1. **Provider-abstracted deploy interface** (`IDeployControlProvider`: trigger / promote / rollback /
   status of a deploy; one reference driver aligned to the platform's deploy path — Docker Compose on the
   Hetzner VPS per CLAUDE.md — plus a generic seam).
2. **Permission class by target.** `status` = `ReadOnly`; `trigger`/`promote` to **staging** =
   `Mutating`; `trigger`/`promote` to **prod** and any `rollback` = `Destructive`. Prod
   promotion/rollback routes to the orchestrator/human via 42-3 — and note Epic 39 already models
   "final production-deploy authorization for regulated/breaking changes" as an **always-escalate
   acceptance-rules class**, so prod deploy stays a human decision by *policy*, not tool code (Epic 41
   out-of-scope item). This tool *executes* an authorized deploy; it never *decides* to skip the gate.
3. **Secret:** `SecretRequirement(ApiKey | SigningKey, "deploy/<platform>", Required)`, scoped via 42-4.
4. **Long deploys suspend.** `Suspends = true`: trigger the deploy, suspend the workflow, resume on
   completion (poll/callback) — resumable-by-design, no blocked worker (mirrors 42-7 §4 and the existing
   `deployment-pipeline` semantics).

## Acceptance Criteria

1. Flag `read`/`list` resolve `ReadOnly`; a **prod** flag set is `Destructive` and routes through 42-3
   authorization before applying (test); a **non-prod** set is `Mutating` and runs at its floor (test).
2. Deploy `status` is `ReadOnly`; **staging** trigger/promote is `Mutating`; **prod** promote and any
   `rollback` are `Destructive` and route to the actor (test per operation).
3. Both tools dispatch through their provider interface to the reference driver via a stub-provider test
   (no concrete-provider knowledge in the tool).
4. Credentials bind tenant/user-scoped (42-4) and never appear in any emitted artifact (grep test).
5. A prod deploy that hits an always-escalate acceptance class routes to a human regardless of autonomy
   level (test — the tool does not bypass the Epic 39 policy gate).
6. A long deploy suspends and resumes on completion; failure yields `Success = false` + `TOOL.FAILED`,
   never a throw or silent success.
7. A `rollback` records the authorizing actor in its `TOOL.*` lineage (test).

## Events

Reuses 42-5 `TOOL.*` with `flag`/`environment` and `deploy`/`target`/`status` tags. No new family.

## Single-user vs SaaS

- **single-user:** the user's flag/deploy credentials; prod authorizations route to the single
  orchestrator/user.
- **SaaS:** tenant-scoped credentials and config (a tenant flips only its own flags / drives only its own
  deploys); prod authorizations route to the tenant orchestrator/role, tenant-isolated.

## Epic 41 consumers

`deployment-pipeline` (promotion + rollback for infra tasks via 41-29), **41-22** (incident kill-switch
+ rollback), **41-24** (release-notes trigger keyed off a promote), 41-29 `infra` `TaskKind`.

## Dependencies

- **42-1** (descriptor, `Suspends`), **42-3** (prod-gating + authorization), **42-4** (secret binding —
  hard-blocked on the Epic 29 reveal path), **42-5** (audit).
- **Epic 39** always-escalate acceptance-rules class for prod deploy (policy, reused not rebuilt).
- **Epic 41 / 41-29** consumers.

## Risks

- **Prod blast radius.** A wrongly-authorized prod flag/deploy is high-impact. Mitigation: `Destructive`
  + 42-3 actor-authorization + the Epic 39 always-escalate class for prod + full 42-5 lineage.
- **Env detection.** Prod-vs-non-prod must be reliable (a flag mislabeled non-prod escapes the gate).
  Mitigation: env comes from validated config/binding, not free-text from the model; unknown env → treat
  as prod (fail-safe).

## Estimated Effort

Large. ~5–6 days (two provider abstractions + reference drivers + suspend/resume + prod-gating wiring),
shared plumbing keeps it from being two separate larges.
</content>
