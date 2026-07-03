using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Blocker;

namespace Tamma.Activities.Tests.Blocker;

/// <summary>
/// Hardening 2026-07-03 — the blocker-diagnosis resume callbacks must read their control-flow
/// boolean (<c>ProgressDetected</c> / <c>Resolved</c>) tolerant of a SERIALIZING workflow
/// runtime. The in-process runtime keeps the resumed value a boxed <see cref="bool"/>, but a
/// distributed / MassTransit / ProtoActor dispatcher round-trips it to a <see cref="string"/>
/// or a <see cref="JsonElement"/>. The prior <c>value is true</c> pattern only matched the
/// boxed-bool path — under serialization it silently evaluated <c>false</c>, returning HTTP 200
/// while advancing the WRONG branch (progress → not-resolved; escalation → not-resolved),
/// reintroducing the exact "never reaches Resolved" bug this branch fixes, masked by success.
///
/// <para>These tests drive the resume-input read path (<see cref="DetectProgressActivity.ReadProgressResult"/>,
/// <see cref="EscalateToSeniorActivity.ReadSeniorOutcome"/>, and the shared
/// <see cref="BlockerResumeInput.AsBool"/> coercion) with the flag supplied as (a) a boxed
/// <c>bool</c>, (b) the string <c>"true"</c>/<c>"false"</c>, and (c) a <see cref="JsonElement"/>,
/// plus a missing key. The string + JsonElement cases fail before the read-back fix and pass
/// after; the boxed-bool path is unchanged.</para>
/// </summary>
[TestFixture]
public class BlockerResumeReadBackTests
{
    private static JsonElement JsonBool(bool value) => JsonDocument.Parse(value ? "true" : "false").RootElement;

    // ---------------------------------------------------------------------
    // Shared coercion — BlockerResumeInput.AsBool
    // ---------------------------------------------------------------------

    [Test]
    public void AsBool_BoxedBool_ReadsThrough()
    {
        BlockerResumeInput.AsBool(true).Should().BeTrue();
        BlockerResumeInput.AsBool(false).Should().BeFalse();
    }

    [Test]
    public void AsBool_String_IsTolerant()
    {
        BlockerResumeInput.AsBool("true").Should().BeTrue();
        BlockerResumeInput.AsBool("True").Should().BeTrue();
        BlockerResumeInput.AsBool("TRUE").Should().BeTrue();
        BlockerResumeInput.AsBool("false").Should().BeFalse();
        BlockerResumeInput.AsBool("nonsense").Should().BeFalse();
    }

    [Test]
    public void AsBool_JsonElement_IsTolerant()
    {
        BlockerResumeInput.AsBool(JsonBool(true)).Should().BeTrue();
        BlockerResumeInput.AsBool(JsonBool(false)).Should().BeFalse();
    }

    [Test]
    public void AsBool_NullOrMissing_IsFalse()
    {
        BlockerResumeInput.AsBool(null).Should().BeFalse();
    }

    // ---------------------------------------------------------------------
    // DetectProgressActivity.ReadProgressResult — ProgressDetected coercion
    // ---------------------------------------------------------------------

    [Test]
    public void ReadProgressResult_BoxedBoolTrue_ReachesDetected()
    {
        var input = Input(("ProgressDetected", true), ("ProgressType", "Commit"), ("Details", "new commit"));
        var result = DetectProgressActivity.ReadProgressResult(input);
        result.ProgressDetected.Should().BeTrue();
        result.ProgressType.Should().Be("Commit");
        result.Details.Should().Be("new commit");
    }

    [Test]
    public void ReadProgressResult_StringTrue_ReachesDetected()
    {
        var input = Input(("ProgressDetected", "true"));
        DetectProgressActivity.ReadProgressResult(input).ProgressDetected.Should().BeTrue();
    }

    [Test]
    public void ReadProgressResult_JsonElementTrue_ReachesDetected()
    {
        var input = Input(("ProgressDetected", JsonBool(true)));
        DetectProgressActivity.ReadProgressResult(input).ProgressDetected.Should().BeTrue();
    }

