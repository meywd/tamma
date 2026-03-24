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
            Id = "CreatePR", Name = "Create PR",
            Repository = new Input<string>(ctx => ctx.GetInput<string>("repository") ?? ""),
            BranchName = new Input<string>(ctx => ctx.GetInput<string>("branchName") ?? ""),
            BaseBranch = new Input<string>(ctx => ctx.GetInput<string>("baseBranch") ?? "main"),
            IssueNumber = new Input<int>(ctx => ctx.GetInput<int>("issueNumber")),
            IssueTitle = new Input<string>(ctx => ctx.GetInput<string>("issueTitle") ?? ""),
            PlanJson = new Input<string?>(ctx => ctx.GetInput<string>("planJson")),
            PrNumber = new Output<int>(prNumberVar),
            PrUrl = new Output<string?>(prUrlVar)
        };

        var outputSuccess = new SetOutput { Id = "OutputSuccess", Name = "Output Success", OutputName = new("success"), OutputValue = new(ctx => (object)(prNumberVar.Get(ctx) > 0)) };
        var outputPrNumber = new SetOutput { Id = "OutputPrNumber", Name = "Output PR Number", OutputName = new("prNumber"), OutputValue = new(ctx => (object)prNumberVar.Get(ctx)) };
        var outputPrUrl = new SetOutput { Id = "OutputPrUrl", Name = "Output PR URL", OutputName = new("prUrl"), OutputValue = new(ctx => (object)(prUrlVar.Get(ctx) ?? "")) };

        builder.Root = new Flowchart
        {
            Id = "PullRequestFlowchart",
            Name = "Pull Request Flowchart",
            Start = createPr,
            Activities = { createPr, outputSuccess, outputPrNumber, outputPrUrl },
            Connections =
            {
                Connect(createPr, outputSuccess),
                Connect(outputSuccess, outputPrNumber),
                Connect(outputPrNumber, outputPrUrl)
            }
        };
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));
}
