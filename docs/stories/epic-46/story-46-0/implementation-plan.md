# Implementation Plan — Story 46-0: Live Model Listing Seam

## Scope & Deliverable

When this story is done, `GET /api/admin/providers` lists every catalogue provider with its
configuration status, `GET /api/admin/providers/{key}/models` and
`GET /api/v1/agents/providers/{provider}/models` return the provider's live model list normalized to
`{id, displayName?, deprecated, current}` with a 5-minute cache and fail-soft stale/empty envelopes,
and every fetch authenticates server-side through the existing BYOK→platform credential resolver.
No UI ships here; no database row exists yet.

## Pre-Reading

- `docs/stories/epic-46/README.md` — the wire-facts table and decisions D4–D6
- `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderDescriptor.cs:98-112` — where
  `ModelsEndpointPath` slots (beside `ChatEndpointPath`, `VersionHeaderName`)
- `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderCatalog.cs:309-370` — `ChatPath`,
  `ChatPathForBase`, `IsDefaultBaseUrl`, `CombineUrl`; the models-path helper mirrors this trio
- `apps/tamma-elsa/src/Tamma.Api/Services/Providers/HttpProviderClient.cs:68-106` — descriptor →
  named client → base URL → `CombineUrl` composition to copy
- `apps/tamma-elsa/src/Tamma.Api/Services/Providers/DefaultProviderCredentialResolver.cs:84-146` —
  the resolver whose BYOK→platform order is the whole credential story
