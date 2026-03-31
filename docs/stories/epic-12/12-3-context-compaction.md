# Story 12.3: Context Compaction

Status: ready-for-dev

## Story

As a **platform engineer**,
I want the agentic tool loop to automatically detect when conversation history approaches the LLM context window limit and compact older messages into a summary,
so that long-running tool sessions (20+ turns) do not fail with context overflow errors, and the LLM retains the essential information from earlier turns.

## Acceptance Criteria

1. `TokenEstimator` class exists with `EstimateTokens(string)` method using ~4 characters per token approximation
2. `ContextCompactor` class exists with `Compact(messages, contextWindowTokens, threshold)` method
3. Compaction is triggered when estimated token usage exceeds `threshold` (default 80%) of `contextWindowTokens`
4. Compaction summarizes the oldest messages (excluding the system prompt and the most recent 4 messages) into a single summary message
5. The summary is generated via a separate LLM call with a dedicated summarization prompt
6. After compaction, the conversation history contains: original system prompt, summary message, and the most recent 4 messages
7. Token count is re-estimated after compaction and the loop continues
8. If compaction fails (LLM error), the loop continues with the uncompacted history (best-effort, not fatal)
9. Compaction events are logged: `"Context compaction triggered at {tokenCount}/{contextWindow} tokens, compacted {messageCount} messages"`
10. 12+ tests covering token estimation, compaction triggering, message preservation, and failure handling

## Technical Context

### Why Compaction?

In a 20-turn tool loop, conversation history can grow to 50-100KB of text. With a 200K token context window, this is usually fine, but:
- Some models have smaller windows (GPT-4o: 128K, Claude Haiku: 200K)
- Tool outputs can be large (50KB per tool, 10 tools = 500KB = ~125K tokens)
- Without compaction, the LLM eventually receives a context-too-large error and the loop fails

The industry standard (Claude Code, Codex CLI) is to auto-compact when approaching the limit.

### Token Estimation

Exact token counting requires a tokenizer library per provider. For the compaction trigger (not billing), a rough estimate is sufficient:

```csharp
public static class TokenEstimator
{
    public static int EstimateTokens(string text) => text.Length / 4;

    public static int EstimateTokens(IEnumerable<ConversationMessage> messages)
        => messages.Sum(m => EstimateTokens(m.Content ?? "") +
                             (m.ToolCalls?.Sum(tc => EstimateTokens(tc.ArgumentsJson)) ?? 0));
}
```

### Compaction Strategy

```
messages = [system, user, assistant+tools, tool_result, assistant+tools, tool_result, ..., assistant+tools, tool_result]
                                                                                         ^-- keep last 4 --^
                          ^----------------------- summarize these ----------------------^

After compaction:
messages = [system, summary_message, last_4_messages...]
```

### Files to Create

- `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/TokenEstimator.cs`
- `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/ContextCompactor.cs`

### Files to Modify

- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` — check token estimate before each LLM call in the loop; if above threshold, compact

### Compaction Prompt

```
Summarize the following conversation history between an AI assistant and its tools.
Preserve all key information: what was asked, what files were read/written, what commands
were run, what errors occurred, and what decisions were made. Be concise but complete.

