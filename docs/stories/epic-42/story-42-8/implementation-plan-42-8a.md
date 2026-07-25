# Implementation Plan — Story 42-8A: Feature-Flag / Config-Toggle Tool

## Reconciled scope — differs from the story file

**Epic 42 was reconciled against Epic 43 on 2026-07-25.** The verdict for 42-8A (with 42-7 / 42-8B / 42-9)
is **"Gating sections stripped. They declare capability and secrets; the catalog governs them."** The
deltas:

| Story file says | Reconciled |
|---|---|
| §2's per-call `PermissionClass` table and `ToolInvocationFacts Describe(string argumentsJson)` | **STRIPPED.** `Describe` was 42-3's seam; 42-3 is deleted. `ToolPermissionClass` no longer exists (42-1 rewritten; `ToolDescriptor` is `(RequiredSecret, Suspends)`). Epic 43's **Seam B** gates on an `ActionKey` derived from the tool name. |
| §2's **read/write executor split** | **KEPT — a capability boundary, not a gate.** `feature_flag_read` physically cannot reach `SetAsync`. Post-reconciliation this is the story's primary structural safety property, because it holds regardless of how any catalog row is configured. |
| **§3 — "the prod/non-prod discriminator comes from the 42-2 binding (`ConfigJson`: the environment map + the `kill_switch` flag list)"** | **42-2 is DELETED and nothing replaces `ConfigJson`.** Epic 43's `action_assignments` stores *policy* — a threshold plus three nullable columns (`Enforce`, `Enabled`, `AllowedRoles`) — and has **no config blob and no per-tool settings**. So the environment map has no home. Resolved by **D3**: it moves to deployment configuration (`IOptions`), platform-scoped. The *containment* rule survives verbatim (the model picks a key from a closed declared set; an undeclared key is refused); the *classification* half is gone. **This is a real capability reduction in SaaS — see G1.** |
| AC1's `ReadOnly`/floor 70, `Destructive`/floor 100 | **STRIPPED.** AC1 becomes: two executors registered, both declaring `SecretRequirement(ApiKey, "flags/<provider>", Required)`, both `Suspends = false`. |
| AC3 (`Describe` table), AC5 (`ToolAuthorizationRequired` + `ToolAuthorizationRequest`), AC6 (target-bound single-use authorization), AC10's "42-3 decision id" | **STRIPPED.** AC4 survives in the rewritten form of D3 (the model cannot select an undeclared environment); AC10's previous/new-value pair survives as an audit requirement. |
| The "Stage-1 filter vs. max-class descriptor" risk | **Gone** — it was about 42-3's stage-1 filter. Epic 43 records the same insight in its Seam B analysis, with credit. |

**Unchanged:** §1's provider abstraction, §4's secret binding, §5's `Suspends = false`, §6's audit, and the
"Where this code lives" siting rule.

## Scope & Deliverable

Two `IToolExecutor`s in `Tamma.Api` — `feature_flag_read` (`get`/`list`) and `feature_flag_write` (`set`) —
over an `IFeatureFlagProvider` abstraction with one reference driver and a generic seam. The read executor
publishes only `get`/`list` and cannot reach `SetAsync`. The write executor accepts an `environmentKey` from
a **closed, configuration-declared set**; an undeclared or missing key is refused before any provider call,
and a free-text `environment` field supplied by the model is never read. Both bind the provider credential
through 42-4 and never leak it. `Suspends = false` — a flag flip is synchronous, and no engine-side work is
added by this story at all. Every operation emits 42-5's `TOOL.*` trio with flag tags, including the
previous and new value for a `set`.

**This is greenfield.** Verified: `grep -rl "FeatureFlag\|feature_flag"` over `apps/tamma-elsa/src` returns
nothing. Interface, driver and configuration are all new.

## Pre-Reading

