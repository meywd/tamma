---
title: "Story 12.3: Context Compaction — Implementation Plan"
sidebar:
  order: 120
---

## Overview

This plan adds automatic context compaction to the agentic tool loop. When the estimated token count of the conversation history exceeds a configurable threshold (default 80% of the model's context window), the system summarizes older messages into a compact summary using a separate LLM call, preserving the system prompt and the most recent 4 messages.

---

## Step-by-Step Implementation Tasks

### Task 1: Create TokenEstimator

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/TokenEstimator.cs`

```csharp
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.ToolExecution;

/// <summary>
/// Estimates token count using ~4 characters per token approximation.
/// This is sufficient for compaction triggers (not billing).
/// Actual tokenizers vary by model, but the 4:1 ratio is within ~20% for
/// English text across GPT/Claude tokenizers.
/// </summary>
public static class TokenEstimator
{
    /// <summary>
    /// Characters-per-token ratio. 4 is the industry standard approximation.
    /// </summary>
    public const int CharsPerToken = 4;

    /// <summary>
    /// Estimate token count for a single string.
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return text.Length / CharsPerToken;
    }

    /// <summary>
    /// Estimate total token count for a conversation message list.
    /// Accounts for: message content, tool call arguments, tool call IDs,
    /// and per-message overhead (~4 tokens for role/structure).
    /// </summary>
    public static int EstimateTokens(IEnumerable<ConversationMessage> messages)
    {
        var total = 0;

        foreach (var msg in messages)
        {
            // Per-message overhead (role, delimiters)
            total += 4;

            // Content
            total += EstimateTokens(msg.Content);

            // Tool calls (assistant messages)
            if (msg.ToolCalls != null)
            {
                foreach (var tc in msg.ToolCalls)
                {
                    // Tool call overhead (id, name, structure)
                    total += 4;
                    total += EstimateTokens(tc.Name);
                    total += EstimateTokens(tc.ArgumentsJson);
                }
            }

            // Tool call ID (tool result messages)
            total += EstimateTokens(msg.ToolCallId);
        }

        return total;
    }

    /// <summary>
    /// Check if the message list exceeds the compaction threshold.
    /// </summary>
    /// <param name="messages">Current conversation messages.</param>
    /// <param name="contextWindowTokens">Model's context window size in tokens.</param>
    /// <param name="threshold">Fraction (0.0-1.0) at which to trigger compaction.</param>
    /// <returns>True if compaction should be triggered.</returns>
    public static bool ShouldCompact(
        IEnumerable<ConversationMessage> messages,
        int contextWindowTokens,
        double threshold)
    {
        var estimated = EstimateTokens(messages);
        var limit = (int)(contextWindowTokens * threshold);
        return estimated > limit;
    }
}
```

---

### Task 2: Create ContextCompactor

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/ContextCompactor.cs`

