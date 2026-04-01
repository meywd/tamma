---
title: "Story 11.2: LLM Input Sanitization"
sidebar:
  order: 110
---

Status: ready-for-dev

## Story

As a **security engineer**,
I want all LLM inputs (system prompts and user prompts) sanitized before API calls,
so that prompt injection attacks in issue descriptions, PR comments, or user-supplied text are neutralized before reaching the LLM.

## Acceptance Criteria

1. `CallLlmInlineActivity` injects `IContentSanitizer` and sanitizes both system and user prompts before the LLM API call
2. `CallLlmActivity` injects `IContentSanitizer` and sanitizes both system and user prompts before the LLM API call
3. `PlanGenerationWorkflow.BuildPlanPrompt()` sanitizes all dynamic inputs interpolated into the prompt
4. `BlockerDiagnosisWorkflow.BuildDiagnosisPrompt()` sanitizes all dynamic inputs interpolated into the prompt
5. `ReviewFixWorkflow` sanitizes review comments and diff content before building the prompt
6. `DebuggingWorkflow` sanitizes error messages, test output, and code snippets before building the prompt
7. A static helper `SecurityHelpers.SanitizeForPrompt()` exists for use in workflow lambda contexts where DI is not available
8. Original unsanitized input is never passed to the LLM provider — all paths verified
9. 15+ tests covering each integration point

## Technical Context

### Injection Surface

LLM inputs arrive from multiple untrusted sources:
- GitHub/GitLab issue body and comments (user-written, could contain adversarial content)
- PR review comments (could contain injected instructions)
- CI/CD test output and error messages (could be manipulated by malicious test code)
- Code file contents (could contain injection strings in comments or string literals)

All of these flow into workflow prompt builders and eventually into `CallLlmInlineActivity` or `CallLlmActivity`.

### Files to Modify

- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` — inject `IContentSanitizer`, sanitize before API call
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmActivity.cs` — inject `IContentSanitizer`, sanitize before API call
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanGenerationWorkflow.cs` — sanitize dynamic inputs in `BuildPlanPrompt()`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/BlockerDiagnosisWorkflow.cs` — sanitize dynamic inputs in `BuildDiagnosisPrompt()`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ReviewFixWorkflow.cs` — sanitize review content before prompt building
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DebuggingWorkflow.cs` — sanitize error/test/code content before prompt building

### Files to Create

- `apps/tamma-elsa/src/Tamma.Activities/Security/SecurityHelpers.cs` — static helper with `SanitizeForPrompt()` for workflow lambda contexts

### Integration Pattern

In activity classes (DI available):
```csharp
public class CallLlmInlineActivity : CodeActivity
{
    private readonly IContentSanitizer _sanitizer;

    public CallLlmInlineActivity(IContentSanitizer sanitizer) { _sanitizer = sanitizer; }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var systemPrompt = _sanitizer.SanitizeInput(rawSystemPrompt);
        var userPrompt = _sanitizer.SanitizeInput(rawUserPrompt);
        // ... call LLM with sanitized prompts
    }
}
```

In workflow lambda contexts (no DI):
```csharp
// Workflow builder lambda — can't inject services
var sanitizedInput = SecurityHelpers.SanitizeForPrompt(rawInput);
```

## Implementation Notes

1. `SecurityHelpers.SanitizeForPrompt()` internally creates a static `ContentSanitizer` instance (thread-safe, no state). This is a convenience wrapper for contexts where constructor injection is not possible.
2. For each workflow prompt builder, identify all string interpolation points that include external data. Wrap each with `SecurityHelpers.SanitizeForPrompt()` or `_sanitizer.SanitizeInput()`.
3. Do NOT sanitize hardcoded prompt templates — only the dynamic values interpolated into them.
4. Add a DEBUG log line at each sanitization point: `"Sanitized input for {ActivityName}, patterns matched: {count}"`.
5. Ensure sanitization happens as late as possible (just before the LLM call) to avoid double-sanitization in chains.

## Testing Strategy

- **Unit tests per activity** (4 tests): Verify that CallLlmInlineActivity and CallLlmActivity pass sanitized content to the LLM provider mock
- **Unit tests per workflow** (8 tests): Verify that each workflow prompt builder calls sanitization on dynamic inputs (mock `IContentSanitizer`, verify `SanitizeInput` called with expected arguments)
- **Static helper tests** (3 tests): Verify `SecurityHelpers.SanitizeForPrompt()` delegates to `ContentSanitizer`, handles null/empty, is thread-safe
- **Integration test**: End-to-end test where an issue body contains a known injection pattern, verify the pattern is neutralized before reaching the LLM provider
- **Test files**:
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/CallLlmInlineActivitySanitizationTests.cs`
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/Security/SecurityHelpersTests.cs`

## Dependencies

- **Story 11.1** (ContentSanitizer C# Port) — provides `IContentSanitizer` and `ContentSanitizer`

## Estimated Effort

2-3 days

## Logging Requirements

### Existing Coverage

Line 78 mentions: "Add a DEBUG log line at each sanitization point: `Sanitized input for {ActivityName}, patterns matched: {count}`." This is adequate for per-call tracing but lacks broader coverage.

### Required Additions

All activities already have `ILogger<T>` (see `CallLlmInlineActivity`, `CallLlmActivity` in codebase). Use existing logger instances.

| Event | Level | Structured Properties | Notes |
|-------|-------|----------------------|-------|
| Input sanitization applied | DEBUG | `{ActivityName}`, `{PatternsMatchedCount}`, `{InputField}` (e.g., "SystemPrompt", "UserPrompt") | Per-field, per-activity. Already partially specified in implementation notes. |
| Workflow prompt sanitization applied | DEBUG | `{WorkflowName}`, `{FieldName}`, `{PatternsMatchedCount}` | For workflow lambda contexts using `SecurityHelpers.SanitizeForPrompt()` |
| Sanitization skipped (empty/null input) | DEBUG | `{ActivityName}`, `{InputField}` | Avoid unnecessary sanitizer calls; log that skip happened |
| Injection pattern detected in input | WARN | `{ActivityName}`, `{PatternName}`, `{InputField}`, `{WorkflowInstanceId}` | Elevated from DEBUG because an actual injection attempt warrants operator attention |
| Total injection patterns detected per LLM call | INFO | `{ActivityName}`, `{TotalPatternsMatched}`, `{Provider}`, `{WorkflowInstanceId}` | Aggregate count per LLM call for security dashboards |

### Sensitive Data Redaction

- **Never** log the raw prompt content (system or user) — prompts may contain proprietary instructions or user-supplied PII.
- Log only field names, pattern counts, and activity/workflow identifiers.
- The `SecurityHelpers.SanitizeForPrompt()` static helper cannot log (no DI). Document that callers in workflow lambdas must log before/after calling the helper.

### Correlation IDs

- Activities have access to `ActivityExecutionContext` which provides `WorkflowInstanceId`. Include `{WorkflowInstanceId}` and `{ActivityId}` in all log messages from activity classes.
- Workflow prompt builders should include `{WorkflowDefinitionId}` when logging sanitization in lambda contexts.

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation from `.dev/plans/llm-injection-security-fix.md` Phase 2 | Architecture Team |
| 2026-03-28 | 1.1 | Added Logging Requirements section | Logging Audit |
