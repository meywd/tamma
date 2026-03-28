using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Security;

namespace Tamma.Activities.Tests.LlmCall;

[TestFixture]
public class OutputSanitizationTests
{
    private ContentSanitizer _sanitizer = null!;

    [SetUp]
    public void SetUp()
    {
        _sanitizer = new ContentSanitizer();
    }

    [Test]
    public void SanitizeOutput_StripsHtmlFromResponseText()
    {
        var result = _sanitizer.SanitizeOutput("Here is the answer: <script>alert('xss')</script>end");
        result.Result.Should().Be("Here is the answer: alert('xss')end");
    }

    [Test]
    public void SanitizeOutput_PreservesCodeBlocks()
    {
        var input = "Example:\n```html\n<div>test</div>\n```\nDone.";
        var result = _sanitizer.SanitizeOutput(input);
        result.Result.Should().Contain("<div>test</div>");
    }

    [Test]
    public void SanitizeOutput_RemovesZeroWidthChars()
    {
        var input = "Hello\u200BWorld";
        var result = _sanitizer.SanitizeOutput(input);
        result.Result.Should().Be("HelloWorld");
    }

    [Test]
    public void SanitizeOutput_EmptyResponse_HandledGracefully()
    {
        var result = _sanitizer.SanitizeOutput("");
        result.Result.Should().BeEmpty();
    }

    [Test]
    public void SanitizeOutput_RemovesNullBytes()
    {
        var result = _sanitizer.SanitizeOutput("response\0text");
        result.Result.Should().Be("responsetext");
    }

    [Test]
    public void SanitizeOutput_PreservesMultipleCodeBlocks()
    {
        var input = "Text ```<b>code1</b>``` middle ```<i>code2</i>``` end";
        var result = _sanitizer.SanitizeOutput(input);
        result.Result.Should().Contain("<b>code1</b>");
        result.Result.Should().Contain("<i>code2</i>");
    }

    [Test]
    public void SanitizeOutput_StripsHtmlOutsideCodeBlocks()
    {
        var input = "<b>bold</b> text ```<b>code</b>``` <i>italic</i>";
        var result = _sanitizer.SanitizeOutput(input);
        result.Result.Should().Contain("bold text");
        result.Result.Should().Contain("<b>code</b>");
        result.Result.Should().Contain("italic");
        result.Result.Should().NotContain("<i>");
    }
}
