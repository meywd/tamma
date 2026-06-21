# Epic 32 — Revised Agent Architecture (Design of Record)

**Status:** Authoritative. Supersedes the persona/agent and credential portions of
`docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` where they conflict.
**Date:** 2026-06-20
**Author:** Lead architect (synthesis of one step-audit + four design analyses, reconciled)
**Scope:** Agents, personas, custom agents, per-tenant enablement, the provider cost-pricing
entity, the tamma-api `call-LLM` mediation endpoint, and the cross-cutting "steps never call
external APIs directly" rule. Companion re-plan: `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md`.

---

## 0. The locked model (non-negotiable source of truth)

These seven rules are the architecture. Everything else in this document derives from them.

1. **A workflow STEP MUST NEVER call an external API/provider directly.** A step that needs an LLM
   call (or any external integration) calls an **internal tamma-api endpoint**, passing params.
   `Tamma.Api` is the single place that holds credentials, decides if the call is allowed, performs
   the external call, and meters it. The Elsa engine (`Tamma.ElsaServer`) **never holds a provider
   key and never hits an external endpoint.**

2. **The LLM path is mediated by a `call-LLM` endpoint.** The step calls a tamma-api `call-LLM`
   endpoint with `{ tenantId, agentId/persona, role, prompt, params }`. That endpoint, in order:
   (a) **gates** (mode/SaaS auth, entitlement, budget);
   (b) **resolves the agent config**;
   (c) **resolves the credential** BYOK→platform from the Epic 29 cabinet;
   (d) **makes the provider call**;
   (e) **meters usage** (cost from provider pricing; price = markup when platform, none when BYOK);
   (f) **returns result + `credentialSource`**.

3. **PROVIDER is a system entity carrying COST PRICING** (per-token, per model) — the platform's
   cost basis. It is the *cost* primitive; it is not the *sell* price.

4. **PERSONA = a system-defined named AGENT** (Claude, Gemini, CodeGPT, …) = a preset system
   provider + model + config (params/tools). Its **PROMPTS come from the existing role/system prompt
   store (Epic 27)** — personas have **NO custom prompts**. Personas work for **ALL roles**, not just
   dev.

5. **CUSTOM AGENT: custom prompts ⇔ custom agent.** A tenant that wants different prompts creates a
   custom (private) agent carrying its own prompts + config.

6. **ENABLEMENT is PER-TENANT.** The tenant enables which personas/agents exist for it; its users
   simply use Tamma with what's enabled. There is **NO per-user enablement layer.**

7. **BYOK is PER-TENANT, PER-PROVIDER.** A tenant sets a key for a provider → used for that tenant's
   calls (`credentialSource=byok`, no platform markup). Otherwise the platform key is used + markup
   priced off the provider's cost.

**Mode reminder (from CLAUDE.md):** "principal" = the tenant in SaaS mode, the sole user in
single-user mode. Every entity below is keyed by `(tenantId XOR userId)` exactly like
`prompt_overrides` and `AgentRoleSelection`. There is no per-user layer in SaaS for prompts,
enablement, or agent selection.

---

## 1. Cross-cutting principle: steps never call external APIs directly

### 1.1 The principle

The Elsa engine is a **deterministic orchestrator that holds no secrets**. Any activity that needs an
external effect (LLM, git platform, CI dispatch, Slack, billing) **delegates over HTTP to a
`Tamma.Api` endpoint** through `TammaApiClient` (the engine→API callback already used for
agent-resolve, budget, diagnostics, and provider-session). The credential-holding code, the
authorization decision, the external HTTP call, and the metering/audit emission **all live in
`Tamma.Api`**.

The reference for "right" already exists in the tree three times and should be the template:
- **`TammaApiClient`** — engine→API HTTP delegation with `Authorization: Bearer <Tamma:ApiToken>`
  (via `TammaEngineAuthHandler`) + `X-Tenant-Id`. Already routes agent-resolve / budget /
  diagnostics / provider-session.
- **`QueueWelcomeEmailActivity`** — the **outbox pattern**: the step writes intent to
  `platform_email_outbox`; an out-of-band sender (in the API) holds the SMTP credential and performs
  transport. This is the model for fire-and-forget external effects.
- **`TriggerCIActivity`** — already POSTs to an internal `Engine:CallbackUrl/api/engine/trigger-ci`;
  it holds no CI-vendor credential.

> **Co-hosting is NOT compliance.** Today many activities resolve a credential-holding *service from
> the same DI container* (because, in the current single-process deploy, the engine activities and
> the API services are co-hosted). Rule #1 forbids this: the step must call an internal endpoint
> **over the wire**, not resolve an injected vendor service. This matters the moment the engine runs
> as per-tenant dedicated compute (the Cranl path), because then the token would have to be pushed
> into the engine process — exactly what we must prevent.

### 1.2 Audit table — every externally-acting activity

Audited against `apps/tamma-elsa/src/Tamma.Activities` on `feat/exec-wave-02`. "Direct?" = does the
activity itself perform the external HTTPS call and/or hold/transit the credential in the engine
process. "Compliant" = already routes through an internal `Tamma.Api`/engine-callback endpoint and
holds no external credential.

