# Story 42-8A: Feature-Flag / Config-Toggle Tool

Status: drafted

*Split from the former combined story 42-8 — see [the split index](./42-8-feature-flag-deploy-control-tools.md).*

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As an **agent running a deploy, promotion, or incident workflow**, I want to **read and flip feature
flags / runtime config** through a governed tool — with a **prod** flip authorized against the concrete
flag by an actor and the provider credential bound — so that a `deployment-pipeline` gradual rollout or
a `41-22` kill-switch is a first-class capability instead of a shell script.

## Priority

P2 / Wave 3. Ships after 42-9 and before 42-8B: it has **no engine-side work**, so it is not gated on
the wait-activity / bookmark / callback chain 42-8B and 42-7 share.

## The gap (READ FIRST)

There is **no feature-flag code anywhere in the C# backend** — `grep -rl "FeatureFlag\|feature_flag"`
over `apps/tamma-elsa/src` returns nothing. This is greenfield: interface, driver, and binding all new.
A kill-switch today means `ShellExecute` + whatever the flag vendor's CLI is, unbound and unaudited.

## Where this code lives (binding)

**Both executors, `IFeatureFlagProvider`, and every driver live in `Tamma.Api`** — package
`Tamma.Api.Services.Tools.FeatureFlags`, registered next to the six built-ins at
`Tamma.Api/Program.cs` L753–766. Nothing here is added to `Tamma.Activities`.

Reasons, in force order: (1) **rule 1** — a workflow step never calls an external API or holds an
external credential, and these do both; (2) **runtime** — `Tamma.ElsaServer/Program.cs` L286–292
records that the tool catalog was *removed* from the engine and "the tool executors are registered
there [`Tamma.Api`], not here", so an engine-side executor would never be resolved; (3) **guardrail
backstop** — `TAMMA001` (`DiagnosticSeverity.Error`, analyzer-referenced by `Tamma.Activities` /
`Tamma.ElsaServer`) denies credential-resolver injection on the engine surface, and
`Allowlist.IsEngineSurface` deliberately excludes `Tamma.Api`. *Honest scope:* `TAMMA001`'s injection
check is a closed denylist that does not name `IFeatureFlagProvider`, and its HTTP check only fires on
a statically-literal external host — it is the backstop, not the mechanical failure. Precedent for the
split: `GetAcceptanceRulesTool` (an `IToolExecutor` in `Tamma.Api.Services.AcceptanceRules`) and
`Allowlist.cs` L57–58 on `InlineToolLoopRunner`.

Only the 42-1 contract types (`IToolExecutor`, `ToolDescriptor`, `SecretRequirement`) stay in
`Tamma.Activities.LlmCall.Tools`; a `ToolDescriptor` never carries a `SecretRef` (42-4).

## Scope

1. **Provider abstraction.** `IFeatureFlagProvider` — `GetAsync` / `ListAsync` / `SetAsync` for a flag
   or a runtime-config key, plus one reference driver and a generic seam, mirroring the Git/AI provider
   pattern. The LLM sees one read tool and one write tool; the provider is chosen by config/binding.

2. **Operation granularity — DECIDED: split the family AND report per call.**
   *Corrected: an earlier draft said "recommend a read/write split as in 42-7" and left the choice to
   the implementer via an AC that accepted either. Both halves are now mandatory and separately
   testable.*

   | Operation | Executor | Per-call `PermissionClass` |
   |---|---|---|
   | `get` / `list` | `feature_flag_read` | `ReadOnly` |
   | `set` on a **non-prod** environment | `feature_flag_write` | `Mutating` |
   | `set` on a **prod** environment, or any flag the binding marks `kill_switch` | `feature_flag_write` | `Destructive` |

   - `feature_flag_read` declares `ReadOnly` / floor 70 and exposes only `get`/`list` in its
     `InputSchema`; it cannot reach `SetAsync`.
   - `feature_flag_write` implements 42-3's per-call seam
     `ToolInvocationFacts Describe(string argumentsJson)` → `{ PermissionClass, Operation, Target }`
     with `Target` = `<environment>:<flagKey>`, so an actor authorizes *this flag in this environment*,
     not "may flip flags".

