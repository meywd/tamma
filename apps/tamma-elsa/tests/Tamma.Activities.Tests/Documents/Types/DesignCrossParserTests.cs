using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;
using TypedDesign = Tamma.Core.Documents.Types.Design;
using CoreAlternative = Tamma.Core.Documents.Types.DesignAlternative;

namespace Tamma.Activities.Tests.Documents.Types;

/// <summary>
/// Story 39-4 AC5/AC8 — the subsumption + round-trip half for <see cref="DesignDocumentType"/>
/// (Design Decision D8). Story 39-13 D9: the legacy <c>DesignParsing.ParseProposal</c> baseline
/// is retired with the parser, so the cross-parser rows are pruned; what remains asserts the
/// typed validator on the same shapes (rejects the baseline negatives, accepts the round-tripped
/// typed payload).
/// </summary>
[TestFixture]
public class DesignCrossParserTests
{
    private static readonly DesignDocumentType Type = new();

    private static DocumentValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Type.Validate(doc.RootElement);
    }

    // JSON-shaped negatives the retired baseline fail-closed on — the typed validator rejects too.
    [TestCase("""{"recommendation":"do X"}""")]  // missing summary
    [TestCase("""{"summary":""}""")]              // empty summary
    public void Every_proposal_the_baseline_fail_closes_the_typed_validator_also_rejects(string json)
    {
        Validate(json).IsValid.Should().BeFalse("the typed validator must reject what the retired ParseProposal fail-closed");
    }

    [Test]
    public void Valid_typed_design_serializes_into_a_validator_accepted_shape()
    {
        var typed = new TypedDesign
        {
            Summary = "Token-bucket limiter as middleware.",
            Alternatives = new[]
            {
                new CoreAlternative { Id = "ALT-1", Name = "Middleware", Tradeoffs = "simple; loses state on restart" },
                new CoreAlternative { Id = "ALT-2", Name = "Redis", Tradeoffs = "durable; adds a dependency" },
            },
            Recommendation = "ALT-1 is lowest-risk.",
            RecommendedAlternativeId = "ALT-1",
            ConstraintEvaluation = "meets no-new-infra constraint",
        };

        var json = JsonSerializer.Serialize(typed, DocumentJson.Options);

        // Our validator is happy with the re-serialized typed payload.
        using (var doc = JsonDocument.Parse(json))
            Type.Validate(doc.RootElement).IsValid.Should().BeTrue();
    }
}
