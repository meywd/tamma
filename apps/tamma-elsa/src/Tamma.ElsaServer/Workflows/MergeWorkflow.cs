using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Contracts;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Tamma.Activities.ADL;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Merge sub-workflow: squash-merges the PR, closes the issue, and cleans up the branch.
///
/// Inputs: repository, prNumber, issueNumber, branchName
/// Outputs: success, mergeSha
/// </summary>
public class MergeWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Merge Complete";
        builder.DefinitionId = "merge-complete";
        builder.Description = "Squash-merge PR, close issue, and delete branch";

        var mergeShaVar = builder.WithVariable<string>("MergeSha", "");
        var successVar = builder.WithVariable<bool>("Success", false);

        var mergePr = new MergePullRequestActivity
        {
            Id = "MergePR",
            Repository = new Input<string>(ctx => ctx.GetInput<string>("repository") ?? ""),
            PrNumber = new Input<int>(ctx => ctx.GetInput<int>("prNumber")),
            IssueNumber = new Input<int>(ctx => ctx.GetInput<int>("issueNumber")),
            BranchName = new Input<string>(ctx => ctx.GetInput<string>("branchName") ?? ""),
            MergeSha = new Output<string?>(mergeShaVar)
        };

        builder.Root = new Sequence
        {
            Activities =
            {
                mergePr,
                new SetVariable
                {
                    Id = "SetMergeSuccess",
                    Variable = successVar,
                    Value = new Input<object?>(ctx => (object)!string.IsNullOrEmpty(mergeShaVar.Get(ctx)))
                },
                new SetOutput
                {
                    OutputName = new("success"),
                    OutputValue = new(ctx => (object)successVar.Get(ctx))
                },
                new SetOutput
                {
                    OutputName = new("mergeSha"),
                    OutputValue = new(ctx => (object)(mergeShaVar.Get(ctx) ?? ""))
                }
            }
        };
    }
}
