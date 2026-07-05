using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Decomposition;
using Tamma.Activities.Decomposition.Models;

namespace Tamma.Activities.Tests.Decomposition;

/// <summary>
/// Story 2.14 — unit coverage for <see cref="DecompositionParsing.ParseDecomposition"/>. Proves
/// the parser recovers the structured decomposition on a well-formed response, keeps the
/// dependency edge set clean (prunes dangling / self references, drops shell + duplicate-id
/// sub-tasks), and FAILS CLOSED (returns null) on every degraded/empty/malformed input so the
/// workflow routes to DECOMPOSITION.FAILED rather than fabricating a breakdown.
/// </summary>
[TestFixture]
public class DecompositionParsingTests
{
    private const string ValidDecomposition =
        """
        Here is the decomposition:
        {
          "summary": "Split the auth feature into a schema slice, an endpoint slice, and a UI slice, preserving the login/logout intent.",
          "subtasks": [
            { "id": "ST-1", "title": "Add users table", "description": "Create the users schema + migration", "acceptanceCriteria": "migration applies cleanly", "estimateHours": 3, "complexity": "low", "dependsOn": [] },
            { "id": "ST-2", "title": "Login endpoint", "description": "POST /login issuing a token", "acceptanceCriteria": "returns 200 + token", "estimateHours": 6, "complexity": "medium", "dependsOn": ["ST-1"] },
            { "id": "ST-3", "title": "Login UI", "description": "Login form wired to the endpoint", "acceptanceCriteria": "user can log in", "estimateHours": 5, "complexity": "high", "dependsOn": ["ST-2"] }
          ]
        }
        """;

    [Test]
    public void ParseDecomposition_ValidResponse_RecoversDecomposition()
    {
        var d = DecompositionParsing.ParseDecomposition(ValidDecomposition);

        d.Should().NotBeNull();
        d!.Summary.Should().Contain("Split the auth feature");
        d.Subtasks.Should().HaveCount(3);
        d.Subtasks[0].Id.Should().Be("ST-1");
        d.Subtasks[0].EstimateHours.Should().Be(3m);
        d.Subtasks[1].DependsOn.Should().Equal("ST-1");
        d.Subtasks[2].Complexity.Should().Be(SubtaskComplexities.High);
    }

    [Test]
    public void ParseDecomposition_PreservesSubtaskOrder()
    {
        var d = DecompositionParsing.ParseDecomposition(ValidDecomposition);

        d!.Subtasks.Select(s => s.Id).ToList()
            .Should().Equal(new[] { "ST-1", "ST-2", "ST-3" },
                "the array order is the initial suggested sequence that Story 2.16 (#139) refines");
    }

    [Test]
    public void ParseDecomposition_NormalizesComplexity_AndClampsNegativeHours()
    {
        const string messy =
            """
            { "summary": "s", "subtasks": [
              { "id": "A", "title": "t", "complexity": "Trivial.", "estimateHours": -4 }
            ] }
            """;

        var d = DecompositionParsing.ParseDecomposition(messy);

        d!.Subtasks[0].Complexity.Should().Be(SubtaskComplexities.Low, "'trivial' folds onto low");
        d.Subtasks[0].EstimateHours.Should().Be(0m, "a negative estimate is clamped to zero");
    }

    [Test]
    public void ParseDecomposition_PrunesDanglingAndSelfDependencies()
    {
        const string withBadDeps =
            """
            { "summary": "s", "subtasks": [
              { "id": "ST-1", "title": "a", "dependsOn": ["ST-1", "ST-99"] },
              { "id": "ST-2", "title": "b", "dependsOn": ["ST-1", "ST-1"] }
            ] }
            """;

        var d = DecompositionParsing.ParseDecomposition(withBadDeps);

        d!.Subtasks[0].DependsOn.Should().BeEmpty(
            "a self-reference (ST-1→ST-1) and a dangling id (ST-99) must both be pruned");
        d.Subtasks[1].DependsOn.Should().Equal(new[] { "ST-1" },
            "a duplicate valid dependency must be de-duplicated to a single edge");
    }

