# Finding: the provider layer is not abstracted, and the next three providers are all OpenAI-compatible

**Date**: 2026-07-25
**Type**: 📚 Lesson Learned / architectural constraint on upcoming work
**Category**: Architecture
**Status**: 🔍 Open — shapes the tool-schema fix and any new-provider work

## Product direction (stated by the product owner, 2026-07-25)

> "this should be abstracted for all providers, kimi k3, glm and DeepSeek are the most candidates"

**Any fix to the LLM egress path must be provider-agnostic.** The named candidates —
**Moonshot Kimi**, **Zhipu GLM**, **DeepSeek** — are all **OpenAI-compatible**
`/chat/completions` APIs using the same `tools` / `function` / `parameters` shape. So the wire
dialect is not the hard part; the registration surface is.

## What adding a provider costs today

There is no provider descriptor. A provider's identity is spread across at least four places, none
of which references the others:

| # | Surface | Where | Shape |
|---|---|---|---|
| 1 | Allowlist | `Tamma.Activities/Security/ProviderAllowlist.cs:19-36` | a 15-member `HashSet<string>` |
| 2 | Named `HttpClient` (base URL + auth header) | `Tamma.Api/Program.cs:100-168` | **only 8** registered |
| 3 | provider key → named client map | `Tamma.Api/Services/Providers/HttpProviderClient.cs:32-46` | a dictionary |
| 4 | Wire-dialect branch | `HttpProviderClient.cs:146`, `InlineToolLoopRunner.cs:203-212`, and `LlmProxyService` | `provider.StartsWith("anthropic")` ? anthropic : openai — **duplicated three times** |

Current allowlist: `anthropic, openai, openrouter, google, github-copilot, local-llm, opencode,
z-ai, zen-mcp, azure-openai, gemini, ollama, lmstudio, together, groq`.

**None of DeepSeek, Kimi or Moonshot is present.** GLM is arguably half-present as `z-ai` — Z.ai is
Zhipu's international brand — but there is no separate `zhipu`/`glm` key and no named client.

So the allowlist (1) and the client registry (2) disagree by seven entries: seven allowlisted
providers have no HTTP client at all.

## Why this matters for the schemaless-tool bug

`ManagedAgent.ToResolvedTools` (`:923-937`) builds `new ResolvedTool { Name = n }` — no description,
no `InputSchema` — and both dialect builders write the null straight through:
`["input_schema"] = t.InputSchema` (Anthropic, `InlineToolLoopRunner.cs:844`, `:1102`) and
`["parameters"] = t.InputSchema` (OpenAI, `:923`).

**The defect is upstream of the dialect**, in the provider-agnostic resolution step. The right fix
populates `ResolvedTool` from `IToolExecutorRegistry` once, and every dialect — Anthropic today,
OpenAI-compatible for Kimi / GLM / DeepSeek tomorrow — gets a correct schema for free. Patching a
body builder would fix one wire format and leave the other broken.

## Product direction, part 2 (2026-07-25)

> "adding a provider that is supported should be an admin config"

**Providers become data, not code.** An admin adds DeepSeek / Kimi / GLM through the admin surface —
no deploy, no PR.

**The boundary that makes this safe:** an admin adds an *instance of a wire dialect the code already
implements*. They do **not** add a dialect. `WireDialect` stays a closed `[Wire]` enum in code
(`anthropic` | `openai-compatible` today); base URL, auth scheme, default model and display name
become row data. "Supported" in the product owner's phrasing means exactly this — the shape is
already handled, only the endpoint is new.

That boundary is what keeps the change tractable. Without it, "add a provider" means "add a
response parser", which is code by definition.

### What this forces, beyond the descriptor

1. **Static `AddHttpClient` registration cannot survive.** `Program.cs:100-168` registers eight
   named clients at startup with baked-in base URLs and auth headers. A DB-driven provider has no
   startup-time name. Either resolve a plain `IHttpClientFactory.CreateClient()` and set base
   address + headers per call from the descriptor, or use a typed handler keyed on the descriptor.
   The current `CreateClient("anthropic")` pattern is incompatible with admin-added providers.
   (Note the existing `llm-{provider}` lookup at `InlineToolLoopRunner.cs:141` already resolves
   nothing — that path is *accidentally* dynamic today, and broken.)
2. **An admin-supplied base URL is an SSRF and credential-exfiltration vector.** This is the sharp
   edge. An admin who can set a provider's base URL can point `openai` at a host they control and
   harvest every API key the platform sends there — including, in SaaS, other tenants' BYOK keys if
   the descriptor is platform-scoped. Mitigations that must be decided, not assumed:
   - host allowlist / denylist on the URL (the same treatment Epic 42's 42-9 specifies for its
     authenticated HTTP tool — reuse it, do not write a second one),
   - block private/loopback/link-local ranges after DNS resolution, rejecting a mixed-resolution
     host rather than filtering survivors (42-9's `SafeConnectAsync` **filters**; `ValidateAsync`
     **rejects** — the distinction was already flagged as a divergence),
   - HTTPS required,
   - who may write a provider descriptor, per mode (below).
