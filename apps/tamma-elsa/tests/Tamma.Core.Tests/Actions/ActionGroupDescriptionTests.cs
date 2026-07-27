using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Actions;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// Group descriptions (Story 43-3 AC6/D8). CONTENT assertions, not just
/// non-empty: three descriptions are the only honest, admin-visible disclosure
/// of known holes in the epic's risk list, and a blank-but-present string would
/// satisfy a weaker test. Trimming them as "UI copy" fails here — deliberately.
/// </summary>
[TestFixture]
public class ActionGroupDescriptionTests
{
    [Test]
    public void Every_group_has_a_nonempty_description()
    {
        foreach (var group in Enum.GetValues<ActionGroup>())
        {
            ActionGroupExtensions.Descriptions.Should().ContainKey(group);
            ActionGroupExtensions.Descriptions[group].Should().NotBeNullOrWhiteSpace(
                $"'{group.ToWire()}' needs UI-facing description text (43-3 AC6)");
        }
    }

    [Test]
    public void DeployControl_disclosure_names_the_llm_tool_loop_limitation()
    {
        // Epic risk 8: production deploy is an LLM tool loop, not a typed
        // activity — gating the deploy effect gates the STAGE TRANSITION while
        // the deploy itself runs inside the loop under shell_execute. This must
        // reach the admin in the UI, not only a design doc.
        var text = ActionGroupExtensions.Descriptions[ActionGroup.DeployControl];

        text.Should().ContainEquivalentOf("tool loop");
        text.Should().ContainEquivalentOf("stage transition");
        text.Should().ContainEquivalentOf("shell_execute");
    }

    [Test]
    public void CommandExecution_disclosure_names_the_shell_bypass()
    {
        // Epic risk list: shell_execute can reach every governed HTTP route by
        // curl, defeating finer-grained gates.
        var text = ActionGroupExtensions.Descriptions[ActionGroup.CommandExecution];

        text.Should().ContainEquivalentOf("curl");
        text.Should().ContainEquivalentOf("bypass");
    }

    [Test]
    public void ModelInvocation_disclosure_names_mcp_coarseness()
    {
        // Epic risk list: MCP is one coarse member with no drift signal — adding
        // a server or a tool on an existing server changes nothing in the catalog.
        var text = ActionGroupExtensions.Descriptions[ActionGroup.ModelInvocation];

        text.Should().ContainEquivalentOf("MCP");
        text.Should().ContainEquivalentOf("coarse");
    }

    [Test]
    public void Authoring_and_DeployControl_both_say_where_infrastructure_authoring_sits()
    {
        // 43-3 D5.1 mitigation: an admin who raises deploy-control believing they
        // gated Terraform edits must be told otherwise in BOTH group descriptions.
        ActionGroupExtensions.Descriptions[ActionGroup.Authoring]
            .Should().ContainEquivalentOf("implement-infrastructure");
        ActionGroupExtensions.Descriptions[ActionGroup.DeployControl]
            .Should().ContainEquivalentOf("authoring");
    }
}
