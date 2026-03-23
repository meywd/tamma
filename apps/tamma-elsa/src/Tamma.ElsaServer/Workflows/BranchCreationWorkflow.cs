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
/// Branch Creation sub-workflow: creates a feature branch for the issue.
///
/// Inputs: repository, issueNumber, issueTitle
/// Outputs: success, branchName
/// </summary>
public class BranchCreationWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Branch Creation";
        builder.DefinitionId = "branch-creation";
        builder.Description = "Create a feature branch for autonomous development";

        var branchNameVar = builder.WithVariable<string>("BranchName", "");
        var successVar = builder.WithVariable<bool>("Success", false);

        var createBranch = new CreateBranchActivity
        {
            Id = "CreateBranch",
            Repository = new Input<string>(ctx => ctx.GetInput<string>("repository") ?? ""),
            IssueNumber = new Input<int>(ctx => ctx.GetInput<int>("issueNumber")),
            IssueTitle = new Input<string>(ctx => ctx.GetInput<string>("issueTitle") ?? ""),
            BranchName = new Output<string?>(branchNameVar)
        };

        builder.Root = new Sequence
        {
            Activities =
            {
                createBranch,
                new SetVariable
                {
                    Id = "SetBranchSuccess",
                    Variable = successVar,
                    Value = new Input<object?>(ctx => (object)!string.IsNullOrEmpty(branchNameVar.Get(ctx)))
                },
                new SetOutput
                {
                    OutputName = new("success"),
                    OutputValue = new(ctx => (object)successVar.Get(ctx))
                },
                new SetOutput
                {
                    OutputName = new("branchName"),
                    OutputValue = new(ctx => (object)(branchNameVar.Get(ctx) ?? ""))
                }
            }
        };
    }
}
