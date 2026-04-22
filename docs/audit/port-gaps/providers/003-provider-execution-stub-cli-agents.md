# Finding 003: Provider execution is an HTTP-only stub — all CLI-agent providers fail

**Scope**: providers
**Severity**: P0 (cutover-blocking; 6+ supported providers fail at runtime)
**Status**: Not-yet-implemented (stub)
**Estimated port effort**: 25–40h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/provider-session.ts`.

- File: `packages/api/src/services/provider-session.ts:130-159`
- Contract/behavior: `ProviderSessionService.create()` delegated to `IAgentProviderFactory.create(entry)` and returned a handle. `execute()` looked up the cached `IAgentProvider` and called `provider.executeTask(config)`, which in turn dispatched to the correct adapter (Anthropic SDK, OpenAI SDK, Claude Code CLI spawn, OpenCode CLI spawn, Zen MCP, OpenRouter HTTP, z.ai HTTP, local LLM HTTP, etc.). The factory is the full `@tamma/providers` package — it owns provider-specific request/response shaping, streaming, tool-calling, context compaction, and instrumentation.

```typescript
// packages/api/src/services/provider-session.ts (9e9a57c~1) — lines 130-159
const provider = await this.factory.create(entry);
const handle = randomUUID();
...
this.sessions.set(handle, { provider, meta });
...
async execute(handle: string, config: AgentTaskConfig): Promise<AgentTaskResult> {
  const session = this.sessions.get(handle);
  if (!session) { throw new Error(`Session not found: ${handle}`); }
  session.meta.lastUsed = Date.now();
  return session.provider.executeTask(config);
}
```

- Dependencies: `@tamma/providers` (8 adapters: `AnthropicClaudeProvider`, `OpenAIProvider`, `GitHubCopilotProvider`, `GeminiProvider`, `OpenCodeProvider`, `ZenMcpProvider`, `OpenRouterProvider`, `LocalLLMProvider`), `AgentProviderFactory`, `InstrumentedAgentProvider` (cost/diagnostics wrapping).
- Tests that exercised this: `packages/api/src/services/provider-session.test.ts`, `packages/api/src/routes/settings/__tests__/provider-factory-routes.test.ts`, full adapter test suites in `packages/providers/src/__tests__/*`.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/Providers/HttpProviderClient.cs:29-113`
- Contract/behavior: A single HTTP client that whitelists **four** providers. Any other provider name (claude-code, opencode, zen-mcp, openrouter, z.ai, local, etc.) falls through to the "generic completion-style" branch at `HttpProviderClient.cs:102-112` and POSTs against an `HttpClient` that is created by name — which, for unconfigured names, resolves to a default `HttpClient` with no `BaseAddress` and no auth header, so the request either 404s or fails with `InvalidOperationException: An invalid request URI was provided`.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Providers/HttpProviderClient.cs — lines 31-39
private static readonly IReadOnlyDictionary<string, string> ProviderHttpClientMap =
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic"] = "anthropic",
        ["anthropic-claude"] = "anthropic",
        ["openai"] = "openai",
        ["github-copilot"] = "github-copilot",
        ["gemini"] = "gemini",
    };
```

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Providers/HttpProviderClient.cs — lines 51-83
public async Task<ProviderInvocationResult> InvokeAsync(
    string provider, string model, ExecuteRequest req, CancellationToken ct = default)
{
    if (!ProviderHttpClientMap.TryGetValue(provider, out var clientName))
    {
        clientName = provider.ToLowerInvariant();
    }
    var client = _factory.CreateClient(clientName);
    ...
    using var response = await client.PostAsJsonAsync(path, payload, ct);
    response.EnsureSuccessStatusCode();
    ...
}
```

- Provider-specific SDK integration is **absent**: no Anthropic Messages SDK streaming, no OpenAI tool-calling, no Claude Code CLI subprocess launcher, no MCP transport.
- Dependencies: `IProviderClient` (interface), `ProviderSessionService.ExecuteAsync` (calls `_client.InvokeAsync`), `Program.cs:xxx` named-client registrations (only 4 are configured).
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/Providers/HttpProviderClientTests.cs`, `ProviderSession/*` — cover the 4 whitelisted providers only; no coverage for CLI-agent adapters because they aren't wired.

## 3. The gap

- TS supported and shipped: Anthropic Claude (HTTP), OpenAI (HTTP), GitHub Copilot (HTTP), Gemini (HTTP), Claude Code (CLI subprocess + event stream), OpenCode (CLI subprocess + IPC), Zen MCP (MCP client over stdio/HTTP), OpenRouter (HTTP), z.ai (HTTP), local LLMs (configurable HTTP e.g. Ollama/LM Studio).
- C# supports: Anthropic, OpenAI, GitHub Copilot, Gemini — **HTTP only**.
- For a caller sending `{provider: "claude-code", model: "sonnet-4"}` to `POST /api/providers/providers/create`:
  - TS would spawn the Claude Code CLI, pipe prompts, stream responses back, capture tool invocations, and emit diagnostics.
  - C# creates a session (OK), then on `POST /.../execute` the `HttpProviderClient` falls through to the OpenAI-style POST branch, tries to hit `/v1/chat/completions` on a client named `claude-code` (no `BaseAddress` configured), and throws `InvalidOperationException`. The provider session is effectively a dead handle.
- **Cost is hardcoded `0m`** on both the Anthropic and the OpenAI branches (`HttpProviderClient.cs:145` and `:169`). The comment says "cost-monitor is responsible for enrichment (Epic 9)" but no Epic 9 enrichment step exists in the C# pipeline. See finding 004.
- In production with existing data / deployed clients, this means: every agent config whose `providerChain[0].provider` is `claude-code` (the default in `HARDCODED_AGENT_CONFIG.agents.defaults.providerChain` — `[{provider:'claude-code'}]` — see archived `013_agent_configs.sql:36-37`) will fail at execute time. The `ProviderChainResolver` will walk the chain, but **every fallback that isn't one of the 4 whitelisted HTTP providers also fails**, so the chain itself is not a remedy.

Error paths:
- TS: `200 {content, tokenUsage, costUsd, durationMs}` via the CLI adapter, or `402` on quota failures.
- C#: `InvalidOperationException` bubbling to `500 Internal Server Error` or `HttpRequestException` with a 4xx/connect-refused for the configured-but-wrong-target branch.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-4/9-4-agent-provider-factory.md`.
- Story 9-4 AC: "Exposes AgentProviderFactory via session-based API endpoints. Elsa workflows call these instead of maintaining factory logic in C#." The design explicitly says the factory stays in TS and Elsa/C# calls the API over HTTP. Epic 19 inverted that (deleted the TS factory; stood up a thin C# HTTP client) without porting the factory.
- Story alignment:
  - [x] Matches C# behavior (stub).
  - [ ] Matches TS behavior.
  - [ ] Describes a third behavior.
  - [x] No story — there is no story documenting "port `AgentProviderFactory` to C# with CLI-agent subprocess support". Epic 19's deletion PR `9e9a57c` removed the factory; no follow-up implementation plan exists.

## 5. Status

- **Classification**: Not-yet-implemented (stub). The largest single gap in the port.
- **What's needed to finish**:
  1. Port `AgentProviderFactory` semantics: a registry keyed by provider name that returns a provider instance capable of `ExecuteAsync(config)`.
  2. For HTTP providers, extend `HttpProviderClient` or split into per-provider clients (`AnthropicProviderClient`, `OpenAIProviderClient`, `GeminiProviderClient`, `GithubCopilotProviderClient`, `OpenRouterProviderClient`, `ZaiProviderClient`, `LocalLLMProviderClient`).
  3. For CLI providers (`claude-code`, `opencode`), implement a subprocess launcher using `System.Diagnostics.Process` with stdin/stdout wiring and streaming event parsing. Consider reusing the JSONL event protocol from the TS CLI adapters.
  4. For MCP providers (`zen-mcp`), port the MCP client transport (stdio or HTTP, per the [MCP spec](https://modelcontextprotocol.io/)).
  5. Wire cost enrichment into `ProviderSessionService.ExecuteAsync` or a middleware around `IProviderClient.InvokeAsync` (see finding 004).
  6. Register each provider-specific `HttpClient` in `Program.cs` with correct `BaseAddress`, auth header, and `HttpClientHandler` timeouts.
- **Is it "just a stub" or is scope missing?** Both. Scope is missing — there is no story spec for the C# factory port — and what exists is explicitly labelled a stub in the XML-doc comment on `HttpProviderClient` itself: "It is *not* a full LLM SDK — complete streaming, tool-calling, and context management live in the TS engine and will be ported separately."
- **Blockers**: Depends on finding 004 (cost accounting), finding 005 (budget enforcement), finding 011 (chain schema) — but those are smaller and can be done in parallel with the provider port.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Providers/HttpProviderClient.cs` (split per provider)
  - `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderSessionService.cs` (cost enrichment hook)
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs` (named HttpClient registrations for every provider)
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Providers/Adapters/AnthropicProviderClient.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Providers/Adapters/OpenAIProviderClient.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Providers/Adapters/GeminiProviderClient.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Providers/Adapters/OpenRouterProviderClient.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Providers/Adapters/ClaudeCodeCliProvider.cs` (subprocess)
  - `apps/tamma-elsa/src/Tamma.Api/Services/Providers/Adapters/OpenCodeCliProvider.cs` (subprocess)
  - `apps/tamma-elsa/src/Tamma.Api/Services/Providers/Adapters/ZenMcpProvider.cs` (MCP client)
  - `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderAdapterRegistry.cs` (key → `IProviderClient`)
- Tests to add:
  - Per-adapter integration tests against the adapter's real API (behind test credentials)
  - `ProviderAdapterRegistry_UnknownProvider_Returns404OrExplicitUnsupported`
  - `HttpProviderClient_ProviderUnconfigured_ReturnsProviderNotSupported_Not500`
- Estimated effort: 30h broken down as:
  - Per-HTTP-adapter implementation + tests (5 adapters × 2h): 10h
  - Claude Code CLI subprocess adapter: 6h
  - OpenCode CLI subprocess adapter: 4h
  - Zen MCP adapter (MCP protocol): 6h
  - Registry + DI wiring: 2h
  - Regression + integration suite: 2h

## Remediation status

- **Confirmed**: 2026-04-19 by agent
- **Outcome**: Fixed (HTTP-only providers + explicit `PROVIDER_NOT_SUPPORTED` for CLI/MCP)
- **Commit**: `498889b` `fix(providers): land P0 pricing/budget/role/CLI-stub fixes [findings 001, 003, 004, 005]`
- **Notes**: Added named `HttpClient` registrations for `openai`, `github-copilot`, `gemini`, `openrouter`, `z.ai`, `local` in `Program.cs` — each with configurable base URL + auth header from `IConfiguration`. Extended `HttpProviderClient.ProviderHttpClientMap` to cover the new provider keys plus the `local`/`ollama`/`lmstudio` aliases. Introduced `ProviderNotSupportedException` and a `NonHttpProviders` set so calls to `claude-code` / `opencode` / `zen-mcp` surface as **501 Not Implemented** with a clear message instead of opaque 500s. Defensive guard fails fast if a configured client has no `BaseAddress`. **Deferred**: full CLI-agent subprocess + MCP transport adapters (the ≈25-40h portion). The HTTP-supported providers now work end-to-end; the failure mode for the others is now actionable.

## References

- TS source: `packages/api/src/services/provider-session.ts`, `packages/providers/src/agent-provider-factory.ts`, `packages/providers/src/*-provider.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/Providers/HttpProviderClient.cs`, `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderSessionService.cs`
- Story: `docs/stories/epic-9/story-9-4/9-4-agent-provider-factory.md` (describes API shape, not the C# implementation)
- Related findings: `004-cost-accounting-hardcoded.md`, `005-budget-enforcement-no-op.md`, `011-provider-chain-schema-mismatch.md`, `018-user-scoped-providers-put-no-op.md`
- CLAUDE.md section: "Multi-Provider AI Abstraction" lists 8+ providers as a core architecture pattern; the C# stub supports 4.