3. **Environment is resolved, never asserted by the model.** The prod/non-prod discriminator comes from
   the **42-2 binding** (`ConfigJson`: the environment map + the `kill_switch` flag list), keyed by an
   `environmentKey` the model selects from a closed, binding-declared set. An `environmentKey` the
   binding does not declare, or a flag key absent from the binding's map, is **treated as prod** →
   `Destructive` (fail-safe), never as non-prod, and never free-text-matched.

4. **Secret binding.** `SecretRequirement(SecretPurpose.ApiKey, "flags/<provider>", Required)` —
   `SecretPurpose` being the `Tamma.Core`-sited enum 42-1 §0 relocates. Resolved by 42-4 to
   **`SecretRef.ForTenant(runTenantId, name)` in SaaS** and **`SecretRef.ForPlatform(name)` in
   single-user**. *Corrected: an earlier draft said "user-scoped in single-user" — there is no user
   scope. `SecretScope` has exactly `Platform` and `Tenant`, `SecretRef`'s constructor throws on either
   mismatch, and the sole user's ownership is `SecretMetadata.OwnerUserId` metadata.* `runTenantId`
   comes from the run context, never from tool config, arguments, or the model.

5. **No suspend.** `Suspends = false`. A flag flip is synchronous at every provider worth supporting; if
   a future driver is async it adopts 42-8B/42-7's `WaitForToolOperationActivity` rather than pretending
   the executor can suspend. *Corrected: the combined draft carried `Suspends = true` for "long deploys"
   — that belonged to the deploy half only.*

6. **Audit.** Every op emits 42-5 `TOOL.*` tagged `provider` / `flagKey` / `environment` / `operation`
   and, for a `set`, the **previous and new value** (both redacted through the same envelope) so a
   rollout is reconstructible. Never the credential.

## Acceptance Criteria

