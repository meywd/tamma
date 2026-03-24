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
            Id = "WaitMergeApproval", Name = "Wait Merge Approval",
            IssueNumber = new Input<int>(ctx => ctx.GetInput<int>("issueNumber")),
            PrNumber = new Input<int>(ctx => ctx.GetInput<int>("prNumber")),
            PrUrl = new Input<string?>(ctx => ctx.GetInput<string>("prUrl")),
            Decision = new Output<string?>(decisionVar),
            Feedback = new Output<string?>(feedbackVar)
        };
        waitMerge.SetDisplayText("Wait Merge Approval");

        var outputDecision = new SetOutput { Id = "OutputDecision", Name = "Output Decision", OutputName = new("decision"), OutputValue = new(ctx => (object)(decisionVar.Get(ctx) ?? "reject")) };
        outputDecision.SetDisplayText("Output Decision");
        var outputFeedback = new SetOutput { Id = "OutputFeedback", Name = "Output Feedback", OutputName = new("feedback"), OutputValue = new(ctx => (object)(feedbackVar.Get(ctx) ?? "")) };
        outputFeedback.SetDisplayText("Output Feedback");

        builder.Root = new Flowchart
        {
            Id = "MergeApprovalFlowchart",
            Name = "Merge Approval Flowchart",
            Start = waitMerge,
            Activities = { waitMerge, outputDecision, outputFeedback },
            Connections =
            {
                Connect(waitMerge, outputDecision),
                Connect(outputDecision, outputFeedback)
            }
        };
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));
}