```csharp
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.ToolExecution;

/// <summary>
/// Compacts conversation history by summarizing older messages via an LLM call.
///
/// Strategy:
///   - Preserve: system prompt (first message) + last N messages (default 4)
///   - Summarize: everything between system prompt and preserved tail
///   - Replace summarized messages with a single user-role "summary" message
///   - If summarization fails, return original messages unchanged (best-effort)
///
/// Result:
///   [system, summary_message, last_4_messages...]
/// </summary>
public class ContextCompactor
{
    /// <summary>
    /// Default number of recent messages to preserve (not summarize).
    /// </summary>
    public const int DefaultPreservedTailCount = 4;

    private readonly ILogger<ContextCompactor> _logger;

    /// <summary>
    /// Delegate for making LLM calls during compaction.
    /// This decouples the compactor from the specific LLM call implementation.
    /// </summary>
    /// <param name="prompt">The summarization prompt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The LLM's summary text, or null on failure.</returns>
    public delegate Task<string?> SummarizationCallDelegate(
        string prompt, CancellationToken cancellationToken);

    public ContextCompactor(ILogger<ContextCompactor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Compact the conversation history if it exceeds the token threshold.
    /// Returns a new list — does NOT mutate the input.
    /// </summary>
    /// <param name="messages">Current conversation messages.</param>
    /// <param name="contextWindowTokens">Model's context window size in tokens.</param>
    /// <param name="threshold">Fraction (0.0-1.0) at which to trigger compaction.</param>
    /// <param name="summarize">Delegate to call the LLM for summarization.</param>
    /// <param name="preservedTailCount">Number of recent messages to preserve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Tuple of (compacted messages, tokens used for summarization, whether compaction occurred).
    /// </returns>
    public async Task<(List<ConversationMessage> Messages, int SummarizationTokens, bool Compacted)>
        CompactIfNeeded(
            List<ConversationMessage> messages,
            int contextWindowTokens,
            double threshold,
            SummarizationCallDelegate summarize,
            int preservedTailCount = DefaultPreservedTailCount,
            CancellationToken cancellationToken = default)
    {
        // Check if compaction is needed
        if (!TokenEstimator.ShouldCompact(messages, contextWindowTokens, threshold))
        {
            return (messages, 0, false);
        }

        var estimatedTokens = TokenEstimator.EstimateTokens(messages);
        _logger.LogInformation(
            "Context compaction triggered at {TokenCount}/{ContextWindow} tokens ({MessageCount} messages)",
            estimatedTokens, contextWindowTokens, messages.Count);

        // Need at least: system + user + preservedTail messages to have something to summarize
        // (system + messages_to_summarize + preservedTail must have messages_to_summarize > 0)
        var minMessagesForCompaction = 1 + preservedTailCount + 1; // system + tail + at least 1 to summarize
        if (messages.Count < minMessagesForCompaction)
        {
            _logger.LogDebug(
                "Skipping compaction: only {Count} messages, need at least {Min}",
                messages.Count, minMessagesForCompaction);
            return (messages, 0, false);
        }

        // Split messages
        var systemMessage = messages[0]; // Always the system prompt
        var tailStart = Math.Max(1, messages.Count - preservedTailCount);
        var messagesToSummarize = messages.Skip(1).Take(tailStart - 1).ToList();
        var preservedTail = messages.Skip(tailStart).ToList();

        if (messagesToSummarize.Count == 0)
        {
            _logger.LogDebug("No messages to summarize after preserving system + tail");
            return (messages, 0, false);
        }

        // Build summarization prompt
        var summaryPrompt = BuildSummarizationPrompt(messagesToSummarize);

        try
        {
            var summary = await summarize(summaryPrompt, cancellationToken);

            if (string.IsNullOrWhiteSpace(summary))
            {
                _logger.LogWarning("Compaction LLM returned empty summary, keeping original messages");
                return (messages, 0, false);
            }

            var summaryTokens = TokenEstimator.EstimateTokens(summaryPrompt) +
                                TokenEstimator.EstimateTokens(summary);

            // Build compacted message list
            var compacted = new List<ConversationMessage>
            {
                systemMessage,
                new ConversationMessage
                {
                    Role = "user",
                    Content = $"[Context summary from earlier conversation]\n\n{summary}"
                }
            };
            compacted.AddRange(preservedTail);

            var compactedTokens = TokenEstimator.EstimateTokens(compacted);

            _logger.LogInformation(
                "Context compacted: {OldCount} messages ({OldTokens} tokens) -> {NewCount} messages ({NewTokens} tokens), summarized {SummarizedCount} messages",
                messages.Count, estimatedTokens, compacted.Count, compactedTokens, messagesToSummarize.Count);

            return (compacted, summaryTokens, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Compaction LLM call failed, continuing with uncompacted history");
            return (messages, 0, false);
        }
    }

    /// <summary>
    /// Build the prompt sent to the LLM for summarizing conversation history.
    /// </summary>
    internal static string BuildSummarizationPrompt(List<ConversationMessage> messages)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Summarize the following conversation history between an AI assistant and its tools.");
        sb.AppendLine("Preserve all key information: what was asked, what files were read/written, what commands");
        sb.AppendLine("were run, what errors occurred, and what decisions were made. Be concise but complete.");
        sb.AppendLine();
        sb.AppendLine("---BEGIN CONVERSATION---");
        sb.AppendLine();

        foreach (var msg in messages)
        {
            sb.AppendLine($"[{msg.Role.ToUpperInvariant()}]");

            if (!string.IsNullOrEmpty(msg.Content))
            {
                // Truncate very long content in the summarization prompt to avoid recursion
                var content = msg.Content.Length > 2000
                    ? msg.Content[..2000] + "...(truncated)"
                    : msg.Content;
                sb.AppendLine(content);
            }

            if (msg.ToolCalls != null)
            {
                foreach (var tc in msg.ToolCalls)
                {
                    sb.AppendLine($"  -> Tool call: {tc.Name}({tc.ArgumentsJson})");
                }
            }

            if (msg.ToolCallId != null)
            {
                sb.AppendLine($"  (tool_call_id: {msg.ToolCallId})");
            }

            sb.AppendLine();
        }

        sb.AppendLine("---END CONVERSATION---");
        sb.AppendLine();
        sb.AppendLine("Provide a concise summary preserving all essential information:");

        return sb.ToString();
    }
}
```

