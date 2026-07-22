using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Tests.LlmCall;

/// <summary>
/// Story 39-9 (AC4) — the additive <see cref="ProviderAttemptDiagnostic.FailureCode"/>
/// and its population by the thin client. Pure JSON + static-method coverage (no
/// Docker, no live provider). Proves: old serialized diagnostics (no
/// <c>failureCode</c>) deserialize cleanly to <c>null</c>; a round-trip preserves the
/// value; the <see cref="CallLlmInlineActivity.MapResponseToVariables"/> mapping table;
/// and <see cref="CallLlmInlineActivity.BuildTransportFailure"/> → <c>transport</c>.
/// </summary>
[TestFixture]
public class DiagnosticFailureCodeTests
{
    // -------------------------------------------------------------------
    // Additive tolerance: old JSON deserializes clean; round-trip preserves.
    // -------------------------------------------------------------------

    [Test]
    public void OldJson_WithoutFailureCode_DeserializesToNull()
    {
        // A diagnostic serialized before this field existed.
        const string oldJson = """
        {"ProviderName":"anthropic","Succeeded":false,"HttpStatusCode":503,"PromptTokens":10}
        """;

        var diag = JsonSerializer.Deserialize<ProviderAttemptDiagnostic>(oldJson);

        diag.Should().NotBeNull();
        diag!.FailureCode.Should().BeNull("older JSON has no failureCode ⇒ additive/null");
        diag.ProviderName.Should().Be("anthropic");
        diag.HttpStatusCode.Should().Be(503);
    }

    [Test]
    public void FailureCode_RoundTrips()
    {
        var original = new ProviderAttemptDiagnostic
        {
            ProviderName = "anthropic",
            Succeeded = false,
            FailureCode = DiagnosticFailureCodes.ContentValidation,
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<ProviderAttemptDiagnostic>(json);

        restored!.FailureCode.Should().Be(DiagnosticFailureCodes.ContentValidation);
    }

    // -------------------------------------------------------------------
    // MapResponseToVariables mapping table (D6).
    // -------------------------------------------------------------------

    [TestCase("CONTENT_VALIDATION_FAILED", 422, "content_validation")]
    [TestCase("BUDGET_EXCEEDED", 200, "budget")]
    [TestCase("PROVIDER_ERROR", 429, "rate_limit")]
    [TestCase("PROVIDER_ERROR", 503, "transport")]
    [TestCase("PROVIDER_ERROR", 0, "transport")]
    [TestCase("AGENT_UNRESOLVED", 422, null)]
    public void MapResponseToVariables_ClassifiesFailureCode(
        string wireFailureCode, int httpStatus, string? expected)
    {
        var response = new LlmCallApiResponse
        {
            Success = false,
            FailureCode = wireFailureCode,
            FailureReason = "boom",
            HttpStatusCode = httpStatus,
        };

        var mapped = CallLlmInlineActivity.MapResponseToVariables(
            response, "anthropic", model: "claude", attemptNumber: 1,
            durationMs: 5, startedAtUtc: DateTime.UtcNow);

        mapped.Diagnostic.FailureCode.Should().Be(expected);
    }

    [Test]
    public void MapResponseToVariables_Success_LeavesFailureCodeNull()
    {
        var response = new LlmCallApiResponse
        {
            Success = true,
            Text = "ok",
            HttpStatusCode = 200,
        };

        var mapped = CallLlmInlineActivity.MapResponseToVariables(
            response, "anthropic", model: "claude", attemptNumber: 1,
            durationMs: 5, startedAtUtc: DateTime.UtcNow);

        mapped.Diagnostic.FailureCode.Should().BeNull();
    }

    // -------------------------------------------------------------------
    // ClassifyFailureCode — the pure classifier used above (direct unit).
    // -------------------------------------------------------------------

    [TestCase("CONTENT_VALIDATION_FAILED", 422, "content_validation")]
    [TestCase("BUDGET_EXCEEDED", 0, "budget")]
    [TestCase(null, 429, "rate_limit")]
    [TestCase(null, 500, "transport")]
    [TestCase(null, 0, "transport")]
    [TestCase("SOMETHING_ELSE", 200, null)]
    public void ClassifyFailureCode_MapsPerVocabulary(string? wire, int http, string? expected)
    {
        CallLlmInlineActivity.ClassifyFailureCode(wire, http).Should().Be(expected);
    }

    // -------------------------------------------------------------------
    // BuildTransportFailure — a null-body/raw-5xx result is a TRANSPORT failure.
    // -------------------------------------------------------------------

    [Test]
    public void BuildTransportFailure_SetsTransportFailureCode()
    {
        var mapped = CallLlmInlineActivity.BuildTransportFailure(
            "anthropic", model: "claude", attemptNumber: 2, durationMs: 3, startedAtUtc: DateTime.UtcNow);

        mapped.Diagnostic.Succeeded.Should().BeFalse();
        mapped.Diagnostic.FailureCode.Should().Be(DiagnosticFailureCodes.Transport);
        mapped.Diagnostic.HttpStatusCode.Should().Be(0);
    }
}
