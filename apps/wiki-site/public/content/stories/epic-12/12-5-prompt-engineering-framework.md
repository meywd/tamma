---
title: "Story 12-5: Prompt Engineering Framework"
sidebar:
  order: 120
---

## Status

**Partially Complete** — core features implemented, remaining items split into sub-stories.

### What's Done

| Feature | Status | Where |
|---|---|---|
| Role+action 2D key system | Done | `packages/api/src/services/default-prompts.ts` |
| 80 role+action templates | Done | `packages/api/src/services/default-prompts.ts` |
| `{{variable}}` interpolation | Done | `packages/api/src/services/prompt-store.ts` |
| 8 role system prompts | Done | `default-prompts.ts` SYSTEM_PROMPTS |
| Chain-of-thought sections | Done | Templates include `<thinking>`, `<plan>`, `<output>` |
| Convention injection `{{conventions}}` | Done | LlmCallWorkflow auto-injects |
| 20 convention templates | Done | `packages/api/src/services/convention-templates.ts` |
| API endpoints (CRUD + render) | Done | `packages/api/src/routes/` |
| LlmCallWorkflow integration | Done | `ResolvePromptFromRegistryActivity` |
| Prompt validation tests | Done | 100+ tests in `default-prompts.test.ts`, `prompt-store.test.ts` |

### Moved to Other Epics

| Feature | Moved To | Reason |
|---|---|---|
| Postgres storage | Epic 27-1 | Part of multi-tenant prompt store |
| Multi-tenant (account overrides) | Epic 27-2, 27-3 | SaaS requirement |
| Provider-specific prompts | Epic 27-1 | Provider column on action_prompts |
| Admin/Account UI | Epic 27-4, 27-5 | Prompt management UI |
| Prompt versioning + audit | Epic 27-7 | Event sourcing for prompt changes |
| Unified TS/C# prompt system | Epic 9 (rewritten) | API-based, one resolver |

### Remaining (split into sub-stories below)

| Feature | Priority | Sub-Story |
|---|---|---|
| Context priority-based truncation | P0 | 12-5a |
| Few-shot example injection | P1 | 12-5b |
| Skill-level adaptation fix | Bug | 12-5c |
| A/B testing hooks | P2 | 12-5d |
| CI retry counter bug | Bug | 12-5e |

---

## Sub-Story 12-5a: Context Priority-Based Truncation

### Summary

Replace the generic `ContextCompactor` (character-count estimation, flat summarization) with priority-tagged context sections that are truncated based on importance and the current role/action.

### Problem

Current `ContextCompactor` uses 4 chars/token estimation and summarizes everything between system prompt and the last 4 messages. A 50-line error log gets the same treatment as 3 lines of critical test output. The summarization prompt is generic and doesn't know what task is being performed.

### Acceptance Criteria

1. Context messages tagged with priority: `CRITICAL`, `IMPORTANT`, `NORMAL`, `LOW`
2. Truncation order: LOW first, then NORMAL, then IMPORTANT. CRITICAL never truncated.
3. Role-aware: for a tester, test output is CRITICAL; for a security reviewer, CVE details are CRITICAL
4. Structured context sections (error logs, file contents, test results) independently truncatable
5. Token budget respected (provider-specific max context)

### Technical Context