- `docs/stories/epic-42/story-42-8/42-8a-feature-flag-tool.md` — the story (**read the Reconciled scope table first**)
- `docs/stories/epic-42/story-42-8/42-8-feature-flag-deploy-control-tools.md` + `implementation-plan.md` — the split index and its plan (why 42-8A and 42-8B share nothing but the envelope)
- `docs/stories/epic-42/README.md` — the verdicts; "Where the code lives"; the families table
- `docs/stories/epic-43/README.md` — Seam B; §3 "one integer per action"; and **Storage** (`action_assignments`' actual columns — the evidence for the `ConfigJson` gap)
- `docs/stories/epic-42/story-42-1/implementation-plan.md` (D2/D8), `story-42-4/implementation-plan.md` (**D2/D3/D8 and G1 — the secret-name gap this story inherits**), `story-42-5/implementation-plan.md` (D2/D3/D5)
- `docs/stories/epic-42/story-42-7/implementation-plan.md` — D1/D2/D3/D4 are the same decisions, argued once; this plan does not re-derive them
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/IToolExecutor.cs:8`, `:33` — the never-throw contract; `LlmCallModels.cs:464-474` — `ToolExecutionResult`
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:753-766` — where the executors register
- `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs:286-292` — the catalog was removed from the engine
- `apps/tamma-elsa/src/Tamma.Activities.Guardrails/Allowlist.cs:16-17`, `:45-64`, `:57-58`
- **`apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ToolOutputHelper.cs:72-120`** — `RedactSecrets`, ≈10 regexes; **pattern-based only** (`sk-`, `AKIA`, `gh?_`, `glpat-`, `xox?-`, JWT, PEM, `Password=`). `Truncate` `:23`, `MaxOutputBytes` `:12`. `apps/tamma-elsa/src/Tamma.Core/Redaction/CredentialRedactor.cs:71` — `Clean(string?)`
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs:500` — `EnableParallelTools` defaults **false**, so the sequential branch is the default path

## Corrections to the story

- **V1 — §3's fail-safe no longer has anything to be safe *about*, and the story's own escape hatch is now
  the steady state.** The story says an unknown `environmentKey` is *"treated as prod → `Destructive`
  (fail-safe)"*, and its Dependencies warn that without 42-2 *"§3's fail-safe degrades to 'everything is
  prod', which is safe but unusable."* Post-reconciliation there is no `Destructive` to degrade to and no
  binding to read. The correct reading is now: an unknown `environmentKey` is a **refusal**, not a
  reclassification — `Success = false`, zero provider calls. That is strictly safer and, unlike
  "everything is prod", it is usable, because D3 gives the declared set a real home.
- **V2 — the risk note about `ToolOutputHelper.RedactSecrets` being pattern-based is verified and
  under-stated.** Confirmed at `:72-120`: ten regexes over known credential shapes. A feature-flag *value* is
  an arbitrary string, so a flag holding a bespoke token is matched by **none** of them. The story's
  mitigation (replace wholesale above a length threshold or on a declared `sensitive` key list) is correct
  and is now the **only** protection, since the `sensitive` list also lived in the deleted `ConfigJson` — it
  moves with the rest to D3's configuration.
- **V3 — `IFeatureFlagProvider` is not on `TAMMA001`'s injection denylist and would not trip it.** The
  denylist is a closed 13-entry list (`Allowlist.cs:45-64`) naming no flag type, and the HTTP check fires only
  on a statically-resolvable literal external host. The story says this; it is verified. Siting is settled by
  rule 1 and by the engine no longer hosting the catalog — **the analyzer is a backstop, not the enforcement.**

## Design Decisions

- **D1 — everything lives in `Tamma.Api`**, package `Tamma.Api.Services.Tools.FeatureFlags`, registered at
  `Program.cs:753-766`. **This story adds nothing engine-side** — it is the only Wave-3 family with no
  suspend path, which is why the split index sequences it before 42-8B. Reasons and precedent: identical to
  42-7 D1; see that plan.
- **D2 — the read/write split is the primary safety property.** `feature_flag_read` publishes an
  `InputSchema` enumerating only `get`/`list` and has no code path to `SetAsync`; an argument object naming
  `set` returns `Success = false` with **zero** calls on a spy `IFeatureFlagProvider` — asserted on the spy,
  not the message. With per-call classification stripped, this is the guarantee that does not depend on a
  catalog row being authored correctly.
- **D3 — the environment map, the kill-switch list and the sensitive-key list move to deployment
  configuration, platform-scoped.** `ConfigJson` is gone with 42-2 and Epic 43's `action_assignments` does
  not model per-tool settings. Replacement:

  ```
  FeatureFlags:Provider                     — the driver key
  FeatureFlags:Environments:<key>:IsProd    — the closed declared set
  FeatureFlags:KillSwitches:[]              — flag keys that are kill-switches in any environment
  FeatureFlags:SensitiveKeys:[]             — flag keys whose values are replaced wholesale in audit (V2)
  FeatureFlags:MaxAuditedValueLength        — above this, replace wholesale (V2)
  ```

  Bound via `IOptions<FeatureFlagOptions>` and validated at startup (fail-loud on a malformed map, the
  `PromptFileLoader` posture). The **containment rule survives verbatim**: the model selects an
  `environmentKey` from this declared set; an undeclared or missing key is refused (V1); a free-text
  `environment` field is **never read**. What is lost is per-tenant maps — see G1.
- **G1 — per-tenant environment maps are a recorded capability gap, not a silently-dropped feature.** In
  SaaS, 42-2 would have let each `tenant_admin` declare their own environments and kill-switches; D3's
  configuration is deployment-wide. Consequences: a single-user deployment is **fully served**; a SaaS
  deployment can offer flag control only over a platform-declared environment set, and cannot let tenant A
  and tenant B have different prod definitions. **This plan does not invent a per-tenant config store** —
  that is exactly the duplication the reconciliation deleted. Options for the owner: extend Epic 43's storage
  with a per-action config blob; revive a minimal tool-config store as an Epic 43 story; or accept
  platform-scoped flags permanently. **Open product question**, flagged in Blocks / Blocked by. It is the
  same gap 42-4 records as G1 (secret names) and 42-8B / 42-9 record for target maps and endpoint bindings —
  **four stories, one missing store.**
- **D4 — secret binding.** `SecretRequirement(SecretPurpose.ApiKey, "flags/<provider>", Required)`, resolved
  by 42-4's `IToolSecretProvider`, which constructs the ref from the run's tenant identity:
  `SecretRef.ForTenant(runTenantId, name)` in SaaS, `SecretRef.ForPlatform(name)` in single-user. There is no
  user scope (`SecretScope` has exactly `Platform` and `Tenant`; `SecretRef`'s ctor throws on either
  mismatch; the sole user's ownership is `SecretMetadata.OwnerUserId`). `runTenantId` comes from the run
  context only. Fetched immediately before the vendor call, used once, dropped; scrubbed **by value** from
  anything `ExecuteAsync` returns (42-4 D8).
- **D5 — `Suspends = false`, and it stays false.** A flag flip is synchronous at every provider worth
  supporting. If a future driver is async it adopts 42-7/42-8B's `WaitForToolOperationActivity` rather than
  pretending an executor can suspend — an `IToolExecutor` cannot, because the loop runs inside a blocking
  `POST /api/v1/llm/call` with no `ActivityExecutionContext` and no bookmark to create.
- **D6 — audit carries the previous and new value, under V2's wholesale-replacement rule.** 42-5's trio with
  `provider` / `flagKey` / `environment` / `operation`, plus `previousValue` / `newValue` for a `set` so a
  rollout is reconstructible. Both values go through `ToolOutputHelper.RedactSecrets` **and** the D3
  wholesale replacement (declared sensitive key, or over the length cap) — because pattern redaction alone
  provably does not match an arbitrary flag value (V2). Never the credential.
- **D7 — the provider abstraction names no vendor type in the executors.** `IFeatureFlagProvider`
  (`GetAsync`/`ListAsync`/`SetAsync`) plus one reference driver and a generic seam, mirroring the Git and AI
  provider abstractions. The LLM sees one read tool and one write tool; the provider is chosen by
  configuration.

## Implementation Steps

1. **Precondition gate.** 42-1 (`ToolDescriptor` + the relocated `SecretPurpose`), 42-4
   (`IToolSecretProvider`), 42-5 (`TOOL.*`) landed.
2. **CREATE `Tamma.Api/Services/Tools/FeatureFlags/FeatureFlagOptions.cs`** — D3's configuration shape with
   startup validation (fail-loud).
3. **CREATE `.../FeatureFlags/IFeatureFlagProvider.cs` + the reference driver + the generic seam** (D7).
4. **CREATE `.../FeatureFlags/FeatureFlagReadTool.cs` + `FeatureFlagWriteTool.cs`** (D2/D3/D4/D5/D6) —
   descriptors, schemas, environment-key containment, fail-closed refusals, secret resolution immediately
   before the vendor call, by-value scrubbing at the `ExecuteAsync` boundary, `Success = false` on every
   driver throw.
5. **MODIFY `Tamma.Api/Program.cs:753-766`** — register both executors, the options binding and the driver.
6. **CREATE the test suites** (Test Plan). Author the Epic 43 catalog entries for `tool:feature_flag_read`
   and `tool:feature_flag_write` as **admin data**, not code.
7. **Finish:** full `dotnet test`; `dotnet ef migrations has-pending-model-changes` clean; confirm **no**
   file was added to `Tamma.Activities` or `Tamma.ElsaServer`.

## Data & Migrations

None. Secrets are Epic 29's `secrets` table; events ride `IEventRepository` → `domain_events`; D3's map is
configuration.

## Events

Reuses 42-5's `TOOL.INVOKED`/`SUCCEEDED`/`FAILED` with `provider` / `flagKey` / `environment` / `operation`
tags, plus `previousValue` / `newValue` on a `set` (D6). **No new family, and no engine-side emission** —
everything here runs in `Tamma.Api`, which appends directly to `IEventRepository`.

## Test Plan

- **`FeatureFlagDescriptorTests`** — both executors registered; descriptors read through an
  **`IToolExecutor`-typed** reference declare `SecretRequirement(ApiKey, "flags/<provider>", Required)` and
  `Suspends == false`. **Covers reconciled AC1.**
- **`FeatureFlagReadCannotMutateTests`** (D2) — `InputSchema` enumerates only `get`/`list`; an argument
  object naming `set` returns `Success = false` with **zero** calls on a spy `IFeatureFlagProvider`.
  **Covers AC2.**
- **`FeatureFlagEnvironmentContainmentTests`** (D3/V1, replacing the stripped AC3/AC4) — a `set` against a
  declared environment reaches the provider; an **undeclared** `environmentKey`, a **missing**
  `environmentKey`, a flag key absent from the declared set, and malformed JSON each yield `Success = false`
  with zero provider calls; and — the surviving half of AC4 — supplying `"environment": "production"` as
  free text alongside a declared `environmentKey` for staging does **not** change which environment is
  touched, because the free-text field is never read (asserted on the spy's received arguments).
- **`FeatureFlagProviderAbstractionTests`** (D7) — a stub provider drives `get → set → get` with the
  reference driver **not registered**; a reflection assertion that the executors name no concrete vendor
  type. **Covers AC7.**
- **`FeatureFlagCredentialScopingTests`** (D4) — SaaS resolves `SecretRef.ForTenant(runTenantId, name)`,
  single-user `SecretRef.ForPlatform(name)`; a `Tenant`-scoped ref with a null tenant id throws; a
  grep-for-value test using a **pattern-non-matching** 40-char token asserts it appears in no
  `ToolExecutionResult.Output`, no `TOOL.*` payload and no captured log line. **Covers AC8.**
- **`FeatureFlagNeverThrowTests`** — a driver that throws yields `Success = false` + `TOOL.FAILED`, and
  `ExecuteAsync` **returns** rather than propagating. Asserted on **both** execution branches
  (`EnableParallelTools` `false` — the default — and `true`). **Covers AC9.**
- **`FeatureFlagAuditValueTests`** (D6/V2) — a `set` emits `previousValue`/`newValue`; a value on the
  declared `SensitiveKeys` list and a value over `MaxAuditedValueLength` are each **replaced wholesale**, not
  pattern-scrubbed; and a control case asserts that a bespoke 40-char token in a flag value is **not** caught
  by `ToolOutputHelper.RedactSecrets` alone — pinning why the wholesale rule exists. **Covers AC10 (the
  surviving half).**

## Definition of Done

| AC (reconciled) | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — two executors, secret requirement, `Suspends = false` | 4, 5 | `FeatureFlagDescriptorTests` |
| 2 — read executor cannot mutate | 4 (D2) | `FeatureFlagReadCannotMutateTests` (spy at zero calls) |
| 4 — the model cannot select an environment it was not given | 2, 4 (D3/V1) | `FeatureFlagEnvironmentContainmentTests` |
| 7 — provider abstraction holds | 3, 4 (D7) | `FeatureFlagProviderAbstractionTests` |
| 8 — credential scoping; nothing leaks | 4 (D4) | `FeatureFlagCredentialScopingTests` |
| 9 — never-throw, both branches | 4 | `FeatureFlagNeverThrowTests` |
| 10 — previous/new value recorded, sensitive values replaced wholesale | 4 (D6) | `FeatureFlagAuditValueTests` |
| ~~3 (`Describe`), 5 (`ToolAuthorizationRequired`), 6 (target-bound single-use), 10's decision id~~ | — | **STRIPPED — Epic 43 governs; see Reconciled scope** |

## Blocks / Blocked by

- **Blocked by — 42-1, 42-4, 42-5.** All hard, all Wave 1.
- **Blocked by — Epic 43 for governance, not for shipping.** The capability is complete and safe without a
  catalog row: the read/write split holds unconditionally and every `set` is audited. What is absent until
  Epic 43 Story 9 lands is any gate on a prod flip. **State this when the family ships** — do not enable
  `feature_flag_write` in a deployment before Seam B is live.
- **Not blocked by — 42-7 or 42-8B.** This story adds nothing engine-side, which is precisely why the split
  index sequences it before 42-8B: it is not gated on the wait-activity / bookmark / callback chain those two
  share.
- **Open product question — G1.** Who owns per-tenant tool configuration now that 42-2's `ConfigJson` is
  gone? The same gap appears in **42-4** (secret names), **42-8B** (deploy target maps) and **42-9**
  (endpoint bindings, where it is most severe). Four stories, one missing store — **it should be decided
  once, at epic or Epic 43 level, not four times inside family plans.**
- **Blocks — Epic 41 consumers:** `deployment-pipeline` (gradual rollout during promotion), **41-22**
  (incident kill-switch), 41-29's `infra` `TaskKind` agent path.

## Risks & Mitigations

- **Flag values can carry secrets, and pattern redaction provably does not catch them (V2).** A
  config-toggle provider stores arbitrary strings; D6 records previous and new values. Mitigation: the
  wholesale-replacement rule on a declared sensitive-key list and a length cap, with
  `FeatureFlagAuditValueTests`' control case pinning that `RedactSecrets` alone is insufficient — so the rule
  cannot be quietly dropped as redundant.
- **Environment mislabelling.** A flag mapped to the wrong environment escapes any future gate. Mitigation:
  the map is deployment configuration validated fail-loud at startup (D3), never model-supplied; an
  undeclared key is refused rather than reclassified (V1). Note the residual honestly: with 42-2 gone the map
  is no longer per-tenant-owned and audited (G1).
- **No gate ships with this story.** Stripping the gating sections means a prod flip is auditable but
  ungoverned until Epic 43's Seam B and a catalog row exist. Mitigation: stated in Blocks / Blocked by and in
  the DoD; the read/write split means the *read* half is safe to enable immediately regardless.
- **Siting drift.** Mitigation: D1's rule, with the honest note (V3) that `TAMMA001` would not mechanically
  catch a misplaced driver.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1–2 | Precondition gate + `FeatureFlagOptions` with fail-loud startup validation | 0.5 |
| 3 | `IFeatureFlagProvider` + reference driver + generic seam | 1.0 |
| 4 | Both executors (split, containment, secret binding, redaction, audit values) | 1.25 |
| 5–6 | DI wiring + seven test suites | 1.0 |
| 7 | Full green + catalog-row authoring notes | 0.25 |
| **Total** | | **4.0** |

Story estimate: ~4–5 days. Stripping the gating sections removed `Describe` and the authorization test
matrix (roughly a day); D3's configuration shape and its startup validation add most of that back, because
the environment map still has to exist somewhere — it simply moved from a store 42-2 would have built to
configuration this story now owns.
