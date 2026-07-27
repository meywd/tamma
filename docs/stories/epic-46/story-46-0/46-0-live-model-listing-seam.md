# Story 46-0: Live model listing — `ModelsEndpointPath`, one fetch/normalize/cache service, platform + tenant routes

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **platform owner or tenant admin choosing a model in the dashboard**,
I want the model dropdown to be populated from the provider's OWN live model-list API, fetched
server-side at view time,
So that when a provider releases a new model it appears in the picker with zero code or config
deploys — and when a provider is down or unconfigured, the picker still renders with the current
selection instead of breaking.

## Priority

P0 — this is the seam both UIs (46-2, 46-3) bind to, and the tool 46-1's defaults-refresh task
uses. It ships standalone: even before 46-1 lands, `GET /api/admin/providers` is a useful
"which providers are actually configured and reachable" status surface.

## Architectural Context (READ FIRST)

### Phase 1 plumbing this story reuses (do not rebuild any of it)

| Piece | Where | What this story does with it |
|---|---|---|
| `ProviderDescriptor` record | `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderDescriptor.cs:61-119` | gains one nullable field, `ModelsEndpointPath`, beside `ChatEndpointPath` (`:101`) |
| `ProviderCatalog.HttpProviders` | `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderCatalog.cs:28-232` | each descriptor gains its verified models path (table below) |
| `ProviderCatalog.CombineUrl` | `ProviderCatalog.cs:363-370` | THE url join — preserves base-path segments (groq's `/openai`, openrouter's `/api`), which is exactly what makes `/v1/models` against `https://api.groq.com/openai` come out as the documented `…/openai/v1/models` |
| `ProviderCatalog.IsDefaultBaseUrl` | `ProviderCatalog.cs:342-348` | the config-override rule (F3) applies to models too: an explicitly overridden base URL (an OpenAI-compatible proxy) gets the dialect-generic `/v1/models`, not a descriptor-specific path grafted onto a proxy |
| Per-call header application | `InlineToolLoopRunner` (unconfigured `RunnerHttpClientName` client, registered at `ProviderHttpClientServiceCollectionExtensions.cs:149`; headers from descriptor + resolved credential) | the models fetch uses the same pattern — never the config-baked named-client keys alone, because the platform key may live in the secret cabinet, not config |
| Credential resolution | `DefaultProviderCredentialResolver.cs:84-146` (`IProviderCredentialResolver`) | **already implements the required key policy**: BYOK when `tenantId` is present and a tenant key exists, platform key as the gated fallback, fail-closed otherwise. The admin route passes `tenantId: null`; the tenant route passes the caller's tenant id. Nothing new to build for "tenant's own key when present, else platform key". |
| Alias normalization | `ProviderCredentialEndpoints.NormalizeProvider` (`ProviderCredentialEndpoints.cs:333-352`) | the tenant models route reuses this exact helper shape: alias → canonical key → allowlist check → 404 on unknown (never enumerate) |
| Egress pins | `apps/tamma-elsa/tests/Tamma.Activities.Tests/Providers/ProviderEgressRegressionTests.cs` | extended with models-URL pins per descriptor |

### The verified wire facts (survey 2026-07-27 — see epic README for the full table + verification method)

`ModelsEndpointPath` values to ship:

| Key(s) | `ModelsEndpointPath` | Notes for the parser |
|---|---|---|
| `anthropic` | `/v1/models` | `{data:[{id, display_name, …}]}`; needs the descriptor's `anthropic-version` header (already per-descriptor data, `ProviderCatalog.cs:44-45`) — verified live |
| `openai` | `/v1/models` | OpenAI list envelope |
| `openrouter` | `/v1/models` | public — works with OR without a key; `name` is the display name |
| `gemini`, `google` | `/v1beta/openai/models` | verified live; ids come back as `models/gemini-…` — keep them verbatim (they are what the chat endpoint accepts on this surface) |
| `groq` | `/v1/models` | base `/openai` preserved by `CombineUrl` |
| `deepseek` | `/models` | no `/v1` — matches their chat-path convention already noted at `ProviderCatalog.cs:204-207` |
| `moonshot` | `/v1/models` | OpenAI list + extra capability fields (ignored) |
| `together` | `/v1/models` | **bare JSON array**, not `{data:[…]}`; `display_name` present |
| `local-llm`, `ollama`, `lmstudio` | `/v1/models` | Ollama OpenAI-compat + LM Studio both serve it; no auth locally |
| `z-ai` | `null` | no documented list route found (docs.z.ai unreachable from the sandbox; search empty). **Implementation task: re-check docs.z.ai before shipping**; if a route exists, set it — one line |
| `azure-openai` | `null` | listing needs `api-version` + returns deployments; out (epic D4) |
| `github-copilot` | `null` | requires Copilot token exchange; out (epic D4) |

