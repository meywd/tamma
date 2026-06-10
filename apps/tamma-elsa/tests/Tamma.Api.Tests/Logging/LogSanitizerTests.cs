using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Logging;

namespace Tamma.Api.Tests.Logging;

[TestFixture]
public class LogSanitizerTests
{
    [Test]
    public void Clean_NullInput_ReturnsPlaceholder()
        => LogSanitizer.Clean(null).Should().Be("<null>");

    [Test]
    public void Clean_EmptyInput_ReturnsEmpty()
        => LogSanitizer.Clean("").Should().Be("");

    [Test]
    public void Clean_PlainAscii_Unchanged()
        => LogSanitizer.Clean("installation.created").Should().Be("installation.created");

    [Test]
    public void Clean_CR_EscapedToBackslashR()
        => LogSanitizer.Clean("a\rb").Should().Be("a\\rb");

    [Test]
    public void Clean_LF_EscapedToBackslashN()
        => LogSanitizer.Clean("a\nb").Should().Be("a\\nb");

    [Test]
    public void Clean_TAB_EscapedToBackslashT()
        => LogSanitizer.Clean("a\tb").Should().Be("a\\tb");

    [Test]
    public void Clean_ForgedLogInjection_Neutralised()
    {
        // Attempt to forge a second log entry by embedding CRLF.
        var attack = "push\r\n[FATAL] logged by attacker";
        LogSanitizer.Clean(attack)
            .Should().NotContain("\r")
            .And.NotContain("\n")
            .And.Contain("\\r\\n");
    }

    [Test]
    public void Clean_OtherControlChars_ReplacedWithQuestionMark()
    {
        LogSanitizer.Clean("a\u0001b\u0007c\u007Fd").Should().Be("a?b?c?d");
    }

    [Test]
    public void Clean_Truncates_OverLimitInputs()
    {
        var longInput = new string('x', 300);
        var cleaned = LogSanitizer.Clean(longInput);
        cleaned.Should().HaveLength(200 + "…[truncated]".Length);
        cleaned.Should().EndWith("…[truncated]");
    }

    [Test]
    public void Clean_UnicodeLettersPreserved()
        => LogSanitizer.Clean("café ☕ 日本語").Should().Be("café ☕ 日本語");
}
