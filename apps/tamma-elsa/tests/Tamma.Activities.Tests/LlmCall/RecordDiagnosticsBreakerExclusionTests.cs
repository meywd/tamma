using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Tests.LlmCall;

/// <summary>
/// Story 39-9 (AC5) — the load-bearing N-vs-N proof that content failures never trip
/// the provider circuit breaker. Drives the SAME decision the two diagnostic recorders
/// make — <see cref="DiagnosticFailureCodes.CountsAsProviderFailure"/> gating
/// <see cref="CheckCircuitBreakerActivity.RecordFailure"/> / <c>RecordSuccess</c> —
/// over the workflow-variable breaker dict. Pure (no Docker, no Elsa context): the
/// shared helper is the single source of the exclusion, so exercising it here is the
/// faithful proof both recorder call sites uphold.
/// </summary>
[TestFixture]
public class RecordDiagnosticsBreakerExclusionTests
{
    private const string Provider = "anthropic";

    /// <summary>The recorder's branch, verbatim: success resets; a real provider
    /// failure records; a content failure records NEITHER.</summary>
    private static Dictionary<string, CircuitBreakerState> Apply(
        Dictionary<string, CircuitBreakerState> states, ProviderAttemptDiagnostic diag)
    {
        if (diag.Succeeded)
            return CheckCircuitBreakerActivity.RecordSuccess(states, Provider);
        if (DiagnosticFailureCodes.CountsAsProviderFailure(diag))
            return CheckCircuitBreakerActivity.RecordFailure(states, Provider, failureThreshold: 5);
        return states; // content_validation ⇒ neither failure nor success
    }

    private static ProviderAttemptDiagnostic Fail(string? failureCode) => new()
    {
        ProviderName = Provider,
        Succeeded = false,
        HttpStatusCode = failureCode == DiagnosticFailureCodes.ContentValidation ? 422 : 503,
        FailureCode = failureCode,
    };

    [Test]
    public void FiveContentValidationFailures_LeaveBreakerClosed_ZeroConsecutive()
    {
        var states = new Dictionary<string, CircuitBreakerState>();

        for (var i = 0; i < 5; i++)
            states = Apply(states, Fail(DiagnosticFailureCodes.ContentValidation));

        // The breaker was never touched: no state row was even created.
        states.TryGetValue(Provider, out var state);
        (state?.Status ?? CircuitBreakerStatus.Closed).Should().Be(CircuitBreakerStatus.Closed);
        (state?.ConsecutiveFailures ?? 0).Should().Be(0,
            "a content_validation failure never increments the breaker's failure count");
    }

    [Test]
    public void FiveTransportFailures_OpenTheBreaker()
    {
        var states = new Dictionary<string, CircuitBreakerState>();

        for (var i = 0; i < 5; i++)
            states = Apply(states, Fail(DiagnosticFailureCodes.Transport));

        states[Provider].Status.Should().Be(CircuitBreakerStatus.Open,
            "5 transport failures at threshold 5 trip the breaker");
        states[Provider].ConsecutiveFailures.Should().Be(5);
    }

    [Test]
    public void ContentValidationFailure_DoesNotResetCounters_NotASuccess()
    {
        var states = new Dictionary<string, CircuitBreakerState>();

        // Two real transport failures accumulate.
        states = Apply(states, Fail(DiagnosticFailureCodes.Transport));
        states = Apply(states, Fail(DiagnosticFailureCodes.Transport));
        states[Provider].ConsecutiveFailures.Should().Be(2);

        // A content failure must NOT reset the count (it is not a success).
        states = Apply(states, Fail(DiagnosticFailureCodes.ContentValidation));
        states[Provider].ConsecutiveFailures.Should().Be(2,
            "a content_validation failure records nothing — it is neither a failure nor a success");
    }

    [Test]
    public void CountsAsProviderFailure_ExcludesOnlyContentValidation()
    {
        DiagnosticFailureCodes.CountsAsProviderFailure(Fail(DiagnosticFailureCodes.ContentValidation))
            .Should().BeFalse();
        DiagnosticFailureCodes.CountsAsProviderFailure(Fail(DiagnosticFailureCodes.Transport))
            .Should().BeTrue();
        DiagnosticFailureCodes.CountsAsProviderFailure(Fail(DiagnosticFailureCodes.RateLimit))
            .Should().BeTrue();
        DiagnosticFailureCodes.CountsAsProviderFailure(Fail(null))
            .Should().BeTrue("an unclassified failure is still a provider failure");
        DiagnosticFailureCodes.CountsAsProviderFailure(
            new ProviderAttemptDiagnostic { Succeeded = true })
            .Should().BeFalse("a success is never a provider failure");
    }
}
