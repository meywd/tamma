using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities;

namespace Tamma.Activities.Tests;

/// <summary>
/// Hardening 2026-07-03 (follow-up to #15) — the promoted shared tolerant resume-bool
/// reader <see cref="ResumeInput.AsBool"/>. Every remaining boxed-bool resume/dispatch
/// read (WaitForCIResults, MergeApproval, Assessment, Tdd, ReviewFix, and the blocker
/// callbacks) now delegates here, so a SERIALIZING runtime that round-trips the flag to
/// a <see cref="string"/> or <see cref="JsonElement"/> no longer silently mis-branches
/// to <c>false</c>. Boxed-<c>bool</c> and string behaviour is unchanged; JsonElement is
/// newly tolerated.
/// </summary>
[TestFixture]
public class ResumeInputTests
{
    private static JsonElement JsonBool(bool value) => JsonDocument.Parse(value ? "true" : "false").RootElement;

    [Test]
    public void AsBool_BoxedBool_ReadsThrough()
    {
        ResumeInput.AsBool(true).Should().BeTrue();
        ResumeInput.AsBool(false).Should().BeFalse();
    }

    [Test]
    public void AsBool_String_IsTolerant()
    {
        ResumeInput.AsBool("true").Should().BeTrue();
        ResumeInput.AsBool("True").Should().BeTrue();
        ResumeInput.AsBool("TRUE").Should().BeTrue();
        ResumeInput.AsBool("false").Should().BeFalse();
        ResumeInput.AsBool("nonsense").Should().BeFalse();
    }

    [Test]
    public void AsBool_JsonElement_IsTolerant()
    {
        // The exact case the switch-expression footgun dropped to `false`.
        ResumeInput.AsBool(JsonBool(true)).Should().BeTrue();
        ResumeInput.AsBool(JsonDocument.Parse("true").RootElement).Should().BeTrue();
        ResumeInput.AsBool(JsonBool(false)).Should().BeFalse();
    }

    [Test]
    public void AsBool_NullOrMissing_IsFalse()
    {
        ResumeInput.AsBool(null).Should().BeFalse();
    }
}