    [Test]
    public void ParseDecomposition_DropsShellSubtasks_AndDuplicateIds()
    {
        const string withShells =
            """
            { "summary": "s", "subtasks": [
              { "id": "", "title": "no id" },
              { "id": "ST-1", "title": "", "description": "" },
              { "id": "ST-2", "title": "real" },
              { "id": "ST-2", "title": "duplicate id kept as first" }
            ] }
            """;

        var d = DecompositionParsing.ParseDecomposition(withShells);

        d!.Subtasks.Should().ContainSingle();
        d.Subtasks[0].Id.Should().Be("ST-2");
        d.Subtasks[0].Title.Should().Be("real", "the first occurrence of a duplicate id wins");
    }

    /// <summary>
    /// A sample matching the EXACT shape the (senior_developer, decompose-issue) system-default
    /// prompt template (SystemPrompts.DecomposeIssueBody, Story 2.14) instructs the LLM to emit.
    /// Proves the template's documented output is parseable end-to-end, so the
    /// IssueDecompositionWorkflow happy path emits a real DECOMPOSITION.COMPLETED breakdown.
    /// </summary>
    private const string TemplateShapedDecomposition =
        """
        {
          "summary": "Deliver per-tenant rate limiting incrementally: middleware first, then config, then metrics — preserving the 'protect the API per tenant' intent.",
          "subtasks": [
            { "id": "ST-1", "title": "Token-bucket middleware", "description": "Add a token-bucket limiter keyed by tenant id", "acceptanceCriteria": "requests over the limit get 429", "estimateHours": 6, "complexity": "medium", "dependsOn": [] },
            { "id": "ST-2", "title": "Per-tenant config", "description": "Read the limit from tenant config", "acceptanceCriteria": "limit is configurable per tenant", "estimateHours": 4, "complexity": "low", "dependsOn": ["ST-1"] }
          ]
        }
        """;

    [Test]
    public void ParseDecomposition_TemplateShapedOutput_RecoversDecomposition()
    {
        var d = DecompositionParsing.ParseDecomposition(TemplateShapedDecomposition);

        d.Should().NotBeNull(
            "the (senior_developer, decompose-issue) template's documented JSON shape must parse into a real decomposition");
        d!.Summary.Should().NotBeNullOrWhiteSpace();
        d.Subtasks.Should().HaveCount(2);
        d.Subtasks[1].DependsOn.Should().Equal("ST-1");
    }

    // ── Fail-closed cases (all → null) ─────────────────────────────────
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("no json here at all")]
    [TestCase("{ not valid json")]
    public void ParseDecomposition_DegradedInput_FailsClosed(string? input)
    {
        DecompositionParsing.ParseDecomposition(input).Should().BeNull(
            "degraded/empty/malformed decomposition output must fail closed (no fabricated breakdown)");
    }

    [Test]
    public void ParseDecomposition_MissingSummary_FailsClosed()
    {
        const string noSummary = """{ "subtasks": [ { "id": "ST-1", "title": "a" } ] }""";
        DecompositionParsing.ParseDecomposition(noSummary).Should().BeNull(
            "the overview summary is load-bearing (it records intent preservation) — fail closed without it");
    }

    [Test]
    public void ParseDecomposition_NoSubtasks_FailsClosed()
    {
        const string emptySubtasks = """{ "summary": "s", "subtasks": [] }""";
        DecompositionParsing.ParseDecomposition(emptySubtasks).Should().BeNull(
            "a decomposition with no sub-tasks decomposed nothing — it must fail closed");
    }

    [Test]
    public void ParseDecomposition_AllShellSubtasks_FailsClosed()
    {
        const string allShells =
            """{ "summary": "s", "subtasks": [ { "id": "", "title": "" }, { "id": "ST-1", "title": "", "description": "" } ] }""";
        DecompositionParsing.ParseDecomposition(allShells).Should().BeNull(
            "when every sub-task is an empty shell / has no id the decomposition has no usable content — fail closed");
    }
}
