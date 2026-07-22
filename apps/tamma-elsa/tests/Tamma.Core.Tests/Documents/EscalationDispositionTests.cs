using FluentAssertions;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Core.Documents;

namespace Tamma.Core.Tests.Documents;

/// <summary>
/// Story 39-8 (AC3 — the escalation-disposition closed set is drift-tested). Pins the
/// <see cref="EscalationDisposition"/> wire round-trips + the exactly-3 count so an accidental
/// member add/remove trips the count pin, and that <c>Parse</c> throws a
/// <see cref="TammaError"/> on unknowns (the <c>AgentRoleTests</c> style).
///
/// <para><see cref="ApprovalChannel"/>'s own round-trip + 3-member count pin lives in 39-5's
/// suite (39-8 CONSUMES that type); 39-8 pins only that <c>ApprovalChannels.Derive</c> maps
/// onto its three members correctly (see <c>DocumentDecisionApiTests</c>).</para>
/// </summary>
[TestFixture]
public class EscalationDispositionTests
{
    [Test]
    public void Has_exactly_three_dispositions() =>
        Enum.GetValues<EscalationDisposition>().Length.Should().Be(3);

    [TestCase(EscalationDisposition.Resolved, "resolved")]
    [TestCase(EscalationDisposition.Overridden, "overridden")]
    [TestCase(EscalationDisposition.Abandoned, "abandoned")]
    public void ToWire_returns_canonical_string(EscalationDisposition disposition, string wire) =>
        disposition.ToWire().Should().Be(wire);

    [Test]
    public void Roundtrip_holds_for_every_disposition()
    {
        foreach (var d in Enum.GetValues<EscalationDisposition>())
            EscalationDispositionExtensions.Parse(d.ToWire()).Should().Be(d);
    }

    [Test]
    public void Parse_throws_TammaError_on_unknown()
    {
        var act = () => EscalationDispositionExtensions.Parse("dismissed");
        act.Should().Throw<TammaError>()
            .Which.Code.Should().Be("ESCALATION.DISPOSITION.UNKNOWN");
    }

    [Test]
    public void Parse_throws_on_null_or_empty()
    {
        ((Action)(() => EscalationDispositionExtensions.Parse(null!))).Should().Throw<TammaError>();
        ((Action)(() => EscalationDispositionExtensions.Parse(""))).Should().Throw<TammaError>();
        ((Action)(() => EscalationDispositionExtensions.Parse("   "))).Should().Throw<TammaError>();
    }

    [Test]
    public void Parse_is_case_sensitive()
    {
        // Wire strings are canonical lowercase; non-canonical casing is rejected.
        ((Action)(() => EscalationDispositionExtensions.Parse("Resolved"))).Should().Throw<TammaError>();
    }

    [Test]
    public void TryParse_returns_false_on_unknown_and_empty()
    {
        EscalationDispositionExtensions.TryParse("nope", out _).Should().BeFalse();
        EscalationDispositionExtensions.TryParse("", out _).Should().BeFalse();
        EscalationDispositionExtensions.TryParse(null, out _).Should().BeFalse();
        EscalationDispositionExtensions.TryParse("resolved", out var d).Should().BeTrue();
        d.Should().Be(EscalationDisposition.Resolved);
    }
}