3. **Two scoping models, answered separately** (CLAUDE.md's universal rule). Single-user: the sole
   user owns provider descriptors. SaaS: platform-owned catalogue plus tenant-scoped additions? Or
   platform-only? A tenant-addable provider descriptor is a credential-exfiltration path *between*
   tenants unless credentials are strictly tenant-scoped and the descriptor is too.
4. **Credentials stay in the secret store**, never in the descriptor row. `IProviderCredentialResolver`
   and the BYOK cabinet already exist; a descriptor references a secret, it does not carry one.
5. **`ProviderAllowlist` inverts.** Today it is the source of truth. It becomes the *seed* for the
   descriptor table — and the runtime check becomes "is there an enabled descriptor for this key",
   not "is this string in a hardcoded set".
6. **Adding a provider is itself a governed action** (Epic 43). `effect:provider.create` /
   `.update` belongs in the action catalog, almost certainly in the `secrets` or a new
   `provider-config` group, and it is a strong candidate for a human-only floor given (2).
7. **The circuit breaker and pricing tables key on provider.** `provider_health` and
   `ProviderPricingService` are keyed by provider string; an admin-added provider needs a pricing
   row or cost attribution silently reads zero. Check `DbProviderPricingService` before assuming a
   new key degrades gracefully.

**Sequencing:** this is a superset of the descriptor work, not a replacement. Build the descriptor
in code first, seed it from the current hardcoded lists, prove Kimi/GLM/DeepSeek work as code
entries — *then* move the descriptor to a table with an admin surface. Shipping the admin surface
and the abstraction in one step means debugging SSRF policy and dialect plumbing simultaneously.

## The shape this argues for

One **provider descriptor** carrying what a provider actually differs by:

- base URL
- auth scheme (`x-api-key` + `anthropic-version` vs `Authorization: Bearer`)
- wire dialect — an enum, **not** a `StartsWith` string test
- default model

…registered once, so adding DeepSeek/Kimi/GLM is one entry rather than four edits across three
assemblies. And **one body builder per dialect**, not three copies.

The duplication has already produced a real defect: the `anthropic` named client sends
`anthropic-version: 2024-01-01` (`Program.cs:111`) — not a published Anthropic API version — while
`InlineToolLoopRunner` sends the correct `2023-06-01` (`:660`, `:1080`). Two copies, two headers,
one wrong. A third path (`LlmProxyService`) rides the broken one.

Separately: `InlineToolLoopRunner.cs:141` calls `CreateClient($"llm-{providerName}")` and **no
`llm-*` named client is registered anywhere**, so those resolve to unconfigured default clients.

## Explicitly NOT the answer

Adopting `Microsoft.Extensions.AI`'s `IChatClient` for this. The audit
(`.dev/findings/` — .NET AI framework survey, 2026-07-25) found the win smaller than it looks:
the provider **chain** is an outer loop retrying the same call across providers, credentials resolve
per-tenant per-request, the repair ring passes a delegate that cannot cross HTTP, and DCB emission
is the compliance backbone. None of that lives inside a single-call abstraction. The pragmatic win —
collapsing three divergent HTTP clients into one descriptor-driven path — needs no new dependency,
and is what makes the three candidate providers cheap to add.

## Action items

- [ ] Fix `ToResolvedTools` at the provider-agnostic layer (registry lookup), not in a dialect builder.
- [ ] Fix the `anthropic-version` drift.
- [ ] Introduce a provider descriptor; collapse the three `StartsWith("anthropic")` branches to one
      dialect switch.
- [ ] Reconcile the allowlist with the named-client registry — seven allowlisted providers have no client.
- [ ] Add `deepseek`, `moonshot`/`kimi`, and a real `glm`/`zhipu` key when the descriptor lands.
      Confirm whether `z-ai` is meant to *be* GLM or is a separate product.
- [ ] Resolve or remove the phantom `llm-*` named-client lookup.
- [ ] **Phase 2 — provider descriptors become an admin config surface** (table + API + UI, seeded
      from the code lists). Blocked on the descriptor existing in code first. Requires an SSRF
      decision on admin-supplied base URLs, a per-mode ownership answer, an Epic 43 catalog entry
      for the mutation, and a pricing-row story for admin-added keys.

## Decided (2026-07-25, product owner)

**Only the platform owner may add a provider. The catalogue is platform-owned; customers pick from
it and supply their own key.**

This settles the SSRF question by removing the attack surface rather than mitigating it: a customer
never supplies a base URL, so no customer can redirect credentials anywhere. Consequences:

- The descriptor write path is gated by the existing platform-owner policy, not a new tenant-level
  permission. No `providers:manage` for tenants.
- A host allowlist is **not** required for safety, because the only writer is already the platform
  owner. Keep HTTPS-required and post-DNS private-range rejection as defence in depth — they are
  cheap and they catch a typo pointing at an internal service — but they are no longer load-bearing.
- The tenant-scoped half is **credentials only**: a tenant supplies its own key for a
  platform-listed provider, through the existing BYOK cabinet.
- Phase 2's admin surface is therefore a *platform-owner* screen, which is materially simpler than
  a tenant-facing one — no per-tenant descriptor rows, no cross-tenant isolation story for the
  descriptor table itself.

## Also decided (2026-07-25)

**`z-ai` IS GLM (Zhipu).** Confirmed by the product owner. So the candidate list is really:

| Candidate | Status |
|---|---|
| **GLM / Zhipu** | **Already half-present as `z-ai`** — it is in the 15-member allowlist and has a named HTTP client. Finishing it is a wiring + naming job, not a new provider. Decide whether the key stays `z-ai` or is renamed; if renamed, it is a wire-string change on persisted config, so prefer keeping `z-ai` and documenting the equivalence. |
| **DeepSeek** | Genuinely new. OpenAI-compatible. |
| **Kimi / Moonshot** | Genuinely new. OpenAI-compatible. |

This makes the first descriptor milestone cheaper than it looked: one of the three is already
reachable, and the other two share its wire dialect exactly.

## Related

- `apps/tamma-elsa/src/Tamma.Api/Services/Providers/HttpProviderClient.cs`
- `apps/tamma-elsa/src/Tamma.Api/Services/Agents/InlineToolLoopRunner.cs`
- `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/LlmProxyService.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Security/ProviderAllowlist.cs`
