using Elsa.Workflows;
using Elsa.Workflows.Activities;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Plan Review — 7-role LLM panel reviews the implementation plan.
/// Roles: Architect, Developer, QA, Security, DevOps, PO, Orchestrator.
/// Iterative discussion rounds until consensus.
///
/// Inputs: repository, issueNumber, planJson, contextIds
/// Outputs: decision (approved/defer/split/needsHuman), planJson (modified), deferred[], split[]
/// </summary>
public class PlanReviewWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Plan Review";
        builder.DefinitionId = "plan-review";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "7-role LLM panel reviews the implementation plan with iterative discussion";

        // STUB — to be implemented during sub-workflow optimization
        var stub = new Finish { Id = "Stub", Name = "Stub: Plan Review" };
        stub.SetDisplayText("Stub: Plan Review — TODO");
        builder.Root = stub;
    }
}
