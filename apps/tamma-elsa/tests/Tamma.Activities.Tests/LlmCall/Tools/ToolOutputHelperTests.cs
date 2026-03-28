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
}