- `apps/tamma-elsa/src/Tamma.Api/Extensions/ProviderHttpClientServiceCollectionExtensions.cs:144-149`
  — the deliberately-unconfigured runner client precedent
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderCredentialEndpoints.cs:333-352` —
  `NormalizeProvider` (alias → canonical → allowlist → 404)
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:1809,2654` — route-group precedents (`/api/admin` with
  `AdminAccess`; `/api/admin/conventions` with `PlatformOwnerAccess`)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Providers/ProviderEgressRegressionTests.cs` — the
  pinning style to extend

## Design Decisions

- **D1 — One service, two routes, tenant id as the only difference.** The admin route calls
  `ListModelsAsync(key, null)`; the tenant route calls `ListModelsAsync(key, tenantContext.TenantId)`.
  The resolver's existing order does the rest (BYOK when present, platform fallback per policy).
  No second fetch path, no duplicated parser.

- **D2 — A `ModelsPathForBase` helper mirrors `ChatPathForBase`, including the F3 override rule.**
  A config-overridden base URL means "you are talking to a proxy"; the descriptor's
  provider-specific models path must not be grafted onto it. Overridden base + OpenAI-compatible
  dialect → `/v1/models`; overridden base + Anthropic dialect → `/v1/models` as well (Anthropic's
  own path IS `/v1/models`, so the distinction is theoretical today — state it in the helper's
  XML doc rather than leaving it to be re-derived).

- **D3 — Per-call headers on the unconfigured runner-style client; base URL from the named
  client.** The named clients' config-baked keys (`{Section}:ApiKey`) are a legacy convenience;
  the cabinet is the real key source. Copy `InlineToolLoopRunner`'s composition: resolve the named
  client only to read its `BaseAddress` (where `{Section}:BaseUrl` overrides land), then issue the
  request on the unconfigured client with descriptor-driven headers (`AuthScheme` switch +
  `VersionHeaderName/Value`) and the resolved credential. This also guarantees the fetch NEVER
  rides a client that has a config key baked in for a different credential scope than the caller's
  (a tenant-scoped fetch must not silently use the platform config key when BYOK resolution
  succeeded).

- **D4 — Key-optional listing allowlist: `openrouter`, `local-llm`, `ollama`, `lmstudio`.**
  For these, a `PROVIDER_CREDENTIAL_UNAVAILABLE` from the resolver downgrades to an
  unauthenticated fetch (OpenRouter's list is public; local servers have no auth). For every other
  provider the resolver failure short-circuits to the fail-soft envelope with
  `errorCode: "credential_unavailable"` — do NOT attempt an unauthenticated call that will 401 and
  burn the 10 s timeout. The allowlist is a private static set in the service with a comment
  citing the survey.

- **D5 — Cache serves stale on failure, keyed `(provider, tenantId?)`.** Entries hold
  `(List, FetchedAt)`; a hit within TTL returns fresh; a miss triggers a fetch; a fetch failure
  with an expired entry returns that entry flagged `Stale = true`. Tenant-keyed entries exist so a
  BYOK-entitlement-filtered list can never be served to another tenant (or to the platform view).
  Unbounded growth is not a concern at (15 providers × tenants-that-open-the-page), but evict
  entries older than 24 h during writes anyway — one line.

- **D6 — `keyConfigured` is answered by the resolver, in a try/catch, never by reading config
  directly.** `ResolveAsync` throwing `PROVIDER_CREDENTIAL_UNAVAILABLE` ⇒ `false`; success ⇒
  `true` (and discard the credential immediately). This keeps the status column truthful for
  cabinet-stored keys, which plain config reads would miss. Skip the resolver entirely for
  key-optional providers — report `keyConfigured: true` semantics as "not required" via a third
  state: the DTO field is `keyStatus: "configured" | "missing" | "not_required"` rather than a
  boolean (the UIs want the three-way distinction anyway).

## Implementation Steps

1. **`ProviderDescriptor.ModelsEndpointPath`** — add the nullable property with XML doc
   (`ProviderDescriptor.cs`, beside `ChatEndpointPath:101`).
2. **Populate the catalogue** — `ProviderCatalog.cs`: add the path per the story table, with the
   verification provenance as a comment on each non-obvious entry (deepseek's missing `/v1`,
   groq/openrouter base-path interplay, gemini's `models/…` ids, z-ai's null + re-check note).
3. **`ModelsPathForBase`** — static helper on `ProviderCatalog` mirroring `ChatPathForBase:333-337`.
4. **DTOs + service** — `ProviderModelInfo`, `ProviderModelList`, `IProviderModelCatalog`,
   `ProviderModelCatalogService` in `Services/Providers/`. Constructor:
   `IHttpClientFactory`, `IProviderCredentialResolver`, `ILogger<>`, `TimeProvider`. Register
   singleton in `ProviderSessionServiceCollectionExtensions` beside
   `IProviderClient` (`ProviderSessionServiceCollectionExtensions.cs:28`) or a sibling extension —
   wherever `IProviderCredentialResolver`'s lifetime allows; verify its registration scope first
   and match it.
5. **Parser** — private static: root array → entries; else `data` array → entries; entry `id`
   (string, required), `display_name` ?? `name` → `DisplayName`, `deprecated` (bool, optional).
6. **Endpoints** — `Endpoints/ProviderAdminEndpoints.cs` (status list + platform models route);
   tenant models route added in `ProviderCredentialEndpoints.cs` beside `ListProviders`. Mapping in
   `Program.cs`: new `app.MapGroup("/api/admin/providers").RequireAuthorization("PlatformOwnerAccess")`
   modelled on `:2654`; tenant route on the existing `/api/v1/agents/providers` mappings with the
   same read policy as `ListProviders`.
7. **Current-model injection** — both models routes compute the effective model via the existing
   precedence (call `IInlineToolLoopRunner.GetDefaultModel` — `IInlineToolLoopRunner.cs:98` — to
   avoid restating `LoadProviderConfig`) and inject/flag it. When 46-1 lands, that call picks up
   the DB layers automatically because 46-1 rewires `LoadProviderConfig` itself — this story needs
   no knowledge of the store.
8. **Egress pins** — extend `ProviderEgressRegressionTests` with the absolute models-URL table.
9. **Unit tests** — per AC9; golden JSON fixtures under the test project's existing fixture
   conventions (check how `ProviderGoldenRequestTests` stores payloads and mirror it).

## Data & Migrations

None. The settings table is 46-1's.

## Events

None emitted. Reads only; the resolver already emits `AGENT.CREDENTIAL_RESOLVED.SUCCESS` /
`AGENT.CREDENTIAL.DENIED` on its own (`DefaultProviderCredentialResolver.cs:241-294`) — note in the
endpoint XML doc that a status-list render therefore emits credential events per provider row, and
that this is accepted (they are the audit trail working as designed). If event volume from status
polling proves noisy, the fix is a resolver-level `Probe` flag — flagged as a possible follow-up,
not built here.

## Test Plan

| # | Test | Asserts |
|---|---|---|
| 1 | Parser: OpenAI envelope fixture | ids + null display names |
| 2 | Parser: Anthropic fixture | `display_name` mapped |
| 3 | Parser: Together bare-array fixture | bare array parsed; `display_name` mapped |
| 4 | Parser: OpenRouter fixture | `name` mapped |
| 5 | Parser: Gemini fixture | `models/…` ids kept verbatim |
| 6 | Parser: entry without id | skipped, list still returned |
| 7 | Cache: second call within TTL | no second HTTP request (Moq handler call count) |
| 8 | Cache: tenant isolation | `(key, tenantA)` fetch never served to `(key, tenantB)` or `(key, null)` |
| 9 | Fail-soft: fetch throws, cache warm | stale list + `Stale=true` + errorCode |
| 10 | Fail-soft: fetch throws, cache cold | empty list + errorCode, HTTP 200 |
| 11 | Credential-unavailable, non-optional provider | no HTTP attempt; fail-soft envelope |
| 12 | Credential-unavailable, `openrouter` | unauthenticated fetch attempted |
| 13 | Current-model injection | delisted current model appears flagged `current` |
| 14 | Egress pins | absolute models URLs per descriptor |
| 15 | Routes: unknown key | 404, body does not enumerate providers |
| 16 | Routes: RBAC | member → 403 on admin routes; tenant route serves member reads |
| 17 | DTO hygiene | response types contain no key-shaped members (reflection scan or review checklist) |

## Definition of Done

- All ACs demonstrably met; tests 1–17 green; `dotnet test` green overall.
- The Z.ai re-check performed and its outcome recorded in the descriptor comment.
- `GET /api/admin/providers` returns a row for all 15 HTTP descriptors + the 2 allow-listed
  non-HTTP keys.
- No change to `HttpProviderClient`, `ProviderRequestShaper`, or any chat-path behaviour
  (golden-request tests untouched and green).

## Dependencies & Sequencing

- **Blocked by:** nothing.
- **Blocks:** 46-2, 46-3 (UI consumers); soft input to 46-1's defaults refresh.
- **Shared-edit register:** `ProviderAdminEndpoints.cs` and the `Program.cs` group are extended by
  46-1 (settings mutations). Land this first.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| A provider's list endpoint shape drifts from the survey (e.g. Together wraps its array) | the parser's two-shape handling covers both envelopes for every provider; fixture tests document today's shapes; fail-soft means drift degrades to a flagged empty list, not an error page |
| Status list is slow (15 sequential fetches) | `GET /api/admin/providers` does NOT fetch models — `keyStatus` + static descriptor data only; model fetches happen per-provider when the UI opens a picker |
| Resolver events flood from status renders | noted under Events; per-row resolution happens only on the status list (15 calls per page load, platform-owner-only page); acceptable, revisit with a `Probe` flag if real |
| Z.ai ships a list route mid-story | one-line descriptor edit; the free-text fallback in the UIs remains for azure-openai/copilot regardless |

## Effort Breakdown

| Task | Days |
|---|---|
| Descriptor field + catalogue values + `ModelsPathForBase` + egress pins | 0.5 |
| Service: fetch composition, parser, cache, fail-soft | 1.25 |
| Endpoints (admin ×2, tenant ×1) + DTOs + RBAC wiring | 0.75 |
| Tests 1–17 + fixtures | 0.75 |
| Z.ai re-check, review, polish | 0.25 |
| **Total** | **3.5** |
