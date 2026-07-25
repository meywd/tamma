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

## Related

- `apps/tamma-elsa/src/Tamma.Api/Services/Providers/HttpProviderClient.cs`
- `apps/tamma-elsa/src/Tamma.Api/Services/Agents/InlineToolLoopRunner.cs`
- `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/LlmProxyService.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Security/ProviderAllowlist.cs`
