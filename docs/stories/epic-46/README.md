# Epic 46: Provider admin surface — runtime provider & model management

## Overview

Product requirement (product owner, 2026-07-27, verbatim intent):

> **"the UI should allow the admins to choose the models from latest models available without code
> updates."**

Clarified the same day: **"admins" means both levels.** The platform owner manages catalogue-wide
settings (platform default model per provider, provider enable/disable) in the admin console
(`packages/dashboard`), AND tenant admins pick per-tenant model overrides from the same live model
lists in the customer app (`packages/dashboard-user`). Two UIs, two apps, one listing seam, one
settings store.

Today a provider's default model is frozen at build time in three places — the `ProviderCatalog`
descriptor (`apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderCatalog.cs:37,55,66,125,213,227`),
the optional `LlmProviders:{key}:DefaultModel` config section
(`apps/tamma-elsa/src/Tamma.ElsaServer/appsettings.json:64-89`), and one hardcoded constant in
`LlmProxyService.cs:30`. When a provider ships a new model, picking it up means editing config and
redeploying, or editing C# and redeploying. Both are code updates by the product owner's definition.

This epic is **Phase 2 of the provider abstraction** — the admin surface the finding
(`.dev/findings/provider-abstraction-and-openai-compatible-candidates.md`) deferred, scoped to what
the requirement asks: **model choice becomes data; the model list comes from the provider's own live
API; provider descriptors stay in code.** Phase 1 (the descriptor catalogue, the single dialect
switch, the `CombineUrl` egress path, 15 HTTP descriptors, golden-request pins) landed and is the
plumbing this epic reuses; nothing here re-opens it.

## The ownership ladder (decided, binding)

| Layer | What it is | Who writes it | Where |
|---|---|---|---|
| **Catalogue** (provider keys, dialects, base URLs, auth schemes, endpoint paths) | code — `ProviderCatalog.HttpProviders` | engineers, per release | in the binary |
| **Provider enable/disable + platform default model** | data — `provider_settings` platform rows | platform owner | admin console (`packages/dashboard`), `/api/admin/providers/*` |
| **Per-tenant model override** | data — `provider_settings` tenant rows | tenant_owner / tenant_admin (SaaS); the sole user (single-user) | customer app (`packages/dashboard-user`), `/api/v1/agents/providers/*` |
| **Model per call** | request field | callers that already name one (chain entries, agent configs) | unchanged |

**Resolution precedence** (46-1, one resolver, asserted by tests):
**tenant override → platform DB override → `LlmProviders:{key}:DefaultModel` config → descriptor
`DefaultModel`.** Both UIs read the same live lists through the same seam.

Four deliverables:

1. **A live model-listing seam** (46-0): a `ModelsEndpointPath` field on `ProviderDescriptor`, a
   server-side fetch through the existing named-client plumbing + `ProviderCatalog.CombineUrl`, one
   normalized shape `{id, displayName?, deprecated?}`, a 5-minute cache, and fail-soft behaviour
   that never leaves either UI without the currently-selected model. Two routes over one service:
   platform-scoped `GET /api/admin/providers/{key}/models` and tenant-scoped
   `GET /api/v1/agents/providers/{provider}/models`.
2. **A persisted model selection that survives redeploys and needs none** (46-1): a control-plane
   `provider_settings` table holding platform rows (default model + enabled flag) and tenant rows
   (model override), plus the four-step resolver above, wired into every default-model consumer.
3. **The platform admin UI** (46-2): a `packages/dashboard` provider-settings page listing every
   catalogue provider with status (key configured? models listable? enabled?), a searchable model
   dropdown fed by the live endpoint, save/reset.
4. **The tenant UI** (46-3): a `packages/dashboard-user` model-settings page for tenant admins —
   same picker, tenant-scoped, BYOK-aware, read-only for member users. Carries an explicit
   dependency on **Epic 45** (the customer app exists but is not yet deployed).

## Why the live list is the point, not a nicety

Shipped default-model strings rot. The catalogue already carries examples:

- `openrouter`'s descriptor default is `anthropic/claude-sonnet-4-20250514`
  (`ProviderCatalog.cs:66`) — a dated snapshot slug on a marketplace whose whole value is that
  models churn weekly.
- `LlmProxyService.cs:30` defaults to `"claude-sonnet-4.5"` — which looks like a *display name*,
  not an Anthropic API model id (the API ids use dashes, e.g. `claude-sonnet-4-5`). The same file's
  price table (`:60-62`) keys `claude-opus-4.7` / `claude-haiku-3.5` the same way. Whether these
  strings are accepted by the API today is exactly the kind of question a live model list answers
  and a code constant cannot. 46-1 carries a task to verify and refresh every shipped default
  against the live lists once, at implementation time — and after that the DB override + live list
  is the permanent cure, because the next rot costs an admin a dropdown click instead of a deploy.

