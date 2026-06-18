# Story 32-4: SaaS Provider Auth Gating — API-key only (CLI/token providers single-user only)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

## User Story

As a **platform operator running Tamma in SaaS mode**,
I want every agent definition, selection, and execution to be restricted to API-key (`ILLMProvider`) providers — never CLI/token (`ICLIAgentProvider`) providers,
So that a tenant can never select or run a local-binary / token-auth agent (e.g. the Claude Code CLI) inside the managed multi-tenant service, where there is no host shell, no per-tenant local credential store, and no isolation boundary for a subprocess agent.

## Priority

P0 — Required guardrail before the managed-execution layer (32-5) goes live; a SaaS tenant selecting a CLI/token provider is a correctness and security defect, not a degraded experience.

## Acceptance Criteria

1. A **provider capability registry** (`IProviderAuthRegistry`) classifies every known provider by its auth model — `ApiKey` vs `CliToken` — and exposes whether it is **SaaS-eligible** (`ApiKey` ⇒ eligible; `CliToken` ⇒ ineligible). The known-provider source of truth stays the existing `ProviderAllowlist.DefaultProviders` list; this story adds the auth-model dimension, it does not duplicate the allowlist.
2. In **SaaS mode**, creating, versioning, or selecting an agent whose resolved provider is `CliToken` returns **HTTP 400** with code `SAAS_PROVIDER_NOT_ALLOWED`, the offending provider named in the message and `TammaError.Context`, and the agent is **not** persisted/selected.
3. In **single-user mode**, `CliToken` providers remain fully usable end-to-end — the gate is a no-op (no rejection, no event, no telemetry counter increment).
4. The managed-execution entrypoint (32-5) **and** the existing provider-chain resolver (`ProviderChainResolver`) consult the registry. In SaaS mode the resolver **skips** `CliToken` entries from a chain (treats them as ineligible, not merely unhealthy); if a chain resolves to *only* ineligible entries, resolution fails closed with `SAAS_PROVIDER_NOT_ALLOWED` and emits `AGENT.PROVIDER.GATED`.
5. Mode is read **once** from the existing process-wide `ITammaModeProvider` (`TammaMode.cs`) — no new per-request mode plumbing, no per-request mode ambiguity. The gate is a pure function of `(mode, providerName)`.
6. The gate **reuses** the existing `ProviderAllowlist` / `ActionGate` seams in `Tamma.Activities/Security/` rather than duplicating them; new logic is **additive and fail-closed** — an unknown provider name (not in the registry) is treated as ineligible in SaaS mode (deny), and the existing allowlist rejection still runs first.
7. A DCB event **`AGENT.PROVIDER.GATED`** is appended (via `IEventRepository`) on every gated rejection, with `Data` = `{ provider, authModel, mode, reason, role?, action? }` and tenant-scoped `Tags`. Single-user mode never emits it.
8. An OpenTelemetry counter `tamma.provider.gated` is incremented on each gating decision, tagged `provider`, `auth_model`, `reason`. (Mirrors the `KekRotationMetrics` / existing-metrics pattern.)
9. **Tests** cover: SaaS rejects a `CliToken` provider at **selection** (agent create/version) and at **execution** (resolver / managed entrypoint); single-user allows the same `CliToken` provider in both paths; an `ApiKey` provider passes in **both** modes; an unknown provider is denied (fail-closed) in SaaS and (per existing allowlist) rejected in single-user; the gate is pure and emits exactly one event + one counter increment per gated decision.

## Technical Design

### Where gating is enforced (two seams)

The constraint is enforced at the **two boundaries a `CliToken` provider can enter the system in SaaS**:

```
                        ┌─────────────────────────────────────────────┐
                        │  ITammaModeProvider.Mode  (process-stable)   │
                        └───────────────────────┬─────────────────────┘
                                                 │
  (A) SELECTION boundary                         │      (B) EXECUTION boundary
  AgentRegistryService (32-2) /                  │      ProviderChainResolver  +
  AgentEndpoints create/version  ───────────────▶│◀───  managed-execution entrypoint (32-5)
                                                 │
                                   ┌─────────────▼─────────────┐
                                   │  ISaaSProviderGate         │  ← NEW, the single seam
                                   │  .Inspect(provider, role?, │
                                   │           action?)         │
                                   └─────────────┬─────────────┘
                                                 │ consults
                          ┌──────────────────────┴───────────────────────┐
                          │ IProviderAuthRegistry (NEW)                   │
                          │   AuthModel(provider) → ApiKey | CliToken     │
                          │   IsSaaSEligible(provider) → bool             │
                          │   (known set = ProviderAllowlist.Default...)  │
                          └───────────────────────────────────────────────┘
```

