using System.Text;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.LlmCall.Tools;

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
    /// Returns a new list -- does NOT mutate the input.
    /// </summary>
    /// <param name="messages">Current conversation messages.</param>
    /// <param name="contextWindowTokens">Model's context window size in tokens.</param>
    /// <param name="threshold">Fraction (0.0-1.0) at which to trigger compaction.</param>
    /// <param name="summarize">Delegate to call the LLM for summarization.</param>
    /// <param name="workflowInstanceId">Workflow instance ID for log correlation.</param>
    /// <param name="turnNumber">Current turn number for log correlation.</param>
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
            string? workflowInstanceId = null,
            int turnNumber = 0,
            int preservedTailCount = DefaultPreservedTailCount,
            CancellationToken cancellationToken = default)
    {
        var estimatedTokens = TokenEstimator.EstimateTokens(messages);
        var isAboveThreshold = TokenEstimator.ShouldCompact(messages, contextWindowTokens, threshold);

        _logger.LogDebug(
            "Token estimate computed: WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, EstimatedTokens={EstimatedTokens}, ContextWindowTokens={ContextWindowTokens}, ThresholdPercent={ThresholdPercent}, IsAboveThreshold={IsAboveThreshold}",
            workflowInstanceId, turnNumber, estimatedTokens, contextWindowTokens,
            threshold * 100, isAboveThreshold);

        // Check if compaction is needed
        if (!isAboveThreshold)
        {
            return (messages, 0, false);
        }

        // Need at least: system + preservedTail + at least 1 to summarize
        var minMessagesForCompaction = 1 + preservedTailCount + 1;
        if (messages.Count < minMessagesForCompaction)
        {
            _logger.LogDebug(
                "Context compaction skipped (too few messages): WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, MessageCount={MessageCount}",
                workflowInstanceId, turnNumber, messages.Count);
            return (messages, 0, false);
        }

        // Split messages
        var systemMessage = messages[0]; // Always the system prompt
        var tailStart = Math.Max(1, messages.Count - preservedTailCount);
        var messagesToSummarize = messages.Skip(1).Take(tailStart - 1).ToList();
        var preservedTail = messages.Skip(tailStart).ToList();

        if (messagesToSummarize.Count == 0)
        {
            _logger.LogDebug(
                "Context compaction skipped (too few messages): WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, MessageCount={MessageCount}",
                workflowInstanceId, turnNumber, messages.Count);
            return (messages, 0, false);
        }

        _logger.LogInformation(
            "Context compaction triggered at {EstimatedTokens}/{ContextWindowTokens} tokens, compacted {MessageCountToSummarize} messages: WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, MessageCountBefore={MessageCountBefore}",
            estimatedTokens, contextWindowTokens, messagesToSummarize.Count,
            workflowInstanceId, turnNumber, messages.Count);

        // Build summarization prompt
        var summaryPrompt = BuildSummarizationPrompt(messagesToSummarize);
        var compactionSw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _logger.LogDebug(
                "Summarization LLM call started: WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, InputSizeChars={InputSizeChars}",
                workflowInstanceId, turnNumber, summaryPrompt.Length);

            var summary = await summarize(summaryPrompt, cancellationToken);

            compactionSw.Stop();

            if (string.IsNullOrWhiteSpace(summary))
            {
                _logger.LogWarning(
                    "Context compaction failed (LLM error): WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, ExceptionType={ExceptionType}, ExceptionMessage={ExceptionMessage}",
                    workflowInstanceId, turnNumber, "EmptyResponse", "Compaction LLM returned empty summary");
                return (messages, 0, false);
            }

            var summaryTokens = TokenEstimator.EstimateTokens(summaryPrompt) +
                                TokenEstimator.EstimateTokens(summary);

            _logger.LogDebug(
                "Summarization LLM call completed: WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, SummaryLengthChars={SummaryLengthChars}, SummarizationTokens={SummarizationTokens}, DurationMs={DurationMs}",
                workflowInstanceId, turnNumber, summary.Length, summaryTokens, compactionSw.ElapsedMilliseconds);

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
                "Context compaction completed: WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, MessageCountBefore={MessageCountBefore}, MessageCountAfter={MessageCountAfter}, TokensBefore={TokensBefore}, TokensAfter={TokensAfter}, CompactionDurationMs={CompactionDurationMs}, SummarizationTokensUsed={SummarizationTokensUsed}",
                workflowInstanceId, turnNumber, messages.Count, compacted.Count,
                estimatedTokens, compactedTokens, compactionSw.ElapsedMilliseconds, summaryTokens);

            return (compacted, summaryTokens, true);
        }
        catch (OperationCanceledException)
        {
            compactionSw.Stop();
            throw; // Cancellation is not a compaction failure — propagate
        }
        catch (Exception ex)
        {
            compactionSw.Stop();
            _logger.LogWarning(
                "Context compaction failed (LLM error): WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, ExceptionType={ExceptionType}, ExceptionMessage={ExceptionMessage}",
                workflowInstanceId, turnNumber, ex.GetType().Name, ex.Message);
            return (messages, 0, false);
        }
    }

    /// <summary>
    /// Build the prompt sent to the LLM for summarizing conversation history.
    /// </summary>
    internal static string BuildSummarizationPrompt(List<ConversationMessage> messages)
    {
        var sb = new StringBuilder();
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
