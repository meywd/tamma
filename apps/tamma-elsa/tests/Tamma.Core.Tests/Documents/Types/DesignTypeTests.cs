using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 39-4 AC5 — <see cref="DesignDocumentType"/> domain rules (≥1 alternative
/// with trade-offs, recommendation references a listed alternative by id). Pure half;
/// the subsumption/round-trip against <c>DesignParsing.ParseProposal</c> lives in
/// Activities.Tests (D8).
/// </summary>
[TestFixture]
public class DesignTypeTests
{
    private static readonly DesignDocumentType Type = new();

    private static DocumentValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Type.Validate(doc.RootElement);
    }

    private static IEnumerable<string> Codes(DocumentValidationResult r) => r.Violations.Select(v => v.Code);

    [Test]
    public void Valid_design_passes()
    {
        var r = Validate(
            """
            {
              "summary": "Token-bucket limiter as middleware.",
              "alternatives": [
                { "id": "ALT-1", "name": "Middleware", "tradeoffs": "simple; loses state on restart" },
                { "id": "ALT-2", "name": "Redis", "tradeoffs": "durable; adds a dependency" }
              ],
              "recommendation": "ALT-1 is lowest-risk.",
              "recommendedAlternativeId": "ALT-1"
            }
            """);
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [Test]
    public void Missing_summary_is_reported()
    {
        var r = Validate(
            """{ "alternatives": [ { "id": "A", "name": "n", "tradeoffs": "t" } ], "recommendedAlternativeId": "A" }""");
        Codes(r).Should().Contain(DesignDocumentType.MissingSummary);
    }

    [Test]
    public void No_alternatives_is_reported()
    {
        var r = Validate("""{ "summary": "s", "alternatives": [], "recommendedAlternativeId": "A" }""");
        Codes(r).Should().Contain(DesignDocumentType.NoAlternatives);
    }

    [Test]
    public void Alternative_missing_tradeoffs_is_reported()
    {
        var r = Validate(
            """{ "summary": "s", "alternatives": [ { "id": "A", "name": "n", "tradeoffs": "" } ], "recommendedAlternativeId": "A" }""");
        Codes(r).Should().Contain(DesignDocumentType.AlternativeMissingTradeoffs);
    }

    [Test]
    public void Recommendation_naming_no_listed_alternative_is_reported()
    {
        var r = Validate(
            """
            {
              "summary": "s",
              "alternatives": [ { "id": "ALT-1", "name": "n", "tradeoffs": "t" } ],
              "recommendation": "the other one",
              "recommendedAlternativeId": "ALT-9"
            }
            """);
        Codes(r).Should().Contain(DesignDocumentType.RecommendationUnknownAlternative);
    }

    [Test]
    public void Contract_carries_bound_tokens_and_is_deterministic()
    {
        var contract = Type.RenderContract();
        foreach (var token in new[] { "\"summary\"", "\"recommendation\"", "\"constraintEvaluation\"", "\"alternatives\"", "\"name\"", "\"tradeoffs\"" })
            contract.Should().Contain(token);
        Type.RenderContract().Should().Be(contract);
    }
}
