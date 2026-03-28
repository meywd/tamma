using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Tools;

namespace Tamma.Activities.Tests.LlmCall.Tools;

[TestFixture]
public class ToolOutputHelperTests
{
    [Test]
    public void Truncate_ShortOutput_ReturnsUnchanged()
    {
        // Arrange
        var output = "Hello, World!";

        // Act
        var result = ToolOutputHelper.Truncate(output);

        // Assert
        result.Should().Be(output);
    }

    [Test]
    public void Truncate_LargeOutput_TruncatesWithSuffix()
    {
        // Arrange — create output larger than 50KB
        var output = new string('X', 100 * 1024); // 100KB

        // Act
        var result = ToolOutputHelper.Truncate(output);

        // Assert
        result.Should().Contain("[truncated:");
        result.Should().Contain("bytes total");
        System.Text.Encoding.UTF8.GetByteCount(result).Should()
            .BeLessOrEqualTo(ToolOutputHelper.MaxOutputBytes + 200); // small margin for suffix
    }

    [Test]
    public void Truncate_ExactlyMaxBytes_ReturnsUnchanged()
    {
        // Arrange — create output exactly at the limit
        var output = new string('A', ToolOutputHelper.MaxOutputBytes);

        // Act
        var result = ToolOutputHelper.Truncate(output);

        // Assert
        result.Should().Be(output);
    }

    [Test]
    public void Truncate_EmptyString_ReturnsEmpty()
    {
        // Act
        var result = ToolOutputHelper.Truncate("");

        // Assert
        result.Should().BeEmpty();
    }

    [Test]
    public void Truncate_NullString_ReturnsEmpty()
    {
        // Act
        var result = ToolOutputHelper.Truncate(null!);

        // Assert
        result.Should().BeEmpty();
    }

    [Test]
    public void RedactSecrets_WithApiKey_RedactsValue()
    {
        // Arrange
        var output = "config: api_key=sk-12345abcde";

        // Act
        var result = ToolOutputHelper.RedactSecrets(output);

        // Assert
        result.Should().Contain("[REDACTED]");
        result.Should().NotContain("sk-12345abcde");
    }

    [Test]
    public void RedactSecrets_NoSecrets_ReturnsUnchanged()
    {
        // Arrange
        var output = "Normal output with no secrets";

        // Act
        var result = ToolOutputHelper.RedactSecrets(output);

        // Assert
        result.Should().Be(output);
    }

    [Test]
    public void RedactSecrets_EmptyString_ReturnsEmpty()
    {
        var result = ToolOutputHelper.RedactSecrets("");
        result.Should().BeEmpty();
    }

    [Test]
    public void Truncate_MultiByteUtf8_DoesNotSplitCharacters()
    {
        // Arrange — create a string of multi-byte UTF-8 characters (CJK = 3 bytes each)
        // that exceeds MaxOutputBytes. This tests that truncation does not produce
        // an invalid UTF-8 sequence by cutting in the middle of a character.
        var cjkChar = "\u4e16"; // "world" in Chinese, 3 bytes in UTF-8
        var repeatCount = (ToolOutputHelper.MaxOutputBytes / 3) + 1000;
        var output = string.Concat(Enumerable.Repeat(cjkChar, repeatCount));

        // Act
        var result = ToolOutputHelper.Truncate(output);

        // Assert
        result.Should().Contain("[truncated:");

        // The truncated portion (before the suffix) must be valid UTF-8.
        // Extract just the content portion and verify it round-trips cleanly.
        var contentPart = result[..result.IndexOf("\n[truncated:", StringComparison.Ordinal)];
        var bytes = System.Text.Encoding.UTF8.GetBytes(contentPart);
        var roundTripped = System.Text.Encoding.UTF8.GetString(bytes);
        roundTripped.Should().Be(contentPart, "truncation should not produce broken UTF-8");

        // Every character should be the original CJK character (no partial bytes decoded as replacement chars)
        contentPart.Should().NotContain("\uFFFD",
            "truncation should not produce Unicode replacement characters");
    }

    [Test]
    public void Truncate_SurrogatePairs_DoesNotSplitPair()
    {
        // Arrange — use emoji (surrogate pair, 4 bytes in UTF-8)
        var emoji = "\U0001F600"; // Grinning face
        var repeatCount = (ToolOutputHelper.MaxOutputBytes / 4) + 1000;
        var output = string.Concat(Enumerable.Repeat(emoji, repeatCount));

        // Act
        var result = ToolOutputHelper.Truncate(output);

        // Assert
        result.Should().Contain("[truncated:");
        var contentPart = result[..result.IndexOf("\n[truncated:", StringComparison.Ordinal)];
        contentPart.Should().NotContain("\uFFFD",
            "truncation should not split surrogate pairs");
    }
}
