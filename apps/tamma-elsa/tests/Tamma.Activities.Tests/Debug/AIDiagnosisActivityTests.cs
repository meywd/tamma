using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Debug;

namespace Tamma.Activities.Tests.Debug;

/// <summary>
/// Regression tests guarding against re-introduction of the simulated diagnosis
/// path. The original SimulateDiagnosisResponse returned hard-coded fake
/// hypotheses ("Logic error in condition evaluation", confidence=0.75 etc.)
/// that leaked into the audit trail and the iterative debug loop, poisoning
/// downstream fix attempts. The activity defaulted to UseMock=true, meaning
/// any deployment that forgot to set Anthropic:UseMock=false silently emitted
/// fabricated LLM output. All diagnoses must now route through the real
/// engine callback or direct Anthropic API.
/// </summary>
[TestFixture]
public class AIDiagnosisActivityTests
{
    [Test]
    public void AIDiagnosisActivity_ShouldNotExposeAnySimulationMethod()
    {
        // Arrange
        var type = typeof(AIDiagnosisActivity);

        // Act
        var simulationMethods = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.Name.Contains("Simulate", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Assert
        simulationMethods.Should().BeEmpty(
            "AIDiagnosisActivity must not contain any simulated diagnosis path — "
            + "fake hypotheses corrupt the audit trail and poison the iterative debug loop");
    }

    [Test]
    public void AIDiagnosisActivity_ShouldNotReferenceUseMockConfig()
    {
        // Arrange
        var type = typeof(AIDiagnosisActivity);

        // Act
        var mockMethods = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.Name.Contains("Mock", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Assert
        mockMethods.Should().BeEmpty(
            "AIDiagnosisActivity must not expose any Mock-named method — production uses real LLM only");
    }

    // ================================================================
    // ParseDiagnosisResponse — no fabricated hypotheses on parse failure
    // (a fabricated rank-1/0.1 hypothesis flipped the DebuggingWorkflow gate
    // to DEBUG.DIAGNOSIS.SUCCESS on garbage output)
    // ================================================================

    private const string ValidDiagnosisJson = """
    {
      "analysis_summary": "Null ref in resolver",
      "hypotheses": [
        {
          "rank": 1,
          "description": "Resolver returns null on cache miss",
          "confidence": 0.85,
          "suggested_fix": "Guard the cache miss",
          "affected_files": ["src/resolver.ts"]
        }
      ]
    }
    """;

    [Test]
    public void ParseDiagnosisResponse_ValidJson_ReturnsHypotheses_NoFailureReason()
    {
        var result = new AIDiagnosisActivity().ParseDiagnosisResponse(ValidDiagnosisJson);

        result.FailureReason.Should().BeNullOrEmpty();
        result.AnalysisSummary.Should().Be("Null ref in resolver");
        result.Hypotheses.Should().HaveCount(1);
        result.Hypotheses[0].Description.Should().Be("Resolver returns null on cache miss");
        result.Hypotheses[0].Confidence.Should().Be(0.85m);
    }

    [Test]
    public void ParseDiagnosisResponse_Garbage_ReturnsFailedResult_WithNoFabricatedHypothesis()
    {
        var result = new AIDiagnosisActivity().ParseDiagnosisResponse("I could not analyze this, sorry.");

        result.Hypotheses.Should().BeEmpty(
            "a parse failure must NEVER fabricate a hypothesis — that becomes a false DEBUG.DIAGNOSIS.SUCCESS");
        result.FailureReason.Should().Be(DebugEvents.ReasonDiagnosisParseFailure);
        result.AnalysisSummary.Should().Contain("Failed to parse");
    }

    [Test]
    public void ParseDiagnosisResponse_MarkdownFencedJson_Parses()
    {
        var fenced = $"```json\n{ValidDiagnosisJson}\n```";

        var result = new AIDiagnosisActivity().ParseDiagnosisResponse(fenced);

        result.FailureReason.Should().BeNullOrEmpty();
        result.Hypotheses.Should().HaveCount(1);
        result.Hypotheses[0].SuggestedFix.Should().Be("Guard the cache miss");
    }

    // ================================================================
    // Caller-gate routing (DebuggingWorkflow diagnosisProduced FlowDecision +
    // emitDiagnosisFailed Reason use these shared predicates)
    // ================================================================

    [Test]
    public void IsDiagnosisProduced_ParseFailure_RoutesToFailedPath()
    {
        var failed = new AIDiagnosisActivity().ParseDiagnosisResponse("garbage");

        DebugEvents.IsDiagnosisProduced(failed).Should().BeFalse(
            "an unparseable diagnosis must take the DEBUG.DIAGNOSIS.FAILED path");
        DebugEvents.DiagnosisFailureReason(failed).Should().Be(DebugEvents.ReasonDiagnosisParseFailure,
            "the FAILED event data must carry the parse-failure reason");
    }

    [Test]
    public void IsDiagnosisProduced_ValidDiagnosis_RoutesToSuccessPath()
    {
        var ok = new AIDiagnosisActivity().ParseDiagnosisResponse(ValidDiagnosisJson);

        DebugEvents.IsDiagnosisProduced(ok).Should().BeTrue();
    }

    [Test]
    public void IsDiagnosisProduced_GenuinelyEmptyHypotheses_KeepsLegacyNoHypothesisReason()
    {
        // A well-formed reply with zero hypotheses is NOT a parse failure —
        // the pre-existing empty-hypotheses handling stays as-is.
        var empty = new AIDiagnosisActivity().ParseDiagnosisResponse(
            """{"analysis_summary": "nothing conclusive", "hypotheses": []}""");

        empty.FailureReason.Should().BeNullOrEmpty();
        DebugEvents.IsDiagnosisProduced(empty).Should().BeFalse();
        DebugEvents.DiagnosisFailureReason(empty).Should().Be(DebugEvents.ReasonNoHypothesis);
    }

    [Test]
    public void IsDiagnosisProduced_NullResult_RoutesToFailedPath()
    {
        DebugEvents.IsDiagnosisProduced(null).Should().BeFalse();
        DebugEvents.DiagnosisFailureReason(null).Should().Be(DebugEvents.ReasonNoHypothesis);
    }
}