| Activity | External target | How it reaches it today | Holds/transits a key in-engine? | Verdict |
|---|---|---|---|---|
| `LlmCall/CallLlmActivity` | Anthropic / OpenAI | `httpClient.PostAsync($"{baseUrl}/v1/messages")`; reads `Anthropic:ApiKey` directly (line ~601) | **YES** | **VIOLATION — worst** |
| `LlmCall/CallLlmInlineActivity` | Anthropic / OpenAI | builds body + `PostAsync(.../v1/messages` & `/v1/chat/completions`); key via `IProviderCredentialResolver` → `config.ApiKey` | **YES** | **VIOLATION — primary path (~1592 LOC, 22 parent workflows)** |
| `AI/ClaudeAnalysisActivity` (Mentorship) | Anthropic | 3-way branch: mock → engine-callback → **direct `CallClaudeApi` reading `Anthropic:ApiKey`** | **YES (fallback path)** | **VIOLATION (easily missed)** |
| `TDD/WriteTestsActivity` | Anthropic | mock → engine-callback → **`CallLlm` via `CreateClient("anthropic")` (keyed handler)** | **YES (fallback path)** | **VIOLATION (fallback)** |
| `TDD/WriteImplementationActivity` | Anthropic | same 3-way branch | **YES (fallback path)** | **VIOLATION (fallback)** |
| `TDD/AnalyzeCodeActivity` | Anthropic | same 3-way branch | **YES (fallback path)** | **VIOLATION (fallback)** |
| `TDD/ApplyRefactoringActivity` | Anthropic | same 3-way branch | **YES (fallback path)** | **VIOLATION (fallback)** |
| `ADL/ApplyReviewFixesActivity` | Anthropic | same 3-way branch (`PostAsJsonAsync("/v1/messages")`) | **YES (fallback path)** | **VIOLATION (fallback)** |
| `Debug/AIDiagnosisActivity` | Anthropic | engine-callback → **direct `/v1/messages`** (no simulated fallback) | **YES (fallback path)** | **VIOLATION (fallback)** |
| `ADL/CreateBranchActivity` | Git platform | `IGitHubIntegrationService` (impl + `GitHub:Token` in `Tamma.Api`; **unregistered/null in engine**) | only if co-hosted | **VIOLATION-by-co-hosting** |
| `ADL/CreatePullRequestActivity` | Git platform | `IGitHubIntegrationService` | only if co-hosted | **VIOLATION-by-co-hosting** |
| `ADL/MergePullRequestActivity` | Git platform | `IGitHubIntegrationService` | only if co-hosted | **VIOLATION-by-co-hosting** |
| `ADL/UpdateIssueStatusActivity` | Git platform | `IGitHubIntegrationService` | only if co-hosted | **VIOLATION-by-co-hosting** |
| `ADL/AnalyzeReviewActivity` | Git platform | `IGitHubIntegrationService` (read PR comments) | only if co-hosted | **VIOLATION-by-co-hosting** |
| `AgentDispatch/DispatchAgentWorkflowActivity` / `GitHubActionsExecutor` | GitHub Actions | `IGitHubActionsClient` (`OctokitGitHubActionsClient` in API; `NullGitHubActionsClient` in engine) | only if co-hosted | **VIOLATION-by-co-hosting** |
| `AgentDispatch/MonitorAgentWorkflowActivity` / `CollectAgentResultsActivity` | GitHub Actions | `IGitHubActionsClient` | only if co-hosted | **VIOLATION-by-co-hosting** |
| `Integration/SlackActivity` | Slack | `IIntegrationService` (impl + Slack token in API; unregistered in engine) | only if co-hosted | **VIOLATION-by-co-hosting (low blast radius)** |
| `Testing/TriggerCIActivity` | CI (internal) | POSTs to `Engine:CallbackUrl/api/engine/trigger-ci` | **NO** | **Compliant (formalize under `/api/v1`)** |
| `TenantLifecycle/QueueWelcomeEmailActivity` | SMTP | outbox row → out-of-band `OutboxSmtpSender` (API) | **NO** | **Compliant (reference pattern)** |
| `AgentDispatch/WebhookSignalRegistry` | (inbound) | in-process bookmark wakeup from API-received webhook | n/a | **Not a violator (inbound)** |
| (future) Billing / Stripe | Stripe | none yet (Epic 35 unbuilt) | n/a | **Enforce by design** |

> **Correction to prior analysis:** the LLM violator set is NOT just `CallLlm*` + `ClaudeAnalysis`.
> The TDD and ADL activities (`WriteTests`, `WriteImplementation`, `AnalyzeCode`, `ApplyRefactoring`,
> `ApplyReviewFixes`) and `AIDiagnosis` each carry a **direct keyed LLM fallback** (`CallLlm` /
> `CallClaudeApi` / direct `/v1/messages`) behind the engine-callback branch. Their engine-callback
> mode is acceptable, but the direct fallback must be removed and routed through `call-LLM`. **Nine
> in-engine direct-LLM callers total**, not three.

### 1.3 Prioritized violator list

- **P0 — active in-engine key holders (LLM):** `CallLlmInlineActivity` (primary), `CallLlmActivity`
  (worst single offender — reads `Anthropic:ApiKey` directly), then `ClaudeAnalysisActivity`,
  `WriteTestsActivity`, `WriteImplementationActivity`, `AnalyzeCodeActivity`,
  `ApplyRefactoringActivity`, `ApplyReviewFixesActivity`, `AIDiagnosisActivity` (all have a direct
  keyed fallback). These are the metering chokepoint and the only steps that, in any deploy topology,
  put a live external key in the engine. **Fixed by Epic 32 now.**
- **P0/P1 — git-platform writes + agent-dispatch:** `CreateBranch`, `CreatePullRequest`,
  `MergePullRequest`, `UpdateIssueStatus`, `AnalyzeReview`, `DispatchAgentWorkflow` /
  `GitHubActionsExecutor`, `Monitor`/`CollectAgentResults`. High the moment the engine is not
  co-hosted with `Tamma.Api`: a mis-scoped platform token = cross-tenant write/merge. **Follow-up
  epic (see §6).**
- **P2 — Slack:** `SlackActivity`. Token-holding but read-no-tenant-data, low blast radius.
  **Follow-up epic.**