- **(A) Selection** is the *primary* gate — reject at create/version/select so a bad config never lands in `agent_configs`. This is where users get the actionable 400.
- **(B) Execution** is the *fail-closed backstop* — a config that predates the gate, or a default chain that still lists a CLI provider, must not silently execute a `CliToken` provider in SaaS. The resolver skips it; if nothing eligible remains, it fails closed.

Single-user mode short-circuits both: `ISaaSProviderGate.Inspect` returns `Allowed` immediately when `Mode == SingleUser`.

### Provider auth registry — `IProviderAuthRegistry`

New file `apps/tamma-elsa/src/Tamma.Activities/Security/IProviderAuthRegistry.cs` + `ProviderAuthRegistry.cs` (co-located with `ProviderAllowlist.cs` — same seam, same project).

```csharp
namespace Tamma.Activities.Security;

/// <summary>Auth model a provider uses to authenticate. Drives SaaS eligibility.</summary>
public enum ProviderAuthModel
{
    /// <summary>Cloud/local LLM API authenticated by an API key (ILLMProvider).
    /// SaaS-eligible — the key resolves from the principal's secret source (32-3).</summary>
    ApiKey,

    /// <summary>Headless CLI / token-based coding agent (ICLIAgentProvider, e.g.
    /// claude-code, opencode). Requires a host shell + local credential — NOT
    /// available in SaaS; single-user/self-hosted only.</summary>
    CliToken,
}

public interface IProviderAuthRegistry
{
    /// <summary>Auth model for a known provider; <c>null</c> if the provider is unknown.</summary>
    ProviderAuthModel? AuthModel(string? providerName);

    /// <summary>True iff the provider is API-key (and therefore SaaS-eligible).
    /// Unknown providers return <c>false</c> (fail-closed).</summary>
    bool IsSaaSEligible(string? providerName);
}
```

`ProviderAuthRegistry` classifies the `ProviderAllowlist.DefaultProviders` set. The `CliToken` members are the providers backed by `ICLIAgentProvider` in `packages/providers`: **`claude-code`**, **`opencode`** (and `zen-mcp` is treated per its backing factory — verify at impl time against `BUILTIN_PROVIDER_NAMES`). Everything else in the allowlist (`anthropic`, `openai`, `openrouter`, `google`/`gemini`, `github-copilot`, `azure-openai`, `local-llm`, `ollama`, `lmstudio`, `together`, `groq`, `z-ai`) is `ApiKey`.

```csharp
public sealed class ProviderAuthRegistry : IProviderAuthRegistry
{
    // CLI/token-backed providers (ICLIAgentProvider). The complement of this
    // set within ProviderAllowlist.DefaultProviders is ApiKey.
    private static readonly HashSet<string> CliTokenProviders =
        new(StringComparer.OrdinalIgnoreCase) { "claude-code", "opencode" };

    public ProviderAuthModel? AuthModel(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return null;
        var name = providerName.Trim();
        if (!ProviderAllowlist.IsAllowedDefault(name)) return null; // unknown
        return CliTokenProviders.Contains(name)
            ? ProviderAuthModel.CliToken
            : ProviderAuthModel.ApiKey;
    }

    public bool IsSaaSEligible(string? providerName) =>
        AuthModel(providerName) == ProviderAuthModel.ApiKey; // null/CliToken ⇒ false
}
```

> **Design note — single source of known providers.** The registry does not re-list every provider; it derives the `ApiKey` set as `ProviderAllowlist.DefaultProviders \ CliTokenProviders`. Adding a future provider to the allowlist auto-classifies it `ApiKey` unless it is also added to `CliTokenProviders`. A unit test asserts every allowlist entry has a deterministic auth model so a new provider can't be silently miscategorised.

### The gate — `ISaaSProviderGate`

New file `apps/tamma-elsa/src/Tamma.Api/Services/Security/ISaaSProviderGate.cs` + `SaaSProviderGate.cs` (Api-side because it needs `ITammaModeProvider` and emits DCB events; the registry stays in Activities so engine activities can reuse the classification).

