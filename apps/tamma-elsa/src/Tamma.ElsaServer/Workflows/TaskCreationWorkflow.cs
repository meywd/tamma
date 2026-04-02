using Elsa.Workflows;
using Elsa.Workflows.Activities;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Task Creation — Senior dev LLM breaks the plan into deep implementation tasks.
/// Each task has: files, code changes, test approach, dependencies (DAG).
///
/// Inputs: repository, issueNumber, planJson, contextIds
/// Outputs: tasksJson (array of detailed task plans)
/// </summary>
public class TaskCreationWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Task Creation";
        builder.DefinitionId = "task-creation";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Senior dev LLM breaks plan into deep implementation task plans";

        var stub = new Finish { Id = "Stub", Name = "Stub: Task Creation" };
        stub.SetDisplayText("Stub: Task Creation — TODO");
        builder.Root = stub;
    }
}
