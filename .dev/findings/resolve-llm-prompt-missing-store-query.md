# ResolveLlmPromptActivity Does Not Query Prompt Store

**Date**: 2026-04-02
**Status**: ✅ Resolved — obsolete (the activity was deleted 2026-07-25)
**Severity**: Medium (historical)

## Resolution (2026-07-25)

Resolved by **deletion, not by wiring**. The gap below was closed from the other
side: `LlmCallWorkflow` moved to `ResolvePromptFromRegistryActivity`, which does
exactly what "Required Changes" asked for — it calls the Prompt Store
(`POST /api/prompts/{role}/{action}/render`) and fails loud on a registry miss
rather than silently falling back. `ResolveLlmPromptActivity` was left behind
with **zero call sites** and has now been removed, along with its
`ResolvedPrompt` / `PromptResolutionLevel` models and its UIHint test.

Its config hierarchy carried a **per-provider** dimension
(`LlmPrompts:{provider}:{role}`) that the Prompt Store deliberately does not
have — see `.dev/findings/no-provider-dimension-in-prompts.md` for why that is
correct and not an omission.

---

## Original problem (historical)

`ResolveLlmPromptActivity` currently resolves prompts locally (hardcoded or
from embedded resources) but never queries the Prompt Store API
(`GET /api/prompts/:role/:action`).  This means prompt templates edited via
the Prompt Registry API have no effect on the Elsa workflow engine's LLM
calls.

### Required Changes

1. The C# `ResolveLlmPromptActivity` (or `LlmCallWorkflow`) needs to call
   `GET /api/prompts/:role/:action` to fetch the latest template before
   rendering.
2. The HTTP call must handle 404 gracefully (fall back to a built-in
   default prompt).
3. Template rendering (variable interpolation) can happen either in C# or
   by calling `POST /api/prompts/:role/:action/render`.

### Why It Was Deferred

Integrating this requires changes to `LlmCallWorkflow` which hasn't been
optimized yet.  Wiring an HTTP client into the activity also needs careful
design (retry, caching, circuit breaker).  This should be its own story.

## Related Files

- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolvePromptFromRegistryActivity.cs` (the live resolver)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs`
- `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs`
