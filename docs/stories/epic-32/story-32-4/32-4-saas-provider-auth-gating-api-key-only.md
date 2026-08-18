# Story 32-4: SaaS Provider Gate (call-LLM endpoint stage)

Status: done
<!-- Flipped drafted -> done 2026-08-18. The deliverable named in the acceptance criteria
     was located in apps/tamma-elsa/src (and its suites in apps/tamma-elsa/tests) before this
     header was changed — not taken from a changelog. The per-story evidence is recorded
     inline on this story's line in docs/sprint-status.yaml.
-->

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **platform operator running Tamma in SaaS mode**,
I want the very first composition stage inside the `call-LLM` endpoint (`POST /api/v1/llm/call`) to inspect the resolved provider's auth model and refuse CLI/token (harness) providers before any agent resolution, credential lookup, or provider call happens,
So that a tenant can never select or run a local-binary / token-auth agent (e.g. the Claude Code CLI, OpenCode, Zen MCP) inside the managed multi-tenant service — where there is no host shell, no per-tenant local credential store, and no isolation boundary for a subprocess agent — while a self-hosted single-user keeps full local harness access.

## Priority

P0 — The fail-closed gate stage of the 32-5 lynchpin endpoint. Without it, `ManagedAgent.RunAsync` would proceed to resolve a credential for, and dispatch, a `cli-token` provider in SaaS — a correctness and security defect, not a degraded experience. This story is sequenced as **gate stage of sequence F** (the call-LLM endpoint), implemented just before 32-5.

## Context

