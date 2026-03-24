using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Tamma.Activities.ADL;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Branch Creation sub-workflow: creates a feature branch for the issue.
///
/// Flow: CreateBranch → SetSuccess → OutputSuccess → OutputBranchName
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
            Name = "Create Branch",
            Repository = new Input<string>(ctx => ctx.GetInput<string>("repository") ?? ""),
            IssueNumber = new Input<int>(ctx => ctx.GetInput<int>("issueNumber")),
            IssueTitle = new Input<string>(ctx => ctx.GetInput<string>("issueTitle") ?? ""),
            BranchName = new Output<string?>(branchNameVar)
        };
        createBranch.SetDisplayText("Create Branch");

        var setSuccess = new SetVariable
        {
            Id = "SetBranchSuccess",
            Name = "Set Success",
            Variable = successVar,
            Value = new Input<object?>(ctx => (object)!string.IsNullOrEmpty(branchNameVar.Get(ctx)))
        };
        setSuccess.SetDisplayText("Set Success");

        var outputSuccess = new SetOutput
        {
            Id = "OutputSuccess",
            Name = "Output Success",
            OutputName = new("success"),
            OutputValue = new(ctx => (object)successVar.Get(ctx))
        };
        outputSuccess.SetDisplayText("Output Success");

        var outputBranchName = new SetOutput
        {
            Id = "OutputBranchName",
            Name = "Output Branch Name",
            OutputName = new("branchName"),
            OutputValue = new(ctx => (object)(branchNameVar.Get(ctx) ?? ""))
        };
        outputBranchName.SetDisplayText("Output Branch Name");

        builder.Root = new Flowchart
        {
            Id = "BranchCreationFlowchart",
            Name = "Branch Creation Flowchart",
            Start = createBranch,
            Activities = { createBranch, setSuccess, outputSuccess, outputBranchName },
            Connections =
            {
                Connect(createBranch, setSuccess),
                Connect(setSuccess, outputSuccess),
                Connect(outputSuccess, outputBranchName)
            }
        };
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));
}
