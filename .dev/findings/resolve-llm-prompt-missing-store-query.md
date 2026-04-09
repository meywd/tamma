# ResolveLlmPromptActivity Does Not Query Prompt Store

**Date**: 2026-04-02
**Status**: Architectural gap -- deferred
**Severity**: Medium

## Problem

`ResolveLlmPromptActivity` currently resolves prompts locally (hardcoded or
from embedded resources) but never queries the Prompt Store API
(`GET /api/prompts/:role/:action`).  This means prompt templates edited via
the Prompt Registry API have no effect on the Elsa workflow engine's LLM
calls.

## Required Changes

1. The C# `ResolveLlmPromptActivity` (or `LlmCallWorkflow`) needs to call
   `GET /api/prompts/:role/:action` to fetch the latest template before
   rendering.
2. The HTTP call must handle 404 gracefully (fall back to a built-in
   default prompt).
3. Template rendering (variable interpolation) can happen either in C# or
   by calling `POST /api/prompts/:role/:action/render`.

## Why It's Deferred

Integrating this requires changes to `LlmCallWorkflow` which hasn't been
optimized yet.  Wiring an HTTP client into the activity also needs careful
design (retry, caching, circuit breaker).  This should be its own story.

## Related Files

- `apps/tamma-elsa/src/Tamma.Activities/LLM/ResolveLlmPromptActivity.cs`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs`
- `packages/api/src/routes/prompts/prompt-routes.ts`
- `packages/api/src/services/prompt-store.ts`
