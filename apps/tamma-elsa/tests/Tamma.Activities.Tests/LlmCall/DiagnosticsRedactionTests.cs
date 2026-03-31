using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Security;

namespace Tamma.Activities.Tests.LlmCall;

[TestFixture]
public class DiagnosticsRedactionTests
{
    private ErrorRedactor _redactor = null!;

    [SetUp]
    public void SetUp()
    {
        _redactor = new ErrorRedactor();
    }

    [Test]
    public void Redact_ApiKeyInErrorMessage_Redacted()
    {
        var error = "Anthropic API error 401: Invalid API key sk-ant-api03-abc123def456";
        var result = _redactor.Redact(error);
        result.Should().NotContain("sk-ant-api03-abc123def456");
        result.Should().Contain("[REDACTED]");
    }

    [Test]
    public void Redact_BearerTokenInError_Redacted()
    {
        var error = "Authorization failed: Bearer eyJhbGciOiJIUzI1NiJ9.test";
        var result = _redactor.Redact(error);
        result.Should().NotContain("eyJhbGciOiJIUzI1NiJ9");
        result.Should().Contain("[REDACTED]");
    }

    [Test]
    public void Redact_InternalUrlInError_Redacted()
    {
        var error = "Connection refused: http://192.168.1.100:5000/api/v1/health";
        var result = _redactor.Redact(error);
        result.Should().NotContain("192.168.1.100");
        result.Should().Contain("[REDACTED]");
    }

    [Test]
    public void Redact_NormalErrorMessage_Preserved()
    {
        var error = "Request timed out after 120s";
        var result = _redactor.Redact(error);
        result.Should().Be(error);
    }

    [Test]
    public void Redact_OpenAiKeyInError_Redacted()
    {
        var error = "OpenAI API error: Invalid key sk-abcdefghijklmnopqrstuvwxyz1234567890";
        var result = _redactor.Redact(error);
        result.Should().NotContain("sk-abcdefghijklmnopqrstuvwxyz");
        result.Should().Contain("[REDACTED]");
    }

    [Test]
    public void Redact_StackTraceInError_Redacted()
    {
        var error = @"System.HttpRequestException: Connection refused
   at System.Net.Http.HttpClient.SendAsync() in /src/Http.cs:line 42
   at Tamma.Activities.LlmCall.CallLlmActivity.ExecuteAsync() in /app/src/CallLlm.cs:line 100";
        var result = _redactor.Redact(error);
        result.Should().Contain("[STACK TRACE REDACTED]");
        result.Should().NotContain("CallLlmActivity.ExecuteAsync");
    }

    [Test]
    public void Redact_MixedSensitiveContent_AllRedacted()
    {
        var error = "Error 401: Bearer secret.token.here failed at http://localhost:3000/api";
        var result = _redactor.Redact(error);
        result.Should().NotContain("secret.token.here");
        result.Should().NotContain("localhost:3000");
        result.Should().Contain("[REDACTED]");
    }

    [Test]
    public void Redact_EmptyErrorMessage_ReturnsEmpty()
    {
        var result = _redactor.Redact("");
        result.Should().BeEmpty();
    }
}
