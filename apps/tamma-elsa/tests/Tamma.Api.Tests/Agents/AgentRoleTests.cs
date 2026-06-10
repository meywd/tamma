using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;

namespace Tamma.Api.Tests.Agents;

[TestFixture]
public class AgentRoleTests
{
    [Test]
    public void Has_exactly_eight_roles() =>
        Enum.GetValues<AgentRole>().Length.Should().Be(8);

    [TestCase(AgentRole.Developer, "developer")]
    [TestCase(AgentRole.ProductOwner, "product_owner")]
    [TestCase(AgentRole.SeniorDeveloper, "senior_developer")]
    [TestCase(AgentRole.TechWriter, "tech_writer")]
    public void ToWire_returns_canonical_string(AgentRole role, string wire) =>
        role.ToWire().Should().Be(wire);

    [TestCase("implementer", AgentRole.Developer)]
    [TestCase("analyst", AgentRole.ProductOwner)]
    [TestCase("developer", AgentRole.Developer)]
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
