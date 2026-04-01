---
title: "Config-Driven Agent Management — Overview"
sidebar:
  order: 90
---

## Provider Config Model

A "provider config" = provider + model + credentials. Examples:

| Label | Provider | Model | Notes |
|-------|----------|-------|-------|
| OpenRouter + z.ai | `openrouter` | `z-ai/z1-mini` | OpenRouter as gateway to z.ai |
| OpenRouter + Claude Opus | `openrouter` | `anthropic/claude-opus-4` | OpenRouter as gateway to Anthropic |
| Claude Code CLI | `claude-code` | `claude-sonnet-4-5` | Subprocess agent |
| OpenCode + ZenMCP | `opencode` | (default) | OpenCode CLI with ZenMCP backend |

Each agent role (architect, implementer, reviewer, etc.) has an ordered **provider chain** — a priority list of these provider configs with automatic fallback.

## Diagnostics Architecture

All telemetry — provider calls, MCP tool calls, LLM API calls — flows through a single async event queue with zero overhead in the hot path:

```
Provider/MCP calls → diagnosticsQueue.emit() (sync) → [bounded queue] → drain(5s) → processor → costTracker.recordUsage()
```

The queue lives in `@tamma/shared` (not `mcp-client`) so both providers and MCP client can use it without circular dependencies. Blocking interceptors for MCP tool sanitization are separate — they live in `@tamma/mcp-client`.

## Stories

| # | Story | Package(s) | Key Output |
|---|-------|-----------|------------|
| 1 | [Configuration Schema](../story-9-1/9-1-configuration-schema.md) | shared, cli | `AgentsConfig`, `SecurityConfig`, `normalizeAgentsConfig()` |
| 2 | [Provider Diagnostics](../story-9-2/9-2-provider-diagnostics.md) | shared, providers, cost-monitor | `InstrumentedAgentProvider`, `InstrumentedLLMProvider` |
| 3 | [Health Tracker](../story-9-3/9-3-provider-health-tracker.md) | providers | `ProviderHealthTracker`, `errors.ts` extraction |
| 4 | [Provider Factory](../story-9-4/9-4-agent-provider-factory.md) | providers | `AgentProviderFactory`, `wrapAsAgent()` |
| 5 | [Provider Chain](../story-9-5/9-5-provider-chain.md) | providers | `ProviderChain` with fallback + budget check |
| 6 | [Prompt Registry](../story-9-6/9-6-agent-prompt-registry.md) | providers | `AgentPromptRegistry` with `render()` |
| 7 | [Content Sanitization](../story-9-7/9-7-content-sanitization.md) | shared, providers | `ContentSanitizer`, `SecureAgentProvider` |
| 8 | [Agent Resolver](../story-9-8/9-8-role-based-agent-resolver.md) | providers | `RoleBasedAgentResolver` |
| 9 | [Engine Integration](../story-9-9/9-9-engine-integration.md) | orchestrator | Engine uses resolver, backward compat |
| 10 | [CLI Wiring](../story-9-10/9-10-cli-wiring.md) | cli | Replace hardcoded agent, wire diagnostics |
| 11 | [Diagnostics Queue & MCP Interceptors](../story-9-11/9-11-diagnostics-queue-mcp-interceptors.md) | shared, mcp-client | `DiagnosticsQueue`, `ToolInterceptorChain` |

## Dependency Order

```
Story 1 (config types)  ──────────────────────────────────────────┐
Story 7 (sanitization)  ──────────────────────┐                   │
Story 3 (health tracker) ─────────┐           │                   │
Story 4 (factory)       ─────────┤           │                   │
Story 6 (prompts)       ─────────┤           │                   │
Story 11a (DiagnosticsQueue)─────┤           │                   │
                                  ↓           │                   │
Story 2 (diagnostics)  ← needs 11a          │                   │
                         ↓                    │                   │
Story 5 (provider chain) ← needs 2,3,4      │                   │
                                              ↓                   ↓
Story 11b (ToolInterceptorChain) ← needs 7   Story 8 (resolver) ← needs 1,5,6,7
                                                                    ↓
                                                  Story 9 (engine) ← needs 8
                                                                    ↓
                                                  Story 10 (CLI)   ← needs 1,9,11a,11b
```

**Parallel groups:**
- Group 1 (no deps): Stories 1, 3, 4, 6, 7, 11a
- Group 2 (needs 11a): Story 2
- Group 3 (needs 2,3,4): Story 5. Also: Story 11b (needs 7)
- Group 4 (needs 1,5,6,7): Story 8
- Group 5 (needs 8): Story 9
- Group 6 (needs 1,9,11a,11b): Story 10