[conversation messages to summarize]
```

## Implementation Notes

1. Token estimation is called at the top of each loop iteration, before the LLM call. If `estimatedTokens > contextWindowTokens * threshold`, trigger compaction.
2. The compaction LLM call uses the same provider as the main loop (reuse the existing LLM call infrastructure). It is a single-turn call (no tool loop) with a summarization prompt.
3. The 4 most recent messages are preserved because the LLM needs immediate context for its next decision. The system prompt is always preserved because it defines the LLM's role.
4. If the history has fewer than 6 messages (system + user + 4 recent), compaction is skipped — there is nothing to summarize.
5. `ContextCompactor.Compact()` returns a new `List<ConversationMessage>` — it does not mutate the input list.
6. The compaction LLM call's token usage is added to the cumulative `ToolLoopTokens` total.
7. Log compaction at INFO level (not DEBUG) since it is a significant event that operators should be aware of.

## Testing Strategy

- **TokenEstimator tests** (3): Empty string returns 0, known string length returns expected estimate, message list estimation aggregates correctly
- **Compaction trigger tests** (3): Below threshold does not compact, at threshold compacts, above threshold compacts
- **Message preservation tests** (3): System prompt preserved, last 4 messages preserved, middle messages replaced with summary
- **Failure handling tests** (2): LLM summarization error does not crash loop, original messages retained on failure
- **Edge case tests** (1): Fewer than 6 messages skips compaction
- **Test files**:
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/TokenEstimatorTests.cs`
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/ContextCompactorTests.cs`

## Dependencies

- **Story 12.2** (Agentic Tool Loop in CallLlm) — the loop must exist before compaction can be wired into it

## Estimated Effort

2 days

## Logging Requirements

### Existing Coverage

Line 9 (AC#9) specifies: "Compaction events are logged: 'Context compaction triggered at {tokenCount}/{contextWindow} tokens, compacted {messageCount} messages'". Line 87 specifies: "Log compaction at INFO level (not DEBUG) since it is a significant event." Good foundation, but insufficient for debugging compaction failures and performance issues.

### Required Additions

`ContextCompactor` **must** inject `ILogger<T>` via constructor. `TokenEstimator` is a static utility — callers log.

| Event | Level | Structured Properties | Notes |
|-------|-------|----------------------|-------|
| Token estimate computed | DEBUG | `{WorkflowInstanceId}`, `{TurnNumber}`, `{EstimatedTokens}`, `{ContextWindowTokens}`, `{ThresholdPercent}`, `{IsAboveThreshold}` | Per-turn check before LLM call |
| Context compaction triggered | INFO | `{WorkflowInstanceId}`, `{TurnNumber}`, `{EstimatedTokens}`, `{ContextWindowTokens}`, `{MessageCountBefore}`, `{MessageCountToSummarize}` | Already partially specified in AC#9 — formalized here |
| Context compaction completed | INFO | `{WorkflowInstanceId}`, `{TurnNumber}`, `{MessageCountBefore}`, `{MessageCountAfter}`, `{TokensBefore}`, `{TokensAfter}`, `{CompactionDurationMs}`, `{SummarizationTokensUsed}` | Post-compaction summary |
| Context compaction skipped (too few messages) | DEBUG | `{WorkflowInstanceId}`, `{TurnNumber}`, `{MessageCount}` | Fewer than 6 messages — nothing to compact |
| Context compaction failed (LLM error) | WARN | `{WorkflowInstanceId}`, `{TurnNumber}`, `{ExceptionType}`, `{ExceptionMessage}` | Best-effort failure — loop continues with uncompacted history |
| Summarization LLM call started | DEBUG | `{WorkflowInstanceId}`, `{TurnNumber}`, `{InputSizeChars}` | The separate summarization call |
| Summarization LLM call completed | DEBUG | `{WorkflowInstanceId}`, `{TurnNumber}`, `{SummaryLengthChars}`, `{SummarizationTokens}`, `{DurationMs}` | Summarization result metadata |

### Sensitive Data Redaction

- **Never** log the summarization prompt content or the generated summary text.
- Log only message counts, token estimates, and durations.

### Correlation IDs

- All compaction logs must include `{WorkflowInstanceId}` and `{TurnNumber}` to correlate with the tool loop turn that triggered compaction.
- Summarization token usage should be included in `{CumulativeTokens}` reported by the tool loop (Story 12.2).

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation from `.dev/plans/llm-agentic-tool-loop.md` Phase 2 | Architecture Team |
| 2026-03-28 | 1.1 | Added Logging Requirements section | Logging Audit |