```csharp
namespace Tamma.Api.Services.Security;

public sealed record ProviderGateContext(
    string ProviderName,
    string? Role = null,
    string? Action = null,
    Guid? TenantId = null);

public sealed record ProviderGateDecision(
    bool Allowed,
    string? Reason,                 // null when Allowed
    ProviderAuthModel? AuthModel);

public interface ISaaSProviderGate
{
    /// <summary>
    /// Pure-ish decision: single-user ⇒ always Allowed (no event, no metric).
    /// SaaS ⇒ Allowed only when the provider is SaaS-eligible (API-key).
    /// On a SaaS denial this emits AGENT.PROVIDER.GATED + the metric as a side
    /// effect (fire-and-forget, never throws back into the decision).
    /// </summary>
    Task<ProviderGateDecision> InspectAsync(ProviderGateContext ctx, CancellationToken ct = default);

    /// <summary>
    /// Convenience for selection paths: throws TammaError(SAAS_PROVIDER_NOT_ALLOWED,
    /// severity High) when denied, so the endpoint's existing TammaError→400 mapping
    /// produces the response with no extra branching.
    /// </summary>
    Task EnsureAllowedAsync(ProviderGateContext ctx, CancellationToken ct = default);
}
```

`EnsureAllowedAsync` throws:

```csharp
throw new TammaError(
    code: "SAAS_PROVIDER_NOT_ALLOWED",
    message: $"Provider '{ctx.ProviderName}' uses CLI/token authentication and is not " +
             "available in SaaS mode. Use an API-key provider (e.g. anthropic, openai).",
    context: new Dictionary<string, object?>
    {
        ["provider"]  = ctx.ProviderName,
        ["authModel"] = "cli-token",
        ["mode"]      = "saas",
        ["role"]      = ctx.Role,
        ["action"]    = ctx.Action,
    },
    retryable: false,
    severity: TammaErrorSeverity.High);
```

### DCB event — `AGENT.PROVIDER.GATED`

Appended via `IEventRepository.AppendAsync` (same pattern as `AgentEndpoints` `AGENT_CONFIG.UPDATED.SUCCESS`, ~line 93). Emitted **only** in SaaS on a denial.

```csharp
await _events.AppendAsync(new DomainEvent
{
    Id = Guid.NewGuid(),
    Type = "AGENT.PROVIDER.GATED",
    TenantId = ctx.TenantId,
    Tags = JsonSerializer.Serialize(new
    {
        tenantId = ctx.TenantId?.ToString(),
        provider = ctx.ProviderName,
        authModel = "cli-token",
        mode = "saas",
        role = ctx.Role,
        action = ctx.Action,
    }),
    Metadata = JsonSerializer.Serialize(new { workflowVersion = "1.0.0", eventSource = "system" }),
    Data = JsonSerializer.Serialize(new
    {
        provider = ctx.ProviderName,
        authModel = "cli-token",
        mode = "saas",
        reason = "SAAS_PROVIDER_NOT_ALLOWED",
        role = ctx.Role,
        action = ctx.Action,
    }),
    CreatedAt = DateTime.UtcNow,
});
```

Event-store note: appended via the CP/tenant `IEventRepository` exactly as sibling endpoints do; no new event topology. Failure to append is logged and swallowed so it never converts a clean 400 into a 500.

### Resolver integration (execution boundary)

`ProviderChainResolver.ResolveAsync` gains an injected `IProviderAuthRegistry?` (null-tolerant ctor overload, matching the existing optional `IDiagnosticsService?` pattern) **plus** an `ITammaModeProvider`. In the per-entry loop, before the circuit-breaker switch:

```csharp
// SaaS gating — exclude CLI/token providers entirely (not "unhealthy",
// "ineligible"). Single-user: registry/mode null-check makes this a no-op.
if (_mode?.Mode == TammaMode.SaaS && _authRegistry is not null
    && !_authRegistry.IsSaaSEligible(handle.Provider))
{
    skipped.Add(new ChainEntry(handle, ChainReason.SaaSIneligible,
        Healthy: false, CircuitOpen: false, CircuitOpenUntil: null,
        BudgetAllowed: budgetAllowed, BudgetSpent: budgetSpent, Recommended: false));
    continue;
}
```

A new `ChainReason.SaaSIneligible` is added to `ProviderChainTypes.cs`. When the ordered set is empty *because of* gating, the existing "all exhausted" return is reused but with `ErrorCode: "SAAS_PROVIDER_NOT_ALLOWED"`, and the gate's `InspectAsync` is called once for the first gated entry so the `AGENT.PROVIDER.GATED` event + metric fire. (Existing `EMPTY_PROVIDER_CHAIN` / `NO_AVAILABLE_PROVIDER` returns are unchanged when no gating occurred.)

### Selection integration (selection boundary)

