using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.LlmCall.Tools;

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
        return estimated >= limit;
    }
}
