using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;

namespace Tamma.Api.Tests.Agents;

[TestFixture]
public class AgentActionTests
{
    [Test]
    public void Roundtrip_holds_for_every_action()
    {
        foreach (var a in Enum.GetValues<AgentAction>())
            AgentActionExtensions.Parse(a.ToWire()).Should().Be(a);
    }

    [Test]
    public void Every_member_has_a_unique_wire()
    {
        var wires = Enum.GetValues<AgentAction>().Select(a => a.ToWire()).ToList();
        wires.Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void Has_the_expected_token_count()
    {
        // 72 original + 2 assessment actions (generate-assessment-questions,
        // analyze-assessment-response) added in assessment P0 + 1 research action
        // (Story 3.4 — dedicated research/investigate token under product_owner)
        // + 1 score-ambiguity action (Story 3.6 — dedicated ambiguity-scoring token
        // under product_owner) + 1 decompose-issue action (Story 2.14 — dedicated
        // issue-decomposition token under senior_developer) + 1 incorporate-answers
        // (product_owner) and 1 propose-design (architect) — taxonomy split so each
        // (role, action) cell carries exactly one output contract.
        // Story 39-15 (D5) — 79 → 80: the split (developer, triage-context-scan) action
        // was minted so the Findings-producing triage-context use is a document contract
        // while ContextGatheringWorkflow keeps context-scan free-text.
        // Story 41-1a — 80 → 96: the 16 Epic 41 tokens (4 scrum_master + the 41-8
        // Phase B write-retro-narrative lockstep cell + 2 project_manager +
        // 4 ux_designer + triage-tech-debt/design-system (architect) + triage-pr
        // (senior_developer) + manage-regression (tester) + incident-rootcause (devops)).
        Enum.GetValues<AgentAction>().Length.Should().Be(96);
    }

    [TestCase("context-scan", AgentAction.ContextScan)]
    [TestCase("implement-feature", AgentAction.ImplementFeature)]
    [TestCase("code-review-security", AgentAction.CodeReviewSecurity)]
    [TestCase("research", AgentAction.Research)]
    [TestCase("score-ambiguity", AgentAction.ScoreAmbiguity)]
    [TestCase("decompose-issue", AgentAction.DecomposeIssue)]
    public void Parse_resolves_canonical_wire(string wire, AgentAction expected)
    {
        AgentActionExtensions.Parse(wire).Should().Be(expected);
    }

    [Test]
    public void Parse_throws_on_unknown_or_empty()
    {
        ((Action)(() => AgentActionExtensions.Parse("teleport"))).Should().Throw<ArgumentException>();
        ((Action)(() => AgentActionExtensions.Parse(null!))).Should().Throw<ArgumentException>();
        ((Action)(() => AgentActionExtensions.Parse(""))).Should().Throw<ArgumentException>();
    }
}
