using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;
using TypedDiagnosis = Tamma.Core.Documents.Types.Diagnosis;

namespace Tamma.Activities.Tests.Documents.Types;

/// <summary>
/// Story 39-4 AC7/AC8 — the legacy-bridge round-trip for <see cref="Diagnosis"/>
/// (Design Decision D8). Story 39-15 retired <c>AIDiagnosisActivity.ParseDiagnosisResponse</c>
/// (diagnosis production moved onto the debug-diagnosis lifecycle binding); the snake_case
/// reader it embodied is now the typed <see cref="Diagnosis.FromLegacyJson"/>. This test is
/// ported to that reader: a typed diagnosis serialized through the snake_case bridge
/// (<see cref="Diagnosis.ToLegacyJson"/>) is recovered with matching hypotheses — a plain
/// camelCase re-serialization would have blanked the snake_case-only fields (why the bridge
/// exists, D4).
/// </summary>
[TestFixture]
public class DiagnosisCrossParserTests
{
    [Test]
    public void Typed_diagnosis_round_trips_through_the_legacy_bridge_into_the_snakecase_reader()
    {
        var typed = new TypedDiagnosis
        {
            AnalysisSummary = "Null ref in resolver",
            Hypotheses = new[]
            {
                new DiagnosisHypothesis { Rank = 1, Description = "Resolver returns null", Confidence = 0.85m, SuggestedFix = "Guard the miss", AffectedFiles = new[] { "src/Resolver.cs" } },
                new DiagnosisHypothesis { Rank = 2, Description = "Warm-up race", Confidence = 0.4m },
            },
        };

        var legacy = typed.ToLegacyJson();

        var result = TypedDiagnosis.FromLegacyJson(legacy);

        result.AnalysisSummary.Should().Be("Null ref in resolver");
        result.Hypotheses.Should().HaveCount(2);
        result.Hypotheses[0].SuggestedFix.Should().Be("Guard the miss");
        result.Hypotheses[0].AffectedFiles.Should().Contain("src/Resolver.cs");
        result.Hypotheses[0].Confidence.Should().Be(0.85m);
    }

    [Test]
    public void Camelcase_serialization_would_NOT_survive_the_snakecase_reader_which_is_why_the_bridge_exists()
    {
        var typed = new TypedDiagnosis
        {
            AnalysisSummary = "Null ref",
            Hypotheses = new[] { new DiagnosisHypothesis { Rank = 1, Description = "a", Confidence = 0.9m, SuggestedFix = "fix", AffectedFiles = new[] { "a.cs" } } },
        };

        var camel = JsonSerializer.Serialize(typed, DocumentJson.Options);

        // The snake_case reader cannot see camelCase suggestedFix / affectedFiles:
        // it recovers a hypothesis but with those fields blanked — exactly the empty,
        // gate-failing read D4 warns about. The paired ToLegacyJson writer is the fix.
        var camelResult = TypedDiagnosis.FromLegacyJson(camel);
        camelResult.Hypotheses.Should().ContainSingle();
        camelResult.Hypotheses[0].SuggestedFix.Should().BeEmpty("camelCase suggestedFix is invisible to the snake_case reader");

        var bridged = TypedDiagnosis.FromLegacyJson(typed.ToLegacyJson());
        bridged.Hypotheses[0].SuggestedFix.Should().Be("fix");
    }
}