---

### Task 3: Integrate Compaction into the Agentic Tool Loop

**File**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs`

**Modify the `AgenticToolLoop` method** (implemented in Story 12.2). Add compaction check at the top of each loop iteration, before the LLM call.

**3a. Inject ContextCompactor**:

Add to the constructor:
```csharp
private readonly ContextCompactor? _contextCompactor;

public CallLlmInlineActivity(
    ILogger<CallLlmInlineActivity>? logger,
    IHttpClientFactory? httpClientFactory,
    IConfiguration? configuration,
    IToolExecutorRegistry? toolRegistry = null,
    ContextCompactor? contextCompactor = null)  // NEW
{
    _logger = logger;
    _httpClientFactory = httpClientFactory;
    _configuration = configuration;
    _toolRegistry = toolRegistry;
    _contextCompactor = contextCompactor;
}
```

**3b. Add compaction check inside the loop** (in `AgenticToolLoop`, at the top of the for-loop body, before the LLM call):

```csharp
for (var step = 0; step < loopConfig.MaxSteps; step++)
{
    // ═══ Context compaction check ═══
    if (_contextCompactor != null && step > 0)
    {
        var (compactedMessages, compactionTokens, wasCompacted) =
            await _contextCompactor.CompactIfNeeded(
                messages,
                loopConfig.ContextWindowTokens,
                loopConfig.CompactionThreshold,
                async (prompt, ct) =>
                {
                    // Make a single-turn summarization LLM call using the same provider
                    var summaryResponse = providerName.Equals("anthropic", StringComparison.OrdinalIgnoreCase)
                        ? await CallAnthropicMessages(httpClient, providerConfig, model,
                            "You are a precise conversation summarizer.", prompt, 2048, 0.3, null)
                        : await CallOpenAiCompatible(httpClient, providerConfig, model,
                            "You are a precise conversation summarizer.", prompt, 2048, 0.3, null);

                    return summaryResponse.Success ? summaryResponse.ResponseText : null;
                },
                cancellationToken: context.CancellationToken);

        if (wasCompacted)
        {
            messages = compactedMessages;
            totalPromptTokens += compactionTokens; // Compaction call counts toward total
        }
    }

    // ... rest of the loop (LLM call, tool execution, etc.) ...
}
```

---

### Task 4: Register ContextCompactor in DI

**File**: `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs`

Add after the tool executor registrations (added in Story 12.1):

```csharp
// Context compaction for long-running tool loops
builder.Services.AddSingleton<Tamma.Activities.ToolExecution.ContextCompactor>();
```

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/TokenEstimator.cs` | Token count estimation (~4 chars/token) |
| 2 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/ContextCompactor.cs` | Conversation compaction logic |

## Files to Modify

| # | File Path | Specific Changes |
|---|-----------|-----------------|
| 1 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` | Inject `ContextCompactor`; add compaction check in `AgenticToolLoop` at top of each iteration |
| 2 | `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` | Register `ContextCompactor` in DI |

