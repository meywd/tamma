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
        Enum.GetValues<AgentAction>().Length.Should().Be(72);
    }

    [TestCase("context-scan", AgentAction.ContextScan)]
    [TestCase("implement-feature", AgentAction.ImplementFeature)]
    [TestCase("code-review-security", AgentAction.CodeReviewSecurity)]
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
