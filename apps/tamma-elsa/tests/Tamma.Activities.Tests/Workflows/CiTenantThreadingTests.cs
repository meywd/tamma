using Elsa.Expressions.Models;
using Elsa.Workflows;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Epic 31 review (F-high) — tenant scope must reach the CI plane.
///
/// <para>The cycle passes <c>tenantId</c> into the CI gate
/// (<c>SingleIssueCycleWorkflow</c> / <c>MergeApprovalWorkflow</c> both send
/// it), but two independent breaks severed it before it could reach
/// <c>TriggerCIActivity</c> / <c>WaitForCIResultsActivity</c>:</para>
/// <list type="number">
///   <item><description><c>ci-with-debug-retry</c> DROPPED the input — its
///     testing-pipeline dispatch forwarded only
///     SessionId/Repository/Branch/SkillLevel;</description></item>
///   <item><description><c>testing-pipeline</c> stored the tenant in a
///     variable named <c>"TenantIdTag"</c>, invisible to the activities'
///     ambient <c>GetVariable("TenantId")</c> lookup (the MediatedLlmText
///     convention, pinned for the debugging workflow by
///     <c>DebuggingWorkflowTests.DeclaresTenantIdVariable_NamedForMediatedResolution</c>).</description></item>
/// </list>
/// <para>Either break alone made every CI trigger and every DG-5 poller
/// resolution run platform-scoped in SaaS (the trigger then fails the
/// cross-tenant repo guard and ci-with-debug-retry burns its LLM debug budget
/// against a call that can never succeed). These tests pin BOTH links by
/// evaluating the REAL built graph — the actual dispatch input delegates and
/// the actual declared variable names — not re-derived copies.</para>
/// </summary>
[TestFixture]
public class CiTenantThreadingTests
{
    private const string Tenant = "6a5ee5c1-8f5a-4d3a-9b6e-000000000042";

    // ================================================================
    // Link 2 — testing-pipeline declares the tenant variable under the ONE
    // name the activities' ambient resolution reads.
    // ================================================================

    [Test]
    public void TestingWorkflow_DeclaresTenantIdVariable_NamedForAmbientResolution()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new TestingWorkflow());

        builder.Object.Variables.Should().Contain(v => v.Name == "TenantId",
            "TriggerCIActivity / WaitForCIResultsActivity (and EventPersistenceMiddleware) resolve "
            + "tenant ambiently via GetVariable(\"TenantId\") — any other name is invisible to them");
        builder.Object.Variables.Should().NotContain(v => v.Name == "TenantIdTag",
            "the old name was exactly the bug: declared but never read by anything");
    }

    [Test]
    public void CiWithDebugRetry_DeclaresTenantIdVariable_NamedForAmbientResolution()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new CiWithDebugRetryWorkflow());

        builder.Object.Variables.Should().Contain(v => v.Name == "TenantId",
            "ci-with-debug-retry must hold the cycle's tenantId so its dispatches can forward it "
            + "and its own DCB events resolve tenant scope");
    }

    // ================================================================
    // Link 1 — ci-with-debug-retry FORWARDS the tenant into both of its
    // dispatches. Evaluated through the real Input delegates of the built
    // graph (the DeploymentPipelineGateTests pattern), not a copy.
    // ================================================================

    [Test]
    public void CiWithDebugRetry_TestingPipelineDispatch_ForwardsTenantId()
    {
        var input = EvaluateDispatchInput(
            new CiWithDebugRetryWorkflow(), "DispatchTestingPipeline", Tenant);

        input.Should().ContainKey("tenantId",
            "the testing-pipeline dispatch is the only route by which tenant scope can reach "
            + "TriggerCI / WaitForCIResults — Elsa variables do not cross DispatchWorkflow "
            + "instance boundaries");
        input["tenantId"].Should().Be(Tenant);
    }

    [Test]
    public void CiWithDebugRetry_DebuggingDispatch_ForwardsTenantId()
    {
        var input = EvaluateDispatchInput(
            new CiWithDebugRetryWorkflow(), "DispatchCiDebugging", Tenant);

        input.Should().ContainKey("tenantId",
            "the debugging workflow's mediated LLM/testing dispatches resolve tenant from this input");
        input["tenantId"].Should().Be(Tenant);
    }

    // ================================================================
    // Helper — evaluate a DispatchWorkflow's REAL Input delegate over a
    // register holding the workflow's own declared variables.
    // ================================================================

    private static IDictionary<string, object> EvaluateDispatchInput(
        WorkflowBase workflow, string dispatchId, string tenant)
    {
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);
        var dispatch = flowchart.Activities.OfType<DispatchWorkflow>()
            .SingleOrDefault(d => d.Id == dispatchId);
        dispatch.Should().NotBeNull($"the workflow must contain the '{dispatchId}' dispatch");

        var variables = builder.Object.Variables;
        // Unnamed builder variables (WithVariable<T>() with no name — the
        // dispatch-result captures) carry no Id until Elsa's real builder
        // assigns one; give them one so MemoryRegister.Declare can key them.
        foreach (var variable in variables.Where(v => string.IsNullOrEmpty(v.Id)))
            variable.Id = Guid.NewGuid().ToString("N");
        var register = new MemoryRegister();
        register.Declare(variables);

        using var services = new ServiceCollection().BuildServiceProvider();
        var context = new ExpressionExecutionContext(services, register);

        var tenantVar = (Variable<string>)variables.Single(v => v.Name == "TenantId");
        tenantVar.Set(context, tenant);

        var func = dispatch!.Input?.Expression?.Value
            as Func<ExpressionExecutionContext, ValueTask<object?>>;
        func.Should().NotBeNull(
            $"'{dispatchId}'.Input must still be a Delegate-expression input; if it became a "
            + "JS/Liquid expression these behaviour pins stop testing the real thing and must be "
            + "rewritten rather than deleted");

        var evaluated = func!(context).AsTask().GetAwaiter().GetResult();
        var dict = evaluated as IDictionary<string, object>;
        dict.Should().NotBeNull($"'{dispatchId}'.Input must evaluate to the dispatch input dictionary");
        return dict!;
    }
}