1. Two executors are registered; descriptors read through an **`IToolExecutor`-typed** reference
   (42-1's DIM caveat): `feature_flag_read` = `ReadOnly` / floor 70 / `Suspends=false`;
   `feature_flag_write` = `Destructive` (family max) / floor 100 / `Suspends=false`. Both declare
   `SecretRequirement(ApiKey, "flags/<provider>", Required)`.
2. `feature_flag_read` cannot mutate: its `InputSchema` enumerates only `get`/`list`, and an argument
   object naming `set` returns `Success = false` with **zero** calls on a spy `IFeatureFlagProvider`.
3. `feature_flag_write.Describe(argumentsJson)` is table-driven-tested: a `set` on a
   binding-declared non-prod environment → `Mutating`; on a binding-declared prod environment →
   `Destructive`; on a flag listed as `kill_switch` in **any** environment → `Destructive`; and each of
   {unknown `environmentKey`, missing `environmentKey`, flag key absent from the binding map, malformed
   JSON} → `Destructive` with `Target` set where derivable and `null` otherwise. `Target` equals
   `<environment>:<flagKey>`.
4. The model cannot downgrade the class: a test supplies `"environment": "production"` **and** an
   `environmentKey` the binding maps to staging, and asserts the class is derived from the binding's
   mapping alone (the free-text field is not read).
5. A prod `set` produces **no** `IFeatureFlagProvider.SetAsync` call and terminates the run with
   `AgentRunFailureCodes.ToolAuthorizationRequired`, the `ToolAuthorizationRequest` carrying
   `Operation = "set"` and `Target = "<prod-env>:<flagKey>"`. Asserted on **both** execution branches —
   `EnableParallelTools = false` (the default) and `true`.
6. Authorization is target-bound and single-use: after an `Authorize` for
   `(session, feature_flag_write, set, prod:checkout_v2)`, exactly one matching flip executes; a second
   identical flip re-gates and a flip of `prod:checkout_v3` re-gates.
7. Provider abstraction holds: a stub `IFeatureFlagProvider` drives `get → set → get` with the
   reference driver **not registered**, and the executors name no concrete vendor type.
8. Credential scoping: SaaS resolves `SecretRef.ForTenant(runTenantId, name)`, single-user resolves
   `SecretRef.ForPlatform(name)`; constructing a `Tenant`-scoped ref with a null tenant id throws; a
   grep-for-value test asserts the credential appears in no `ToolExecutionResult.Output`, no `TOOL.*`
   payload, and no captured log line.
9. A driver that throws yields `Success = false` + `TOOL.FAILED`; the test asserts `ExecuteAsync`
   **returns** rather than propagating (never-throw contract).
10. A `set`'s `TOOL.*` row carries the authorizing actor id and 42-3 decision id when it was gated, and
    the previous/new value pair in every case.

## Events

Reuses 42-5 `TOOL.INVOKED/SUCCEEDED/FAILED` with `flagKey` / `environment` / `operation` tags. No new
family, and no engine-side emission — everything here runs in `Tamma.Api`, which appends directly to
`IEventRepository`.

## Single-user vs SaaS

- **single-user:** the sole user's flag credential as a **platform-scoped** secret
  (`SecretRef.ForPlatform`) owned via `SecretMetadata.OwnerUserId`; prod authorizations route to the
  single orchestrator/user.
- **SaaS:** tenant-scoped credential and tenant-scoped binding (a tenant flips only the flags its
  `tenant_admin` bound); prod authorizations route to the tenant orchestrator/role, and the
  authorization bookmark is tenant-folded.

## Epic 41 consumers

`deployment-pipeline` (gradual rollout during promotion), **41-22** (incident kill-switch), 41-29
`infra` `TaskKind`.

## Dependencies

- **42-1** — `ToolDescriptor`; the `Tamma.Core`-sited `SecretPurpose` (§0). DIM caveats apply:
  read descriptors through an `IToolExecutor`-typed reference; a mocked executor returns **null**.
- **42-2** — the binding's `ConfigJson` carries the environment map and the `kill_switch` list; without
  it §3's fail-safe degrades to "everything is prod", which is safe but unusable.
- **42-3** — `Describe` + stage-2 argument-bound authorization on both branches, the
  `ToolAuthorizationRequired` code, and the engine-side gate.
- **42-4** — credential binding. *Corrected: an earlier draft called this "hard-blocked on the Epic 29
  reveal path". It is not — four runtime plaintext readers already ship and 42-4 generalizes them. The
  residual dependency is a non-null `ISecretAccessAuditor`; only `NullSecretAccessAuditor` is
  registered today.*
- **42-5** — `TOOL.*` audit.
- **`Tamma.Activities` holds no external credential** and carries the `TAMMA001` analyzer; no code from
  this story is added to it. This story adds **nothing** engine-side.
- **Epic 41 / 41-29** consumers.

## Risks

- **Stage-1 filter vs. max-class descriptor — settled in 42-3, and a hard prerequisite here.**
  `feature_flag_write`'s max **is** `Destructive`, so a stage-1 filter reading the raw descriptor max
  would never hand it to the agent and stage 2 would never fire. 42-3 Scope 1 now keys stage 1 on the
  **binding-resolved effective ceiling**, with `Destructive` as a stage-2 discriminator and a
  max-class tool with a non-empty ceiling still offered (42-3 AC1b). Treat that as a prerequisite:
  if it is reverted, this family is unreachable.
- **Environment mislabelling.** A flag whose binding maps it to the wrong environment escapes the gate.
  Mitigation: unknown/absent → prod (§3, AC3), and the binding is `tenant_admin`-owned and audited by
  42-2, not model-supplied.
- **Flag values can carry secrets.** A config-toggle provider stores arbitrary strings; the
  previous/new value pair in AC10 can therefore contain a credential. It must ride the 42-4/42-5
  redaction envelope, and note honestly that `ToolOutputHelper.RedactSecrets` is **pattern-based**
  (`sk-`, `AKIA`, `gh?_`, `glpat-`, `xox?-`, JWT, PEM, `Password=`) — an arbitrary value is not matched.
  Values above a configured length, or on a binding-declared `sensitive` key list, must be replaced
  wholesale rather than pattern-scrubbed.

## Estimated Effort

Medium. ~4–5 days — one provider abstraction + reference driver, the read/write split, `Describe`, the
binding-driven environment resolution, and the gating/redaction test matrix. No engine-side work.
