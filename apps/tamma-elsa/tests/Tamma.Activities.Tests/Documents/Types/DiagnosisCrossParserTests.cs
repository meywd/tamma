using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Debug;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;
using TypedDiagnosis = Tamma.Core.Documents.Types.Diagnosis;

namespace Tamma.Activities.Tests.Documents.Types;

/// <summary>
/// Story 39-4 AC7/AC8 — the legacy-bridge round-trip for <see cref="Diagnosis"/>
/// (Design Decision D8). Invokes the INTERNAL <c>AIDiagnosisActivity.ParseDiagnosisResponse</c>
/// (in-assembly via InternalsVisibleTo): a typed diagnosis serialized through the
/// snake_case bridge (<see cref="Diagnosis.ToLegacyJson"/>) is recovered by the old
/// parser with matching hypotheses — a plain camelCase re-serialization would have
/// "parsed" into an empty, gate-failing result (why the bridge exists, D4).
/// </summary>
[TestFixture]
public class DiagnosisCrossParserTests
{
    [Test]
    public void Typed_diagnosis_round_trips_through_the_legacy_bridge_into_the_old_parser()
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

        var result = new AIDiagnosisActivity().ParseDiagnosisResponse(legacy);

        result.FailureReason.Should().BeNullOrEmpty("a valid bridged payload is not a parse failure");
        result.AnalysisSummary.Should().Be("Null ref in resolver");
        result.Hypotheses.Should().HaveCount(2);
        result.Hypotheses[0].SuggestedFix.Should().Be("Guard the miss");
        result.Hypotheses[0].AffectedFiles.Should().Contain("src/Resolver.cs");
        result.Hypotheses[0].Confidence.Should().Be(0.85m);
    }

    [Test]
    public void Camelcase_serialization_would_NOT_survive_the_old_parser_which_is_why_the_bridge_exists()
    {
        var typed = new TypedDiagnosis
        {
            AnalysisSummary = "Null ref",
            Hypotheses = new[] { new DiagnosisHypothesis { Rank = 1, Description = "a", Confidence = 0.9m, SuggestedFix = "fix", AffectedFiles = new[] { "a.cs" } } },
        };

        var camel = JsonSerializer.Serialize(typed, DocumentJson.Options);

        // The old snake_case reader cannot see camelCase suggested_fix / affected_files:
        // it recovers a hypothesis but with the fields blanked — exactly the empty,
        // gate-failing read D4 warns about. The paired ToLegacyJson writer is the fix.
        var camelResult = new AIDiagnosisActivity().ParseDiagnosisResponse(camel);
        camelResult.Hypotheses.Should().ContainSingle();
        camelResult.Hypotheses[0].SuggestedFix.Should().BeEmpty("camelCase suggestedFix is invisible to the snake_case reader");

        var bridged = new AIDiagnosisActivity().ParseDiagnosisResponse(typed.ToLegacyJson());
        bridged.Hypotheses[0].SuggestedFix.Should().Be("fix");
    }
}
