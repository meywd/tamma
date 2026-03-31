---
title: "Epic 9: Config-Driven Agent Management & Diagnostics"
---

## Overview

This epic replaces the hardcoded single-agent setup with a config-driven multi-agent system. Each agent role (architect, implementer, reviewer, etc.) gets an ordered provider chain with automatic fallback. All provider calls are instrumented for cost tracking, usage reporting, and health monitoring.

**Key capabilities:**
- **Provider chains per role** — priority-ordered provider+model combos with automatic fallback
- **Diagnostics collection** — costs, tokens, latency, errors per provider+model, available for reporting
- **Circuit breaker** — unhealthy providers automatically skipped, half-open probing for recovery
- **Content sanitization** — HTML stripping, zero-width char removal, prompt injection detection
- **Backward compatible** — legacy single `agent` config still works via normalizer

## Stories

| Story | Title | Package(s) | Priority | Status |
|-------|-------|-----------|----------|--------|
| 9-0 | [Overview](story-9-0/9-0-overview.md) | — | — | Reference |
| 9-1 | [Configuration Schema](story-9-1/9-1-configuration-schema.md) | shared, cli | P0 | Planned |
| 9-2 | [Provider Diagnostics](story-9-2/9-2-provider-diagnostics.md) | shared, providers, cost-monitor | P0 | Planned |
| 9-3 | [Health Tracker](story-9-3/9-3-provider-health-tracker.md) | providers | P0 | Planned |
| 9-4 | [Provider Factory](story-9-4/9-4-agent-provider-factory.md) | providers | P0 | Planned |
| 9-5 | [Provider Chain](story-9-5/9-5-provider-chain.md) | providers | P0 | Planned |
| 9-6 | [Prompt Registry](story-9-6/9-6-agent-prompt-registry.md) | providers | P1 | Planned |
| 9-7 | [Content Sanitization](story-9-7/9-7-content-sanitization.md) | shared, providers | P1 | Planned |
| 9-8 | [Agent Resolver](story-9-8/9-8-role-based-agent-resolver.md) | providers | P0 | Planned |
| 9-9 | [Engine Integration](story-9-9/9-9-engine-integration.md) | orchestrator | P0 | Planned |
| 9-10 | [CLI Wiring](story-9-10/9-10-cli-wiring.md) | cli | P0 | Planned |
| 9-11 | [Diagnostics Queue & MCP Interceptors](story-9-11/9-11-diagnostics-queue-mcp-interceptors.md) | shared, mcp-client | P0 | Planned |

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
