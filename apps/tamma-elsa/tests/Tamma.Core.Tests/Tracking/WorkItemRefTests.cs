using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Tracking;

namespace Tamma.Core.Tests.Tracking;

/// <summary>
/// Story 44-0 AC7 — the frozen human key. Strict, ordinal, non-normalizing:
/// a bad key is rejected, never coerced, because <c>ToWire()</c> is the string
/// written into <c>DocumentInstance.IssueId</c> and DCB <c>tags.issueId</c>.
/// </summary>
[TestFixture]
public class WorkItemRefTests
{
    [Test]
    public void Roundtrip_holds()
    {
        var reference = new WorkItemRef("TAM", 142);
        reference.ToWire().Should().Be("TAM-142");
        reference.ToString().Should().Be("TAM-142");

        WorkItemRef.TryParse("TAM-142", out var parsed).Should().BeTrue();
        parsed.Should().Be(reference);
        parsed.ProjectKey.Should().Be("TAM");
        parsed.Number.Should().Be(142);

        WorkItemRef.Parse("TAM-1").Should().Be(new WorkItemRef("TAM", 1));

        // Digits are allowed after the leading letter; minimum key length is 2.
        WorkItemRef.Parse("A1-9").Should().Be(new WorkItemRef("A1", 9));
        WorkItemRef.Parse("PROJECT10-77").ToWire().Should().Be("PROJECT10-77");
    }

    [Test]
    public void Value_equality_holds()
    {
        new WorkItemRef("TAM", 7).Should().Be(new WorkItemRef("TAM", 7));
        new WorkItemRef("TAM", 7).Should().NotBe(new WorkItemRef("TAM", 8));
        new WorkItemRef("TAM", 7).Should().NotBe(new WorkItemRef("TAMMA", 7));
    }

    [TestCase("tam-1", Description = "lower-case key — rejected, never upper-cased")]
    [TestCase("Tam-1", Description = "mixed-case key")]
    [TestCase("T-1", Description = "key too short (min 2)")]
    [TestCase("TOOLONGKEYX-1", Description = "key too long (max 10)")]
    [TestCase("1AM-1", Description = "key must start with a letter")]
    [TestCase("TAM-0", Description = "numbers start at 1")]
    [TestCase("TAM--1", Description = "negative number")]
    [TestCase("TAM-01", Description = "leading zero would re-serialize as TAM-1 — a coercion")]
    [TestCase("TAM-1x", Description = "trailing garbage")]
    [TestCase("TAM-1-2", Description = "second separator")]
    [TestCase("TAM-", Description = "missing number")]
    [TestCase("TAM", Description = "missing separator")]
    [TestCase("-1", Description = "missing key")]
    [TestCase("TAM_1", Description = "wrong separator")]
    [TestCase(" TAM-1", Description = "leading whitespace")]
    [TestCase("TAM-1 ", Description = "trailing whitespace")]
    [TestCase("TAM-9999999999", Description = "number overflow")]
    [TestCase("", Description = "empty")]
    [TestCase(null, Description = "null")]
    public void Malformed_keys_are_rejected(string? input)
    {
        WorkItemRef.TryParse(input, out _).Should().BeFalse(because: $"'{input}' is not a canonical key");

        var act = () => WorkItemRef.Parse(input!);
        act.Should().Throw<TammaError>().Which.Code.Should().Be("TRACKER.INVALID_WORK_ITEM_KEY");
    }

    [Test]
    public void Constructor_rejects_invalid_components()
    {
        var badKey = () => new WorkItemRef("tam", 1);
        badKey.Should().Throw<TammaError>().Which.Code.Should().Be("TRACKER.INVALID_WORK_ITEM_KEY");

        var badNumber = () => new WorkItemRef("TAM", 0);
        badNumber.Should().Throw<TammaError>().Which.Code.Should().Be("TRACKER.INVALID_WORK_ITEM_KEY");

        var negative = () => new WorkItemRef("TAM", -5);
        negative.Should().Throw<TammaError>().Which.Code.Should().Be("TRACKER.INVALID_WORK_ITEM_KEY");
    }

    [Test]
    public void IsValidProjectKey_matches_the_documented_pattern()
    {
        // ^[A-Z][A-Z0-9]{1,9}$ — 2-10 chars, upper-case, starts with a letter.
        WorkItemRef.IsValidProjectKey("TA").Should().BeTrue();
        WorkItemRef.IsValidProjectKey("TAM").Should().BeTrue();
        WorkItemRef.IsValidProjectKey("A1").Should().BeTrue();
        WorkItemRef.IsValidProjectKey("ABCDEFGHIJ").Should().BeTrue("10 chars is the maximum");

        WorkItemRef.IsValidProjectKey("A").Should().BeFalse("1 char is below the minimum");
        WorkItemRef.IsValidProjectKey("ABCDEFGHIJK").Should().BeFalse("11 chars exceeds the maximum");
        WorkItemRef.IsValidProjectKey("tam").Should().BeFalse("lower-case is rejected, not coerced");
        WorkItemRef.IsValidProjectKey("1AM").Should().BeFalse("must start with a letter");
        WorkItemRef.IsValidProjectKey("TA M").Should().BeFalse();
        WorkItemRef.IsValidProjectKey("TA-M").Should().BeFalse();
        WorkItemRef.IsValidProjectKey("TÄM").Should().BeFalse("ASCII only");
        WorkItemRef.IsValidProjectKey("").Should().BeFalse();
        WorkItemRef.IsValidProjectKey(null).Should().BeFalse();
    }
}
