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
/// Issue Selection sub-workflow: queries GitHub for the next unassigned issue
/// matching configured labels and assigns it to the bot.
///
/// Inputs: repository, issueLabels, botAssignee
/// Outputs: success, issueJson, issueNumber, issueTitle
/// </summary>
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
            Id = "SelectIssue",
            Repository = new Input<string>(ctx => ctx.GetInput<string>("repository") ?? repositoryVar.Get(ctx)),
            IssueLabels = new Input<string[]>(ctx => ctx.GetInput<string[]>("issueLabels") ?? issueLabelsVar.Get(ctx)),
            BotAssignee = new Input<string>(ctx => ctx.GetInput<string>("botAssignee") ?? botAssigneeVar.Get(ctx)),
            IssueJson = new Output<string?>(issueJsonVar),
            IssueNumber = new Output<int>(issueNumberVar),
            IssueTitle = new Output<string?>(issueTitleVar)
        };

        builder.Root = new Sequence
        {
            Activities =
            {
                selectIssue,
                new SetOutput
                {
                    OutputName = new("success"),
                    OutputValue = new(ctx => (object)(issueNumberVar.Get(ctx) > 0))
                },
                new SetOutput
                {
                    OutputName = new("issueJson"),
                    OutputValue = new(ctx => (object)(issueJsonVar.Get(ctx) ?? ""))
                },
                new SetOutput
                {
                    OutputName = new("issueNumber"),
                    OutputValue = new(ctx => (object)issueNumberVar.Get(ctx))
                },
                new SetOutput
                {
                    OutputName = new("issueTitle"),
                    OutputValue = new(ctx => (object)(issueTitleVar.Get(ctx) ?? ""))
                }
            }
        };
    }
}