This story is a **REWRITE**. The previous framing (a standalone gating story with its own selection-boundary activity, its own provider-auth registry, and resolver-side skip logic in `ProviderChainResolver`) is **superseded** by the locked agent architecture (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`). Under the locked model:

- A workflow STEP never calls a provider directly; the LLM path is mediated by a single internal endpoint `POST /api/v1/llm/call`, composed by `ManagedAgent.RunAsync` (Story 32-5). That endpoint's **composition step 1 is the gate** (design §2.6: "Gate (32-4) — `ISaaSProviderGate.InspectAsync` … CLI-token providers excluded in SaaS; unknown providers denied fail-closed").
- So 32-4 is no longer a free-standing activity/endpoint. It is **the gate stage**: a small, pure-ish service `ISaaSProviderGate` in `Tamma.Api/Services/Security/` that the endpoint invokes **before** agent resolution (step 2), credential resolution (step 3), and the provider call (step 5). It returns a **typed decision**; the endpoint maps that decision to the §2.4 error envelope. The gate never throws a bare exception that would leak a 500.
- Provider SaaS-eligibility is no longer a hard-coded `CliTokenProviders` set inside this story. It is driven by the new **`Provider` entity's `AuthModel` field** (`api-key` | `cli-token`) introduced by sibling story **34-11** (Provider Cost Price-Book, design §4.2: "`AuthModel` … feeds 32-4 SaaS-eligibility"). `cli-token` ⇒ not SaaS-eligible. **Until 34-11 lands**, a static allowlist of API-key providers is the interim source of truth (documented below) — this story is buildable and testable against the interim source and flips to the entity read with no AC change.

The provider duality this gate enforces is the deep-dive's §1 invariant: API providers (`ILLMProvider`/`IAIProvider`, `type:'llm-api'`) run server-side inside the endpoint; harness providers (`ICLIAgentProvider`, `type:'cli-agent'` — `claude-code`, `opencode`, `zen-mcp`) "spawn local processes … run their own loop … routing them through the endpoint adds a hop with no security benefit … In SaaS the 32-4 gate makes them structurally unreachable (`400 SAAS_PROVIDER_NOT_ALLOWED`)." This story delivers exactly that structural unreachability.

## Acceptance Criteria

1. An **`ISaaSProviderGate`** interface and a **`SaaSProviderGate`** implementation exist in `apps/tamma-elsa/src/Tamma.Api/Services/Security/`. `InspectAsync(ProviderGateContext, CancellationToken)` returns a typed `ProviderGateDecision` (`{ Allowed, Outcome, Reason?, AuthModel?, HttpStatusHint }`) — it does **not** throw to signal a denial.
2. **Mode is read once** from the process-stable `ITammaModeProvider` (`Tamma.Api/Services/PromptStore/TammaMode.cs`). In **single-user / self-hosted mode** the gate is a hard no-op: every provider (including `cli-token` harness providers) ⇒ `Allowed`, **zero** events, **zero** metric increments — harness providers are a legitimate local affordance (design §5.3).
3. In **SaaS mode**, a provider whose resolved `AuthModel == cli-token` (harness providers: `claude-code`, `opencode`, `zen-mcp`, …) ⇒ **denied** with `Outcome = SaasProviderNotAllowed` and `HttpStatusHint = 400`; the endpoint maps this to **HTTP 400** body `{ "success": false, "failureCode": "SAAS_PROVIDER_NOT_ALLOWED", … }` per design §2.4. This is the selection/execution backstop.
4. In **SaaS mode**, an **unknown** provider (no `Provider` entity / not in the interim allowlist) ⇒ **denied fail-closed** with `Outcome = SaasProviderNotAllowed`, `HttpStatusHint = 400`. Eligibility that cannot be determined (entity read fails, mode-source unavailable) ⇒ **DENY** — never a silent allow (consistent with `feedback_resolution_no_empty_fallback`: never fall back to an empty/permissive default).
5. In **SaaS mode**, a SaaS **auth / entitlement** rejection of the tenant (the caller is not authorized to use the managed LLM path for this tenant) ⇒ denied with `Outcome = TenantNotEntitled`, `HttpStatusHint = 403`; the endpoint maps it to **HTTP 403** (design §2.4). Entitlement evaluation is delegated to the existing SaaS auth/entitlement seam (Epic 34 gating) — this story owns only the provider-auth-model branch and the typed surfacing of the entitlement result; it does not re-implement entitlement.
6. In **SaaS mode**, an **`api-key`** provider (`anthropic`, `openai`, `openrouter`, `google`/`gemini`, …) that is entitled ⇒ `Allowed`, **no event**, no `failure` metric.
7. **Eligibility source of truth:** the gate consults the `Provider` entity's `AuthModel` via the 34-11 `IProviderPricingService`-adjacent provider read (`IProviderAuthLookup` — a thin read seam this story defines and 34-11 backs). **Interim (pre-34-11):** `IProviderAuthLookup` is implemented by a static `StaticProviderAuthLookup` keyed off the existing `ProviderAllowlist.DefaultProviders` set, classifying `claude-code`/`opencode`/`zen-mcp` as `cli-token` and everything else as `api-key`. Swapping to the entity-backed lookup when 34-11 lands is a DI registration change only — no AC/contract change. Provider-name matching is case-insensitive and trimmed.
8. On a **SaaS denial**, the gate emits **exactly one** DCB event `AGENT.PROVIDER.GATED` via the tenant `IEventRepository` (`Data` = `{ provider, authModel, mode, reason, role?, action? }`, tenant-scoped `Tags`) and increments **exactly one** OpenTelemetry counter `tamma.provider.gated` (tags `provider`, `auth_model`, `reason`). Event-append failure is logged and swallowed so it never converts a clean typed decision into a 500. Single-user mode emits neither.
9. The decision is **idempotent and side-effect-bounded**: `InspectAsync` is called once per `call-LLM` invocation by `ManagedAgent.RunAsync` (step 1); an `Allowed` decision has zero side effects; a denial has exactly one event + one metric increment. The gate touches **provider names and the mode only** — never a key, token, or secret.
10. **Tests** cover the full mode × auth-model × known/unknown matrix (see Testing Strategy): single-user allows all; SaaS allows entitled `api-key`, denies `cli-token` (400), denies unknown fail-closed (400), denies un-entitled tenant (403); exactly one event + one metric per SaaS denial; zero side effects on allow; the typed decision maps to the §2.4 envelope codes; credential safety (no secret ever reaches the gate).

## Technical Design

### Where the gate sits in the endpoint (composition step 1)

The gate is the first stage of `ManagedAgent.RunAsync`, invoked by the `POST /api/v1/llm/call` handler (`LlmCallEndpoints.cs`, Story 32-5). It runs **before** agent resolution, credential resolution, and the provider call — the design §2.6 order:

```
POST /api/v1/llm/call   (LlmCallEndpoints -> ManagedAgent.RunAsync)
  │
  ▼
┌─────────────────────────────────────────────────────────────────────────┐
│ STEP 1  ── ISaaSProviderGate.InspectAsync(ctx)            ◄── THIS STORY  │
│            mode = ITammaModeProvider.Mode  (process-stable)               │
│            single-user → Allowed (no event, no metric)                    │
│            SaaS:                                                          │
│              authModel = IProviderAuthLookup.AuthModel(provider)          │
│                cli-token → DENY  (Outcome=SaasProviderNotAllowed, 400)    │
│                unknown   → DENY fail-closed (400)                         │
│                api-key + not entitled → DENY (Outcome=TenantNotEntitled,  │
│                                                403)                       │
│                api-key + entitled → Allowed                              │
└───────────────────────────────┬─────────────────────────────────────────┘
                                 │ ProviderGateDecision (typed)
                                 ▼
   denied → handler maps to §2.4 envelope (400 SAAS_PROVIDER_NOT_ALLOWED / 403)
   allowed → STEP 2 resolve agent (32-2) → STEP 3 credential (32-3)
             → STEP 4 prompt (Epic 27) → STEP 5 provider call (32-5) → meter
```

The provider name available at step 1 comes from the request's `persona`/`agentId`/explicit `model`+`provider` hint. When the request names a `persona`/`agentId` whose provider is only known after resolution, the endpoint performs a **lightweight provider lookup ahead of full resolution** (the resolver exposes the provider for a persona without materialising the full config), so the gate still runs first. The gate is a pure function of `(mode, providerName, tenantEntitlement)` and never resolves a credential.

### `ISaaSProviderGate` (the gate stage contract)

New files `apps/tamma-elsa/src/Tamma.Api/Services/Security/ISaaSProviderGate.cs` + `SaaSProviderGate.cs` (Api-side: it needs `ITammaModeProvider`, the entitlement seam, and emits DCB events via the tenant `IEventRepository`).

```csharp
namespace Tamma.Api.Services.Security;

/// <summary>Auth model a provider authenticates with — drives SaaS eligibility.
/// Mirrors the Provider entity AuthModel field (34-11, design §4.2).</summary>
public enum ProviderAuthModel
{
    /// <summary>Cloud/local LLM API authenticated by an API key (ILLMProvider).
    /// SaaS-eligible — the key resolves from the principal's secret source (32-3).</summary>
    ApiKey,

    /// <summary>Headless CLI / token-based harness agent (ICLIAgentProvider, e.g.
    /// claude-code, opencode, zen-mcp). Needs a host shell + local credential —
    /// NOT reachable in SaaS; single-user / self-hosted only (deep-dive §1).</summary>
    CliToken,
}

/// <summary>The reason a gate decision resolves the way it does — maps 1:1 to the
/// §2.4 call-LLM error envelope.</summary>
public enum ProviderGateOutcome
{
    Allowed,                 // pass to step 2
    SaasProviderNotAllowed,  // cli-token in SaaS, or unknown (fail-closed) -> HTTP 400
    TenantNotEntitled,       // SaaS auth/entitlement rejection           -> HTTP 403
}

public sealed record ProviderGateContext(
    string ProviderName,
    string? Role = null,
    string? Action = null,
    Guid? TenantId = null);

public sealed record ProviderGateDecision(
    bool Allowed,
    ProviderGateOutcome Outcome,
    string? Reason,                 // null when Allowed; key-free otherwise
    ProviderAuthModel? AuthModel,   // resolved model; null when provider unknown
    int HttpStatusHint)             // 200 allow / 400 not-allowed / 403 not-entitled
{
    public static ProviderGateDecision Allow(ProviderAuthModel? model) =>
        new(true, ProviderGateOutcome.Allowed, null, model, 200);
}

public interface ISaaSProviderGate
{
    /// <summary>
    /// Composition step 1 of the call-LLM endpoint. Single-user ⇒ always Allow
    /// (no event, no metric). SaaS ⇒ Allow only when the provider is api-key AND
    /// the tenant is entitled; cli-token / unknown ⇒ SaasProviderNotAllowed (400,
    /// fail-closed); un-entitled tenant ⇒ TenantNotEntitled (403). On a SaaS denial
    /// emits AGENT.PROVIDER.GATED + the metric as a swallowed side effect. NEVER
    /// throws to signal a denial — returns a typed decision the endpoint maps to the
    /// §2.4 envelope.
    /// </summary>
    Task<ProviderGateDecision> InspectAsync(ProviderGateContext ctx, CancellationToken ct = default);
}
```

> **Why a typed decision, not a throw (design §2.4).** The call-LLM endpoint owns the error envelope: it returns a typed, key-free body, never a leaked provider error or a bare exception → 500. The gate hands back `ProviderGateDecision`; `LlmCallEndpoints`/`ManagedAgent` translate `Outcome`+`HttpStatusHint` into the §2.4 response. This keeps the fail-closed contract (`PROVIDER_CREDENTIAL_UNAVAILABLE`-style guarantees) intact and consistent with 32-5's "typed failures, never lost runs."

### Provider auth lookup — `IProviderAuthLookup` (34-11 seam, interim static impl)

New `apps/tamma-elsa/src/Tamma.Api/Services/Security/IProviderAuthLookup.cs`. This is the single read seam the gate consults; 34-11 backs it with the `Provider` entity's `AuthModel` field.

```csharp
namespace Tamma.Api.Services.Security;

public interface IProviderAuthLookup
{
    /// <summary>Auth model for a known provider; <c>null</c> if the provider is unknown
    /// (drives the fail-closed DENY in SaaS).</summary>
    Task<ProviderAuthModel?> AuthModelAsync(string? providerName, CancellationToken ct = default);
}
```

**Interim implementation (pre-34-11)** — `StaticProviderAuthLookup`, keyed off the existing `ProviderAllowlist.DefaultProviders` set (single source of known providers; do not re-list providers):

```csharp
public sealed class StaticProviderAuthLookup : IProviderAuthLookup
{
    // Harness providers (ICLIAgentProvider). The complement within
    // ProviderAllowlist.DefaultProviders is ApiKey. Verify against
    // BUILTIN_PROVIDER_NAMES / agent-provider-factory.ts at impl time.
    private static readonly HashSet<string> CliTokenProviders =
        new(StringComparer.OrdinalIgnoreCase) { "claude-code", "opencode", "zen-mcp" };

    public Task<ProviderAuthModel?> AuthModelAsync(string? providerName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return Task.FromResult<ProviderAuthModel?>(null);
        var name = providerName.Trim();
        if (!ProviderAllowlist.IsAllowedDefault(name)) return Task.FromResult<ProviderAuthModel?>(null); // unknown
        return Task.FromResult<ProviderAuthModel?>(
            CliTokenProviders.Contains(name) ? ProviderAuthModel.CliToken : ProviderAuthModel.ApiKey);
    }
}
```

**34-11 implementation** — `EntityProviderAuthLookup` reads `Provider.AuthModel` (`api-key` | `cli-token`) for the provider key (alias-normalised on write per design §4.2), returning `null` for an unknown key. **Swapping is a DI registration change only** (`AddSingleton<IProviderAuthLookup, StaticProviderAuthLookup>()` → `AddScoped<IProviderAuthLookup, EntityProviderAuthLookup>()`). The `ProviderGateDecision` contract is identical.

> **Cross-reference 34-11.** This story does NOT define the `Provider`/`ProviderModelPrice` entities or the `AuthModel` column — 34-11 owns them (sequence A, before 34-5). 32-4 defines only the read seam `IProviderAuthLookup` and the interim static impl, so it is buildable independently and flips to the entity with no contract change.

### `SaaSProviderGate.InspectAsync` flow

```csharp
public async Task<ProviderGateDecision> InspectAsync(ProviderGateContext ctx, CancellationToken ct = default)
{
    // 1. single-user / self-hosted: hard no-op (harness providers are legitimate locally).
    if (_mode.Mode != TammaMode.SaaS)
        return ProviderGateDecision.Allow(model: null);   // no lookup, no event, no metric

    // 2. SaaS: classify the provider (fail-closed on unknown).
    var authModel = await _authLookup.AuthModelAsync(ctx.ProviderName, ct);
    if (authModel is null || authModel == ProviderAuthModel.CliToken)
    {
        var reason = authModel is null ? "PROVIDER_UNKNOWN" : "CLI_TOKEN_PROVIDER";
        await EmitGatedAsync(ctx, authModel, reason, ct);   // event + metric, swallowed on failure
        return new ProviderGateDecision(false, ProviderGateOutcome.SaasProviderNotAllowed,
            Reason: $"Provider '{ctx.ProviderName}' is not available in SaaS mode (api-key providers only).",
            AuthModel: authModel, HttpStatusHint: 400);
    }

    // 3. SaaS auth / entitlement (Epic 34 seam) — provider is api-key; is the tenant entitled?
    if (!await _entitlement.IsTenantEntitledAsync(ctx.TenantId, ctx.ProviderName, ct))
    {
        await EmitGatedAsync(ctx, authModel, "TENANT_NOT_ENTITLED", ct);
        return new ProviderGateDecision(false, ProviderGateOutcome.TenantNotEntitled,
            Reason: "Tenant is not entitled to the managed LLM path for this provider.",
            AuthModel: authModel, HttpStatusHint: 403);
    }

    return ProviderGateDecision.Allow(authModel);           // api-key + entitled
}
```

### DCB event — `AGENT.PROVIDER.GATED`

Appended via `IEventRepository.AppendAsync` (same pattern as `AgentEndpoints` `AGENT_CONFIG.UPDATED.SUCCESS`). Emitted **only** in SaaS on a denial. Append failure is logged and swallowed — a clean typed decision must never become a 500.

```csharp
await _events.AppendAsync(new DomainEvent
{
    Id = Guid.NewGuid(),
    Type = "AGENT.PROVIDER.GATED",
    TenantId = ctx.TenantId,
    Tags = JsonSerializer.Serialize(new
    {
        tenantId  = ctx.TenantId?.ToString(),
        provider  = ctx.ProviderName,
        authModel = authModel is null ? "unknown" : (authModel == ProviderAuthModel.CliToken ? "cli-token" : "api-key"),
        mode      = "saas",
        role      = ctx.Role,
        action    = ctx.Action,
    }),
    Metadata = JsonSerializer.Serialize(new { workflowVersion = "1.0.0", eventSource = "system" }),
    Data = JsonSerializer.Serialize(new
    {
        provider  = ctx.ProviderName,
        authModel = authModel is null ? "unknown" : (authModel == ProviderAuthModel.CliToken ? "cli-token" : "api-key"),
        mode      = "saas",
        reason,                       // CLI_TOKEN_PROVIDER | PROVIDER_UNKNOWN | TENANT_NOT_ENTITLED
        role      = ctx.Role,
        action    = ctx.Action,
    }),
    CreatedAt = DateTime.UtcNow,
});
```

### OpenTelemetry metric

New `ProviderGatingMetrics` (mirrors `KekRotationMetrics`): a `Meter`-registered `Counter<long> tamma.provider.gated`, incremented in `InspectAsync` on every SaaS denial with tags `provider`, `auth_model` (`cli-token`|`unknown`|`api-key`), `reason`.

### Endpoint mapping (consumed by 32-5)

`LlmCallEndpoints` / `ManagedAgent.RunAsync` (32-5) call `InspectAsync` as step 1 and map the decision:

```csharp
var gate = await _saasGate.InspectAsync(new ProviderGateContext(provider, role, action, tenantId), ct);
if (!gate.Allowed)
{
    return gate.Outcome switch
    {
        ProviderGateOutcome.SaasProviderNotAllowed =>
            Results.Json(new { success = false, failureCode = "SAAS_PROVIDER_NOT_ALLOWED",
                               failureReason = gate.Reason }, statusCode: 400),
        ProviderGateOutcome.TenantNotEntitled =>
            Results.Json(new { success = false, failureCode = "TENANT_NOT_ENTITLED",
                               failureReason = gate.Reason }, statusCode: 403),
        _ => Results.Json(new { success = false, failureCode = "SAAS_PROVIDER_NOT_ALLOWED" }, statusCode: 400),
    };
}
// gate.Allowed → proceed to step 2 (resolve agent), step 3 (credential), …
```

> **Ownership boundary with 32-5.** 32-5 owns the endpoint, the mapping, and `ManagedAgent.RunAsync`. 32-4 owns `ISaaSProviderGate`, `IProviderAuthLookup` (+ interim static impl), `ProviderGatingMetrics`, and the `AGENT.PROVIDER.GATED` event. The mapping snippet above is illustrative of the consumption contract; 32-5 implements it.

## Dependencies

**Internal:**

- **Story 34-11** (Provider Cost Price-Book) — owns the `Provider` entity + `AuthModel` field (`api-key` | `cli-token`, design §4.2) that backs `IProviderAuthLookup`. **Soft prerequisite:** this story ships with `StaticProviderAuthLookup` (interim) and flips to the entity-backed lookup via DI when 34-11 lands — no contract/AC change. Sequence A (34-11) precedes; this gate stage ships in sequence F.
- **Story 32-5** (Call-LLM endpoint + managed execution) — the **consumer**. Its `ManagedAgent.RunAsync` invokes `ISaaSProviderGate.InspectAsync` as composition step 1 and maps the decision to the §2.4 envelope. This story delivers the gate it consumes; 32-5 is implemented immediately after.
- **Story 32-3** (Per-tenant provider credential resolution, BYOK → platform) — the `api-key` classification presumes 32-3's credential model; gating runs **before** credential resolution (so a `cli-token` provider never reaches the cabinet).
- **Reuses (no change required):** `ITammaModeProvider` (`Services/PromptStore/TammaMode.cs`), `ProviderAllowlist` (`Tamma.Activities/Security/` — interim known-set source), `IEventRepository` (`Tamma.Data/Repositories/`), the SaaS auth/entitlement seam (Epic 34 gating), `TammaError` (`Tamma.Core/`, only if a contract-violation throw is needed — null context).

**External:** none new.

**Design alignment:** `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` §2.4 (error/gating semantics), §2.6 step 1 (gate stage), §4.2 (`AuthModel` feeds SaaS-eligibility); `docs/superpowers/specs/2026-06-20-managed-llm-execution-deep-dive.md` §1 (provider duality — harness providers unreachable in SaaS).

## Testing Strategy

1. **Gate unit tests** (`tests/Tamma.Api.Tests/Security/SaaSProviderGateTests.cs`):
   - **single-user** — every provider (incl. `claude-code`, `opencode`, `zen-mcp`, an unknown name) ⇒ `Allowed`, **zero** events, **zero** counter increments (no lookup even attempted is acceptable; assert zero side effects).
   - **SaaS, api-key + entitled** (`anthropic`, `openai`, `openrouter`, `gemini`) ⇒ `Allowed`, no event, no metric.
   - **SaaS, cli-token** (`claude-code`/`opencode`/`zen-mcp`) ⇒ denied `Outcome=SaasProviderNotAllowed`, `HttpStatusHint=400`, **exactly one** `AGENT.PROVIDER.GATED` event with `reason=CLI_TOKEN_PROVIDER` and the right `Data`/`Tags`, **exactly one** counter increment.
   - **SaaS, unknown provider** ⇒ denied fail-closed `Outcome=SaasProviderNotAllowed`, `HttpStatusHint=400`, `reason=PROVIDER_UNKNOWN`, one event, one metric.
   - **SaaS, api-key + NOT entitled** (entitlement seam returns false) ⇒ denied `Outcome=TenantNotEntitled`, `HttpStatusHint=403`, `reason=TENANT_NOT_ENTITLED`, one event, one metric.
   - **Event-append failure** is swallowed: the typed decision is still returned (assert no throw escapes `InspectAsync`).
   - **Case-insensitivity / trimming**: `"Claude-Code "` classifies as `cli-token`.
2. **Lookup unit tests** (`tests/Tamma.Api.Tests/Security/StaticProviderAuthLookupTests.cs`): every `ProviderAllowlist.DefaultProviders` entry returns a deterministic `AuthModel` (no `null` for a known provider — guards against a new provider being silently mis-classified); `claude-code`/`opencode`/`zen-mcp` ⇒ `CliToken`; everything else ⇒ `ApiKey`; unknown name ⇒ `null`.
3. **Mode × auth-model matrix table test**: parameterise `(mode ∈ {SingleUser, SaaS}) × (provider ∈ {anthropic, claude-code, unknown}) × (entitled ∈ {true,false})` and assert the allow/deny/outcome/status/event/metric for each cell — the canonical regression guard, including the §2.4 status-hint mapping.
4. **Endpoint-mapping contract test** (lives with 32-5 but referenced here): a denied `ProviderGateDecision` with `Outcome=SaasProviderNotAllowed` ⇒ HTTP 400 `SAAS_PROVIDER_NOT_ALLOWED`; `TenantNotEntitled` ⇒ HTTP 403 — proving the typed decision drives the §2.4 envelope.
5. **34-11 swap test**: with `EntityProviderAuthLookup` registered (fake `Provider` rows: `anthropic`=api-key, `claude-code`=cli-token), the same matrix passes — proving the DI swap is contract-neutral.
6. **Credential-safety assertion**: a test passes a `ProviderGateContext` carrying only a provider name and asserts no code path on the gate reads a credential/secret service (gate dependencies are mode + lookup + entitlement + events + metrics only).
7. **C# suites run** via `sg docker -c "dotnet test ..."` (session docker group is stale — `reference_dotnet_test_docker.md`). TDD: tests authored red before implementation per `superpowers:test-driven-development`.

## Estimated Effort

1.5-2 days (smaller than the prior standalone framing — no resolver-skip logic, no selection-endpoint hooks; the gate is one service consumed by 32-5).

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Security/ISaaSProviderGate.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Security/SaaSProviderGate.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Security/IProviderAuthLookup.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Security/StaticProviderAuthLookup.cs` | Create (interim; 34-11 adds `EntityProviderAuthLookup`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Security/ProviderGatingMetrics.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (DI: register gate, `IProviderAuthLookup` → `StaticProviderAuthLookup`, metrics) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Security/SaaSProviderGateTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Security/StaticProviderAuthLookupTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Security/SaaSProviderGateMatrixTests.cs` | Create |

> No new control-plane / public-schema **table** is added by this story (no `Program.cs` startup-reset DROP-list change needed — the DROP-list note applies only to new tables; the `Provider` table is owned by 34-11). No EF migration is added here; the gate is a pure service over existing seams.

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md) (real path: `docs/guides/BEFORE_YOU_CODE.md`)
2. Searched `.dev/` directory for related spikes, bugs, findings, and decisions
3. Reviewed the locked design spec `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` §2.4, §2.6 step 1, §4.2, and the deep-dive §1 (provider duality)
4. Confirmed the consumer contract with 32-5 (`ManagedAgent.RunAsync` step 1 calls `InspectAsync`; the endpoint maps the typed decision) and the 34-11 `AuthModel` source
5. Confirmed the two provider hierarchies in `packages/providers/src/types.ts`: `ILLMProvider` (`type:'llm-api'`, api-key) vs `ICLIAgentProvider` (`type:'cli-agent'`, cli-token)
6. Planned TDD approach (Red-Green-Refactor) — write the mode × auth-model matrix test first

### Reframe note (what changed from v1.0.0)

The previous v1 framing was a **standalone gating story** with its own `IProviderAuthRegistry`, a selection-boundary hook in `AgentEndpoints`, and execution-boundary skip logic inside `ProviderChainResolver` (a `ChainReason.SaaSIneligible`). Under the locked model the LLM path is mediated by a single endpoint and the provider chain/resolver concerns move server-side into 32-5's `ManagedAgent`. So this rewrite:

- **Removes** the resolver-skip integration and the selection-endpoint hook — those belonged to the old "two seams" topology. The single seam now is **the gate stage of the call-LLM endpoint**.
- **Replaces** the in-story hard-coded `CliTokenProviders` registry with the `IProviderAuthLookup` read seam backed by 34-11's `Provider.AuthModel` (static interim impl until 34-11 lands).
- **Returns a typed decision** the endpoint maps to the §2.4 envelope (400/403) rather than throwing a `TammaError` from a convenience `EnsureAllowedAsync`.
- **Adds the 403 entitlement outcome** (design §2.4) alongside the 400 provider-not-allowed outcome.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Does the gate run? | Yes, but it is a hard **no-op** — `InspectAsync` returns `Allowed` for every provider before any lookup. Harness (`cli-token`) providers are a legitimate local affordance (spawn a local process; no remote credential to centralise — design §5.3). | Yes, and it is **load-bearing**: `cli-token` and unknown providers are denied (400), un-entitled tenants denied (403), only entitled `api-key` providers pass. |
| Who is the principal the decision is scoped to? | The sole user (the tenant-equivalent). No event is emitted, so no per-user record is written. | The tenant. `ProviderGateContext.TenantId` is set; the `AGENT.PROVIDER.GATED` event is tenant-scoped (lands in the tenant `t_<hex>` event store via the tenant `IEventRepository`). |
| Who owns the gating audit data? | N/A (no gating events in single-user — nothing to own). | The tenant — gating events are tenant-scoped, never cross-tenant; platform admin sees none of it (design ownership rule). |
| Where does provider eligibility come from? | Irrelevant (no-op) — but the same `IProviderAuthLookup`/`Provider.AuthModel` source is wired; it simply isn't consulted because mode short-circuits first. | The `Provider` entity's `AuthModel` (34-11) via `IProviderAuthLookup`; interim static allowlist until 34-11. Platform-global (cost/auth model is the provider's, identical for every tenant). |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable, read once. | same |

### Fail-closed semantics (AC4, design §2.4)

In SaaS the order is: (1) classify via `IProviderAuthLookup` → `cli-token` or `null` (unknown) ⇒ DENY (400); (2) `api-key` ⇒ check entitlement → not entitled ⇒ DENY (403); else ALLOW. If the lookup itself fails (entity read error) or the mode source is unavailable, **DENY** — never a permissive default. This honours `feedback_resolution_no_empty_fallback` (never fall back to an empty/permissive credential or eligibility) and the §2.4 fail-closed rule ("if the credential, gate, or budget cannot be evaluated, deny").

### Single-user is a hard no-op

`InspectAsync` checks `Mode != SaaS` first and returns `Allowed` with no lookup, no event, no metric — verified by tests asserting zero side effects. This keeps self-hosted users' Claude Code / OpenCode / Zen MCP usage completely untouched and avoids polluting their event stream / metrics.

### Reuse, don't duplicate

The gate reads mode from the existing `ITammaModeProvider`, classifies via the 34-11 `Provider.AuthModel` (through `IProviderAuthLookup`; interim static off `ProviderAllowlist.DefaultProviders`), emits via the existing `IEventRepository`, and delegates entitlement to the existing Epic 34 SaaS auth seam. No new allowlist of provider names beyond the small interim `CliTokenProviders` set, no new mode plumbing, no new event store, no new entitlement engine.

### Provider classification source of truth

`claude-code`, `opencode`, `zen-mcp` are the `ICLIAgentProvider` implementations in `packages/providers` (`claude-agent-provider.ts` `name='claude-code'`, `opencode-provider.ts` `name='opencode'`; both registered via `BUILTIN_PROVIDER_NAMES`). At implementation time cross-check `BUILTIN_PROVIDER_NAMES` / `agent-provider-factory.ts` for any additional CLI-agent registrations and add them to the interim `CliTokenProviders` set; once 34-11 lands, the `Provider.AuthModel` column is the canonical source and the interim set is retired. The lookup unit test (every allowlist entry resolves to a deterministic model) surfaces any miss.

## Risks & Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| A `cli-token` provider leaks into SaaS execution | High | Gate is composition **step 1** of the endpoint — runs before agent resolution / credential / provider call; explicit SaaS+`cli-token` test asserts denial (400) and that no credential is resolved. |
| Unknown / future provider silently allowed in SaaS | High | Fail-closed: `IProviderAuthLookup` returns `null` for unknown ⇒ DENY (400); lookup unit test asserts every known provider has a deterministic model so a new provider can't be `null`-by-accident. |
| 34-11 not yet landed blocks this story | Medium | Ships against `StaticProviderAuthLookup` (interim); swap to `EntityProviderAuthLookup` is a DI-registration change with a contract-neutral test (Testing Strategy #5). |
| Gate throws and the endpoint returns a leaked 500 | Medium | `InspectAsync` returns a typed decision; only a contract violation (null context) may throw; event-append failure is swallowed; endpoint maps the typed decision to the §2.4 envelope. |
| 403 entitlement logic drifts from Epic 34 | Medium | Entitlement is **delegated** to the existing Epic 34 SaaS auth seam; this story owns only the typed surfacing of its result, not the entitlement rules. |
| Event/metric double-count or miss | Low | `EmitGatedAsync` is called once per denial path; tests assert exactly one event + one metric per SaaS denial and zero on allow. |

## Success Metrics

- [ ] In SaaS, 100% of `cli-token` and unknown providers are denied at endpoint step 1 (400) before any credential resolution; 0 `cli-token` provider calls reach a provider in SaaS.
- [ ] In single-user, the gate is a verified no-op (zero `AGENT.PROVIDER.GATED` events, zero `tamma.provider.gated` increments).
- [ ] Every SaaS denial emits exactly one `AGENT.PROVIDER.GATED` event + one metric increment; every allow has zero side effects.
- [ ] DI swap from `StaticProviderAuthLookup` to `EntityProviderAuthLookup` (34-11) passes the matrix with no AC/contract change.

## Related

- Design of record: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§2.4 error/gating, §2.6 step 1 gate stage, §4.2 `AuthModel` → SaaS-eligibility)
- Deep dive: `docs/superpowers/specs/2026-06-20-managed-llm-execution-deep-dive.md` (§1 provider duality — harness providers unreachable in SaaS)
- Re-plan: `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md`
- Implementation plan: `docs/superpowers/plans/2026-06-21-32-4-saas-provider-gate-plan.md`
- Consumer: `docs/stories/epic-32/story-32-5/` (call-LLM endpoint + `ManagedAgent.RunAsync` step 1)
- AuthModel source: `docs/stories/epic-34/story-34-11/` (Provider Cost Price-Book — `Provider.AuthModel`)
- Credential model: `docs/stories/epic-32/story-32-3/` (BYOK → platform; runs after the gate)

## Logging Requirements

- **INFO**: SaaS provider gated (provider, authModel, outcome, reason, role, action, tenantId) — one line per denial.
- **DEBUG**: gate inspected and allowed (provider, mode); single-user no-op short-circuit (mode).
- **WARN**: unknown provider denied fail-closed in SaaS (provider) — signals a request naming a provider with no `Provider` entity / not in the allowlist.
- **ERROR**: `AGENT.PROVIDER.GATED` event append failed (the typed decision still returns; the deny/allow is never masked by an event-store failure).
- **Structured context**: include `{ provider, authModel, mode, outcome, reason, role, action, tenantId }` where applicable.
- **Credential safety**: NEVER log API keys, tokens, or CLI credentials. The gate operates on provider **names** and the mode only and must never touch, read, or resolve secret material — assert this in tests (the gate has no credential-resolver dependency).

## Change Log

| Date       | Version | Changes                                                                                                                                                                                                 | Author |
| ---------- | ------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation (standalone two-seam gating: `IProviderAuthRegistry` + selection hook in `AgentEndpoints` + `ProviderChainResolver` skip)                                                          | Claude |
| 2026-06-21 | 2.0.0   | **Rewrite to the gate-stage model.** Reframed from a standalone gating story to the GATE STAGE (composition step 1) consumed by 32-5's `call-LLM` endpoint. `ISaaSProviderGate.InspectAsync` returns a TYPED `ProviderGateDecision` (no bare throw) the endpoint maps to the §2.4 envelope (400 `SAAS_PROVIDER_NOT_ALLOWED` / 403 `TENANT_NOT_ENTITLED`). Eligibility now sourced from 34-11's `Provider.AuthModel` via `IProviderAuthLookup` (interim `StaticProviderAuthLookup`); removed resolver-skip + selection-endpoint hooks. Added 403 entitlement outcome and fail-closed unknown-provider deny. | Claude |