## Verified wire facts — model-list endpoints per descriptor

Surveyed 2026-07-27. "Live" = probed from this environment (an unauthenticated request returning the
provider's own auth error proves the route and the auth header it wants). "Docs" = the sandbox proxy
blocked the host, verified from the provider's published API reference instead.

| Descriptor key(s) | Models path (relative to `DefaultBaseUrl`) | Auth | Response shape | Verified |
|---|---|---|---|---|
| `anthropic` | `/v1/models` | `x-api-key` + `anthropic-version` | `{data:[{id, display_name, created_at, type}], has_more}` | **Live** — 401 `"x-api-key header is required"`, and with a bad key 401 `"invalid x-api-key"` |
| `openai` | `/v1/models` | Bearer | `{object:"list", data:[{id, owned_by, created}]}` | Docs (platform.openai.com/docs/api-reference/models) |
| `openrouter` | `/v1/models` (base is `https://openrouter.ai/api`, so absolute URL is `…/api/v1/models`) | **None — public** | `{data:[{id, name, created, pricing, …}]}` | Docs (openrouter.ai/docs — models API is public) |
| `gemini`, `google` | `/v1beta/openai/models` | Bearer | OpenAI list; ids like `models/gemini-2.0-flash` | **Live** — bad Bearer → 400 `"Please pass a valid API key"` (route + auth scheme confirmed); no-auth → 404 |
| `groq` | `/v1/models` (base is `https://api.groq.com/openai`, so absolute URL is `…/openai/v1/models`) | Bearer | OpenAI list | Docs (console.groq.com/docs/api-reference, /docs/models) |
| `deepseek` | `/models` (note: **no** `/v1` — matches their chat path convention) | Bearer | `{object:"list", data:[{id, object, owned_by}]}` | Docs (api-docs.deepseek.com/api/list-models) |
| `moonshot` | `/v1/models` | Bearer | OpenAI list + extras (`context_length`, capability flags) | Docs (platform.kimi.ai/docs/api/list-models) |
| `together` | `/v1/models` | Bearer | **Bare JSON array** `[{id, display_name, organization, pricing, …}]` — NOT `{data:[…]}` | Docs (docs.together.ai/reference/models) |
| `local-llm`, `ollama`, `lmstudio` | `/v1/models` | none (Bearer accepted) | OpenAI list — Ollama's OpenAI-compat layer and LM Studio both serve it | Docs (docs.ollama.com/api/openai-compatibility; github.com/ollama/ollama/blob/main/docs/openai.md) |
| `z-ai` | **None found.** docs.z.ai documents chat at `/api/paas/v4/chat/completions` but no models-list route surfaced in survey (host blocked from this sandbox; search found nothing) | — | — | **Unresolved** — `ModelsEndpointPath` stays null; 46-0 carries an implementation-time re-check task |
| `azure-openai` | None in v1 — Azure's listing needs an `api-version` query and returns deployments, a different resource model | — | — | Deliberately out (D4) |
| `github-copilot` | None in v1 — `api.githubcopilot.com` requires a Copilot token exchange, not a plain API key | — | — | Deliberately out (D4) |

Two normalization consequences the seam must own: (a) **two envelope shapes** — `data`-array
(everything else) vs bare array (Together); (b) **display names** come from `display_name`
(Anthropic, Together), `name` (OpenRouter), or nowhere (OpenAI/Groq/DeepSeek — the id is the name).

## Decisions

**D1 — Providers stay code; model choice becomes data.** The finding's full Phase-2 ambition
("providers become rows") is NOT this epic. `ProviderCatalog.HttpProviders` stays the in-code,
platform-owned descriptor list; `ProviderWireDialect` stays a closed enum; no admin- or
tenant-supplied base URLs exist, so the finding's SSRF analysis stays settled by the product owner's
2026-07-25 decision (platform-owned catalogue, customers supply keys only). Moving descriptors to a
table remains a possible follow-on and is strictly easier once `provider_settings` exists.

**D2 — The DB layers slot ABOVE config.** Precedence is tenant → platform-DB → config → descriptor.
The alternative ("config wins because an operator wrote it") was rejected: the requirement is that a
UI choice takes effect **without a deploy**, and config that silently outranks the UI makes the UI a
lie. An operator who needs to pin a model against UI drift deletes the DB rows (the reset buttons)
or governs the UI itself. The precedence lives in one resolver (46-1) and is asserted by tests.

**D3 — Two scoping models, answered separately (CLAUDE.md universal rule), per layer.**

*Platform layer (default model + enabled flag):*
- **single-user:** the sole user owns it — they are the platform operator of their install; the
  existing `PlatformOwnerAccess` policy already resolves to them (`Program.cs:1483`).
- **SaaS:** the platform owner owns it; same policy, same gating shape as the conventions admin
  group (`Program.cs:2654`).

*Tenant layer (model override) — decided by the product owner 2026-07-27, a feature, not deferred:*
- **single-user:** the sole user owns their override row (keyed `user_id`); in practice it is the
  same person as the platform layer, and the resolver simply reads their row first.
- **SaaS:** `tenant_owner` / `tenant_admin` write the tenant's override (keyed `tenant_id`);
  `member` users see the resolved value read-only. Gated by the same `AgentManage` policy that
  already guards tenant BYOK writes on the `/api/v1/agents/providers` surface
  (`ProviderCredentialEndpoints.cs:32-34`) — model choice and key custody sit at the same trust
  level for a tenant.

**RBAC summary:**

| Action | single-user | SaaS |
|---|---|---|
| GET live model list (tenant route) | sole user | any tenant member |
| GET resolved model + override state (tenant route) | sole user | any tenant member |
| PUT/DELETE tenant model override | sole user | `tenant_owner` / `tenant_admin` only (member → 403) |
| GET provider status list (admin route) | sole user | platform owner only |
| PUT/DELETE platform default model, enable/disable | sole user | platform owner only |

**D3a — Tenant rows live on the control plane, beside the platform rows.** One sentence of
justification: the resolver runs on hot LLM egress paths (`InlineToolLoopRunner`,
`LlmProxyService`) that carry a `tenantId` but not a tenant `DbContext`, and splitting one
four-step precedence chain across two databases would buy schema purity at the cost of a
per-call cross-DB read — so `provider_settings` is CP-resident with the
`user_id`/`tenant_id` XOR pattern borrowed from `prompt_overrides` (and unlike
`AgentRoleSelection`, which is tenant-schema-resident because agent rows are read inside
tenant-scoped request flows, not on the egress hot path).

**D3b — Billing guard, not billing block.** Cost attribution is keyed `(provider, model)` —
`DbProviderPricingService` on the runner path, and `UsagePricingEngine.cs:51-59` **throws
`"No cost pricing exists for {provider}/{model}"`** on the SaaS pricing path. A tenant picking a
model with no platform pricing row would either fail their calls or silently record cost 0. 46-1
therefore has the settings endpoints warn (response field, surfaced by both UIs) when the chosen
model has no pricing row; whether SaaS mode should hard-block is an open question for the product
owner (below). The pricing-row admin surface itself is unchanged (`pages/admin/pricing/`).

**D4 — Providers without a listable models endpoint degrade to free text, not to broken.**
`ModelsEndpointPath` is nullable. `z-ai` (no documented endpoint found), `azure-openai` (different
resource model + `api-version` semantics), and `github-copilot` (token-exchange auth) ship with
null. Both UIs show a plain text input with the current effective model for those; the settings
store works identically. If docs.z.ai turns out to document a list route (the host was unreachable
from this sandbox), filling the field in is a one-line data change — which is the entire point of
the descriptor design.

**D5 — The credential never comes from, or goes to, the browser — and the tenant's own key is
preferred when they have one.** The models fetch runs server-side with the key from
`IProviderCredentialResolver.ResolveAsync(tenantId, key)` — which **already implements exactly the
required policy**: tenant BYOK key when present, platform key as the gated fallback
(`DefaultProviderCredentialResolver.cs:84-146`). The admin route passes `tenantId: null` (platform
key); the tenant route passes the caller's tenant id (BYOK preferred). Headers are applied
per-request the way `InlineToolLoopRunner` already does (unconfigured named client, absolute URL,
descriptor-driven headers). Responses to either browser carry model metadata only. A provider whose
key is unresolvable reports `keyConfigured: false` and an empty, clearly-flagged list — never an
error page, never a key prompt on these screens (keys live in the existing secrets/BYOK surfaces).

**D6 — Fail-soft is a contract, not a hope.** The models endpoints always return HTTP 200 with a
typed envelope: fresh list, or stale-cached list flagged `stale: true` with an error code, or empty
list with the error code — and in every case the currently-effective model is present as an entry
flagged `current: true` (synthesized if the provider delisted it). Either UI can therefore always
render a working selector. A provider being down must never make a settings page unusable.

## Corrections

Recorded here because this epic's survey found them; each is fixed or carried by the story named.

- **The finding's named-client line numbers are stale.**
  `.dev/findings/provider-abstraction-and-openai-compatible-candidates.md:25,68` cites
  `Program.cs:100-168` for the named `HttpClient` registrations. Phase 1 moved them to
  `apps/tamma-elsa/src/Tamma.Api/Extensions/ProviderHttpClientServiceCollectionExtensions.cs`
  (hand-registered seven at `:24-95`, descriptor-driven loop at `:113-142`). The finding's Phase-2
  appendix added by this epic notes it; the historical body is left as written.
- **The finding's "only 8 named clients / seven allowlisted providers have no client" is fixed
  code.** The descriptor-driven loop closed the gap; the keyset agreement is pinned by
  `ProviderCatalogTests.Allowlist_And_Catalog_Are_In_Exact_Keyset_Agreement`. The finding body
  predates this; its Action-items list is partially done. The appendix says which items remain.
- **`LlmProxyService`'s default model and price-table keys look like display names, not API ids**
  (`LlmProxyService.cs:30,60-62` — `claude-sonnet-4.5`, `claude-opus-4.7`, `claude-haiku-3.5`;
  Anthropic API ids are dash-formed). Not fixed in this README — 46-1 verifies against the live
  list and refreshes as part of its defaults-refresh task, because guessing the correct slug in a
  planning doc would be repeating the original mistake.
- **An earlier draft of this epic recommended against per-tenant overrides.** The product owner
  decided otherwise on 2026-07-27 (both levels, two UIs). The billing-correctness concern that
  drove the old recommendation did not disappear — it became D3b's warning contract and open
  question 1.

## Stories

| Story | Title | Effort | Blocked by |
|---|---|---|---|
| **46-0** | Live model listing: `ModelsEndpointPath`, one fetch/normalize/cache service, platform + tenant routes | 3.5 d | — |
| **46-1** | Persisted model selection: `provider_settings` (platform + tenant rows), the four-step resolver, defaults refresh | 4 d | — |
| **46-2** | Platform admin UI: provider settings page in `packages/dashboard` | 3 d | 46-0, 46-1 |
| **46-3** | Tenant UI: model settings page in `packages/dashboard-user` | 3 d | 46-0, 46-1; **Epic 45** for reachability |

**Total: 13.5 person-days. Critical path: 7 days** (46-1 → 46-2 or 46-3; 46-0 runs beside 46-1).
See `EXECUTION-PLAN.md`.

## Out of scope

- **Descriptor rows in the DB** (base URLs, auth schemes, new provider keys as data) — the rest of
  the finding's Phase 2. Blocked on nothing technical after 46-1, but it is a different risk class
  (SSRF-adjacent, allowlist inversion) and the product requirement does not ask for it.
- **Model-capability metadata** (context windows, tool support, pricing display in the picker).
  The normalized shape deliberately carries `{id, displayName?, deprecated?}` only; capability
  columns differ per provider and would couple the seam to every provider's metadata dialect.
- **Per-role / per-agent model selection** — `DefaultAgentConfig.DefaultModel` and the agent
  registry's per-agent `Model` fields are the Epic 32 agent surface, already editable there. This
  epic changes what "the provider's default model" resolves to when an agent or chain entry does
  not name one.
- **Pricing-row management UI.** 46-1 warns when a chosen model has no pricing row; creating the
  row stays in the existing pricing admin surface (`pages/admin/pricing/`).
- **Per-tenant provider enable/disable.** The enabled flag is platform-level only in this epic.
  Tenants control their own usage through BYOK key presence and (future) automation toggles.

## Open questions for the product owner

1. **Should choosing a model that has no pricing row be blocked, or allowed with a warning?**
   46-1 ships allow-with-warning on both layers (the runner path degrades to cost 0 today, so
   blocking would be stricter than current behaviour, and `UsagePricingEngine` already fails loud
   on the SaaS billing path). If the answer is "block in SaaS mode", it is a one-guard change in
   the tenant settings endpoint.
2. **Does `z-ai` have a models-list API?** The survey could not reach docs.z.ai and found no
   documented route. If the product owner's Z.ai account shows one in their console, 46-0's
   implementation should fill `ModelsEndpointPath` in and delete the free-text fallback for that
   row.
3. **Should the tenant UI ship before Epic 45 deploys the customer app?** 46-3 can merge behind
   Epic 45 (the code exists and tests run in CI today), but no customer can reach it until 45-5/45-6
   put `dash.tamma.dev` up. If Epic 45 slips, an interim option is exposing the tenant model picker
   in the admin console's tenant-admin area — noted in 46-3, not planned.

## Related

- `.dev/findings/provider-abstraction-and-openai-compatible-candidates.md` — Phase 1 spec + the
  Phase-2 appendix this epic adds.
- `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderCatalog.cs`, `ProviderDescriptor.cs`,
  `HttpProviderClient.cs`, `ProviderRequestShaper.cs` — Phase 1 code.
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Providers/ProviderEgressRegressionTests.cs` — the
  golden-request pins 46-0 extends.
- `docs/stories/epic-45/` — the customer-app shipping epic 46-3 depends on.
- `docs/stories/epic-43/story-43-1` — the "one constant, published over the wire, UI binds" pattern
  the UI stories follow for effective-model provenance.