    [Test]
    public void ReadProgressResult_FalseRepresentations_ReachNotDetected()
    {
        DetectProgressActivity.ReadProgressResult(Input(("ProgressDetected", false))).ProgressDetected.Should().BeFalse();
        DetectProgressActivity.ReadProgressResult(Input(("ProgressDetected", "false"))).ProgressDetected.Should().BeFalse();
        DetectProgressActivity.ReadProgressResult(Input(("ProgressDetected", JsonBool(false)))).ProgressDetected.Should().BeFalse();
    }

    [Test]
    public void ReadProgressResult_MissingKey_ReachesNotDetected()
    {
        var input = Input(("ProgressType", "Commit"));
        DetectProgressActivity.ReadProgressResult(input).ProgressDetected.Should().BeFalse();
    }

    [Test]
    public void ReadProgressResult_JsonElementStringFields_ReadThrough()
    {
        // A serializing runtime also delivers the informational strings as JsonElement.
        var input = Input(
            ("ProgressDetected", JsonBool(true)),
            ("ProgressType", (object)JsonDocument.Parse("\"CIPassed\"").RootElement),
            ("Details", (object)JsonDocument.Parse("\"all green\"").RootElement));
        var result = DetectProgressActivity.ReadProgressResult(input);
        result.ProgressDetected.Should().BeTrue();
        result.ProgressType.Should().Be("CIPassed");
        result.Details.Should().Be("all green");
    }

    // ---------------------------------------------------------------------
    // EscalateToSeniorActivity.ReadSeniorOutcome — Resolved coercion
    // ---------------------------------------------------------------------

    [Test]
    public void ReadSeniorOutcome_BoxedBoolTrue_ReachesResolved()
    {
        var input = Input(("Resolved", true), ("SeniorResponse", "fixed it"));
        var (resolved, seniorResponse) = EscalateToSeniorActivity.ReadSeniorOutcome(input);
        resolved.Should().BeTrue();
        seniorResponse.Should().Be("fixed it");
    }

    [Test]
    public void ReadSeniorOutcome_StringTrue_ReachesResolved()
    {
        var (resolved, _) = EscalateToSeniorActivity.ReadSeniorOutcome(Input(("Resolved", "true")));
        resolved.Should().BeTrue();
    }

    [Test]
    public void ReadSeniorOutcome_JsonElementTrue_ReachesResolved()
    {
        var (resolved, _) = EscalateToSeniorActivity.ReadSeniorOutcome(Input(("Resolved", JsonBool(true))));
        resolved.Should().BeTrue();
    }

    [Test]
    public void ReadSeniorOutcome_FalseRepresentations_ReachNotResolved()
    {
        EscalateToSeniorActivity.ReadSeniorOutcome(Input(("Resolved", false))).Resolved.Should().BeFalse();
        EscalateToSeniorActivity.ReadSeniorOutcome(Input(("Resolved", "false"))).Resolved.Should().BeFalse();
        EscalateToSeniorActivity.ReadSeniorOutcome(Input(("Resolved", JsonBool(false)))).Resolved.Should().BeFalse();
    }

    [Test]
    public void ReadSeniorOutcome_MissingKey_ReachesNotResolved()
    {
        var (resolved, seniorResponse) = EscalateToSeniorActivity.ReadSeniorOutcome(Input(("SeniorResponse", "n/a")));
        resolved.Should().BeFalse();
        seniorResponse.Should().Be("n/a");
    }

    [Test]
    public void ReadSeniorOutcome_JsonElementSeniorResponse_ReadsThrough()
    {
        var input = Input(
            ("Resolved", JsonBool(true)),
            ("SeniorResponse", (object)JsonDocument.Parse("\"see thread\"").RootElement));
        var (resolved, seniorResponse) = EscalateToSeniorActivity.ReadSeniorOutcome(input);
        resolved.Should().BeTrue();
        seniorResponse.Should().Be("see thread");
    }

    // ---------------------------------------------------------------------

    private static IDictionary<string, object> Input(params (string Key, object Value)[] entries)
    {
        var dict = new Dictionary<string, object>();
        foreach (var (key, value) in entries)
            dict[key] = value;
        return dict;
    }
}
