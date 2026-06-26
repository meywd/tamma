using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Review fix (IMPORTANT) — the parent <c>issue-triage</c> workflow must thread a
/// <c>tenantId</c> into each <c>triage-item-cycle</c> dispatch so the cycle's
/// <c>TRIAGE.ISSUE.*</c> events are tenant-scoped (the cycle reads
/// <c>GetInput&lt;string&gt;("tenantId")</c> and forwards it onward). Previously the
/// dispatch passed only repository + itemJson, leaving every cycle event platform-scope.
/// </summary>
[TestFixture]
public class IssueTriageWorkflowTests
{
    private Flowchart _flowchart = null!;
    private Moq.Mock<Elsa.Workflows.IWorkflowBuilder> _builder = null!;

    [SetUp]
    public void SetUp()
    {
        _builder = WorkflowTestHelper.BuildWorkflow(new IssueTriageWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(_builder);
    }

    [Test]
    public void Workflow_BuildsWithExpectedDefinitionId()
    {
        _builder.Object.DefinitionId.Should().Be("issue-triage");
    }

    [Test]
    public void Workflow_DeclaresATenantIdVariable_ReadFromInput()
    {
        // The TenantId variable is the carrier: FetchItems reads it from this workflow's
        // own `tenantId` input, and the cycle dispatch forwards it. Without the variable
        // there is nothing to thread (the old platform-scope bug).
        _builder.Object.Variables.Any(v => v.Name == "TenantId").Should().BeTrue(
            "issue-triage must carry a TenantId variable threaded into each cycle dispatch");
    }

    [Test]
    public void CycleDispatch_TargetsTriageItemCycle_AndThreadsTenantId()
    {
        var dispatch = _flowchart.Activities
            .OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "DispatchTriageCycle");
        dispatch.Should().NotBeNull("the parent must dispatch the per-item cycle");
        ReadDefinitionId(dispatch!).Should().Be("triage-item-cycle");

        // The dispatch Input is a runtime delegate; assert it is wired (the input
        // expression exists) so the {repository,itemJson,tenantId} dictionary is built.
        dispatch!.Input.Should().NotBeNull("the cycle dispatch must build an input dictionary");
        dispatch.Input!.Expression.Should().NotBeNull(
            "the cycle dispatch input (carrying tenantId) must be wired to an expression");
    }

    private static string? ReadDefinitionId(DispatchWorkflow dispatch)
    {
        var prop = typeof(DispatchWorkflow).GetProperty("WorkflowDefinitionId");
        var value = prop?.GetValue(dispatch);
        var expression = value?.GetType().GetProperty("Expression")?.GetValue(value)
            as Elsa.Expressions.Models.Expression;
        return expression?.Value?.ToString();
    }
}
