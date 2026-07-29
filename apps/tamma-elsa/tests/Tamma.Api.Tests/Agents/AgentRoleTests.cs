using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;

namespace Tamma.Api.Tests.Agents;

[TestFixture]
public class AgentRoleTests
{
    [Test]
    public void Has_exactly_eleven_roles() =>
        // 8 → 11 (Story 41-1a): scrum_master, project_manager, ux_designer.
        Enum.GetValues<AgentRole>().Length.Should().Be(11);

    [TestCase(AgentRole.Developer, "developer")]
    [TestCase(AgentRole.ProductOwner, "product_owner")]
    [TestCase(AgentRole.SeniorDeveloper, "senior_developer")]
    [TestCase(AgentRole.TechWriter, "tech_writer")]
    public void ToWire_returns_canonical_string(AgentRole role, string wire) =>
        role.ToWire().Should().Be(wire);

    [TestCase("implementer", AgentRole.Developer)]
    [TestCase("analyst", AgentRole.ProductOwner)]
    [TestCase("developer", AgentRole.Developer)]
    // Story 41-1a (D3 proof at the Parse level): scrum_master resolved to
    // ProductOwner via the removed alias; it now parses to its own role, while
    // analyst/researcher stay aliased.
    [TestCase("scrum_master", AgentRole.ScrumMaster)]
    [TestCase("researcher", AgentRole.ProductOwner)]
    [TestCase("project_manager", AgentRole.ProjectManager)]
    [TestCase("ux_designer", AgentRole.UxDesigner)]
    public void Parse_applies_legacy_aliases_then_exact(string input, AgentRole expected) =>
        AgentRoleExtensions.Parse(input).Should().Be(expected);

    [Test]
    public void Parse_throws_ArgumentException_on_unknown()
    {
        var act = () => AgentRoleExtensions.Parse("wizard");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Roundtrip_holds_for_every_role()
    {
        foreach (var r in Enum.GetValues<AgentRole>())
            AgentRoleExtensions.Parse(r.ToWire()).Should().Be(r);
    }

    [Test]
    public void Parse_throws_on_null_or_empty()
    {
        ((Action)(() => AgentRoleExtensions.Parse(null!))).Should().Throw<ArgumentException>();
        ((Action)(() => AgentRoleExtensions.Parse(""))).Should().Throw<ArgumentException>();
        ((Action)(() => AgentRoleExtensions.Parse("   "))).Should().Throw<ArgumentException>();
    }

    [Test]
    public void Parse_is_case_sensitive_for_canonical_roles()
    {
        // Wire strings are canonical lowercase; non-canonical casing is rejected.
        ((Action)(() => AgentRoleExtensions.Parse("DEVELOPER"))).Should().Throw<ArgumentException>();
    }
}