Non-HTTP descriptors (`opencode`, `zen-mcp`) have no models endpoint by construction; the status
list reports them `modelsSupported: false` like any null-path descriptor.

### Why fetches go through the runner-style per-call pattern, not the config-baked named clients

The named clients (`ProviderHttpClientServiceCollectionExtensions.cs:24-142`) attach an auth header
only when `{Section}:ApiKey` exists in **configuration**. Production keys increasingly live in the
secret cabinet and resolve through `IProviderCredentialResolver` (Story 32-3), so a models fetch
riding the named client alone would silently go out unauthenticated for cabinet-keyed providers.
The correct composition — proven by `InlineToolLoopRunner` — is: unconfigured client
(`RunnerHttpClientName`), absolute URL via `CombineUrl`, headers applied per call from the
descriptor (`AuthScheme`, `VersionHeaderName/Value`) + the resolved credential. The **base URL**
still comes from the named client's `BaseAddress` when configured (that is where
`{Section}:BaseUrl` overrides land), falling back to `descriptor.DefaultBaseUrl` — same effective
base resolution as `HttpProviderClient.InvokeAsync` (`HttpProviderClient.cs:81-106`).

One deliberate exception to fail-closed: `openrouter`'s list is public and `local-llm`-family
providers have no key at all — a `PROVIDER_CREDENTIAL_UNAVAILABLE` from the resolver must downgrade
to an unauthenticated fetch attempt for descriptors marked key-optional-for-listing, and to the
fail-soft empty envelope for everything else. Keep this as a small allowlist inside the service
(`openrouter` + the three `local` clients), commented with why.

## Acceptance Criteria

1. **`ModelsEndpointPath` exists on `ProviderDescriptor`** (`string?`, XML-doc'd: *relative to the
   effective base URL; null = this provider's models cannot be listed and UIs fall back to free-text
   entry*), and every catalogue descriptor carries the value from the table above. The Z.ai
   implementation-time re-check is a checklist item in the PR description, with the result recorded
   in the descriptor comment either way.

