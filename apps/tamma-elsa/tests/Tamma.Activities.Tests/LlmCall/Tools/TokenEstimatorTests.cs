using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.LlmCall.Tools;

namespace Tamma.Activities.Tests.LlmCall.Tools;

/// <summary>
/// Tests for TokenEstimator (Story 12.3).
/// Covers string estimation, message list estimation, and compaction threshold checks.
/// </summary>
[TestFixture]
public class TokenEstimatorTests
{
    // =====================================================================
    // EstimateTokens(string) Tests
    // =====================================================================

    [Test]
    public void EstimateTokens_EmptyString_ReturnsZero()
    {
        TokenEstimator.EstimateTokens("").Should().Be(0);
    }

    [Test]
    public void EstimateTokens_NullString_ReturnsZero()
    {
        TokenEstimator.EstimateTokens((string?)null).Should().Be(0);
    }

    [Test]
    public void EstimateTokens_KnownLength_ReturnsExpected()
    {
        // "hello world!" = 12 chars -> 12 / 4 = 3 tokens
        TokenEstimator.EstimateTokens("hello world!").Should().Be(3);
    }

    [Test]
    public void EstimateTokens_LargeString_ProportionalResult()
    {
        var largeString = new string('a', 40_000);
        TokenEstimator.EstimateTokens(largeString).Should().Be(10_000);
    }

    [Test]
    public void EstimateTokens_ShortString_IntegerDivision()
    {
        // "Hi" = 2 chars -> 2 / 4 = 0 (integer division)
        TokenEstimator.EstimateTokens("Hi").Should().Be(0);

        // "Hello" = 5 chars -> 5 / 4 = 1
        TokenEstimator.EstimateTokens("Hello").Should().Be(1);
    }

    // =====================================================================
    // EstimateTokens(IEnumerable<ConversationMessage>) Tests
    // =====================================================================

    [Test]
    public void EstimateTokens_MessageList_AggregatesCorrectly()
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = new string('a', 100) },   // 100/4 = 25 + 4 overhead = 29
            new() { Role = "user", Content = new string('b', 200) },     // 200/4 = 50 + 4 overhead = 54
            new() { Role = "assistant", Content = new string('c', 80) }  // 80/4  = 20 + 4 overhead = 24
        };

        var total = TokenEstimator.EstimateTokens(messages);

        // 3 messages * 4 overhead = 12, content = 25 + 50 + 20 = 95, total = 107
        total.Should().Be(107);
    }

    [Test]
    public void EstimateTokens_MessageWithToolCalls_IncludesArguments()
    {
        var messages = new List<ConversationMessage>
        {
            new()
            {
                Role = "assistant",
                Content = new string('a', 40), // 40/4 = 10
                ToolCalls = new[]
                {
                    new ToolCallInfo("id1", "file_read", new string('x', 80)) // name: 9/4=2, args: 80/4=20, overhead=4
                }
            }
        };

        var total = TokenEstimator.EstimateTokens(messages);

        // message overhead = 4, content = 10, tool call: overhead(4) + name(2) + args(20) = 26
        // total = 4 + 10 + 26 = 40
        total.Should().Be(40);
    }

    [Test]
    public void EstimateTokens_MessageWithToolCallId_IncludesIt()
    {
        var messages = new List<ConversationMessage>
        {
            new()
            {
                Role = "tool",
                Content = new string('a', 40), // 40/4 = 10
                ToolCallId = new string('b', 20) // 20/4 = 5
            }
        };

        var total = TokenEstimator.EstimateTokens(messages);

        // overhead = 4, content = 10, toolCallId = 5
        total.Should().Be(19);
    }

    [Test]
    public void EstimateTokens_EmptyMessageList_ReturnsZero()
    {
        var messages = new List<ConversationMessage>();
        TokenEstimator.EstimateTokens(messages).Should().Be(0);
    }

    // =====================================================================
    // ShouldCompact Tests
    // =====================================================================

    [Test]
    public void ShouldCompact_BelowThreshold_ReturnsFalse()
    {
        // Create messages with ~40 tokens of content (well below threshold)
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = new string('a', 80) },  // 80/4 + 4 = 24
            new() { Role = "user", Content = new string('b', 80) }     // 80/4 + 4 = 24
        };
        // Total ~48 tokens, far below 200_000 * 0.8 = 160_000

        TokenEstimator.ShouldCompact(messages, 200_000, 0.8).Should().BeFalse();
    }

    [Test]
    public void ShouldCompact_AboveThreshold_ReturnsTrue()
    {
        // 200_000 * 0.8 = 160_000 token threshold
        // Need ~160_001+ tokens of estimated content
        // At 4 chars/token, we need 640_000+ chars just in content
        var bigContent = new string('a', 640_004); // 640_004/4 = 160_001 content tokens
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = bigContent }
        };

        TokenEstimator.ShouldCompact(messages, 200_000, 0.8).Should().BeTrue();
    }

    [Test]
    public void ShouldCompact_AtExactThreshold_ReturnsTrue()
    {
        // 200_000 * 0.8 = 160_000 threshold
        // We need exactly 160_000 estimated tokens
        // 1 message: overhead(4) + content tokens = 160_000
        // So content tokens = 159_996, content chars = 159_996 * 4 = 639_984
        var content = new string('a', 639_984);
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = content }
        };

        var estimated = TokenEstimator.EstimateTokens(messages);
        estimated.Should().Be(160_000);
        TokenEstimator.ShouldCompact(messages, 200_000, 0.8).Should().BeTrue();
    }
}
