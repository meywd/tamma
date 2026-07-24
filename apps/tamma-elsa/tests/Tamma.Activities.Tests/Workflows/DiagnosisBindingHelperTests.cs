using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Debug;
using Tamma.Activities.Debug.Models;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;
using Tamma.ElsaServer.Workflows.Helpers;
using TypedDiagnosis = Tamma.Core.Documents.Types.Diagnosis;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-15 (D4) — the debug-diagnosis binding bridge. The retired
/// <c>AIDiagnosisActivityTests</c> corpus (valid/garbage/empty diagnosis reads + the
/// fail-closed caller-gate routing) is ported here: the loop consumes a bare
/// <see cref="Hypothesis"/>[] JSON, so <see cref="DiagnosisBindingHelper.ToLegacyHypothesesJson"/>
/// must project the typed <see cref="TypedDiagnosis.Hypotheses"/> onto exactly the shape
/// <c>SelectHypothesisActivity</c> deserializes — never fabricating a hypothesis and never a throw.
/// </summary>
[TestFixture]
public class DiagnosisBindingHelperTests
{
    private static string DiagnosisDocJson(TypedDiagnosis d) => JsonSerializer.Serialize(d, DocumentJson.Options);

    private static readonly TypedDiagnosis ValidDiagnosis = new()
    {
        AnalysisSummary = "Null ref in resolver",
        Hypotheses = new[]
        {
            new DiagnosisHypothesis { Rank = 1, Description = "Resolver returns null on cache miss", Confidence = 0.85m, SuggestedFix = "Guard the cache miss", AffectedFiles = new[] { "src/Resolver.cs" } },
            new DiagnosisHypothesis { Rank = 2, Description = "Warm-up race", Confidence = 0.4m },
        },
    };

    [Test]
    public void ToLegacyHypothesesJson_ValidDiagnosis_ProjectsUntriedHypotheses_TheLoopCanSlice()
    {
        var json = DiagnosisBindingHelper.ToLegacyHypothesesJson(DiagnosisDocJson(ValidDiagnosis));

        // The loop's SelectHypothesisActivity deserializes exactly this shape.
        var hypotheses = JsonSerializer.Deserialize<List<Hypothesis>>(json);
        hypotheses.Should().NotBeNull();
        hypotheses!.Should().HaveCount(2);
        hypotheses[0].Description.Should().Be("Resolver returns null on cache miss");
        hypotheses[0].Confidence.Should().Be(0.85m);
        hypotheses[0].SuggestedFix.Should().Be("Guard the cache miss");
        hypotheses[0].AffectedFiles.Should().Contain("src/Resolver.cs");
        hypotheses.Should().OnlyContain(h => h.Outcome == HypothesisOutcome.Untried,
            "the loop's select/refine bookkeeping starts clean (every projected hypothesis Untried)");
    }

    [Test]
    public void ToLegacyHypothesesJson_GarbageOrEmpty_FailsClosedToEmptyArray_NoFabrication()
    {
        DiagnosisBindingHelper.ToLegacyHypothesesJson(null).Should().Be("[]");
        DiagnosisBindingHelper.ToLegacyHypothesesJson("").Should().Be("[]");
        DiagnosisBindingHelper.ToLegacyHypothesesJson("I could not analyze this, sorry.").Should().Be("[]");
        DiagnosisBindingHelper.ToLegacyHypothesesJson("{}").Should().Be("[]");
        DiagnosisBindingHelper.ToLegacyHypothesesJson(
            DiagnosisDocJson(new TypedDiagnosis { AnalysisSummary = "nothing conclusive" }))
            .Should().Be("[]", "a well-formed diagnosis with zero hypotheses is not a usable diagnosis");
    }

    [Test]
    public void HasUsableHypotheses_RoutesTheCallerGate()
    {
        DiagnosisBindingHelper.HasUsableHypotheses("[]").Should().BeFalse();
        DiagnosisBindingHelper.HasUsableHypotheses(null).Should().BeFalse();
        DiagnosisBindingHelper.HasUsableHypotheses("  ").Should().BeFalse();
        DiagnosisBindingHelper.HasUsableHypotheses(
            DiagnosisBindingHelper.ToLegacyHypothesesJson(DiagnosisDocJson(ValidDiagnosis))).Should().BeTrue();
    }

    [Test]
    public void BuildFailureReason_MapsOutcomesOntoDebugReasons()
    {
        var exhausted = new LifecycleBindingHelper.LifecycleExit(
            DocumentLifecycleResult.StatusEscalated,
            DocumentLifecycleOutcome.ValidationExhausted.ToWire(), null, "{}", "");
        DiagnosisBindingHelper.BuildFailureReason(exhausted).Should().Be(DebugEvents.ReasonDiagnosisParseFailure,
            "a validation-exhausted escalation is the parse-failure equivalent (the model never produced a valid Diagnosis)");

        var rejected = new LifecycleBindingHelper.LifecycleExit(
            DocumentLifecycleResult.StatusRejected, null, null, "{}", "");
        DiagnosisBindingHelper.BuildFailureReason(rejected).Should().Be(DebugEvents.ReasonDiagnosisCallFailed);

        DiagnosisBindingHelper.BuildFailureReason(exhausted).Should().NotBeNullOrEmpty(
            "a failed diagnosis always carries a reason (no silent failure)");
    }
}
