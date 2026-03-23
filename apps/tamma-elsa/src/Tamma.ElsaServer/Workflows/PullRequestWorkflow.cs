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
/// Pull Request sub-workflow: creates a PR with the implementation plan and test summary.
///
/// Inputs: repository, branchName, baseBranch, issueNumber, issueTitle, planJson
/// Outputs: success, prNumber, prUrl
/// </summary>
public class PullRequestWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Pull Request";
        builder.DefinitionId = "pull-request";
        builder.Description = "Create a pull request with plan and test summary";

        var prNumberVar = builder.WithVariable<int>("PrNumber", 0);
        var prUrlVar = builder.WithVariable<string>("PrUrl", "");

        var createPr = new CreatePullRequestActivity
        {
            Id = "CreatePR",
            Repository = new Input<string>(ctx => ctx.GetInput<string>("repository") ?? ""),
            BranchName = new Input<string>(ctx => ctx.GetInput<string>("branchName") ?? ""),
            BaseBranch = new Input<string>(ctx => ctx.GetInput<string>("baseBranch") ?? "main"),
            IssueNumber = new Input<int>(ctx => ctx.GetInput<int>("issueNumber")),
            IssueTitle = new Input<string>(ctx => ctx.GetInput<string>("issueTitle") ?? ""),
            PlanJson = new Input<string?>(ctx => ctx.GetInput<string>("planJson")),
            PrNumber = new Output<int>(prNumberVar),
            PrUrl = new Output<string?>(prUrlVar)
        };

        builder.Root = new Sequence
        {
            Activities =
            {
                createPr,
                new SetOutput
                {
                    OutputName = new("success"),
                    OutputValue = new(ctx => (object)(prNumberVar.Get(ctx) > 0))
                },
                new SetOutput
                {
                    OutputName = new("prNumber"),
                    OutputValue = new(ctx => (object)prNumberVar.Get(ctx))
                },
                new SetOutput
                {
                    OutputName = new("prUrl"),
                    OutputValue = new(ctx => (object)(prUrlVar.Get(ctx) ?? ""))
                }
            }
        };
    }
}
