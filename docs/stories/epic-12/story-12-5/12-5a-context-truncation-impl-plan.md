# Story 12-5a Implementation Plan — Context Priority-Based Truncation

**Status**: Planned (2026-04-20)
**Parent brief**: [`12-5-prompt-engineering-framework.md`](./12-5-prompt-engineering-framework.md) §12-5a
**Team**: Layer 4 Team D
**Branch**: `feat/story-12-5a-context-truncation`
**Worktree**: `/home/meywd/tamma-worktrees/layer-4-team-d-12-5a-truncation`

---

## 1. Objective

Replace the generic `ContextCompactor` (char-count estimation, flat LLM
summarisation) with a priority-tagged context system. Messages carry
`CRITICAL | IMPORTANT | NORMAL | LOW` tags, and when the token budget is
exceeded we drop LOW first, then NORMAL, then IMPORTANT — CRITICAL is
never truncated. Role-aware priority rules mean the same context
section (e.g. a CVE detail block) is CRITICAL for a security reviewer
but NORMAL for a junior developer. The system respects per-provider max
context and emits a diagnostics event per truncation so the prompt
engineering dashboard can observe what was dropped.

## 2. Dependencies

Hard blockers:

- **Story 27-2** (prompt store service) — prompt templates carry
  role-specific priority rules; this story reads them.
- **Story 9-1** (agent config) — provider-specific token limits live here.

Soft dependencies:

- **Story 27-3** (prompt store API endpoints) — not strictly required, but
  admins can author new priority rules via this endpoint if present.
- `tiktoken-sharp` or `SharpToken` NuGet — accurate token counting per
  provider's tokenizer (see research in §6).

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ContextPriorityTagger.cs` | Assigns priorities to context sections based on role + section type + template rules. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/PriorityTruncator.cs` | Drops messages by priority tier until under budget. Replaces `ContextCompactor`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/TokenCounter.cs` | Uses `SharpToken` for OpenAI/Anthropic providers; falls back to char/4 for unknown. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/PriorityRules.cs` | Static map of (role × section-type) → priority. Editable via prompt-store template. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/Tools/PriorityTruncatorTests.cs` | 12+ unit tests covering drop order, role awareness, budget respect. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/Tools/TokenCounterTests.cs` | Round-trip accuracy tests with `SharpToken` fixtures. |
| `/home/meywd/tamma/docs/stories/epic-12/story-12-5/priority-rules-matrix.md` | Human-readable matrix of default role × section priorities; updated as new roles are added. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ContextCompactor.cs` | Deprecate — mark `[Obsolete]` and route to `PriorityTruncator` for one release cycle. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` | Swap the compaction call site: `ContextCompactor.Compact` → `PriorityTruncator.Truncate(messages, budget, role)`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer/ServiceRegistration.cs` | Register `IContextPriorityTagger`, `IPriorityTruncator`, `ITokenCounter` as scoped services. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/DiagnosticsEndpoints.cs` | Accept a new `TRUNCATION` diagnostic type (fields: role, originalTokens, finalTokens, droppedByTier). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Diagnostics/DiagnosticsSchema.cs` | Add `Truncation` record + JSON schema validator. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Prompts/PromptResolver.cs` | On resolve, attach `PriorityRules` from the prompt template's metadata so truncator can consult role-specific overrides. |
| `/home/meywd/tamma/apps/tamma-elsa/Tamma.sln` | Add `SharpToken` package reference if not already pinned. |

## 5. Sequence of changes

### Step 1 — Token counter (2h)

- Add `SharpToken` (MIT, Apache 2.0 tokenizer port). Map provider names to
  tokenizer encoding (`cl100k_base` for OpenAI/Anthropic, `o200k_base` for
  GPT-4o).
- `TokenCounter.Count(string, provider)` returns exact count.
- Unit test against known fixtures (OpenAI's published samples).
- **Commit**: `feat(llm): token counter with SharpToken`.

### Step 2 — Priority rule matrix (2h)

- Author default rules in `PriorityRules.cs`:
  - `coder` role: `test_output=CRITICAL`, `error_log=IMPORTANT`, `file_contents=NORMAL`, `metadata=LOW`.
  - `tester` role: `test_output=CRITICAL`, `coverage_report=IMPORTANT`, `implementation=NORMAL`, `docs=LOW`.
  - `security-reviewer`: `cve_detail=CRITICAL`, `changed_files=IMPORTANT`, `test_output=NORMAL`, `readme=LOW`.
  - `mentor`: `assessment=CRITICAL`, `student_work=IMPORTANT`, `history=NORMAL`, `meta=LOW`.
  - Default fallback: everything `NORMAL`; `system_prompt` always `CRITICAL`.
- Unit test: every role in `RoleEnum` resolves without falling back, and a
  fabricated "fake_role" hits the default matrix.
- Commit the matrix doc (`priority-rules-matrix.md`) alongside the code.
- **Commit**: `feat(llm): default priority rules per role`.

### Step 3 — Context priority tagger (3h)

- `ContextPriorityTagger.Tag(messages, role, ruleOverrides)`:
  - Inspects each message's metadata (`SectionType` field or heuristic
    from content shape — regex on `# ERROR`, `--- TEST OUTPUT ---`).
  - Applies role-specific rule, then template override if present.
  - Returns `IReadOnlyList<TaggedMessage>` with immutable priority tier.
- Covered by unit tests that seed a mixed transcript and assert the
  priority distribution.
- **Commit**: `feat(llm): context priority tagger`.

### Step 4 — Priority truncator (4h)