- **Formalize-only:** `TriggerCIActivity` (already internal). **Enforce-by-design:** Stripe/billing
  (Epic 35).

---

## 2. The tamma-api `call-LLM` endpoint (the LLM mediation)

### 2.1 Route, auth, ownership

```
POST /api/v1/llm/call        (internal, engine-only)
```
- **Auth:** same plane as the other `TammaApiClient` callbacks — `Authorization: Bearer
  <Tamma:ApiToken>` (via `TammaEngineAuthHandler`) + `X-Tenant-Id`. Missing/invalid bearer → **401**.
- **Home:** new `Tamma.Api/Endpoints/LlmCallEndpoints.cs`, delegating to `IManagedAgent.RunAsync`
  (Story 32-5, `Tamma.Api/Services/Agents/`).

### 2.2 Request (`LlmCallRequest`)

```jsonc
{
  "tenantId":   "guid|null",        // null => single-user/platform scope (also from X-Tenant-Id)
  "agentId":    "guid|null",        // explicit custom/persona agent; else resolved by role
  "persona":    "string|null",      // system persona name (claude/gemini/codegpt) — preset provider+model+config
  "role":       "string",           // one of the 8 valid roles — drives Epic 27 prompt resolution
  "action":     "string|null",      // role+action prompt key (Epic 27)
  "phase":      "string|null",      // workflow phase for ResolveForPhaseAsync (32-2)
  "prompt":     "string",           // task/user prompt (variables merged server-side)
  "variables":  { },                // template vars for Epic 27 render
  "model":      "string|null",      // optional model override (clamped to persona/agent allowance)
  "tools":      ["name", …] | null, // requested tools (intersected with the agent's allowed set)
  "enableToolLoop": false,
  "toolLoopConfig": { } | null,
  "params":     { "maxTokens": 4096, "temperature": 0.7, "budgetCapUsd": 0.0 },
  "correlationId": "string"         // workflow instance id — ties run to audit + outcome
}
```

### 2.3 Response (`LlmCallResponse`, success — HTTP 200)

Superset of today's `NormalizedLlmResponse` + the 32-5 `AgentRunResult` fields. **`credentialSource`
is returned; the key is NEVER returned.**

```jsonc
{
  "success": true,
  "text": "…assistant response…",
  "usage": { "promptTokens": 0, "completionTokens": 0, "totalTokens": 0,
             "toolLoopTokens": 0, "toolLoopTurns": 0, "toolLoopExhausted": false },
  "credentialSource": "byok" | "platform",   // from ProviderCredential.Source (32-3) — never the key
  "providerUsed": "anthropic",                 // the chain entry that succeeded
  "modelUsed": "claude-sonnet-4-…",
  "cost": { "providerCostUsd": 0.0,            // IProviderPricingService.Compute — provider cost basis (§4)
            "priceUsd": 0.0,                   // markup applied when platform; 0 token-price when byok (Epic 34-5)
            "currency": "USD" },
  "toolCalls": [ { "toolName": "…", "success": true, "durationMs": 0 } ],
  "agentId": "guid", "agentVersion": 3, "role": "implementer",
  "correlationId": "…", "durationMs": 0
}
```

### 2.4 Error / gating semantics (fail-closed)

The endpoint always returns a **typed, key-free body** — never a leaked provider error or key.

- **HTTP 200 with `success:false`** for *expected execution failures*, so the engine's
  provider-chain/retry logic stays intact. `httpStatusCode` MUST be preserved so the engine's
  `RetryCheck` (429/502/503/504/0 → retry) and circuit breaker keep working:
  ```jsonc
  { "success": false,
    "failureCode": "PROVIDER_ERROR" | "PROVIDER_CREDENTIAL_UNAVAILABLE"
        | "BUDGET_EXCEEDED" | "LOOP_EXHAUSTED",
    "failureReason": "…key-free message…", "httpStatusCode": 429,
    "credentialSource": "platform", "providerUsed": "anthropic",
    "usage": { …accrued-before-failure… } }
  ```
- **HTTP 400 `SAAS_PROVIDER_NOT_ALLOWED`** when 32-4's `ISaaSProviderGate` denies a CLI-token
  provider in SaaS (selection/execution backstop).
- **HTTP 403** when SaaS auth/entitlement (32-4) rejects the tenant.
- **HTTP 401** when the engine bearer token is absent/invalid.
- **Fail-closed rule:** if the credential, gate, or budget cannot be evaluated, **deny** — never
  silently call the provider with an empty/wrong key. Preserves the existing
  `PROVIDER_CREDENTIAL_UNAVAILABLE` (`retryable:false, severity:High`) and the budget/circuit-breaker
  "fail closed → deny" guarantees. (Consistent with `feedback_resolution_no_empty_fallback`: never
  fall back to an empty/plain credential or prompt.)

### 2.5 What `CallLlmInlineActivity` becomes — a thin client

`CallLlmInlineActivity` collapses from ~1592 lines to a ~80-line shim that **owns no provider logic,
no key, no HTTP-to-provider, no tool loop**:

- **Sends** an `LlmCallRequest` to `POST /api/v1/llm/call` via a **new
  `TammaApiClient.CallLlmAsync(LlmCallRequest, tenantId, ct)`** (following the existing
  `PostAsync<T>` + `AddTenantHeader` + `RecordHealthAsync` pattern). It maps its current `Input<>`
  props (`InputJsonProp`, `ProviderNameProp`, `SystemPromptProp`, `ToolsJsonProp`,
  `AttemptNumberProp`, `EnableToolLoopProp`, `ToolLoopConfigJsonProp`, `TenantIdProp`) into the request.