- Current: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ContextCompactor.cs`
- Needs: Priority tagging in the context assembly step, role-aware priority rules

### Dependencies

- Epic 27-2 (prompt store — role determines priority rules)
- Epic 9-1 (agent config — provider-specific token limits)

### Effort: 16 hours

---

## Sub-Story 12-5b: Few-Shot Example Injection

### Summary

Store successful (input, output) pairs from previous LLM calls and inject 1-3 relevant examples into prompts to improve output quality.

### Acceptance Criteria

1. Store successful LLM call input/output pairs in vector DB (ChromaDB)
2. Query relevant examples by similarity to current task
3. Inject 1-3 examples into prompt before the user's request
4. Respect context window limits — examples yielded first if space is tight
5. Per-account isolation (examples from one account not visible to another)

### Technical Context

- Vector DB: ChromaDB (already in stack)
- Store: after each successful LLM call, embed the input+output and store
- Retrieve: before prompt rendering, query similar examples
- Inject: add `{{fewShotExamples}}` section to templates

### Dependencies

- Epic 27-2 (prompt store — templates need `{{fewShotExamples}}` variable)
- ChromaDB integration (already exists in `packages/intelligence/`)

### Effort: 20 hours

---

## Sub-Story 12-5c: Mentorship Skill-Level Adaptation Fix

### Summary

Fix the hardcoded `skillLevel = 3` in MentorshipWorkflow. The assessment activities produce skill-level outcomes but the value is never updated.

### Problem

`MentorshipWorkflow` passes `["skillLevel"] = 3` to every sub-workflow dispatch. Assessment outcomes (Correct/Partial/Incorrect) never update this value. All juniors get the same intermediate-level guidance regardless of their assessed skill.

### Acceptance Criteria

1. Assessment result updates the `skillLevel` variable
2. Correct → increment (max 5), Partial → no change, Incorrect → decrement (min 1)
3. Updated skill level propagated to all downstream sub-workflow dispatches
4. Mentor prompt uses conditional sections based on skill level (already in templates)

### Technical Context

- File: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/MentorshipWorkflow.cs`
- Assessment outcomes exist but don't update the variable

### Dependencies: None (self-contained fix)

### Effort: 4 hours

---

## Sub-Story 12-5d: A/B Testing Hooks

### Summary

Add infrastructure for prompt variant testing — not the full A/B framework, just the hooks.

### Acceptance Criteria

1. Prompt templates support a `variantId` field (e.g., "v1", "v2-concise")
2. Variant selection: deterministic based on `hash(accountId + sessionId) % variantCount`
3. Selected variant recorded in LLM call output alongside `providerUsed`, `modelUsed`
4. Dashboard can filter workflow traces by prompt variant
5. No automated outcome tracking (that's a separate story)

### Dependencies

- Epic 27-1 (prompt store — variants stored as separate prompt rows with same role+action)
- Epic 9-2 (diagnostics — variant recorded in diagnostics)

### Effort: 12 hours

---

## Sub-Story 12-5e: CI Retry Counter Bug Fix

### Summary

Fix the documented bug where `ciRetryCount` persists across re-entries from review-fix and merge re-test paths in SingleIssueCycleWorkflow.

### Problem

Lines 349-351 of SingleIssueCycleWorkflow.cs contain a self-documented bug: the CI retry counter passes through to `ci-with-debug-retry` sub-workflow and isn't reset when re-entering from review-fix or merge re-test. After a review-fix cycle, the CI retry budget may be partially consumed from the previous run.

### Acceptance Criteria

1. CI retry counter resets to 0 on each new entry to the CI check phase
2. The counter is scoped per-entry, not per-workflow-instance
3. Test verifying the counter resets after review-fix loop

### Dependencies: None (self-contained fix)

### Effort: 2 hours

---

## Summary

| Sub-Story | Priority | Effort | Dependencies |
|---|---|---|---|
| 12-5a Context Truncation | P0 | 16h | Epic 27-2, Epic 9-1 |
| 12-5b Few-Shot Examples | P1 | 20h | Epic 27-2, ChromaDB |
| 12-5c Skill-Level Fix | Bug | 4h | None |
| 12-5d A/B Testing Hooks | P2 | 12h | Epic 27-1, Epic 9-2 |
| 12-5e CI Retry Bug Fix | Bug | 2h | None |
| **Total remaining** | | **54h** | |

---

**Last Updated**: 2026-04-08
**Epic**: 12 (Agentic Tool Loop)
**Status**: Partially Complete — remaining items split into sub-stories