2. **`IProviderModelCatalog` + `ProviderModelCatalogService`** exist in
   `apps/tamma-elsa/src/Tamma.Api/Services/Providers/`:
   `Task<ProviderModelList> ListModelsAsync(string providerKey, Guid? tenantId, CancellationToken)`.
   - Normalized entry: `ProviderModelInfo { Id, DisplayName?, Deprecated }` — `DisplayName` from
     `display_name` / `name` when present, else null; `Deprecated` true only when the provider
     payload carries a truthy `deprecated` field (none of the surveyed providers does today — the
     field exists so the shape doesn't change when one starts).
   - Envelope: `ProviderModelList { Models, FetchedAt, Stale, ErrorCode? }`.
   - Parser handles exactly **two shapes**: root object with `data` array, and root bare array
     (Together). Entries without a string `id` are skipped, not fatal.
   - Credential via `IProviderCredentialResolver.ResolveAsync(tenantId, key)` (BYOK-preferred for
     tenant callers, platform for `null`), with the key-optional allowlist described above.
   - HTTP via `IHttpClientFactory` + `ProviderCatalog.CombineUrl`, honouring the
     `IsDefaultBaseUrl` config-override rule: overridden base → generic `/v1/models`, per the same
     F3 semantics as `ChatPathForBase` (`ProviderCatalog.cs:333-337`). Timeout ≤ 10 s per fetch.

3. **5-minute cache, keyed `(providerKey, tenantId?)`.** In-process, `TimeProvider`-driven (same
   `CacheEntry`+TTL pattern as `DefaultProviderCredentialResolver.cs:52,296`). A fetch failure
   serves the last-known-good list for that key flagged `Stale = true` + `ErrorCode`; with no
   cached copy it returns an empty list + `ErrorCode`. The cache key includes the tenant id because
   a BYOK-fetched list may differ (entitlements) and must never leak across tenants.

4. **Platform route — `GET /api/admin/providers`** (new `ProviderAdminEndpoints.cs` under
   `Endpoints/`, mapped as its own group with `RequireAuthorization("PlatformOwnerAccess")`,
   modelled on the `adminConventions` group at `Program.cs:2654`). Returns one row per HTTP
   descriptor: `key`, `displayName`, `dialect`, `effectiveBaseUrl`, `keyConfigured` (credential
   resolver answers without throwing — never the key itself), `modelsSupported`
   (`ModelsEndpointPath != null`), `currentModel` + `source`
   (`config` | `descriptor` until 46-1 adds the DB layers; computed via the same precedence
   `InlineToolLoopRunner.LoadProviderConfig` uses, `InlineToolLoopRunner.cs:1099-1151`), and
   `aliases`. Non-HTTP descriptors appear with `transport` and `modelsSupported: false`.

5. **Platform route — `GET /api/admin/providers/{key}/models`** (same policy): resolves alias →
   canonical key (404 unknown, never enumerate — `NormalizeProvider` shape), calls
   `ListModelsAsync(key, tenantId: null)`, and **always injects the currently-effective model** as
   an entry flagged `current: true` (synthesized with `DisplayName = null` if the live list lacks
   it). Response: `{ provider, models: [{id, displayName?, deprecated, current}], fetchedAt,
   stale, errorCode? }`. Always HTTP 200 for a known provider — fail-soft per epic D6.

6. **Tenant route — `GET /api/v1/agents/providers/{provider}/models`**, registered beside the
   existing BYOK routes (`ProviderCredentialEndpoints.cs` maps; read-level policy — same as
   `ListProviders`, any tenant member; single-user: the sole user). Same response contract as AC5,
   but `ListModelsAsync(key, tenantContext.TenantId)` so a tenant BYOK key is used when present
   (epic D5). No tenant context in SaaS mode → empty providers behaviour consistent with
   `ProviderCredentialEndpoints.ListProviders` (`:57-61`).

7. **The credential never reaches the browser and never enters a log.** Response DTOs contain no
   key material; the service logs provider key, status code and duration only. Grep-checkable:
   no `ApiKey`/`Plaintext` reference in the endpoints file or DTOs.

8. **Egress pins.** `ProviderEgressRegressionTests` gains a models-URL table test: for every
   descriptor with a non-null `ModelsEndpointPath`, `CombineUrl(DefaultBaseUrl, ModelsEndpointPath)`
   equals the documented absolute URL (e.g. `https://api.groq.com/openai/v1/models`,
   `https://openrouter.ai/api/v1/models`, `https://api.deepseek.com/models`,
   `https://generativelanguage.googleapis.com/v1beta/openai/models`). This is the drift guard for
   the wire-facts table.

9. **Unit tests** (NUnit + FluentAssertions + Moq, in `Tamma.Api.Tests`): the two parser shapes
   (golden JSON fixtures per surveyed provider, including Together's bare array and Gemini's
   `models/…` ids kept verbatim); cache TTL + per-tenant key isolation; stale-serve on failure;
   empty+error with no cache; current-model injection when delisted; key-optional allowlist
   (openrouter fetch proceeds without credential; anthropic fetch without credential returns the
   fail-soft envelope, does not throw); 404 on unknown key; policy gating (member hits 403 on the
   admin routes; the tenant route serves members).

## Dependencies

- **Blocked by: nothing.** Phase 1 is merged; this builds directly on it.
- **Blocks:** 46-2 and 46-3 (both UIs consume these routes); 46-1's defaults-refresh task uses the
  live lists (soft — it can also use curl).
- **Coordination:** 46-1 edits `ProviderAdminEndpoints.cs` (adds settings mutation routes and the
  DB-aware `source` values) — land 46-0 first or expect a small merge in one file.

## Out of Scope

- The settings store, precedence resolver, and any mutation route (46-1).
- Both UIs (46-2, 46-3).
- Model capability metadata (context length, tool support) — deliberately not in the normalized
  shape (epic Out of scope).
- Azure OpenAI deployment listing and GitHub Copilot token exchange (epic D4).
- A background refresher/warmer for the cache. View-time fetch + 5-min TTL is the requirement;
  add a warmer only if the UIs prove slow in practice.

## Estimated Effort

3.5 days

## Change Log

| Date       | Version | Changes                                                             | Author |
| ---------- | ------- | ------------------------------------------------------------------- | ------ |
| 2026-07-27 | 1.0.0   | Initial story creation                                              | Claude |
| 2026-07-27 | 1.1.0   | Tenant-callable route + BYOK-preferred fetch added (PO decision)    | Claude |
