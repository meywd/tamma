using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 41-1a (C2/D7) — the guard the codebase was missing: DefaultAgentConfig.ForRole
/// asserts the role via <see cref="RolePhaseMap.AssertValidRole"/> (which PASSES for any
/// enum member) and then indexes its per-role table RAW, so a new AgentRole member with
/// no row is an untyped KeyNotFoundException from AgentResolverService — invisible until
/// runtime. This fixture regression-pins that class of bug for every future role.
/// </summary>
[TestFixture]
public class DefaultAgentConfigTests
{
    [Test]
    public void ForRole_Returns_A_Config_For_Every_Valid_Role()
    {
        foreach (var role in RolePhaseMap.ValidRoles)
        {
            var config = DefaultAgentConfig.ForRole(role);
            config.Should().NotBeNull($"every role in RolePhaseMap.ValidRoles needs a DefaultAgentConfig row (role '{role}')");
            config.Role.Should().Be(role);
            config.Provider.Should().NotBeNullOrEmpty();
            config.Model.Should().NotBeNullOrEmpty();
            config.Handle.Should().NotBeNullOrEmpty();
            config.Source.Should().Be("platform-default");
        }
    }

    [TestCase("scrum_master", "tamma-scrum-master")]
    [TestCase("project_manager", "tamma-project-manager")]
    [TestCase("ux_designer", "tamma-ux-designer")]
    public void ForRole_NewEpic41Roles_Have_Their_Own_Handles(string role, string handle)
    {
        // D7: planning/prose roles cloned from the product_owner row's shape — no
        // code tools, own handle, own system prompt.
        var config = DefaultAgentConfig.ForRole(role);
        config.Handle.Should().Be(handle);
        config.Tools.Should().BeEmpty("planning/prose roles ship no code tools by default");
        config.SystemPrompt.Should().NotBeNullOrEmpty();
    }
}
