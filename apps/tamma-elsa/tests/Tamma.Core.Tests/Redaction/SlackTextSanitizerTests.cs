using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Redaction;

namespace Tamma.Core.Tests.Redaction;

/// <summary>
/// Unit tests for <see cref="SlackTextSanitizer"/> — the single shared helper that
/// neutralizes Slack broadcast / mention / channel control tokens in untrusted bodies
/// (issue titles, LLM output) before they are posted. The invariant: every
/// <c>&lt;</c>…<c>&gt;</c>-delimited control token is rendered literal, while ordinary
/// text and URLs are left intact.
/// </summary>
[TestFixture]
public class SlackTextSanitizerTests
{
    [Test]
    public void Escape_Null_ReturnsEmpty()
    {
        SlackTextSanitizer.Escape(null).Should().Be(string.Empty);
    }

    [Test]
    public void Escape_Empty_ReturnsEmpty()
    {
        SlackTextSanitizer.Escape(string.Empty).Should().Be(string.Empty);
    }

    [Test]
    public void Escape_PlainText_ReturnsVerbatim()
    {
        SlackTextSanitizer.Escape("deploy finished successfully")
            .Should().Be("deploy finished successfully");
    }

    [Test]
    public void Escape_Url_LeftIntact()
    {
        // A plain URL carries no & < > so it must survive unchanged (no mangling).
        SlackTextSanitizer.Escape("see https://tamma.dev/pr/123 for details")
            .Should().Be("see https://tamma.dev/pr/123 for details");
    }

    [Test]
    public void Escape_ChannelBroadcast_Neutralized()
    {
        var escaped = SlackTextSanitizer.Escape("<!channel> ship it");

        escaped.Should().NotContain("<!channel>", "the @channel broadcast must not stay live");
        escaped.Should().Be("&lt;!channel&gt; ship it");
    }

    [Test]
    public void Escape_HereAndEveryone_Neutralized()
    {
        SlackTextSanitizer.Escape("<!here>").Should().Be("&lt;!here&gt;");
        SlackTextSanitizer.Escape("<!everyone>").Should().Be("&lt;!everyone&gt;");
    }

    [Test]
    public void Escape_UserMention_Neutralized()
    {
        var escaped = SlackTextSanitizer.Escape("ping <@U123> now");

        escaped.Should().NotContain("<@U123>");
        escaped.Should().Be("ping &lt;@U123&gt; now");
    }

    [Test]
    public void Escape_Subteam_Neutralized()
    {
        var escaped = SlackTextSanitizer.Escape("<!subteam^S1>");

        escaped.Should().NotContain("<!subteam^S1>");
        escaped.Should().Be("&lt;!subteam^S1&gt;");
    }

    [Test]
    public void Escape_ChannelLink_Neutralized()
    {
        SlackTextSanitizer.Escape("<#C123|general>").Should().Be("&lt;#C123|general&gt;");
    }

    [Test]
    public void Escape_Ampersand_EscapedFirst_NoDoubleEscaping()
    {
        // & is escaped first so the &lt; / &gt; it introduces are not re-escaped.
        SlackTextSanitizer.Escape("a & b").Should().Be("a &amp; b");
        SlackTextSanitizer.Escape("<x>").Should().Be("&lt;x&gt;")
            .And.NotContain("&amp;lt;", "a single Escape pass must not double-escape");
    }
}