`AgentRegistryService` (32-2 — **NEW**, dependency) and the agent create/version path in `AgentEndpoints` call `ISaaSProviderGate.EnsureAllowedAsync` for **each** provider referenced by the submitted agent config (primary + every fallback in the chain) before persisting. The existing `TammaError`→400 endpoint mapping handles the response. Where 32-2 is not yet merged, this story wires the gate into the existing `AgentEndpoints` create/version write (`configRepo.UpsertAsync`, ~line 89) and leaves a documented hook for `AgentRegistryService`.

### OpenTelemetry metric

New `ProviderGatingMetrics` (mirrors `KekRotationMetrics`): a `Meter`-registered `Counter<long> tamma.provider.gated`, incremented in `InspectAsync` on a SaaS denial with tags `provider`, `auth_model`, `reason`.

## Dependencies

- **Prerequisite**: Story **32-2** (Agent registry, resolution & RBAC API) — provides the selection seam (`AgentRegistryService`) the gate hooks into. If 32-2 is not yet merged, the selection gate attaches to the existing `AgentEndpoints` create/version write and the resolver gate (execution boundary) ships fully regardless.
- **Prerequisite**: Story **32-3** (Per-tenant provider credential resolution, BYOK → platform) — defines the API-key credential model the `ApiKey` classification presumes; gating runs *before* credential resolution.
- **Reuses (no change required)**: `ProviderAllowlist` / `ActionGate` (`Tamma.Activities/Security/`), `ITammaModeProvider` (`Services/PromptStore/TammaMode.cs`), `IEventRepository` (`Tamma.Data/Repositories/`), `ProviderChainResolver` + `ProviderChainTypes` (`Services/Providers/`), `TammaError` (`Tamma.Core/`).
- **Blocks**: Story **32-5** (Managed agent execution layer) — its `IManagedAgent` entrypoint must call the execution-boundary gate before dispatching; this story delivers the gate it consumes.
- **Design alignment**: `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` §"Provider credential & auth model" ("SaaS = API-key auth only … `ICLIAgentProvider` are NOT available in SaaS").

## Testing Strategy

1. **Registry unit tests** (`tests/Tamma.Activities.Tests/Security/ProviderAuthRegistryTests.cs`): every `ProviderAllowlist.DefaultProviders` entry has a deterministic `AuthModel` (no `null`); `claude-code`/`opencode` ⇒ `CliToken` / not SaaS-eligible; `anthropic`/`openai`/`openrouter`/`gemini` ⇒ `ApiKey` / SaaS-eligible; unknown provider ⇒ `AuthModel == null` and `IsSaaSEligible == false` (fail-closed); case-insensitivity.
2. **Gate unit tests** (`tests/Tamma.Api.Tests/Security/SaaSProviderGateTests.cs`): **single-user** — every provider (incl. `claude-code`) ⇒ `Allowed`, **zero** events, **zero** counter increments; **SaaS** — `ApiKey` provider ⇒ `Allowed` (no event); `CliToken` provider ⇒ denied, **exactly one** `AGENT.PROVIDER.GATED` event with the right `Data`/`Tags`, **exactly one** counter increment; unknown provider ⇒ denied (fail-closed); `EnsureAllowedAsync` throws `TammaError("SAAS_PROVIDER_NOT_ALLOWED", severity High)` with the provider named in `Context`.
3. **Resolver mode-gating tests** (`tests/Tamma.Api.Tests/Providers/ProviderChainResolverSaaSGatingTests.cs`): SaaS — chain `[claude-code, anthropic]` ⇒ `claude-code` in `Skipped` with `ChainReason.SaaSIneligible`, `anthropic` recommended; chain `[claude-code]` only ⇒ `ErrorCode == "SAAS_PROVIDER_NOT_ALLOWED"`, `AllExhausted`, one event; single-user — same chains resolve `claude-code` normally, no event, no skip-for-gating; the existing `EMPTY_PROVIDER_CHAIN` / `NO_AVAILABLE_PROVIDER` / budget paths are unchanged (regression assertions on existing resolver tests).
4. **Selection endpoint tests** (`tests/Tamma.Api.Tests/Agents/` or extend `AgentEndpoints` tests): SaaS create/version with a `CliToken` provider in the chain ⇒ **400** `SAAS_PROVIDER_NOT_ALLOWED`, **not** persisted, one event; SaaS with all-`ApiKey` chain ⇒ persists; single-user with a `CliToken` chain ⇒ persists, no event.
5. **Mode-matrix table test**: parameterise `(mode ∈ {SingleUser, SaaS}) × (provider ∈ {anthropic, claude-code, unknown})` over both boundaries and assert the 12-cell allow/deny/event/metric matrix — the canonical guard against regressions.
6. **C# suites run** via `sg docker -c "dotnet test ..."` (session docker group is stale — see `reference_dotnet_test_docker.md`). TDD: tests authored red before implementation per `superpowers:test-driven-development`.