---

## Detailed Compaction Flow

```
Turn 0: messages = [system, user]
  -> LLM responds with tool call
  -> messages = [system, user, assistant+tool_calls, tool_result]

Turn 1: messages = [system, user, assistant, tool, assistant, tool]
  -> Estimate tokens: 30,000 — below 160,000 threshold (80% of 200K)
  -> No compaction
  -> LLM responds with tool call
  -> messages = [system, user, asst, tool, asst, tool, asst, tool]

...

Turn 8: messages = [system, user, asst, tool, asst, tool, ..., asst, tool]  (20 messages)
  -> Estimate tokens: 165,000 — ABOVE 160,000 threshold
  -> Compaction triggered!
  -> messagesToSummarize = messages[1..16] (skipping system, keeping last 4)
  -> preservedTail = messages[16..20]
  -> LLM summarizes messagesToSummarize into a compact summary
  -> New messages = [system, summary_message, messages[16], messages[17], messages[18], messages[19]]
  -> Estimate tokens: 45,000 — well below threshold
  -> Continue loop with compacted history

Turn 9: messages = [system, summary, last_4, ...]
  -> Normal LLM call with compacted context
```

---

## Test Cases

### TokenEstimator Tests

**Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/TokenEstimatorTests.cs`

| # | Test Method | Verifies |
|---|-------------|----------|
| 1 | `EstimateTokens_EmptyString_ReturnsZero` | `""` returns 0 |
| 2 | `EstimateTokens_NullString_ReturnsZero` | `null` returns 0 |
| 3 | `EstimateTokens_KnownLength_ReturnsExpected` | `"hello world!"` (12 chars) returns 3 |
| 4 | `EstimateTokens_LargeString_ProportionalResult` | 40,000 char string returns 10,000 |
| 5 | `EstimateTokens_MessageList_AggregatesCorrectly` | List of 3 messages sums content + overhead |
| 6 | `EstimateTokens_MessageWithToolCalls_IncludesArguments` | Tool call arguments contribute to count |
| 7 | `ShouldCompact_BelowThreshold_ReturnsFalse` | 10K tokens, 200K window, 0.8 threshold -> false |
| 8 | `ShouldCompact_AboveThreshold_ReturnsTrue` | 170K tokens, 200K window, 0.8 threshold -> true |
| 9 | `ShouldCompact_AtExactThreshold_ReturnsTrue` | 160K tokens, 200K window, 0.8 threshold -> true (boundary) |

### ContextCompactor Tests

**Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/ContextCompactorTests.cs`

| # | Test Method | Verifies |
|---|-------------|----------|
| 10 | `CompactIfNeeded_BelowThreshold_ReturnsOriginal` | No compaction when under threshold |
| 11 | `CompactIfNeeded_AboveThreshold_CompactsMessages` | Compaction triggered, messages reduced |
| 12 | `CompactIfNeeded_SystemPromptPreserved` | System prompt is always first message after compaction |
| 13 | `CompactIfNeeded_Last4MessagesPreserved` | Last 4 messages are unchanged after compaction |
| 14 | `CompactIfNeeded_MiddleMessagesReplacedWithSummary` | Messages between system and tail are replaced with summary |
| 15 | `CompactIfNeeded_SummaryMessageHasCorrectRole` | Summary message has role "user" |
| 16 | `CompactIfNeeded_SummaryMessageContainsSummaryText` | Summary message content includes "[Context summary]" prefix |
| 17 | `CompactIfNeeded_LlmFailure_ReturnsOriginal` | Summarization throws -> original messages returned, no crash |
| 18 | `CompactIfNeeded_EmptySummary_ReturnsOriginal` | Summarization returns empty -> original messages returned |
| 19 | `CompactIfNeeded_FewerThan6Messages_SkipsCompaction` | With only 5 messages, compaction is skipped (nothing to summarize) |
| 20 | `CompactIfNeeded_ReturnsTokensUsed` | `SummarizationTokens` output reflects estimated cost of compaction call |
| 21 | `CompactIfNeeded_DoesNotMutateInput` | Original message list is unchanged after compaction |

