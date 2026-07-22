using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 39-3 AC8 — each type's <see cref="IDocumentType.RenderContract"/> output
/// must carry every JSON token its prompt cells are bound to in
/// <c>ContractBindingTests.Bindings</c> (Tamma.Activities.Tests), plus be
/// deterministic (39-16 diffs it in CI).
///
/// <para>The binding map is private to <c>ContractBindingTests</c>, so the token
/// lists are duplicated here DELIBERATELY (cross-referenced by comment). Story
/// 39-16 later collapses this duplication by GENERATING the contract from the type
/// — until then this pin is the guard that a contract does not drift out of the
/// tokens the fail-closed parsers require.</para>
/// </summary>
[TestFixture]
public class RenderContractTokenTests
{
    // (senior_developer, decompose-issue) → DecompositionParsing.ParseDecomposition (9 tokens)
    private static readonly string[] DecompositionTokens =
    {
        "\"summary\"", "\"subtasks\"", "\"id\"", "\"title\"", "\"description\"",
        "\"acceptanceCriteria\"", "\"estimateHours\"", "\"complexity\"", "\"dependsOn\"",
    };

    // (product_owner, research) → ResearchParsing.ParseReport (7 tokens)
    private static readonly string[] FindingsTokens =
    {
        "\"summary\"", "\"findings\"", "\"title\"", "\"relevance\"",
        "\"confidence\"", "\"citations\"", "\"overallConfidence\"",
    };

    // (product_owner, score-ambiguity) → AmbiguityParsing.ParseAssessment (8 tokens)
    private static readonly string[] AmbiguityTokens =
    {
        "\"score\"", "\"confidence\"", "\"rationale\"", "\"ambiguities\"",
        "\"type\"", "\"description\"", "\"severity\"", "\"recommendation\"",
    };

    // (product_owner, clarify-requirements) → ParseQuestions pins the phrase "JSON array";
    // (product_owner, incorporate-answers) → ParseClarification pins the three object tokens.
    private static readonly string[] ClarificationTokens =
    {
        "JSON array", "\"clarifiedRequirement\"", "\"remainingAmbiguities\"", "\"resolved\"",
    };

    [TestCaseSource(nameof(Cases))]
    public void Contract_contains_every_bound_token(IDocumentType type, string[] tokens)
    {
        var contract = type.RenderContract();
        foreach (var token in tokens)
            contract.Should().Contain(token, $"{type.Key} contract must carry the bound token {token}");
    }

    [TestCaseSource(nameof(Cases))]
    public void Contract_is_deterministic(IDocumentType type, string[] tokens)
    {
        _ = tokens;
        var first = type.RenderContract();
        type.RenderContract().Should().Be(first);
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        yield return new TestCaseData(new DecompositionDocumentType(), DecompositionTokens).SetName("decomposition");
        yield return new TestCaseData(new FindingsDocumentType(), FindingsTokens).SetName("findings");
        yield return new TestCaseData(new AmbiguityAssessmentDocumentType(), AmbiguityTokens).SetName("ambiguity-assessment");
        yield return new TestCaseData(new ClarificationDocumentType(), ClarificationTokens).SetName("clarification");
    }
}
