---
title: "Story 9-6: Agent Prompt Registry"
sidebar:
  order: 90
---

## Status: SUPERSEDED BY EPIC 27

This story has been **superseded by Epic 27 (Prompt Store -- Multi-Tenant Prompt Management)**.

### Rationale

The original Story 9-6 defined an in-process `AgentPromptRegistry` class with a 6-level resolution chain and `{{variable}}` template interpolation. Epic 27 subsumes this functionality with:

- **Postgres-backed prompt storage** (not in-memory Map)
- **Multi-tenant isolation** (per-account prompt overrides with system default fallback)
- **Provider dimension** on prompt resolution (per-provider-per-role prompts)
- **Admin UI** for prompt management
- **Full audit trail** via DCB events

### What Happened to the Existing Code

The existing `AgentPromptRegistry` class at `packages/providers/src/agent-prompt-registry.ts` continues to function as a local cache/resolver. Epic 27's `PromptStore` service replaces the database layer and provides the API. The `AgentPromptRegistry` will be updated to delegate to the Prompt Store API instead of resolving from in-memory config.

### References

- **Epic 27 README**: `/home/meywd/tamma/docs/stories/epic-27/README.md`
- **Story 27-2**: Postgres-backed PromptStore service (replaces this story)
- **Story 27-3**: Prompt Store API endpoints
- **Story 27-6**: Elsa workflow integration with the new prompt store

### Migration Path

1. Epic 27 Story 27-2 implements the Postgres-backed service
2. Epic 27 Story 27-3 exposes API endpoints
3. Story 9-8 (Unified Agent Resolver) calls the Prompt Store API for prompt resolution
4. The existing `AgentPromptRegistry` class becomes a thin wrapper around the API
