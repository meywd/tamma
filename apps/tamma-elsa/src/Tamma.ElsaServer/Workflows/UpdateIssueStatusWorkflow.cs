using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Tamma.Activities.ADL;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Tiny sub-workflow that posts a status comment on a GitHub issue.
/// Called via DispatchWorkflow with WaitForCompletion=false (fire and forget).
/// Has built-in retries in the activity.
///
/// Inputs:
///   - repository: owner/repo
///   - issueNumber: int
///   - message: string (the comment body)
///   - addLabels: string[] (optional)
///   - removeLabels: string[] (optional)
/// </summary>
public class UpdateIssueStatusWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Update Issue Status";
        builder.DefinitionId = "update-issue-status";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Post a status comment on a GitHub issue (fire-and-forget with retries)";

        var updateIssue = new UpdateIssueStatusActivity
        {
            Id = "UpdateIssue",
            Name = "Update Issue",
            Repository = new Input<string>(ctx => ctx.GetInput<string>("repository") ?? ""),
            IssueNumber = new Input<int>(ctx => ctx.GetInput<int>("issueNumber")),
            Message = new Input<string>(ctx => ctx.GetInput<string>("message") ?? ""),
            AddLabels = new Input<string[]?>(ctx => ctx.GetInput<string[]>("addLabels")),
            RemoveLabels = new Input<string[]?>(ctx => ctx.GetInput<string[]>("removeLabels")),
        };
        updateIssue.SetDisplayText("Update Issue");

        builder.Root = updateIssue;
    }
}
