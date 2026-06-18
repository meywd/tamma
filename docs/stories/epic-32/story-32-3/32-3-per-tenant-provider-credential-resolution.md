# Story 32-3: Per-Tenant Provider Credential Resolution (BYOK → platform)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../BEFORE_YOU_CODE.md)

> **Boundary note (canonical ownership):** This story is the **canonical owner of BYOK provider-key
> resolution** from the Epic 29 secret cabinet into the LLM call path. Epics 34 (pricing/markup) and
> 35 (billing) **consume** the `credentialSource` (`byok | platform`) this story emits; they MUST NOT
> re-wire provider keys. All "where does the provider API key come from at execution time?" logic
> lives behind the single `IProviderCredentialResolver` seam introduced here.

## User Story

As a **SaaS tenant administrator (and, in single-user mode, the self-hosted operator)**,
I want each LLM call to use **my own provider API key (BYOK) when I have configured one**, and to
fall back to the platform-provided key only when I have not — with the call **denied loudly** rather
than silently using the wrong key when neither is allowed,
So that my LLM usage hits **my** provider account and budget where I want it, cost/performance is
genuinely attributed per-tenant for benchmarking (Epic 32) and pricing (Epics 34/35), and a missing
key never leaks across tenants or degrades into a wrong-account charge.

## Priority

P0 — Closes the global-env API-key gap. Without it, every tenant's LLM call uses the single
platform `Anthropic:ApiKey` / `OpenAI:ApiKey`, so cost attribution, BYOK, and per-tenant budgets
(Epic 32 §"Provider credential & auth model", Risk "Global provider keys today") are all impossible.
Blocks 32-5 (managed agent execution), 32-9 (usage/cost emission), 34/35 (pricing/billing branch on
`credentialSource`).

## Acceptance Criteria

1. A new `IProviderCredentialResolver` resolves `(tenantId?, providerName)` →
   `ProviderCredential { ApiKey, Source ∈ {Byok, Platform}, SecretRef?, VersionNumber? }` by first
   querying the Epic 29 cabinet for a **Tenant-scoped `ApiKey` secret named by provider**
   (`SecretRef.ForTenant(tenantId, "provider/<name>/api-key")`, `SecretScope.Tenant`,
   `SecretPurpose.ApiKey`) and only when absent falling back to the platform-provided key.
2. The platform fallback key is read through the **existing cabinet runtime path**
   (`IRuntimeSecretResolver.GetAsync("<provider>/api-key")`, e.g. `StopgapSecretMap.PlatformAnthropicApiKey`)
   — NOT a fresh `_configuration["Anthropic:ApiKey"]` read — so the env-var fallback removal of
   Story 29-10 applies uniformly and there is one platform-key source of truth.
3. `CallLlmInlineActivity` **stops reading `_configuration["<Provider>:ApiKey"]` directly** in
   `LoadProviderConfig`; the activity instead receives the workflow's tenant context (new
   `TenantIdProp` input, threaded from `LlmCallWorkflow`'s existing `TenantId` variable) and resolves
   the key via `IProviderCredentialResolver`. `LlmProviderConfig.ApiKey` is populated from the
   resolved credential; `BaseUrl` / `DefaultModel` / `TimeoutSeconds` keep their current config
   source.
4. The resolved **credential source** (`byok | platform`) is propagated into the call result
   (`ProviderAttemptDiagnostic.CredentialSource`) and surfaced to the diagnostics / action-trail
   layer as a tag (`credentialSource`) so metering/pricing (32-9, 34, 35) and benchmarking (32-10)
   can branch on it — never the key itself.
5. Tenant BYOK keys are **written and read only through the secret cabinet** (create uses the
   reveal-once UX of Story 29-3; runtime read uses `ISecretStoreBackend.GetVersionPlaintextAsync` on
   the active version, mirroring `RuntimeSecretResolver`). Raw keys **never** appear in DCB events,
   diagnostics, action-trail tags, exceptions, or logs — proven by a dedicated redaction test that
   feeds a known sentinel key through the full resolve→call→event path and asserts the sentinel is
   absent from every emitted artifact.
