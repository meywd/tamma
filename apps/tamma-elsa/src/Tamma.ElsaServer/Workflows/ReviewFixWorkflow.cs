using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Contracts;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.ADL;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Review Fix sub-workflow: fetches review comments, analyzes them via AI,
/// and applies fixes.
///
/// Inputs: repository, prNumber, branchName
/// Outputs: success, hasComments, fixesApplied
/// </summary>
public class ReviewFixWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Review Fix";
        builder.DefinitionId = "review-fix";
        builder.Description = "Analyze PR review comments and apply AI-generated fixes";

        var hasActionableVar = builder.WithVariable<bool>("HasActionable", false);
        var analysisJsonVar = builder.WithVariable<string>("AnalysisJson", "");
        var fixesAppliedVar = builder.WithVariable<bool>("FixesApplied", false);
        var llmResultVar = builder.WithVariable<IDictionary<string, object>?>();

        var analyze = new AnalyzeReviewActivity
        {
            Id = "AnalyzeReview",
            Repository = new Input<string>(ctx => ctx.GetInput<string>("repository") ?? ""),
            PrNumber = new Input<int>(ctx => ctx.GetInput<int>("prNumber")),
            HasActionableComments = new Output<bool>(hasActionableVar),
            AnalysisJson = new Output<string?>(analysisJsonVar)
        };

        // If there are actionable comments, dispatch llm-call to generate fixes
        var generateFixes = new DispatchWorkflow
        {
            Id = "DispatchFixGeneration",
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

        var applyFixes = new ApplyReviewFixesActivity
        {
            Id = "ApplyFixes",
            AnalysisJson = new Input<string>(ctx => analysisJsonVar.Get(ctx)),
            Repository = new Input<string>(ctx => ctx.GetInput<string>("repository") ?? ""),
            BranchName = new Input<string>(ctx => ctx.GetInput<string>("branchName") ?? ""),
            FixesApplied = new Output<bool>(fixesAppliedVar)
        };

        builder.Root = new Sequence
        {
            Activities =
            {
                analyze,
                new If
                {
                    Condition = new(ctx => hasActionableVar.Get(ctx)),
                    Then = new Sequence
                    {
                        Activities = { generateFixes, applyFixes }
                    }
                },
                new SetOutput
                {
                    OutputName = new("success"),
                    OutputValue = new(ctx => (object)true)
                },
                new SetOutput
                {
                    OutputName = new("hasComments"),
                    OutputValue = new(ctx => (object)hasActionableVar.Get(ctx))
                },
                new SetOutput
                {
                    OutputName = new("fixesApplied"),
                    OutputValue = new(ctx => (object)fixesAppliedVar.Get(ctx))
                }
            }
        };
    }
}
