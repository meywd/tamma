using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.ADL;
using Tamma.Activities.CodeIndex;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

namespace Tamma.ElsaServer.Workflows;

public class ReviewFixWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Review Fix";
        builder.DefinitionId = "review-fix";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Analyze PR review comments and apply AI-generated fixes";

        var hasActionableVar = builder.WithVariable<bool>("HasActionable", false);
        var analysisJsonVar = builder.WithVariable<string>("AnalysisJson", "");
        var fixesAppliedVar = builder.WithVariable<bool>("FixesApplied", false);
        var llmResultVar = builder.WithVariable<IDictionary<string, object>?>();

        var analyze = new AnalyzeReviewActivity
        {
            Id = "AnalyzeReview", Name = "Analyze Review",
            Repository = new Input<string>(ctx => ctx.GetInput<string>("repository") ?? ""),
            PrNumber = new Input<int>(ctx => ctx.GetInput<int>("prNumber")),
            HasActionableComments = new Output<bool>(hasActionableVar),
            AnalysisJson = new Output<string?>(analysisJsonVar)
        };
        analyze.SetDisplayText("Analyze Review");

        var hasActionable = new FlowDecision(ctx => hasActionableVar.Get(ctx))
        { Id = "HasActionable", Name = "Has Actionable?" };
        hasActionable.SetDisplayText("Has Actionable?");

        var generateFixes = new DispatchWorkflow
        {
            Id = "DispatchFixGeneration", Name = "Generate Fixes",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["agentRole"] = "implementer",
                ["taskPrompt"] = $"Apply fixes for the following review comments:\n{analysisJsonVar.Get(ctx)}",
                ["sessionId"] = $"adl-review-fix-{ctx.GetInput<int>("prNumber")}"
            }),
            WaitForCompletion = new(true),
            Result = new(llmResultVar)
        };
        generateFixes.SetDisplayText("Generate Fixes");

        var applyFixes = new ApplyReviewFixesActivity
        {
            Id = "ApplyFixes", Name = "Apply Fixes",
            AnalysisJson = new Input<string>(ctx => analysisJsonVar.Get(ctx)),
            Repository = new Input<string>(ctx => ctx.GetInput<string>("repository") ?? ""),
            BranchName = new Input<string>(ctx => ctx.GetInput<string>("branchName") ?? ""),
            FixesApplied = new Output<bool>(fixesAppliedVar)
        };
        applyFixes.SetDisplayText("Apply Fixes");

        // ApplyReviewFixesActivity only outputs FixesApplied (bool), no file paths —
        // pass null so the indexer falls back to git-diff detection.
        var updateCodeIndex = new UpdateCodeIndexActivity
        {
            Id = "UpdateCodeIndex",
            Name = "Update Code Index",
            ChangedFilesJson = new Input<string?>(ctx => (string?)null),
            RepositoryPath = new Input<string?>(ctx => ctx.GetInput<string>("repository"))
        };
        updateCodeIndex.SetDisplayText("Update Code Index");

        var outputSuccess = new SetOutput { Id = "OutputSuccess", Name = "Output Success", OutputName = new("success"), OutputValue = new(ctx => (object)true) };
        outputSuccess.SetDisplayText("Output Success");
        var outputHasComments = new SetOutput { Id = "OutputHasComments", Name = "Output Has Comments", OutputName = new("hasComments"), OutputValue = new(ctx => (object)hasActionableVar.Get(ctx)) };
        outputHasComments.SetDisplayText("Output Has Comments");
        var outputFixesApplied = new SetOutput { Id = "OutputFixesApplied", Name = "Output Fixes Applied", OutputName = new("fixesApplied"), OutputValue = new(ctx => (object)fixesAppliedVar.Get(ctx)) };
        outputFixesApplied.SetDisplayText("Output Fixes Applied");

        builder.Root = new Flowchart
        {
            Id = "ReviewFixFlowchart",
            Name = "Review Fix Flowchart",
            Start = analyze,
            Activities = { analyze, hasActionable, generateFixes, applyFixes, updateCodeIndex, outputSuccess, outputHasComments, outputFixesApplied },
            Connections =
            {
                Connect(analyze, hasActionable),
                ConnectOutcome(hasActionable, "True", generateFixes),
                Connect(generateFixes, applyFixes),
                Connect(applyFixes, updateCodeIndex),
                Connect(updateCodeIndex, outputSuccess),
                ConnectOutcome(hasActionable, "False", outputSuccess),
                Connect(outputSuccess, outputHasComments),
                Connect(outputHasComments, outputFixesApplied)
            }
        };
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