- `PriorityTruncator.Truncate(taggedMessages, budgetTokens)`:
  1. Compute current total via `TokenCounter`.
  2. If ≤ budget, return unchanged.
  3. Else drop lowest-priority tier whole; recompute; repeat up to
     IMPORTANT. If still over budget after dropping IMPORTANT,
     log ERROR and emit `TRUNCATION_CRITICAL_OVERFLOW` event but keep
     CRITICAL messages intact (the LLM will 400 rather than silently
     losing critical context).
  4. Return `TruncationResult { finalMessages, droppedByTier, originalTokens, finalTokens }`.
- Emit `TRUNCATION` diagnostic per invocation via
  `IDiagnosticsService.RecordAsync(DiagnosticsEventType.Truncation, …)`.
- Unit test: 12 cases covering tier-drop order, budget-exact, budget-exceeded-critical-only.
- **Commit**: `feat(llm): priority-based truncator`.

### Step 5 — Wire into `CallLlmInlineActivity` (2h)

- Resolve `IContextPriorityTagger` + `IPriorityTruncator` via DI.
- Replace the `ContextCompactor.Compact(...)` call with:
  ```csharp
  var tagged = tagger.Tag(messages, role, rules);
  var truncated = truncator.Truncate(tagged, providerBudget);
  ```
- Attach `truncated.DroppedByTier` to workflow output variables so
  downstream activities can read it.
- Integration test via Elsa workflow: start `LlmCallWorkflow` with an
  oversized context, assert the diagnostic event is recorded and the
  final message list fits.
- **Commit**: `feat(llm): wire priority truncator into call-llm`.

### Step 6 — Deprecate old compactor (1h)

- Mark `ContextCompactor.Compact` `[Obsolete("Replaced by PriorityTruncator")]`.
- Redirect its implementation to the new path so any legacy callers
  continue to work.
- Plan to delete after the next release (tracked in Story 12-5 followup).
- **Commit**: `refactor(llm): deprecate ContextCompactor`.

### Step 7 — Dashboard hook (2h)

- `DiagnosticsEndpoints` already returns events. Add
  `GET /api/v1/diagnostics/truncation?tenantId=...&since=...` as a
  typed convenience endpoint; returns the last N truncation events for
  quick dashboard rendering.
- Team B (prompt UIs) consumes this endpoint separately; no UI code in
  this story.
- **Commit**: `feat(api): diagnostics endpoint for truncation events`.

## 6. Test strategy

### Unit tests

- `TokenCounterTests` (6 cases): GPT-4, Claude-Haiku, GPT-4o, unknown
  provider fallback, empty string, 100k-char stress.
- `ContextPriorityTaggerTests` (10 cases): every role × representative
  section-type combination hits the expected priority.
- `PriorityTruncatorTests` (12 cases):
  - Under budget → no change.
  - Drop LOW only.
  - Drop LOW + NORMAL.
  - Drop LOW + NORMAL + IMPORTANT.
  - Budget still exceeded after dropping IMPORTANT → CRITICAL kept + `TRUNCATION_CRITICAL_OVERFLOW` emitted.
  - Role-specific override (security-reviewer keeps CVE details).
  - Template metadata override beats role default.
  - Empty input returns empty.

### Integration tests

- `CallLlmInlineActivity` integration: oversized context, assert final
  prompt fits in the provider's stated budget and a truncation
  diagnostic is recorded.

### Regression

- `ContextCompactor.Compact` path still works (deprecated) — existing
  Epic 12-3 tests must pass without modification.

## 7. Rollback plan

- **Feature flag**: new resolver behind `LLM:UsePriorityTruncator` (default
  `true` after ship). Flipping to `false` routes back to the legacy
  `ContextCompactor.Compact` path for a release cycle.
- **Revertable commits**: each step above is independent. If step 7's
  diagnostic endpoint introduces a regression, revert it without
  touching the truncator core.
- **Non-reversible**: none — no migrations, no secret changes.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Token counter + SharpToken | 2 |
| 2. Priority rule matrix + doc | 2 |
| 3. Context priority tagger | 3 |
| 4. Priority truncator | 4 |
| 5. Wire into CallLlmInlineActivity | 2 |
| 6. Deprecate old compactor | 1 |
| 7. Diagnostics endpoint hook | 2 |
| **Total** | **16** (matches brief) |

## 9. Open questions

- **Does SharpToken ship a cl100k_base encoding bundle or require a
  runtime download?** NuGet bundles the encoding tables, so no network
  call at runtime. Verified in research; cross-check at implementation
  time that the pinned version's table matches Anthropic's published
  one (their tokenizer is close to but not identical to cl100k_base).
  If Anthropic drifts, fall back to char/4 estimation for anthropic
  providers and emit a WARN.
- **What happens when a single CRITICAL message alone exceeds the
  provider budget?** Plan: emit a `TRUNCATION_CRITICAL_OVERFLOW` event,
  forward the oversize prompt anyway, and let the provider return 400.
  Alternative: apply Epic 12-3's LLM-based summarisation to the
  CRITICAL message. Open for Team D lead decision — leaning toward the
  explicit-failure approach because silent summarisation of CRITICAL
  content defeats the point.
- **How do templates declare priority overrides?** Current plan:
  add a `priorityRules` JSON field on prompt_overrides.template_metadata
  (read-through in 27-2 service). Requires a small migration
  (additive JSONB field, no data migration). Needs explicit sign-off
  from Team B who owns the prompt store surface.
- **Role propagation when no role is set on the workflow.** Default to
  `coder` and emit a WARN — confirmed with the story brief.
