using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Design;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;
using TypedDesign = Tamma.Core.Documents.Types.Design;
using CoreAlternative = Tamma.Core.Documents.Types.DesignAlternative;

namespace Tamma.Activities.Tests.Documents.Types;

/// <summary>
/// Story 39-4 AC5/AC8 — the subsumption + round-trip half for <see cref="DesignDocumentType"/>
/// (Design Decision D8). Invokes the OLD <c>DesignParsing.ParseProposal</c> baseline:
/// every JSON-shaped input it fail-closes (null) the typed validator also rejects; and
/// a valid typed <see cref="Design"/> re-serializes into a shape ParseProposal still
/// recovers with matching summary/alternatives.
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

    // JSON-shaped negatives from DesignResumeReadBackTests.cs (baseline → null).
    [TestCase("""{"recommendation":"do X"}""")]  // missing summary
    [TestCase("""{"summary":""}""")]              // empty summary
    public void Every_proposal_the_baseline_fail_closes_the_typed_validator_also_rejects(string json)
    {
        DesignParsing.ParseProposal(json).Should().BeNull("baseline floor");
        Validate(json).IsValid.Should().BeFalse("the typed validator must also reject what ParseProposal fail-closes");
    }

    [Test]
    public void Valid_typed_design_round_trips_through_the_old_parser()
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

        // Our validator is happy...
        using (var doc = JsonDocument.Parse(json))
            Type.Validate(doc.RootElement).IsValid.Should().BeTrue();

        // ...and the old parser recovers the same summary + alternatives.
        var proposal = DesignParsing.ParseProposal(json);
        proposal.Should().NotBeNull("the old parser must still parse the re-serialized typed payload");
        proposal!.Summary.Should().Be("Token-bucket limiter as middleware.");
        proposal.Alternatives.Should().HaveCount(2);
        proposal.Alternatives.Select(a => a.Name).Should().Equal("Middleware", "Redis");
    }
}
