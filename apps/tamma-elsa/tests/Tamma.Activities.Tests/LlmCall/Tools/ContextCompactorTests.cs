using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.LlmCall.Tools;

namespace Tamma.Activities.Tests.LlmCall.Tools;

/// <summary>
/// Tests for ContextCompactor (Story 12.3).
/// Covers compaction triggering, message preservation, failure handling, and edge cases.
/// </summary>
[TestFixture]
public class ContextCompactorTests
{
    private ContextCompactor _compactor = null!;
    private Mock<ILogger<ContextCompactor>> _loggerMock = null!;

    [SetUp]
    public void SetUp()
    {
        _loggerMock = new Mock<ILogger<ContextCompactor>>();
        _compactor = new ContextCompactor(_loggerMock.Object);
    }

    /// <summary>
    /// Helper: create a message list with enough content to exceed the threshold.
    /// </summary>
    private static List<ConversationMessage> CreateLargeConversation(int messageCount, int contentSize = 1000)
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "You are a helpful assistant." }
        };

        for (var i = 0; i < messageCount - 1; i++)
        {
            var role = i % 2 == 0 ? "user" : "assistant";
            messages.Add(new ConversationMessage
            {
                Role = role,
                Content = new string((char)('a' + (i % 26)), contentSize)
            });
        }

        return messages;
    }

    // =====================================================================
    // Compaction Trigger Tests
    // =====================================================================

    [Test]
    public async Task CompactIfNeeded_BelowThreshold_ReturnsOriginal()
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System prompt" },
            new() { Role = "user", Content = "Hello" },
            new() { Role = "assistant", Content = "Hi there" },
            new() { Role = "user", Content = "How are you?" },
            new() { Role = "assistant", Content = "I'm fine" },
            new() { Role = "user", Content = "Great" },
            new() { Role = "assistant", Content = "Thanks" }
        };

        // Very large context window -- won't trigger compaction
        var (result, tokens, compacted) = await _compactor.CompactIfNeeded(
            messages, 200_000, 0.8,
            async (_, _) => "Summary");

        compacted.Should().BeFalse();
        tokens.Should().Be(0);
        result.Should().BeSameAs(messages);
    }

    [Test]
    public async Task CompactIfNeeded_AboveThreshold_CompactsMessages()
    {
        // Create messages that will exceed a small context window
        var messages = CreateLargeConversation(10, 500);

        // Small context window to force compaction: 10 messages * ~129 tokens each = ~1290 total
        // With threshold 0.8 and window 1000, limit = 800
        var (result, tokens, compacted) = await _compactor.CompactIfNeeded(
            messages, 1000, 0.8,
            async (_, _) => "This is the summary of earlier conversation.");

        compacted.Should().BeTrue();
        tokens.Should().BeGreaterThan(0);
        result.Count.Should().BeLessThan(messages.Count);
    }

    // =====================================================================
    // Message Preservation Tests
    // =====================================================================

    [Test]
    public async Task CompactIfNeeded_SystemPromptPreserved()
    {
        var messages = CreateLargeConversation(10, 500);
        var systemContent = messages[0].Content;

        var (result, _, compacted) = await _compactor.CompactIfNeeded(
            messages, 1000, 0.8,
            async (_, _) => "Summary text");

        compacted.Should().BeTrue();
        result[0].Role.Should().Be("system");
        result[0].Content.Should().Be(systemContent);
    }

    [Test]
    public async Task CompactIfNeeded_Last4MessagesPreserved()
    {
        var messages = CreateLargeConversation(10, 500);
        var last4 = messages.Skip(messages.Count - 4).ToList();

        var (result, _, compacted) = await _compactor.CompactIfNeeded(
            messages, 1000, 0.8,
            async (_, _) => "Summary text");

        compacted.Should().BeTrue();
        // Result should be: system + summary + last 4 = 6 messages
        result.Count.Should().Be(6);

        // Last 4 messages should be preserved exactly
        for (var i = 0; i < 4; i++)
        {
            result[result.Count - 4 + i].Role.Should().Be(last4[i].Role);
            result[result.Count - 4 + i].Content.Should().Be(last4[i].Content);
        }
    }

    [Test]
    public async Task CompactIfNeeded_MiddleMessagesReplacedWithSummary()
    {
        var messages = CreateLargeConversation(10, 500);

        var (result, _, compacted) = await _compactor.CompactIfNeeded(
            messages, 1000, 0.8,
            async (_, _) => "This is the summary.");

        compacted.Should().BeTrue();
        // result[0] = system, result[1] = summary, result[2..5] = last 4
        result[1].Role.Should().Be("user");
        result[1].Content.Should().Contain("[Context summary from earlier conversation]");
        result[1].Content.Should().Contain("This is the summary.");
    }

    [Test]
    public async Task CompactIfNeeded_SummaryMessageHasCorrectRole()
    {
        var messages = CreateLargeConversation(10, 500);

        var (result, _, compacted) = await _compactor.CompactIfNeeded(
            messages, 1000, 0.8,
            async (_, _) => "Summary");

        compacted.Should().BeTrue();
        result[1].Role.Should().Be("user");
    }

    [Test]
    public async Task CompactIfNeeded_SummaryMessageContainsSummaryText()
    {
        var messages = CreateLargeConversation(10, 500);

        var (result, _, compacted) = await _compactor.CompactIfNeeded(
            messages, 1000, 0.8,
            async (_, _) => "Important decisions were made about file handling.");

        compacted.Should().BeTrue();
        result[1].Content.Should().Contain("[Context summary from earlier conversation]");
        result[1].Content.Should().Contain("Important decisions were made about file handling.");
    }

    // =====================================================================
    // Failure Handling Tests
    // =====================================================================

    [Test]
    public async Task CompactIfNeeded_LlmFailure_ReturnsOriginal()
    {
        var messages = CreateLargeConversation(10, 500);

        var (result, tokens, compacted) = await _compactor.CompactIfNeeded(
            messages, 1000, 0.8,
            (_, _) => throw new HttpRequestException("LLM API error"));

        compacted.Should().BeFalse();
        tokens.Should().Be(0);
        result.Should().BeSameAs(messages);
    }

    [Test]
    public async Task CompactIfNeeded_EmptySummary_ReturnsOriginal()
    {
        var messages = CreateLargeConversation(10, 500);

        var (result, tokens, compacted) = await _compactor.CompactIfNeeded(
            messages, 1000, 0.8,
            async (_, _) => "");

        compacted.Should().BeFalse();
        tokens.Should().Be(0);
        result.Should().BeSameAs(messages);
    }

    [Test]
    public async Task CompactIfNeeded_NullSummary_ReturnsOriginal()
    {
        var messages = CreateLargeConversation(10, 500);

        var (result, tokens, compacted) = await _compactor.CompactIfNeeded(
            messages, 1000, 0.8,
            async (_, _) => (string?)null);

        compacted.Should().BeFalse();
        tokens.Should().Be(0);
        result.Should().BeSameAs(messages);
    }

    // =====================================================================
    // Edge Case Tests
    // =====================================================================

    [Test]
    public async Task CompactIfNeeded_FewerThan6Messages_SkipsCompaction()
    {
        // With default preservedTailCount=4, need at least 6 messages (system + 4 tail + 1 to summarize)
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = new string('a', 4000) }, // large enough to trigger
            new() { Role = "user", Content = new string('b', 4000) },
            new() { Role = "assistant", Content = new string('c', 4000) },
            new() { Role = "user", Content = new string('d', 4000) },
            new() { Role = "assistant", Content = new string('e', 4000) }
        };

        // Only 5 messages — too few to compact (need system + 1 to summarize + 4 preserved)
        var (result, tokens, compacted) = await _compactor.CompactIfNeeded(
            messages, 1000, 0.8,
            async (_, _) => "Summary");

        compacted.Should().BeFalse();
        tokens.Should().Be(0);
        result.Should().BeSameAs(messages);
    }

    [Test]
    public async Task CompactIfNeeded_ExactlyMinMessages_Compacts()
    {
        // 6 messages = system + 1 to summarize + 4 tail = minimum for compaction
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = new string('a', 2000) },
            new() { Role = "user", Content = new string('b', 2000) },     // This gets summarized
            new() { Role = "assistant", Content = new string('c', 2000) }, // Preserved tail
            new() { Role = "user", Content = new string('d', 2000) },     // Preserved tail
            new() { Role = "assistant", Content = new string('e', 2000) }, // Preserved tail
            new() { Role = "user", Content = new string('f', 2000) }      // Preserved tail
        };

        // Small window to trigger compaction
        var (result, _, compacted) = await _compactor.CompactIfNeeded(
            messages, 1000, 0.8,
            async (_, _) => "Summary of the user message.");

        compacted.Should().BeTrue();
        result.Count.Should().Be(6); // system + summary + 4 tail
        result[0].Role.Should().Be("system");
        result[1].Content.Should().Contain("[Context summary from earlier conversation]");
    }

    // =====================================================================
    // Token Tracking Tests
    // =====================================================================

    [Test]
    public async Task CompactIfNeeded_ReturnsTokensUsed()
    {
        var messages = CreateLargeConversation(10, 500);

        var (_, tokens, compacted) = await _compactor.CompactIfNeeded(
            messages, 1000, 0.8,
            async (_, _) => "Summary of the conversation with key details.");

        compacted.Should().BeTrue();
        // Tokens should include both prompt and response estimates
        tokens.Should().BeGreaterThan(0);
    }

    // =====================================================================
    // Input Immutability Tests
    // =====================================================================

    [Test]
    public async Task CompactIfNeeded_DoesNotMutateInput()
    {
        var messages = CreateLargeConversation(10, 500);
        var originalCount = messages.Count;
        var originalFirstContent = messages[0].Content;
        var originalLastContent = messages[^1].Content;

        var (result, _, compacted) = await _compactor.CompactIfNeeded(
            messages, 1000, 0.8,
            async (_, _) => "Summary");

        compacted.Should().BeTrue();

        // Original list should be unchanged
        messages.Count.Should().Be(originalCount);
        messages[0].Content.Should().Be(originalFirstContent);
        messages[^1].Content.Should().Be(originalLastContent);

        // Result should be a different list
        result.Should().NotBeSameAs(messages);
    }

    // =====================================================================
    // BuildSummarizationPrompt Tests
    // =====================================================================

    [Test]
    public void BuildSummarizationPrompt_IncludesAllMessages()
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "user", Content = "Read the config file" },
            new() { Role = "assistant", Content = "I'll read it now." },
            new() { Role = "tool", Content = "config content here", ToolCallId = "t1" }
        };

        var prompt = ContextCompactor.BuildSummarizationPrompt(messages);

        prompt.Should().Contain("[USER]");
        prompt.Should().Contain("Read the config file");
        prompt.Should().Contain("[ASSISTANT]");
        prompt.Should().Contain("I'll read it now.");
        prompt.Should().Contain("[TOOL]");
        prompt.Should().Contain("config content here");
        prompt.Should().Contain("(tool_call_id: t1)");
        prompt.Should().Contain("---BEGIN CONVERSATION---");
        prompt.Should().Contain("---END CONVERSATION---");
    }

    [Test]
    public void BuildSummarizationPrompt_TruncatesLongContent()
    {
        var longContent = new string('x', 3000);
        var messages = new List<ConversationMessage>
        {
            new() { Role = "user", Content = longContent }
        };

        var prompt = ContextCompactor.BuildSummarizationPrompt(messages);

        prompt.Should().Contain("...(truncated)");
        // Should contain the first 2000 chars but not the full 3000
        prompt.Should().NotContain(longContent);
    }

    [Test]
    public void BuildSummarizationPrompt_IncludesToolCallNames()
    {
        var messages = new List<ConversationMessage>
        {
            new()
            {
                Role = "assistant",
                Content = "Let me read that.",
                ToolCalls = new[]
                {
                    new ToolCallInfo("t1", "file_read", "{\"path\":\"README.md\"}")
                }
            }
        };

        var prompt = ContextCompactor.BuildSummarizationPrompt(messages);

        prompt.Should().Contain("file_read");
        prompt.Should().Contain("README.md");
        prompt.Should().Contain("-> Tool call:");
    }

    [Test]
    public void BuildSummarizationPrompt_NullContent_DoesNotCrash()
    {
        var messages = new List<ConversationMessage>
        {
            new()
            {
                Role = "assistant",
                Content = null,
                ToolCalls = new[] { new ToolCallInfo("t1", "search_code", "{\"query\":\"main\"}") }
            }
        };

        var action = () => ContextCompactor.BuildSummarizationPrompt(messages);
        action.Should().NotThrow();

        var prompt = ContextCompactor.BuildSummarizationPrompt(messages);
        prompt.Should().Contain("[ASSISTANT]");
        prompt.Should().Contain("search_code");
    }

    // =====================================================================
    // Cancellation Tests
    // =====================================================================

    [Test]
    public async Task CompactIfNeeded_CancellationRespected()
    {
        var messages = CreateLargeConversation(10, 500);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // OperationCanceledException should propagate (not be swallowed)
        var act = async () => await _compactor.CompactIfNeeded(
            messages, 1000, 0.8,
            async (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return "Summary";
            },
            cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