- **Receives** `LlmCallResponse`, then writes the **same workflow variables it writes today** so the
  retry loop, success/failure check, and output builders in `LlmCallWorkflow.cs` are unchanged:
  `LastDiagnostic` (a `ProviderAttemptDiagnostic` carrying `CredentialSource`, `HttpStatusCode`,
  token counts), `LastResponse` (a `NormalizedLlmResponse`), and
  `ToolLoopTokens`/`ToolLoopTurns`/`ToolLoopExhausted`.
- **Provider-chain & retry semantics stay at the workflow boundary, not in the activity.** The
  activity is still invoked once per provider per attempt inside `BuildRetryLoop` →
  `ForEach<provider>`. The endpoint runs the tool loop server-side per call; the activity just
  propagates `success`/`httpStatusCode` into `LastDiagnostic` so `RetryCheck` /
  `SkipIfSucceeded` / circuit-breaker advance the chain exactly as today. `enableToolLoop` +
  `toolLoopConfig` are passed through to the endpoint instead of executed locally.
- **Removed from the engine:** the injected provider-side deps (`IHttpClientFactory`,
  `IContentSanitizer`, `IToolExecutorRegistry`, `IToolCallValidator`, `ContextCompactor`,
  `ToolLoopEventEmitter`, `ParallelToolExecutor`, and crucially **`IProviderCredentialResolver`** —
  the engine no longer resolves keys).

`CallLlmActivity` becomes a thin client the same way **or is deleted** in favour of the inline path
(it is the most severe rule-1 violator). The TDD/ADL/Debug/Mentorship activities (§1.2) have their
direct-LLM fallback **deleted** and route through `call-LLM` (or keep only their engine-callback
mode, which itself terminates at the mediated path).

### 2.6 Where credential resolution / provider call / metering / gating now live

All of it moves into **`Tamma.Api`**, composed by **`ManagedAgent.RunAsync` (32-5)**, invoked by the
`/api/v1/llm/call` handler. Composition order is the locked rule #2 sequence:

1. **Gate (32-4)** — `ISaaSProviderGate.InspectAsync` (`Tamma.Api/Services/Security/`) + SaaS
   auth/entitlement. CLI-token providers excluded in SaaS; unknown providers denied fail-closed.
2. **Resolve agent config (32-2)** — `IManagedAgentResolver`/`AgentResolverService` resolves
   persona/custom-agent → provider + model + params + allowed tools, **after applying the per-tenant
   enablement gate (§5)**.
3. **Resolve credential BYOK→platform (32-3)** — the **cabinet-backed
   `DefaultProviderCredentialResolver`** (`Tamma.Api/Services/Providers/`), which can reach the Epic
   29 cabinet (`ITenantProviderKeyReader`, runtime secret resolver). **This is the canonical home.**
   The engine-side `ConfigPlatformProviderCredentialResolver` (platform-only, no BYOK leg) becomes
   vestigial and is removed from the call path; the engine no longer resolves credentials at all.
4. **Render prompt (Epic 27)** — tenant→system→error resolution. Personas have **no** custom prompts
   (prompt comes from Epic 27 keyed `(principal, role, action)`); custom agents carry their own
   prompts. **Never fall back to empty/plain** (fail-loud).
5. **Provider call** — the extracted tool loop (`IInlineToolLoopRunner`/`InlineToolLoopRunner`, 32-5
   AC3, moved verbatim from `CallLlmInlineActivity.AgenticToolLoop`) makes the actual external HTTPS
   call **inside `Tamma.Api`**, using the plaintext key request-scoped (set on the header, dropped
   after — 32-3 AC5). The sanitizer/registry/validator/compactor are injected here, in the API process.
