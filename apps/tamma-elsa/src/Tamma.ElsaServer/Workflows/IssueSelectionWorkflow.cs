using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Contracts;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Tamma.Activities.ADL;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

namespace Tamma.ElsaServer.Workflows;

public class IssueSelectionWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Issue Selection";
        builder.DefinitionId = "issue-selection";
        builder.Description = "Select and assign the next GitHub issue for autonomous development";

        var repositoryVar = builder.WithVariable<string>("Repository", "");
        var issueLabelsVar = builder.WithVariable<string[]>("IssueLabels", Array.Empty<string>());
        var botAssigneeVar = builder.WithVariable<string>("BotAssignee", "tamma-bot");
        var issueJsonVar = builder.WithVariable<string>("IssueJson", "");
        var issueNumberVar = builder.WithVariable<int>("IssueNumber", 0);
        var issueTitleVar = builder.WithVariable<string>("IssueTitle", "");

        var selectIssue = new SelectIssueActivity
        {
            Id = "SelectIssue", Name = "Select Issue",
            Repository = new Input<string>(ctx => ctx.GetInput<string>("repository") ?? repositoryVar.Get(ctx)),
            IssueLabels = new Input<string[]>(ctx => ctx.GetInput<string[]>("issueLabels") ?? issueLabelsVar.Get(ctx)),
            BotAssignee = new Input<string>(ctx => ctx.GetInput<string>("botAssignee") ?? botAssigneeVar.Get(ctx)),
            IssueJson = new Output<string?>(issueJsonVar),
            IssueNumber = new Output<int>(issueNumberVar),
            IssueTitle = new Output<string?>(issueTitleVar)
        };

        var outputSuccess = new SetOutput { Id = "OutputSuccess", Name = "Output Success", OutputName = new("success"), OutputValue = new(ctx => (object)(issueNumberVar.Get(ctx) > 0)) };
        var outputIssueJson = new SetOutput { Id = "OutputIssueJson", Name = "Output Issue JSON", OutputName = new("issueJson"), OutputValue = new(ctx => (object)(issueJsonVar.Get(ctx) ?? "")) };
        var outputIssueNumber = new SetOutput { Id = "OutputIssueNumber", Name = "Output Issue Number", OutputName = new("issueNumber"), OutputValue = new(ctx => (object)issueNumberVar.Get(ctx)) };
        var outputIssueTitle = new SetOutput { Id = "OutputIssueTitle", Name = "Output Issue Title", OutputName = new("issueTitle"), OutputValue = new(ctx => (object)(issueTitleVar.Get(ctx) ?? "")) };

        builder.Root = new Flowchart
        {
            Id = "IssueSelectionFlowchart",
            Start = selectIssue,
            Activities = { selectIssue, outputSuccess, outputIssueJson, outputIssueNumber, outputIssueTitle },
            Connections =
            {
                Connect(selectIssue, outputSuccess),
                Connect(outputSuccess, outputIssueJson),
                Connect(outputIssueJson, outputIssueNumber),
                Connect(outputIssueNumber, outputIssueTitle)
            }
        };
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));
}
