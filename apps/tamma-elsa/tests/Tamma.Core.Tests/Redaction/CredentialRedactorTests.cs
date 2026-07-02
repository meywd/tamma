using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Redaction;

namespace Tamma.Core.Tests.Redaction;

/// <summary>
/// Wave C.4 — unit tests for <see cref="CredentialRedactor"/>. The alert
/// pipeline emits events whose <c>data.lastError</c> / <c>data.finalError</c>
/// fields are user-facing exception messages. Exception messages
/// occasionally contain plaintext Bearer tokens, passwords, or API keys
/// (especially from HTTP client libraries that dump the failing request).
/// The redactor MUST scrub these before they land on disk so the event
/// store never becomes a credential leak vector.
/// </summary>
[TestFixture]
public class CredentialRedactorTests
{
    [Test]
    public void Clean_Null_ReturnsEmpty()
    {
        CredentialRedactor.Clean(null).Should().Be(string.Empty);
    }

    [Test]
    public void Clean_Empty_ReturnsEmpty()
    {
        CredentialRedactor.Clean(string.Empty).Should().Be(string.Empty);
    }

    [Test]
    public void Clean_PlainText_ReturnsVerbatim()
    {
        CredentialRedactor.Clean("connection refused")
            .Should().Be("connection refused");
    }

    [Test]
    public void Clean_BearerTokenInHeader_RedactsValue()
    {
        // Using a synthetic-looking token (not matching any well-known
        // provider prefix) to avoid tripping secret-scanning false
        // positives in CI.
        const string fakeToken = "FAKE-TEST-TOKEN-abcdef1234567890ABCDEFxyz";
        var input = $"Authorization: Bearer {fakeToken} failed";
        var output = CredentialRedactor.Clean(input);
        output.Should().NotContain(fakeToken);
        output.Should().Contain("Bearer [REDACTED]");
    }

    [Test]
    public void Clean_BearerTokenLowercase_RedactsValue()
    {
        var input = "got bearer abc123def456ghi789jkl012mno345pqr from api";
        var output = CredentialRedactor.Clean(input);
        output.Should().NotContain("abc123def456ghi789jkl012mno345pqr");
        output.Should().Contain("bearer [REDACTED]");
    }

    [Test]
    public void Clean_BasicAuthInUrl_RedactsUserInfo()
    {
        // Per RFC 3986, '@' in userinfo must be percent-encoded.
        // Real-world Npgsql / HttpClient error messages emit the
        // encoded form.
        var input = "failed to connect: https://admin:passw0rd%40@db.example.com:5432/tamma";
        var output = CredentialRedactor.Clean(input);
        output.Should().NotContain("passw0rd%40");
        output.Should().Contain("[REDACTED]@db.example.com");
    }

    [Test]
    public void Clean_PostgresConnectionString_RedactsPassword()
    {
        var input = "Npgsql: Host=db;Port=5432;Database=tamma;Username=app;Password=hunter2supersecret;";
        var output = CredentialRedactor.Clean(input);
        output.Should().NotContain("hunter2supersecret");
        output.Should().Contain("Password=[REDACTED]");
    }

    [Test]
    public void Clean_ApiKeyAssignment_RedactsValue()
    {
        var input = "Request failed with api_key=sk-very-secret-key-dont-leak";
        var output = CredentialRedactor.Clean(input);
        output.Should().NotContain("sk-very-secret-key-dont-leak");
        output.Should().Contain("[REDACTED]");
    }

    [Test]
    public void Clean_TammaApiKeyPrefix_Redacts()
    {
        var input = "Auth failed: key=tamma_sk_1234567890abcdefghijklmnopqrstuv reason=expired";
        var output = CredentialRedactor.Clean(input);
        output.Should().NotContain("tamma_sk_1234567890abcdefghijklmnopqrstuv");
        output.Should().Contain("[REDACTED]");
    }

    [Test]
    public void Clean_XApiKeyHeader_Redacts()
    {
        var input = "X-Api-Key: abcdef1234567890 was rejected";
        var output = CredentialRedactor.Clean(input);
        output.Should().NotContain("abcdef1234567890");
        output.Should().Contain("[REDACTED]");
    }

    [Test]
    public void Clean_JsonPasswordField_Redacts()
    {
        var input = """{"user":"admin","password":"super-secret-pw","status":"failed"}""";
        var output = CredentialRedactor.Clean(input);
        output.Should().NotContain("super-secret-pw");
        output.Should().Contain("\"password\":\"[REDACTED]\"");
    }

    [Test]
    public void Clean_TruncatesVeryLongStrings()
    {
        var input = new string('x', 2000);
        var output = CredentialRedactor.Clean(input);
        // Max length is 1024 for event-data fields (large enough for a
        // truncated stacktrace snippet, small enough to keep payloads
        // well under the 16KB JSON limit).
        output.Length.Should().BeLessThanOrEqualTo(1024);
    }

    [Test]
    public void Clean_MultipleSecretsInOneString_RedactsAll()
    {
        var input =
            "Bearer sk_one_1234567890abcdef1234 failed, retried with " +
            "Password=hunter2deadbeef and got 401";
        var output = CredentialRedactor.Clean(input);
        output.Should().NotContain("sk_one_1234567890abcdef1234");
        output.Should().NotContain("hunter2deadbeef");
    }

    [Test]
    public void Clean_ControlCharacters_Stripped()
    {
        // Control characters can be used for log injection — strip them.
        var input = "error: \r\nFAKE LOG ENTRY\t more";
        var output = CredentialRedactor.Clean(input);
        output.Should().NotContain("\r");
        output.Should().NotContain("\n");
    }

    [Test]
    public void Clean_ShortPlainMessage_Preserved()
    {
        // Don't over-aggressively redact things that aren't secrets.
        // Short exception messages must be recognisable.
        var input = "handler_not_registered";
        var output = CredentialRedactor.Clean(input);
        output.Should().Be("handler_not_registered");
    }

    [Test]
    public void Clean_HttpStatus_Preserved()
    {
        // Status codes and generic exception names are not secrets.
        var input = "HttpRequestException: The operation was canceled.";
        var output = CredentialRedactor.Clean(input);
        output.Should().Contain("HttpRequestException");
        output.Should().Contain("canceled");
    }

    // ── Finding 3 (Story 37-10 review) — provider-key prefix backstop ──
    // The backstop must match its "belt-and-suspenders" doc: common provider-key
    // shapes (Anthropic sk-ant-, OpenAI project sk-proj-) must be redacted even
    // when the surrounding key name doesn't match the credential heuristic.

    [Test]
    public void Clean_AnthropicKeyPrefix_Redacts()
    {
        // Synthetic value (clearly fake) with the real sk-ant- banner shape.
        const string fakeKey = "sk-ant-api03-FAKE0123456789abcdefFAKE";
        var input = $"anthropic call failed using {fakeKey} at boundary";
        var output = CredentialRedactor.Clean(input);
        output.Should().NotContain(fakeKey);
        output.Should().Contain("[REDACTED]");
    }

    [Test]
    public void Clean_OpenAiProjectKeyPrefix_Redacts()
    {
        const string fakeKey = "sk-proj-FAKE0123456789abcdefFAKE";
        var input = $"openai auth error token={fakeKey} rejected";
        var output = CredentialRedactor.Clean(input);
        output.Should().NotContain(fakeKey);
        output.Should().Contain("[REDACTED]");
    }
}
