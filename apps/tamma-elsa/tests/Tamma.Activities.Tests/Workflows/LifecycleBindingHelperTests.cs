using FluentAssertions;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-25 (step 1) — unit pins for <see cref="LifecycleBindingHelper.TryReadAssessmentScore"/>,
/// the TOTAL, fail-closed reader every threading binding funnels its fetched
/// <c>ambiguity-assessment</c> body through before conditionally adding the
/// <c>ambiguityScore</c> dispatch key. The semantics mirror the lifecycle's own private
/// <c>TryReadAmbiguityScore</c> (root <c>score</c> number; anything else ⇒ no score).
///
/// <para><b>The load-bearing distinction (AC2)</b>: <c>null</c> means "not measured" (the
/// dispatch key is OMITTED); <c>0.0</c> means "measured unambiguous" (a real payload value
/// that THREADS). Fabricating a zero for an absent assessment would be the exact lie the
/// story forbids.</para>
/// </summary>
[TestFixture]
public class LifecycleBindingHelperTests
{
    // ── null legs — every "no assessment" shape yields null, never 0.0 ──

    [Test]
    public void TryReadAssessmentScore_NotFound_IsNull_EvenWithAParseableBody()
        => LifecycleBindingHelper.TryReadAssessmentScore(false, "{\"score\":0.95}")
            .Should().BeNull("Found=false is the fetch's fail-closed wire — the body is a stale default, not a measurement");

    [Test]
    public void TryReadAssessmentScore_NullOrBlankJson_IsNull()
    {
        LifecycleBindingHelper.TryReadAssessmentScore(true, null).Should().BeNull();
        LifecycleBindingHelper.TryReadAssessmentScore(true, "").Should().BeNull();
        LifecycleBindingHelper.TryReadAssessmentScore(true, "   ").Should().BeNull();
    }

    [Test]
    public void TryReadAssessmentScore_MalformedJson_IsNull()
        => LifecycleBindingHelper.TryReadAssessmentScore(true, "{\"score\":")
            .Should().BeNull("an unreadable payload is a no-score, never a throw out of a dispatch lambda");

    [Test]
    public void TryReadAssessmentScore_NonObjectRoot_IsNull()
    {
        LifecycleBindingHelper.TryReadAssessmentScore(true, "[0.9]").Should().BeNull();
        LifecycleBindingHelper.TryReadAssessmentScore(true, "0.9").Should().BeNull();
        LifecycleBindingHelper.TryReadAssessmentScore(true, "\"0.9\"").Should().BeNull();
    }

    [Test]
    public void TryReadAssessmentScore_MissingOrNonNumericScore_IsNull()
    {
        LifecycleBindingHelper.TryReadAssessmentScore(true, "{}").Should().BeNull();
        LifecycleBindingHelper.TryReadAssessmentScore(true, "{\"confidence\":0.9}").Should().BeNull();
        LifecycleBindingHelper.TryReadAssessmentScore(true, "{\"score\":\"0.9\"}")
            .Should().BeNull("a string score is not a measurement — mirror TryReadAmbiguityScore, no coercion");
        LifecycleBindingHelper.TryReadAssessmentScore(true, "{\"score\":null}").Should().BeNull();
    }

    // ── value legs — a real measurement threads, INCLUDING a measured zero ──

    [Test]
    public void TryReadAssessmentScore_MeasuredZero_ThreadsZero_DistinctFromAbsent()
        => LifecycleBindingHelper.TryReadAssessmentScore(true, "{\"score\":0.0}")
            .Should().Be(0.0, "a MEASURED zero is 'assessed unambiguous' and threads; only an ABSENT assessment omits the key");

    [Test]
    public void TryReadAssessmentScore_HighScore_ThreadsTheDouble()
        => LifecycleBindingHelper.TryReadAssessmentScore(true, "{\"score\":0.95,\"ambiguityCount\":3}")
            .Should().Be(0.95);
}
