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
            Id = "MergePR", Name = "Merge PR",
            Repository = new Input<string>(ctx => ctx.GetInput<string>("repository") ?? ""),
            PrNumber = new Input<int>(ctx => ctx.GetInput<int>("prNumber")),
            IssueNumber = new Input<int>(ctx => ctx.GetInput<int>("issueNumber")),
            BranchName = new Input<string>(ctx => ctx.GetInput<string>("branchName") ?? ""),
            MergeSha = new Output<string?>(mergeShaVar)
        };

        var setSuccess = new SetVariable { Id = "SetMergeSuccess", Name = "Set Success", Variable = successVar, Value = new Input<object?>(ctx => (object)!string.IsNullOrEmpty(mergeShaVar.Get(ctx))) };
        var outputSuccess = new SetOutput { Id = "OutputSuccess", Name = "Output Success", OutputName = new("success"), OutputValue = new(ctx => (object)successVar.Get(ctx)) };
        var outputMergeSha = new SetOutput { Id = "OutputMergeSha", Name = "Output Merge SHA", OutputName = new("mergeSha"), OutputValue = new(ctx => (object)(mergeShaVar.Get(ctx) ?? "")) };

        builder.Root = new Flowchart
        {
            Id = "MergeFlowchart",
            Start = mergePr,
            Activities = { mergePr, setSuccess, outputSuccess, outputMergeSha },
            Connections =
            {
                Connect(mergePr, setSuccess),
                Connect(setSuccess, outputSuccess),
                Connect(outputSuccess, outputMergeSha)
            }
        };
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));
}
