using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Task Review — 4-role panel (Architect, Senior Dev, Dev, QA) reviews implementation tasks.
///
/// Inputs: repository, issueNumber, tasksJson, planJson
/// Outputs: decision (approved/needsChanges/needsHuman), tasksJson (modified)
/// </summary>
public class TaskReviewWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Task Review";
        builder.DefinitionId = "task-review";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "4-role panel reviews implementation tasks before execution";

        var stub = new Finish { Id = "Stub", Name = "Stub: Task Review" };
        stub.SetDisplayText("Stub: Task Review — TODO");
        builder.Root = stub;
    }
}