6. **Fail-closed (SaaS):** when mode is SaaS and **no tenant BYOK key exists** and **platform
   fallback is disabled for that provider/plan**, the call returns a typed
   `TammaError("PROVIDER_CREDENTIAL_UNAVAILABLE", …, retryable: false, severity: High)` and emits
   `AGENT.CREDENTIAL.DENIED` — it does NOT silently use a wrong/empty key. **Single-user** mode falls
   back to the local platform/env credential (its sole user owns everything).
6.1. Whether platform fallback is allowed is decided by a `IPlatformFallbackPolicy` seam
   (`IsPlatformFallbackAllowed(tenantId, providerName)`). v1 default: single-user ⇒ always allowed;
   SaaS ⇒ allowed unless explicitly disabled by config (`Providers:PlatformFallbackDisabled` set, or
   per-provider override). The plan-level / per-provider gating that Epics 34/35 will drive plugs in
   here without changing the resolver.
7. A **tenant-admin BYOK management API** registers / rotates / removes a tenant provider key,
   delegating to the cabinet (create → reveal-once; rotate → `ISecretStore.RotateAsync`; remove →
   retire), RBAC-gated to `tenant_owner` / `tenant_admin` (member → 403; cross-tenant → 404), mounted
   on `AgentEndpoints` (or a new `ProviderCredentialEndpoints`) at
   `POST/DELETE /api/v1/agents/providers/{provider}/credential` and `POST …/rotate`.
8. DCB events are emitted via `IEventRepository.AppendAsync`: `AGENT.CREDENTIAL_RESOLVED.SUCCESS`
   (tags: `tenantId`, `provider`, `source`, `secretRef` storage-key, `mode`; **no secret in `Data`**)
   on every successful resolve, and `AGENT.CREDENTIAL.DENIED` (tags: `tenantId`, `provider`, `reason`,
   `mode`) on fail-closed. Event `Type` follows the `AGGREGATE.ACTION.STATUS` convention.
9. **Cache + rotation:** resolved BYOK plaintext is cached in-process with a short TTL (default 60s,
   matching `RuntimeSecretResolver.DefaultCacheTtl`), keyed by `(tenantId, provider)`. A BYOK
   register/rotate/remove (AC7) **invalidates** the affected cache entry immediately, and a
   `SECRET.ROTATE.ACTIVATED` event for a matching cabinet ref invalidates it too — so the next call
   resolves the new version. A rotation test asserts the resolver returns the new key after
   invalidation, not the stale cached one.
