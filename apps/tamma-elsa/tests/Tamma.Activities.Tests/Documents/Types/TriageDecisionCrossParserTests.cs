using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.ElsaServer.Workflows.Helpers;
using TypedTriage = Tamma.Core.Documents.Types.TriageDecision;

namespace Tamma.Activities.Tests.Documents.Types;

/// <summary>
/// Story 39-4 AC6/AC8 — the round-trip half for the triage decision (Design Decision
/// D8). Invokes the OLD <c>TriagePoDecisionHelper.ParseDecision</c> baseline: a valid
/// typed <see cref="TypedTriage"/> re-serializes into JSON the helper parses as a clean
/// StatusOk decision with ZERO clamp notes and identical field values. Also pins that
/// the helper's honest-failure markers (<c>unparsed</c>) stay LIFECYCLE outcomes — they
/// have no representation in the typed vocabulary.
/// </summary>
[TestFixture]
public class TriageDecisionCrossParserTests
{
    [Test]
    public void Valid_typed_decision_round_trips_to_a_clean_StatusOk_with_no_clamp_notes()
    {
        var typed = new TypedTriage
        {
            Priority = "normal",
            Type = "feature",
            Complexity = "medium",
            Automation = "needs-human",
            Reasoning = "Well-scoped enhancement.",
            Labels = new[] { "feature" },
        };

        var json = JsonSerializer.Serialize(typed, DocumentJson.Options);
        var decision = TriagePoDecisionHelper.ParseDecision(json);

        decision.Status.Should().Be(TriagePoDecisionHelper.StatusOk);
        decision.Priority.Should().Be("normal");
        decision.Type.Should().Be("feature");
        decision.Complexity.Should().Be("medium");
        decision.Automation.Should().Be("needs-human");
        decision.Reasoning.Should().Be("Well-scoped enhancement.");
        decision.Labels.Should().Contain("feature");
        decision.Comment.Should().NotContain("invalid", "canonical wires must not trip any vocab-clamp note");
    }

    [Test]
    public void Helper_unparsed_marker_is_a_lifecycle_outcome_not_a_typed_decision()
    {
        // Prose the model returns is honestly marked "unparsed" by the baseline (needs-human),
        // NOT laundered into a clean classified decision. The typed vocabulary has no such
        // member — that distinction lives in the lifecycle (ValidationExhausted), per AC6.
        var decision = TriagePoDecisionHelper.ParseDecision("I think this is probably a bug, maybe P1?");
        decision.Status.Should().Be(TriagePoDecisionHelper.StatusUnparsed);

        Enum.GetNames<Tamma.Core.Documents.Types.TriagePriority>()
            .Should().NotContain(new[] { "Unparsed", "LlmFailed", "Skipped" });
    }
}
