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
/// Merge Approval sub-workflow: suspends and waits for a human to decide
/// whether to merge, run additional tests, or reject the PR.
///
/// Inputs: issueNumber, prNumber, prUrl
/// Outputs: decision (merge|test|reject), feedback
/// </summary>
public class MergeApprovalWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Merge Approval";
        builder.DefinitionId = "merge-approval";
        builder.Description = "Wait for human merge/test/reject decision";

        var decisionVar = builder.WithVariable<string>("Decision", "");
        var feedbackVar = builder.WithVariable<string>("Feedback", "");

        var waitMerge = new WaitForMergeApprovalActivity
        {
            Id = "WaitMergeApproval",
            IssueNumber = new Input<int>(ctx => ctx.GetInput<int>("issueNumber")),
            PrNumber = new Input<int>(ctx => ctx.GetInput<int>("prNumber")),
            PrUrl = new Input<string?>(ctx => ctx.GetInput<string>("prUrl")),
            Decision = new Output<string?>(decisionVar),
            Feedback = new Output<string?>(feedbackVar)
        };

        builder.Root = new Sequence
        {
            Activities =
            {
                waitMerge,
                new SetOutput
                {
                    OutputName = new("decision"),
                    OutputValue = new(ctx => (object)(decisionVar.Get(ctx) ?? "reject"))
                },
                new SetOutput
                {
                    OutputName = new("feedback"),
                    OutputValue = new(ctx => (object)(feedbackVar.Get(ctx) ?? ""))
                }
            }
        };
    }
}