10. **Per-mode ownership** is explicit (two-scoping-model rule, CLAUDE.md "Operating Modes"):
    single-user resolves with `tenantId == null` → platform/local credential; SaaS resolves with the
    workflow's `tenantId` → BYOK-then-platform. The resolver never reads a `user_id`-keyed credential
    (BYOK is tenant-scoped only, mirroring the Prompt Store's "no per-user override in SaaS").
11. **Tenant isolation:** the resolver only ever reads `SecretScope.Tenant` rows for the **caller's**
    `tenantId`; an isolation test proves tenant A's resolve never returns tenant B's BYOK key, and a
    tenant with no BYOK key gets the platform key (never another tenant's).
12. Backward compatibility: when no BYOK key and platform fallback allowed (single-user, or SaaS with
    fallback enabled), behaviour is **byte-identical** to today's platform-key call path; existing
    `CallLlmInlineActivity` single-turn and tool-loop tests continue to pass with the platform source.
13. Unit tests cover: BYOK-present → tenant key + `source=byok`; BYOK-absent → platform key +
    `source=platform`; both-absent in SaaS → `PROVIDER_CREDENTIAL_UNAVAILABLE` + `AGENT.CREDENTIAL.DENIED`;
    secret redaction (AC5); rotation invalidates cache (AC9); tenant isolation (AC11); single-user
    fallback (AC6); RBAC matrix on the management API (AC7).

## Technical Design

### The gap today (verified 2026-06-17)

`apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` → `LoadProviderConfig`
(lines ~1398–1443) reads provider keys **directly from process config**, tenant-agnostic:

```csharp
// CURRENT — the global-env-key gap (CallLlmInlineActivity.LoadProviderConfig)
"anthropic" => new LlmProviderConfig {
    ApiKey = _configuration?["Anthropic:ApiKey"] ?? "",   // ← one key for ALL tenants
    ...
},
"openai" => new LlmProviderConfig {
    ApiKey = _configuration?["OpenAI:ApiKey"] ?? "",       // ← same problem
    ...
},
```

`LlmCallWorkflow` already threads a `TenantId` string variable into other activities
(`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` lines ~70, 153, 208, 276 —
`tenantIdVar.Set(context, context.GetInput<string>("tenantId") ?? "")`), but **CallLlmInlineActivity
does not receive it**. Wiring that variable into a new `TenantIdProp` input is the integration seam.

### New seam: `IProviderCredentialResolver`

Lives in `apps/tamma-elsa/src/Tamma.Api/Services/Providers/` next to `ProviderChainResolver`.

```csharp
namespace Tamma.Api.Services.Providers;

public enum CredentialSource { Byok, Platform }

/// <summary>
/// Resolved provider credential. ApiKey is plaintext for the immediate HTTP call ONLY —
/// it is never serialized into events, diagnostics, or logs (see redaction test, AC5).
/// </summary>
public sealed record ProviderCredential(
    string ApiKey,
    CredentialSource Source,
    string? SecretRefStorageKey,   // e.g. "tenant:<guid>:provider/anthropic/api-key" — safe to log/tag
    int? VersionNumber)
{
    /// <summary>Tag-safe projection (NEVER includes ApiKey) for diagnostics/DCB events.</summary>
    public object ToTag() => new { source = Source.ToString().ToLowerInvariant(),
                                   secretRef = SecretRefStorageKey, version = VersionNumber };
}

public interface IProviderCredentialResolver
{
    /// <summary>
    /// Resolve the API key for (tenant, provider). tenantId == null ⇒ single-user / platform scope.
    /// Order: tenant BYOK cabinet key → platform-provided key. Fail-closed in SaaS when neither
    /// is available/allowed (throws TammaError "PROVIDER_CREDENTIAL_UNAVAILABLE" + emits DENIED).
    /// </summary>
    Task<ProviderCredential> ResolveAsync(
        Guid? tenantId, string providerName, CancellationToken ct = default);

    /// <summary>Invalidate the cached BYOK entry for (tenant, provider). Called on register/rotate/remove.</summary>
    void Invalidate(Guid? tenantId, string providerName);
}
```

### Resolution algorithm (`DefaultProviderCredentialResolver`)

```csharp
public async Task<ProviderCredential> ResolveAsync(Guid? tenantId, string providerName, CancellationToken ct)
{
    var provider = NormalizeProvider(providerName);            // lower-invariant, allowlist-checked

    // 1) BYOK — only in SaaS / when a tenant is present.
    if (tenantId is { } tid)
    {
        if (_cache.TryGet((tid, provider), out var cached) && !cached.Expired)
            return EmitResolved(tid, provider, cached.Credential, ct);

        var byokRef = SecretRef.ForTenant(tid, ByokName(provider));      // "provider/anthropic/api-key"
        var meta = await _secretStore.GetAsync(byokRef, ct);
        if (meta is { ActiveVersionNumber: > 0 })
        {
            // Plaintext via backend, NOT ISecretStore (which never surfaces plaintext) — mirrors RuntimeSecretResolver.
            var plaintext = await _backend.GetVersionPlaintextAsync(meta.Id, meta.ActiveVersionNumber, ct);
            if (!string.IsNullOrWhiteSpace(plaintext))
            {
                var cred = new ProviderCredential(plaintext, CredentialSource.Byok,
                                                  byokRef.ToStorageKey(), meta.ActiveVersionNumber);
                _cache.Set((tid, provider), cred, _cacheTtl);
                return await EmitResolved(tid, provider, cred, ct);
            }
        }
    }

    // 2) Platform fallback — gated.
    if (_fallbackPolicy.IsPlatformFallbackAllowed(tenantId, provider))
    {
        var platformKey = await _runtimeSecrets.GetAsync(PlatformCabinetName(provider), ct); // cabinet → (29-9 window) config
        if (!string.IsNullOrWhiteSpace(platformKey))
        {
            var cred = new ProviderCredential(platformKey, CredentialSource.Platform,
                                              $"platform:{PlatformCabinetName(provider)}", null);
            return await EmitResolved(tenantId, provider, cred, ct);
        }
    }

    // 3) Fail-closed (SaaS). Single-user reaches here only if even the platform key is unset → still loud.
    await EmitDenied(tenantId, provider, reason: "no_byok_and_platform_unavailable", ct);
    throw new TammaError(
        "PROVIDER_CREDENTIAL_UNAVAILABLE",
        $"No usable credential for provider '{provider}'" +
        (tenantId is null ? " (platform key unset)." : " (no tenant BYOK key and platform fallback unavailable)."),
        new Dictionary<string, object?> { ["tenantId"] = tenantId, ["provider"] = provider },
        retryable: false, severity: TammaErrorSeverity.High);
}
```

- **Provider → cabinet name map** (constants, no string drift): `anthropic` → BYOK
  `provider/anthropic/api-key`, platform `StopgapSecretMap.PlatformAnthropicApiKey` (`"anthropic/api-key"`);
  `openai` → BYOK `provider/openai/api-key`, platform `"openai/api-key"`; `openrouter` similar. Map
  lives in a `ProviderCabinetNames` static so AC1/AC2 names cannot drift from the management API.
- **Plaintext read path is the cabinet backend** (`ISecretStoreBackend.GetVersionPlaintextAsync`),
  exactly as `RuntimeSecretResolver.TryReadCabinetAsync` does — `ISecretStore` deliberately never
  returns plaintext through its public surface (see `ISecretStore.cs` "Plaintext rule").

### Cache + rotation (AC9)

In-process `ConcurrentDictionary<(Guid,string), CacheEntry>`, TTL default
`RuntimeSecretResolver.DefaultCacheTtl` (60s). `Invalidate((tenant,provider))` removes the entry and
is called by the management API on register/rotate/remove. A lightweight handler subscribes to
`SECRET.ROTATE.ACTIVATED` (the same event `RuntimeSecretResolver` documents for invalidation) and, if
the rotated `SecretRef` matches a `provider/*/api-key` tenant ref, evicts `(tenantId, provider)`.

### Wiring into `CallLlmInlineActivity`

```csharp
// New input, set from LlmCallWorkflow's existing TenantId variable.
[Input(Description = "Tenant id (GUID string) for BYOK credential resolution; empty = single-user/platform")]
public Input<string?> TenantIdProp { get; set; } = default!;

// New ctor dependency (null-tolerant for [JsonConstructor] + existing tests).
private readonly IProviderCredentialResolver? _credentialResolver;
```

`LoadProviderConfig` no longer sets `ApiKey`; a new async step resolves it just before the HTTP call:

```csharp
private async Task<LlmProviderConfig> LoadProviderConfigWithKeyAsync(
    string providerName, Guid? tenantId, ActivityExecutionContext ctx)
{
    var cfg = LoadProviderConfig(providerName);  // BaseUrl / DefaultModel / TimeoutSeconds only
    if (_credentialResolver is not null)
    {
        var cred = await _credentialResolver.ResolveAsync(tenantId, providerName, ctx.CancellationToken);
        cfg.ApiKey = cred.ApiKey;                 // plaintext used immediately, never stored
        ctx.SetVariable("CredentialSource", cred.Source.ToString().ToLowerInvariant()); // → diagnostic tag
    }
    return cfg;
}
```

- Tenant id parsed from `TenantIdProp` (empty/whitespace → `null` → single-user/platform).
- `ProviderAttemptDiagnostic` gains `string? CredentialSource` (already serialized into
  `LastDiagnostic`; the diagnostics/action-trail layer reads it as the `credentialSource` tag).
- `PROVIDER_CREDENTIAL_UNAVAILABLE` from the resolver is caught in the existing per-attempt
  try/catch so the provider chain can advance to the next provider; if the **whole chain** yields no
  usable credential, the last failure surfaces (fail-closed, no silent success).
- `LlmCallWorkflow`: set `TenantId = new Input<string>(ctx => tenantIdVar.Get(ctx))` on the
  `CallLlmInlineActivity` step (same pattern as the prompt/convention activities at lines 208, 276).

### BYOK management API (AC7)

```
POST   /api/v1/agents/providers/{provider}/credential   body { apiKey } → 201 (reveal-once token via 29-3)
POST   /api/v1/agents/providers/{provider}/credential/rotate  body { apiKey } → 200
DELETE /api/v1/agents/providers/{provider}/credential   → 204 (retire active version)
GET    /api/v1/agents/providers                          → list configured BYOK providers (metadata only, NO key)
```

- RBAC: `tenant_owner` / `tenant_admin` only (member → 403; cross-tenant → 404) — mirrors Prompt
  Store and `AgentEndpoints.UpdateConfig` tenant-context guard.
- Create → `ISecretStore.CreateAsync(new CreateSecretRequest { Scope = Tenant, TenantId = tid,
  Name = ProviderCabinetNames.Byok(provider), Purpose = ApiKey, InitialPlaintext = apiKey })`; rotate
  → `RotateAsync`; delete → retire active version. Every mutation calls `resolver.Invalidate` and is
  recorded via the cabinet's existing audit pipeline; the endpoint emits no raw key.

### Events (AC8)

```csharp
await _events.AppendAsync(new DomainEvent {
    Id = Guid.NewGuid(),
    Type = "AGENT.CREDENTIAL_RESOLVED.SUCCESS",
    TenantId = tenantId,
    Tags = JsonSerializer.Serialize(new {
        tenantId = tenantId?.ToString(), provider, source = cred.Source.ToString().ToLowerInvariant(),
        secretRef = cred.SecretRefStorageKey, mode = _mode.Mode.ToString() }),
    Metadata = JsonSerializer.Serialize(new { workflowVersion = "1.0.0", eventSource = "system" }),
    Data = JsonSerializer.Serialize(new { version = cred.VersionNumber }),  // NEVER cred.ApiKey
    CreatedAt = DateTime.UtcNow,
});
// AGENT.CREDENTIAL.DENIED on fail-closed: Tags { tenantId, provider, reason, mode }, Data {}.
```

## Dependencies

- **Prerequisite (hard): Epic 29 (secret cabinet)** — `ISecretStore`, `ISecretStoreBackend`,
  `SecretRef.ForTenant`, `SecretScope.Tenant`, `SecretPurpose.ApiKey`, `IRuntimeSecretResolver`
  (platform key path + cache/invalidate prior art), reveal-once create (Story 29-3), and the
  `SECRET.ROTATE.ACTIVATED` invalidation signal (Story 29-7).
- **Prerequisite: 32-1** — agent entity model (provider attribute / config that names the provider
  chain whose keys this story resolves).
- **Blocks: 32-4** (SaaS auth gating builds on the resolver), **32-5** (managed agent execution
  consumes resolved credentials), **32-9** (usage/cost emission tags by `credentialSource`),
  **Epic 34/35** (pricing/billing branch on `byok | platform`).
- **Related:** `ProviderChainResolver` (chooses *which* provider; this story resolves *its key*);
  `ITammaModeProvider` (`TammaMode.cs`) for single-user vs SaaS branch.

## Testing Strategy

1. **Resolver unit tests** (`tests/Tamma.Api.Tests/Providers/ProviderCredentialResolverTests.cs`,
   in-memory `ISecretStoreBackend` + fake `IRuntimeSecretResolver`):
   - BYOK present → returns tenant plaintext, `source=byok`, correct `SecretRefStorageKey` + version.
   - BYOK absent, platform present, fallback allowed → `source=platform`.
   - SaaS + BYOK absent + fallback disabled → throws `PROVIDER_CREDENTIAL_UNAVAILABLE`, emits
     `AGENT.CREDENTIAL.DENIED`, emits NO `RESOLVED.SUCCESS`.
   - Single-user (`tenantId=null`) + platform present → `source=platform` (never throws while key set).
   - Single-user + platform unset → still throws loud (no empty key).
2. **Tenant isolation** (AC11): seed BYOK for tenant A only; resolve as A → A's key; resolve as B →
   platform key (or denied), **never A's key**.
3. **Redaction** (AC5): seed a sentinel BYOK key (`SENTINEL-BYOK-XYZ`); run resolve → full
   `CallLlmInlineActivity` (mocked HTTP) → assert the sentinel appears in the outbound `x-api-key` /
   `Authorization` header ONLY, and is **absent** from every `DomainEvent` (`Tags`/`Data`),
   `ProviderAttemptDiagnostic`, exception message, and captured log line.
4. **Rotation / cache** (AC9): resolve (caches v1) → rotate BYOK → assert resolver returns v2 after
   `Invalidate` / `SECRET.ROTATE.ACTIVATED`; assert stale v1 not returned; assert TTL expiry also
   re-reads.
5. **Activity integration** (`tests/Tamma.Activities.Tests/LlmCall/`): `CallLlmInlineActivity` with
   `TenantIdProp` set resolves BYOK and sends it; with empty tenant resolves platform; existing
   single-turn + tool-loop tests still green with `source=platform` (AC12 backward-compat).
6. **Management API RBAC** (`tests/Tamma.Api.Tests/Agents/ProviderCredentialEndpointsTests.cs`):
   tenant_owner/admin can create/rotate/delete; member → 403; cross-tenant → 404; response bodies
   never contain the raw key; create→list shows metadata only; mutations invalidate the cache.
7. **Edge cases:** unknown/non-allowlisted provider; whitespace key rejected on create; cabinet probe
   throws → resolver does not leak, treats as BYOK-absent and proceeds to fallback (logs WARN).

## Estimated Effort

3–4 days (resolver + cache/invalidation ~1.5d; activity wiring + workflow input + diagnostics ~1d;
management API + RBAC ~0.75d; tests incl. redaction/isolation/rotation ~0.75d).

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Providers/IProviderCredentialResolver.cs` | Create (NEW — seam + `ProviderCredential`/`CredentialSource`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Providers/DefaultProviderCredentialResolver.cs` | Create (NEW — BYOK→platform algorithm + cache) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Providers/IPlatformFallbackPolicy.cs` | Create (NEW — fallback gating seam) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ConfigPlatformFallbackPolicy.cs` | Create (NEW — v1 mode/config-driven policy) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderCabinetNames.cs` | Create (NEW — BYOK + platform cabinet-name map) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderCredentialCacheInvalidator.cs` | Create (NEW — `SECRET.ROTATE.ACTIVATED` handler) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderCredentialEndpoints.cs` | Create (NEW — BYOK management API) |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/ProviderCredentialServiceCollectionExtensions.cs` | Create (NEW — DI wiring) |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` | Modify (NEW `TenantIdProp`, resolver ctor dep, drop direct `Anthropic/OpenAI:ApiKey` read, `CredentialSource` tag) |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs` | Modify (`ProviderAttemptDiagnostic.CredentialSource`) |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` | Modify (thread `TenantId` into `CallLlmInlineActivity` step) |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` | Modify (register resolver + invalidator) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map endpoints + DI) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Providers/ProviderCredentialResolverTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/ProviderCredentialEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/CallLlmInlineCredentialTests.cs` | Create |

## Dev Notes

### Development Process Reminder

1. Read [BEFORE_YOU_CODE.md](../../../BEFORE_YOU_CODE.md).
2. Search `.dev/` for related spikes/bugs/findings/decisions (esp. Epic 29 secret cabinet, KEK
   decision `project_epic28_kek_decision.md`).
3. Re-read the Epic 32 design (`docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`
   §"Provider credential & auth model") — this story is the canonical owner of its key wiring.
4. TDD (Red-Green-Refactor); docker-bound C# suites run via `sg docker -c "dotnet test ..."`.

### Key Design Decisions

- **Reuse the cabinet runtime path, don't reinvent it.** `RuntimeSecretResolver` already proves the
  exact runtime-plaintext-from-cabinet pattern (cabinet → backend `GetVersionPlaintextAsync` → cache
  → invalidate) — but platform-scoped only (`s.Scope == "platform"`, `StopgapSecretMap.Platform`).
  This story adds the **tenant-scoped** sibling and delegates the platform leg back to
  `IRuntimeSecretResolver` so there is one platform-key source of truth (AC2). Do NOT add a new
  `_configuration["…:ApiKey"]` read anywhere.
- **`ISecretStore` never surfaces plaintext** (its own doc-comment): use `ISecretStoreBackend` for the
  byte read at runtime, `ISecretStore` for create/rotate/retire/metadata. Honour that boundary.
- **Plaintext is request-scoped only.** `ProviderCredential.ApiKey` exists to feed one HTTP header
  and is dropped after; `ToTag()` is the only thing that ever reaches an event/diagnostic.
- **Fail-closed beats wrong-key.** A taxonomy of "no key" must throw (`PROVIDER_CREDENTIAL_UNAVAILABLE`),
  mirroring the project's no-empty-fallback rule for prompts/conventions
  (`feedback_resolution_no_empty_fallback`): never silently substitute.
- **Null-tolerant activity ctor.** Mirror the existing optional-dependency pattern in
  `CallLlmInlineActivity` (`[JsonConstructor]` + nullable deps) so DI + Elsa hydration + existing
  tests keep working; assert real wiring in a Program-level test.

### Integration Points

- `LlmCallWorkflow` `TenantId` variable (already present) → new `CallLlmInlineActivity.TenantIdProp`.
- `ProviderChainResolver` selects the provider; this resolver supplies that provider's key.
- 32-9 reads `credentialSource` from the diagnostic/action-trail to attribute cost (BYOK = tenant's
  account → cost basis differs from platform-metered).

### Risks and Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| Raw key leaks into a DCB event / log | Critical | Tag-only projection (`ToTag()`), `Data` never includes `ApiKey`, dedicated redaction test (AC5) on the full path |
| Cross-tenant key bleed via cache key collision | High | Cache keyed by `(tenantId, provider)`; isolation test (AC11); resolver only reads `SecretScope.Tenant` for caller's tenant |
| Stale key after rotation | Medium | Short TTL + explicit `Invalidate` on mutate + `SECRET.ROTATE.ACTIVATED` handler; rotation test (AC9) |
| Fail-closed breaks single-user dev who hasn't set any key | Medium | Single-user always allows platform fallback; only throws if even platform key unset (which is already broken today) — error is loud + actionable |
| Behaviour change for existing platform-key tenants | Medium | AC12 backward-compat: identical bytes when `source=platform`; existing activity tests must stay green |
| `_configuration` read sneaks back in during a future edit | Medium | Resolver is the only key source; add a test asserting `LoadProviderConfig` returns empty `ApiKey` |

### Success Metrics

- [ ] 100% of LLM calls resolve their key via `IProviderCredentialResolver` (zero direct
      `_configuration["…:ApiKey"]` reads remain in the call path).
- [ ] Tenants with a BYOK key see `credentialSource=byok` on their calls; cost attributes to them.
- [ ] Zero raw keys in any DCB event / log (redaction test green; spot-check of event store clean).

## Logging Requirements

- **INFO**: credential resolved (`tenantId`, `provider`, `source`, `version` — **never the key**);
  BYOK registered/rotated/removed (`tenantId`, `provider`, action); cache invalidated on rotation.
- **DEBUG**: cache hit/miss for `(tenantId, provider)`; cabinet probe result (found/absent, **no
  bytes**); platform fallback taken.
- **WARN**: cabinet probe threw (treated as BYOK-absent, fell through to fallback); platform key
  unset while fallback allowed.
- **ERROR**: fail-closed denial (`PROVIDER_CREDENTIAL_UNAVAILABLE`) — include `tenantId`, `provider`,
  `reason`, `mode`; never the (absent) key.
- **Credential safety**: NEVER log API keys, plaintext, or any substring thereof. Only
  `SecretRef.ToStorageKey()`, source, version, provider, tenant id are loggable.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