## Estimated Effort

2-3 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Activities/Security/IProviderAuthRegistry.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/Security/ProviderAuthRegistry.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Security/ISaaSProviderGate.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Security/SaaSProviderGate.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Security/ProviderGatingMetrics.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderChainResolver.cs` | Modify (inject registry + mode; skip ineligible; fail-closed return) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderChainTypes.cs` | Modify (add `ChainReason.SaaSIneligible`) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs` | Modify (call `EnsureAllowedAsync` on create/version) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentRegistryService.cs` | Modify (gate at selection — **NEW from 32-2; hook only if merged**) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (DI: register registry, gate, metrics) |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Security/ProviderAuthRegistryTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Security/SaaSProviderGateTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Providers/ProviderChainResolverSaaSGatingTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentSelectionGatingTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` directory for related spikes, bugs, findings, and decisions
3. Reviewed the Epic 32 design spec (`docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`), §"Provider credential & auth model"
4. Confirmed the two provider hierarchies in `packages/providers/src/types.ts`: `ILLMProvider` (`type: 'llm-api'`, API-key) vs `ICLIAgentProvider` (`type: 'cli-agent'`, CLI/token)
5. Planned TDD approach (Red-Green-Refactor) — write the mode-matrix test first

### Why two seams, not one

The selection gate alone is insufficient: default provider chains and pre-gate configs already in `agent_configs` can name a CLI provider. The resolver/execution gate is the fail-closed backstop required by AC4 — without it, a stale config would silently dispatch a `CliToken` provider in SaaS. The selection gate alone would also be insufficient because 32-5's managed entrypoint can be reached by paths other than a fresh create. Both boundaries consult the *same* `IProviderAuthRegistry`, so classification is single-sourced.

### Fail-closed semantics (AC6)

In SaaS, the order at every boundary is: (1) existing `ProviderAllowlist.IsAllowed` (rejects junk/injection names — unchanged), then (2) `IProviderAuthRegistry.IsSaaSEligible`. An *unknown* provider is rejected by (1) already; a *known CliToken* provider passes (1) but is denied by (2). The registry returning `false` for unknown names is belt-and-suspenders so a future code path that bypasses (1) still denies in SaaS.

### Single-user is a hard no-op

`InspectAsync` checks `Mode == SingleUser` first and returns `Allowed` with no event and no metric increment — verified by tests asserting zero side effects. This keeps self-hosted users' Claude Code CLI usage completely untouched and avoids polluting their event stream / metrics.

### Reuse, don't duplicate

`ProviderAuthRegistry` derives its known set from `ProviderAllowlist.DefaultProviders` (does not re-declare provider names beyond the small `CliTokenProviders` set). `SaaSProviderGate` reads mode from the existing `ITammaModeProvider` and emits via the existing `IEventRepository`. No new allowlist, no new mode plumbing, no new event store.

### Provider classification source of truth

`claude-code` and `opencode` are the `ICLIAgentProvider` implementations in `packages/providers` (`claude-agent-provider.ts` `name = 'claude-code'`, `opencode-provider.ts` `name = 'opencode'`; both registered via `BUILTIN_PROVIDER_NAMES`). At implementation time, cross-check `BUILTIN_PROVIDER_NAMES` / `agent-provider-factory.ts` for any additional CLI-agent registrations (e.g. `zen-mcp`) and add them to `CliTokenProviders` — the registry test that asserts a deterministic auth model for every allowlist entry will surface any miss.

## Logging Requirements

- **INFO**: SaaS provider gated (provider, authModel, role, action, tenantId) — one line per denial.
- **DEBUG**: Gate inspected and allowed (provider, mode); resolver skipped a SaaS-ineligible entry (provider).
- **WARN**: Unknown provider denied in SaaS (provider) — signals a config referencing a provider not in the allowlist.
- **ERROR**: `AGENT.PROVIDER.GATED` event append failed (the decision still returns; the throw/deny is never masked by an event-store failure).
- **Structured context**: include `{ provider, authModel, mode, role, action, tenantId, reason }` where applicable.
- **Credential safety**: NEVER log API keys, tokens, or CLI credentials — the gate operates on provider *names* only and must never touch secret material.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