### BuildSummarizationPrompt Tests

| # | Test Method | Verifies |
|---|-------------|----------|
| 22 | `BuildSummarizationPrompt_IncludesAllMessages` | All message roles and content appear in prompt |
| 23 | `BuildSummarizationPrompt_TruncatesLongContent` | Content over 2000 chars is truncated in prompt |
| 24 | `BuildSummarizationPrompt_IncludesToolCallNames` | Tool call names appear in the prompt |

---

## Verification Steps

1. **Build**: `cd apps/tamma-elsa && dotnet build` — compiles without errors
2. **Unit tests**: `cd apps/tamma-elsa && dotnet test --filter "TokenEstimator|ContextCompactor"` — all 24 tests pass
3. **Integration test (manual)**: Run a tool loop with a small `ContextWindowTokens` (e.g., 1000) to force compaction early. Verify:
   - Log message: `"Context compaction triggered at X/Y tokens"`
   - Post-compaction messages have correct structure
   - Loop continues normally after compaction
4. **No compaction when not needed**: Run a short tool loop (2 turns) and verify no compaction occurs
5. **Failure resilience**: Make the summarization delegate throw an exception, verify the loop continues with original messages

---

## Risks and Edge Cases

| Risk | Mitigation |
|------|------------|
| **Token estimation inaccuracy** | 4:1 ratio is ~20% off for English text; the 80% threshold provides a 20% buffer, so actual overflow is unlikely. For non-English text (CJK), the ratio is closer to 2:1 — consider adding a `CharsPerToken` config option for CJK workspaces. |
| **Compaction loses critical context** | The summarization prompt explicitly instructs the LLM to preserve "what files were read/written, what commands were run, what errors occurred, and what decisions were made." Preserving the last 4 messages ensures immediate context is intact. |
| **Compaction LLM call fails** | Best-effort: if summarization fails, the original messages are returned unchanged and the loop continues. The next iteration will try compaction again. |
| **Recursive compaction** | The summarization prompt truncates individual message contents to 2000 chars, preventing the summarization prompt itself from being too large. |
| **Compaction during a multi-tool-result turn** | If the last 4 messages are in the middle of an assistant→tool_result sequence, the preserved tail may not form a valid conversation. Mitigation: adjust `preservedTailCount` to align with message pairs (assistant + tool_results). |
| **Double compaction** | Compaction only runs at the top of the loop (before the LLM call), so it runs at most once per turn. After compaction, the token count drops well below the threshold, so the next turn will not trigger compaction again unless tool outputs are very large. |
| **Cost of compaction LLM call** | Uses the same provider/model as the main loop. Consider using a smaller/cheaper model for summarization (e.g., Claude Haiku). This can be a future optimization. |

---

## Implementation Order

1. `TokenEstimator` — pure static methods, no dependencies
2. `ContextCompactor` — depends on `TokenEstimator` and `ConversationMessage`
3. Inject `ContextCompactor` into `CallLlmInlineActivity`
4. Add compaction check in `AgenticToolLoop`
5. Register in DI (`Program.cs`)
6. Write tests (TokenEstimator tests first, then ContextCompactor)