6. **Meter** — `IProviderPricingService.Compute(provider, model, in, out)` gives the **provider cost
   basis** (rule #3); Epic 34-5's markup engine derives `priceUsd` (markup when
   `credentialSource==platform`, none when `byok` — rule #7); 32-9 emits the usage record consumed by
   Epic 35 billing / Epic 36 analytics.
7. **Return** `text`, `usage`, `credentialSource`, `providerUsed`, `cost` + the `AgentRunResult` fields.

DCB events (`AGENT.CREDENTIAL_RESOLVED.SUCCESS`/`DENIED`, `AGENT.PROVIDER.GATED`,
`AGENT.RUN.STARTED/SUCCESS/FAILED`) are emitted from `Tamma.Api`, where the tenant `IEventRepository`
and the cabinet live — not from the engine's optional-sink path.

> **Why the inline tool loop lives in `Tamma.Activities` but runs in `Tamma.Api`:** `Tamma.Api`
> references `Tamma.Activities`, so the extracted `InlineToolLoopRunner` can be shared verbatim
> (no fork — 32-5 AC3). The *provider HTTP call* executes in the API process, where the key is
> resolved; the engine activity never touches the runner.

---

## 3. The agent model: persona / custom agent / enablement

### 3.0 The core reframe

The locked model redefines two words that the shipped 32-1/drafted 32-12 used differently:

| Term | 32-1/32-12 as built/drafted | Locked model (rules 4–6) |
|---|---|---|
| **Public agent** | per-role `tamma-<role>`, role IS its identity, single provider chain | a **named PERSONA** (claude/gemini/codegpt) that PRESETS provider+model+config, usable across **ALL roles**; role is NOT its identity |
| **Persona (32-12)** | a style/tone overlay within one role (`atlas`/`nova` in `reviewer`) | the **system agent itself** — the preset provider+model+config; prompts from Epic 27, **no custom prompts** |
| **Custom agent** | any private `Agent` (config blob, role-keyed) | private agent whose differentiator is **custom prompts** (custom prompts ⇔ custom agent) |
| **Selection** | per-`(principal,role)` pick of any visible agent | per-`(principal,role)` pick, **constrained to the tenant's enabled set** |
| **Enablement** | (missing) | a **per-tenant** layer: which personas/agents exist for a tenant |

**Biggest structural change:** public agents stop being per-role `tamma-<role>` and become
**cross-role named personas**. Role becomes a *selection-time* concern (which persona serves which
role for a principal), not a baked-in attribute of the public agent.

### 3.1 PERSONA = system-agent entity

**Definition.** A persona is a Public/system `Agent` that presets `{ provider, model,
config(params/tools/budget/temperature/RAG) }` with a stable named identity (`claude`, `gemini`,
`codegpt`). It is **cross-role** and **prompt-free**: at call time the role/system prompt comes from
the Epic 27 store keyed `(principal, role, action)`.

**Changes to the shipped `Agent` (32-1):**
- **`Agent.Role` is no longer identity for Public personas.** Make `Role` nullable: public personas
  have `Role = NULL` (cross-role). The `ConfigJson` may still carry per-role hints (e.g. a `roles`
  map of temperature), but the persona is selectable for any role.
- **Public unique index `(Name, Role) WHERE Public` → `(Name) WHERE Public`** (persona handles are
  globally unique among public agents). `Role` drops out of the seeder idempotency key.
- **`AgentEntitySeeder` rewritten:** instead of 8 `tamma-<role>` rows on one provider chain, seed
  **N named cross-role personas**, each presetting a real provider+model and carrying an **explicit
  `model`** in `ConfigJson` (today the seed omits `model` and relies on `DefaultAgentConfig.ForRole`
  → `claude-sonnet-4` — fine for one provider, wrong when personas differ by provider):
  - `claude` → `{ provider: anthropic, model: claude-sonnet-4-… }`
  - `gemini` → `{ provider: google, model: gemini-… }`
  - `codegpt`/`gpt` → `{ provider: openai, model: gpt-… }`
  - (optionally OpenRouter-backed personas)
- **`GetSystemDefaultPublicAsync(role)` rewritten:** it can no longer find "the public agent whose
  `Role==role`." It returns the platform's configured **default persona** (e.g. `DefaultPersonaName =
  claude`) regardless of role. The per-role ">1 public agent" ambiguity warning is deleted.
- **`AgentResolverService.MaterialiseAsync`** keeps merging the agent's `ConfigJson` onto
  `DefaultAgentConfig.ForRole(role)` and stamping `AgentId`/`AgentVersion`, but **the system/role
  prompt now comes from Epic 27**, not from the persona config. This is the key wiring change.

### 3.2 CUSTOM AGENT = custom prompts (rule 5)

A tenant that wants *different prompts* creates a **custom (private) agent** carrying its own prompts
+ config. The private-`Agent` from 32-1 becomes the home for tenant-authored prompts — exactly the
layer Epic 27 deliberately does NOT expose as per-tenant persona-prompt editing in SaaS. The custom
agent is the sanctioned escape hatch: instead of editing the shared role/action prompt store, the
tenant authors a self-contained private agent (provider+model+config **+ its own prompt set**).

**Changes:**
- `AgentVersion.ConfigJson` gains an **optional `prompts` block** for private agents:
  `{ provider, model, params, prompts: { system?, byRoleAction?: {...} } }`. Personas (public) MUST
  leave it empty (prompt-free by contract).
- `MaterialiseAsync` prompt source becomes a **documented conditional branch**: persona/public →
  Epic 27 store `(principal, role, action)`; custom/private with embedded prompts → the agent's own
  prompts. Both fail-loud, never empty/plain.
- No new entity: a custom agent is just a `Visibility=Private` `Agent`. The only schema delta is the
  optional `prompts` block + the resolver's prompt-source branch. The existing visibility/ownership
  XOR CHECK and per-owner partial indexes are reused.

### 3.3 PER-TENANT ENABLEMENT (rule 6) — the genuinely missing layer

Neither 32-1 nor 32-2 has this. `AgentRoleSelection` answers "which agent serves role X for principal
P" but lets a principal select **any** visible agent. The locked model requires: the tenant **enables**
which personas/agents exist for it; its users just use what's enabled; no per-user enablement.

**New entity — `TenantAgentEnablement`** (CP-resident in SaaS, user-keyed in single-user, same
XOR/index discipline as `AgentRoleSelection` / `prompt_overrides`):

```sql
TenantAgentEnablement (
  Id          UUID PK,
  TenantId    UUID NULL,   -- set in SaaS  (XOR)
  UserId      UUID NULL,   -- set in single-user (XOR)
  AgentId     UUID NOT NULL, -- a public persona OR an own private/custom agent
  Enabled     BOOLEAN NOT NULL,
  CreatedAt/By, UpdatedAt/By,
  CHECK ((TenantId IS NOT NULL AND UserId IS NULL) OR (TenantId IS NULL AND UserId IS NOT NULL)),
  UNIQUE NULLS NOT DISTINCT (TenantId, UserId, AgentId)
)
```

**Semantics (per-tenant, NOT per-user):**
- The tenant's **usable** set tightens the design-of-record's `public ∪ own-private` to
  **`enabled(public) ∪ own-private`**. Own private agents are implicitly enabled (you authored them);
  enablement is primarily about which **public personas** the tenant exposes.
- **No per-user layer** (matches CLAUDE.md's "no per-user override layer in SaaS"). Members see
  exactly the tenant's enabled set and cannot enable/disable. Single-user mode: the sole user is the
  tenant-equivalent (keyed by `UserId`).
- **Enablement = catalog membership; selection (`AgentRoleSelection`) = role binding.** Enablement is
  the gate that constrains selection.

**Changes to 32-2:**
- `IAgentRegistryService.SelectForRoleAsync` / `ResolveUsableAgentAsync` add the enablement gate: a
  public persona not enabled for the tenant is NOT selectable (→ `AGENT.SELECT.NOT_ENABLED`,
  404/409). Today `CanUse()` returns true for *any* public agent — that becomes
  `IsPublic && IsEnabledForPrincipal`.
- `ListVisibleAsync` (or new `ListEnabledAsync`) returns `enabled(public) ∪ own-private`.
- `GetSystemDefaultPublicAsync` returns the tenant's **enabled** default persona; fail-loud if the
  tenant has enabled nothing (no empty fallback).

**RBAC:** enable/disable a persona for the tenant requires `tenant_owner`/`tenant_admin` (member →
403). Public-catalog management (which personas exist platform-wide) stays `PlatformOwnerAccess`
(NOT `OwnerAccess`, which admits every personal-tenant owner).

**New events:** `AGENT.ENABLED.SUCCESS` / `AGENT.DISABLED.SUCCESS`
(tags `agentId, personaName, mode, tenantId|userId`).

### 3.4 Disposition of 32-12

32-12 ("persona = style overlay within a role") **directly contradicts** the locked model
(persona = named cross-role system agent). **Rewrite 32-12** so "persona" = the public/system named
agent (renaming the `tamma-<role>` concept). The style/voice overlay idea (`atlas`/`nova` tone /
verbosity) is still valuable but is a **different, optional feature** — split it out as a separate
**"style/voice variant"** story (a *variant* or *style profile*, not a *persona*) so the vocabulary
matches the source of truth. The variant reuses the same visibility/XOR/index discipline.

### 3.5 BYOK ∘ persona at call time (rule 7)

32-3's `IProviderCredentialResolver.ResolveAsync(tenantId?, providerName)` is keyed by
`(tenant, provider)` and is BYOK→platform with `credentialSource`. The persona supplies the
`providerName` (and model); 32-3 resolves the key. End-to-end (all inside `call-LLM`; the step never
touches a provider):

1. Step → `call-LLM` `{ tenantId, role, (optional) agentId/persona, prompt, params }`.
2. **Gate** (mode/SaaS auth, entitlement, budget) — 32-4 / Epic 34.
3. **Resolve agent**: `ResolveForRoleAsync(role)` or explicit `agentId` → **enablement gate** (§3.3)
   → `ResolvedAgentConfig` with `Provider` + `Model` + `AgentId`/`AgentVersion` stamped.
4. **Resolve prompt**: Epic 27 `(principal, role, action)` (persona) OR the custom agent's own
   prompts (custom agent).
5. **Resolve credential**: `ResolveAsync(tenantId, resolvedConfig.Provider)` →
   `{ ApiKey, Source ∈ {byok, platform} }`. The persona only names the provider; the key is the
   tenant's BYOK-for-that-provider if present, else platform.
6. **Provider call → meter** (cost from provider pricing; price = markup when `platform`, none when
   `byok` — Epic 34-5) → return `result + credentialSource`.

`credentialSource` is decided by `(tenant, persona.provider)`, persona-independent: tenant A with a
BYOK Anthropic key running persona `claude` → `byok`, no markup; the same tenant running `gemini`
with no Google BYOK → `platform`, markup. Persona/enablement is orthogonal to credential source.

---

## 4. The PROVIDER entity (cost pricing) and its relation to Epic 34 / 36-7

### 4.1 What exists today

A cost-pricing layer already exists as the single cost-basis source of truth:
`Tamma.Api/Services/Providers/IProviderPricingService` / `ProviderPricingService` — a **hard-coded
`FrozenDictionary<provider, FrozenDictionary<model, Rate(InputPerToken, OutputPerToken)>>`** ported
from `packages/cost-monitor`. There is **no DB-backed PROVIDER entity** today. Stories 34-5, 36-2,
36-7, and 32-9 all reference `IProviderPricingService.Compute/IsKnown` as THE cost basis and forbid
re-deriving it.

### 4.2 The new PROVIDER entity (cost)

Promote the cost rate sheet to a first-class, admin-editable, **versioned control-plane entity** —
**behind the existing `IProviderPricingService` seam** (the interface is the contract; the entity is
an implementation detail). It is platform-global (NOT tenant-scoped — cost is the provider's
published rate, identical for every tenant), and immutable-versioned like `Plan`/`MarginPolicy` so
historical usage re-prices under the rate effective at call time.

```
Provider (system entity — the cost identity)
  Id            UUIDv7
  Key           string   -- canonical: "anthropic","openai","google","openrouter","local","claude-code"
  DisplayName   string
  AuthModel     string   -- "api-key" | "cli-token"   (feeds 32-4 SaaS-eligibility)
  Status        string   -- "active" | "retired"
  CreatedAt/UpdatedAt

ProviderModelPrice (the COST pricing — per model, versioned)
  Id              UUIDv7
  ProviderKey     string   -- canonical, alias-normalized on write
  Model           string   -- e.g. "claude-sonnet-4-20250514", "gpt-4o"
  InputUsdPer1M   decimal  -- provider's published input rate
  OutputUsdPer1M  decimal  -- provider's published output rate
  -- (nullable room for cache-read / cache-write / per-request later)
  EffectiveFrom   timestamptz (UTC)
  Status          string   -- "active" | "superseded"
  Source          string   -- "seed" | "admin"  (insert-missing-only seeder; never reverts admin edits)
  CreatedAt/UpdatedAt
```

**Load-bearing behaviours preserved** (34-5's `IsKnown` gate and the diagnostic write path depend on
these — they move into the entity-backed resolver, not get dropped):
- alias normalization (`anthropic-claude`→`anthropic`, `claude`→`anthropic`, `gemini`→`google`,
  `github-copilot`→`openai`, `ollama`/`lmstudio`→`local`);
- loose prefix match (`claude-sonnet-4` matches `claude-sonnet-4-20250514`);
- `null`/`"default"` → first-model rule.

**Versioning:** an edit **supersedes** rather than mutates — partial unique index `(ProviderKey,
Model) WHERE status='active'` + `EffectiveFrom`-windowed resolution so a usage event prices under the
cost rate active at its `OccurredAt`. This makes the pricing chain byte-stable/reproducible (34-5 AC7)
on the cost side too.

**Seeding:** a `ProviderPricingSeeder` (insert-missing-only, deterministic UUIDv7) ports the current
frozen table verbatim as v1 rows — mirrors `PlansSeeder`/`MarginPolicySeeder` (admin edits never
reverted). The frozen table is retained as seed/fallback.

### 4.3 COST vs PRICE — the three layers

| Layer | Owns | Entity / seam | Scope | Answers |
|---|---|---|---|---|
| **COST (NEW)** | provider's published per-token rate | `Provider` + `ProviderModelPrice` behind `IProviderPricingService` | CP, platform-global | "What did this call cost us at the provider?" |
| **PRICE — subscription** | recurring/seat/metered sell price per plan version, split by `PricingMode` | `Plan` / `PlanPrice` (34-1, DONE) | CP, platform-global | "What does the plan cost the tenant?" |
| **PRICE — markup** | margin policy: `cost × MarkupMultiplier (+ FixedUsdPer1M)`; BYOK token markup = 0 | `MarginPolicy` + `IUsagePricingEngine`/`IMarginPolicyResolver` (34-5) | CP, platform-global | "What sell price for platform-provided tokens?" |
| **VIEW** | `Σ PlatformBilledUsd − Σ CostUsd` per tenant + fleet | `MarginAnalyticsService` (36-7) — pure read, no recompute | tenant schema + CP fan-out | "Are we making money?" |

- **`ProviderModelPrice` is the input to 34-5.** `CostBasisUsd = ProviderPricingService.Compute(...)`;
  `SellPriceUsd = CostBasisUsd × MarkupMultiplier (+ FixedUsdPer1M × tokens/1M)`. Cost comes from the
  PROVIDER entity; price is cost × MarginPolicy. `MarginPolicy.Scope='provider'` can override *margin*
  per provider — never the *cost rate*.
- **`PlanPrice` ≠ `ProviderModelPrice`.** `PlanPrice` is subscription/seat sell price keyed by
  `(PlanId, PricingMode)`; `ProviderModelPrice` is per-token cost keyed by `(ProviderKey, Model)`.
  No overlap.
- **36-7 reads neither cost nor markup config.** It reads the already-persisted `CostUsd`
  (= provider cost basis) and `PlatformBilledUsd` (= 34-5 sell price) columns that 36-2 wrote, and
  reports `revenue − cost`.

### 4.4 Cost→price flow (platform markup vs BYOK no-token-markup)

`credentialSource` (32-3's `ProviderCredential.Source`, persisted by 34-3 as
`ProviderDiagnostic.BillingMode`) branches the flow. **Cost basis is computed identically in both
modes; only the sell price differs.**

```
LLM call (via /api/v1/llm/call — never the step directly)
   │  (input/output tokens, provider, model from the diagnostic)
   ▼
ProviderModelPrice.Compute(provider, model, in, out)  ──► costBasisUsd   [NEW entity; same for byok & platform]
   │
   ├─ credentialSource = PLATFORM
   │     MarginPolicy.Resolve(provider→plan→global) → markup
   │     sellPriceUsd = round6( costBasisUsd × MarkupMultiplier + FixedUsdPer1M × totalTokens/1M )
   │     marginUsd    = sellPriceUsd − costBasisUsd            [34-5, PricingMode.PlatformProvided]
   │
   └─ credentialSource = BYOK  (tenant's own key)
         costBasisUsd STILL computed (for analytics/reporting)
         sellPriceUsd (token component) = 0
         marginUsd     = 0                                     [34-5, PricingMode.Byok]
         (tenant pays the provider directly; we bill only plan/seat via Epic 35)
   ▼
ProviderDiagnostic { Cost = costBasisUsd, BillingMode } → 32-9 DCB usage event
   → 36-2 persists CostUsd (cost) + PlatformBilledUsd (sell; 0 for BYOK)
   → 36-7 reports Σ revenue − Σ cost  (BYOK ⇒ revenue 0, margin = −cost)
```

### 4.5 New story, not an extension

This is a **NEW entity and a NEW story** ("Provider Cost Price-Book"), introduced as a control-plane
data model behind the existing `IProviderPricingService` seam (swap the frozen table for a DB read;
keep the interface). It is NOT an extension of `Plan`/`PlanPrice` (sell-side), NOT `MarginPolicy`
(markup), NOT 36-7 (read-only view). It fills a gap: 34-1 owns the *price* book, 34-5 owns *markup*,
but no story owns the *cost* book. Sequence it **before 34-5** (its cost-basis input), alongside 34-1
(reuse the CP-entity + versioning + insert-missing-only seeder patterns). Downstream stories need at
most a one-line dependency edit; none need AC or code changes (the whole point of preserving the seam).

---

## 5. Non-LLM step mediation + phasing

### 5.1 Endpoints (follow-up epic; see the audit table §1.2)

- **Class A — Git platform** (`CreateBranch`/`CreatePullRequest`/`MergePullRequest`/`UpdateIssueStatus`/`AnalyzeReview`):
  ```
  POST  /api/v1/git/{repo}/branches
  POST  /api/v1/git/{repo}/pull-requests
  PUT   /api/v1/git/{repo}/pull-requests/{n}/merge
  GET   /api/v1/git/{repo}/pull-requests/{n}/comments
  PATCH /api/v1/git/{repo}/issues/{n}
  ```
  API holds the PAT/installation token (+ per-tenant tokens via Epic 28/29 cabinet), authorizes that
  *this* tenant may act on *this* repo (cross-tenant guard), emits the audit event.
- **Class C — Agent dispatch** (`DispatchAgentWorkflow`/`CollectAgentResults`):
  ```
  POST /api/v1/agent-dispatch/{repo}/runs
  GET  /api/v1/agent-dispatch/{repo}/runs/{id}
  ```
- **Class D — Slack** (`SlackActivity`): `POST /api/v1/notifications/slack`.
- **Class E — Billing/Stripe (Epic 35, future):** the activity emits an intent
  (`POST /api/v1/billing/...` or an outbox row); the API holds the Stripe key, performs the
  charge/invoice, and meters. **Prohibited at design time** to call Stripe from an activity.
- **Already compliant:** `TriggerCIActivity` (formalize under `/api/v1`); `QueueWelcomeEmailActivity`
  (outbox — the model for fire-and-forget effects).

### 5.2 Phasing

**Now — Epic 32 (LLM path only):**
1. Build `POST /api/v1/llm/call` (gate → resolve agent/persona+enablement → resolve credential
   BYOK→platform via Epic 29 cabinet → provider call → meter → return `{ result, usage,
   credentialSource }`).
2. Re-point `CallLlmActivity` + `CallLlmInlineActivity` to it via `TammaApiClient`; **delete the
   engine's credential resolver registration** (`ConfigPlatformProviderCredentialResolver` /
   `AddEngineProviderCredentialResolution` in `ElsaServer/Program.cs`) so the engine holds no LLM key.
   Move the per-provider HTTP logic out of the activities into the API.
3. **Redirect the remaining in-engine direct-LLM callers** (`ClaudeAnalysisActivity`,
   `WriteTests`/`WriteImplementation`/`AnalyzeCode`/`ApplyRefactoring`/`ApplyReviewFixes`,
   `AIDiagnosis`): delete their direct keyed fallback and route through `call-LLM` (their
   engine-callback mode terminates at the mediated path).

**Follow-up epic — "Step → internal-API mediation for non-LLM integrations" (new sibling, e.g. Epic 38):**
- Add `Tamma.Api` controllers for Class A (git), Class C (agent-dispatch), Class D (Slack). Re-point
  the activities to `TammaApiClient` calls. Remove the engine's reliance on co-located credential
  services entirely. Centralize per-tenant credential resolution + cross-tenant authorization + audit
  emission, mirroring `/llm/call`. Adopt the outbox pattern for fire-and-forget effects. Enforce the
  rule for Class E when Epic 35 lands.
- Add a **guardrail analyzer/test:** fail the build if any class under `Tamma.Activities` references
  `HttpClient`/`PostAsync`/`PostAsJsonAsync` to a non-`TammaApiClient` host, or injects a
  credential-holding vendor service — so violations can't reappear.

### 5.3 What legitimately CANNOT be (fully) mediated

- **Inbound webhooks** (`WebhookSignalRegistry`, the GitHub `workflow_run.completed` receiver behind
  `TriggerCIActivity`/agent-dispatch): received by `Tamma.Api`, signalled to the engine in-process —
  no outbound external call to mediate. Signature verification + secret stay in the API. Out of scope
  by nature.
- **Local CLI agent providers (`ICLIAgentProvider`) in single-user/self-hosted mode:** spawn local
  processes and shell out to local git, not an external HTTP API. No remote credential to centralize,
  no SaaS multi-tenancy. Routing through an endpoint adds a hop with no security benefit. Per the Epic
  32 design these are single-user-only and legitimately exempt (the rule targets *external
  API/provider* calls — a local process is not one).
- **In-engine local tools** (`FileReadTool`, `ShellExecuteTool`, `GitOperationsTool`): operate on the
  local checkout/filesystem, not an external provider. Out of scope. When they run under SaaS via the
  managed LLM path, the LLM call itself is mediated by `/llm/call`; the local tools execute against
  the tenant's own sandbox.

---

## 6. Summary of changes by area

- **Engine (`Tamma.ElsaServer` / `Tamma.Activities`):** holds no keys; activities become thin
  `TammaApiClient` clients. Delete the engine credential resolver registration; gut
  `CallLlmInlineActivity`'s provider logic; delete `CallLlmActivity`'s direct path and the
  TDD/ADL/Debug/Mentorship direct-LLM fallbacks.
- **`Tamma.Api`:** new `LlmCallEndpoints` → `IManagedAgent.RunAsync` (32-5) composing gate (32-4) →
  resolve+enablement (32-2/§3.3) → credential (32-3) → prompt (Epic 27) → tool loop → meter (32-9 +
  34-5 + new provider-cost entity). New `TenantAgentEnablement` entity + endpoints. New
  `Provider`/`ProviderModelPrice` cost entity behind `IProviderPricingService`.
- **Agent model:** `Agent.Role` nullable for public personas; seeder rewritten to named cross-role
  personas with explicit `model`; `ConfigJson.prompts` for custom agents; resolver pulls persona
  prompts from Epic 27; enablement gate added to selection.
- **Pricing:** cost (new entity) is mode-independent; markup (34-5) applies only when `platform`;
  BYOK zeroes the token sell price but keeps cost for reporting.

See `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md` for the per-story impact map and the
"what to do right now" recommendation.
