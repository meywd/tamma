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
/// Plan Generation sub-workflow: generates an AI implementation plan
/// and waits for human approval via bookmark.
///
/// Inputs: issueNumber, issueTitle, issueBody, contextJson, repository
/// Outputs: approved, planJson, editedPlanJson
///
/// Flow:
///   1. Dispatch llm-call to generate plan
///   2. Wait for plan approval (bookmark)
///   3. If EditRequested → loop back to step 1 with feedback
///   4. Set outputs
/// </summary>
public class PlanGenerationWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Plan Generation";
        builder.DefinitionId = "plan-generation";
        builder.Description = "Generate AI plan and wait for human approval";

        var issueNumberVar = builder.WithVariable<int>("IssueNumber", 0);
        var issueTitleVar = builder.WithVariable<string>("IssueTitle", "");
        var issueBodyVar = builder.WithVariable<string>("IssueBody", "");
        var contextJsonVar = builder.WithVariable<string>("ContextJson", "");
        var repositoryVar = builder.WithVariable<string>("Repository", "");
        var planJsonVar = builder.WithVariable<string>("PlanJson", "");
        var approvedVar = builder.WithVariable<bool>("Approved", false);
        var editedPlanJsonVar = builder.WithVariable<string>("EditedPlanJson", "");
        var llmResultVar = builder.WithVariable<IDictionary<string, object>?>();
        var planLoopVar = builder.WithVariable<bool>("PlanLoop", true);
        var feedbackVar = builder.WithVariable<string>("Feedback", "");

        // Initialize from inputs
        var initVars = new SetVariable
        {
            Id = "InitPlanVars",
            Variable = issueNumberVar,
            Value = new Input<object?>(ctx =>
            {
                issueTitleVar.Set(ctx, ctx.GetInput<string>("issueTitle") ?? "");
                issueBodyVar.Set(ctx, ctx.GetInput<string>("issueBody") ?? "");
                contextJsonVar.Set(ctx, ctx.GetInput<string>("contextJson") ?? "");
                repositoryVar.Set(ctx, ctx.GetInput<string>("repository") ?? "");
                return (object)ctx.GetInput<int>("issueNumber");
            })
        };

        // LLM call to generate plan
        var generatePlan = new DispatchWorkflow
        {
            Id = "DispatchPlanGeneration",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["agentRole"] = "analyst",
                ["taskPrompt"] = BuildPlanPrompt(
                    issueTitleVar.Get(ctx),
                    issueBodyVar.Get(ctx),
                    contextJsonVar.Get(ctx),
                    feedbackVar.Get(ctx)),
                ["sessionId"] = $"adl-plan-{issueNumberVar.Get(ctx)}"
            }),
            WaitForCompletion = new(true),
            Result = new(llmResultVar)
        };

        // Extract plan from LLM response
        var extractPlan = new SetVariable
        {
            Id = "ExtractPlan",
            Variable = planJsonVar,
            Value = new Input<object?>(ctx =>
            {
                var result = llmResultVar.Get(ctx);
                if (result != null && result.TryGetValue("llmResponse", out var resp))
                    return resp?.ToString() ?? "{}";
                return "{}";
            })
        };

        // Wait for approval
        var waitApproval = new WaitForPlanApprovalActivity
        {
            Id = "WaitPlanApproval",
            IssueNumber = new Input<int>(ctx => issueNumberVar.Get(ctx)),
            PlanJson = new Input<string>(ctx => planJsonVar.Get(ctx)),
            ApprovalResultJson = new Output<string?>(new Variable<string>()),
            EditedPlanJson = new Output<string?>(editedPlanJsonVar)
        };

        // The While loop handles edit-requested cycles
        var planLoop = new While(ctx => planLoopVar.Get(ctx))
        {
            Id = "PlanApprovalLoop",
            Body = new Sequence
            {
                Activities =
                {
                    generatePlan,
                    extractPlan,
                    waitApproval,
                    // After approval decision, check if we need to loop
                    new SetVariable
                    {
                        Id = "CheckApprovalDecision",
                        Variable = planLoopVar,
                        Value = new Input<object?>(ctx =>
                        {
                            var edited = editedPlanJsonVar.Get(ctx);
                            if (!string.IsNullOrEmpty(edited))
                            {
                                // Edit requested — set feedback and loop
                                feedbackVar.Set(ctx, $"User requested edits. Previous plan feedback: {edited}");
                                return (object)true;
                            }
                            // Approved or Rejected — stop loop
                            planLoopVar.Set(ctx, false);
                            return (object)false;
                        })
                    }
                }
            }
        };

        builder.Root = new Sequence
        {
            Activities =
            {
                initVars,
                planLoop,
                new SetOutput
                {
                    OutputName = new("approved"),
                    OutputValue = new(ctx => (object)(!string.IsNullOrEmpty(planJsonVar.Get(ctx))))
                },
                new SetOutput
                {
                    OutputName = new("planJson"),
                    OutputValue = new(ctx => (object)(planJsonVar.Get(ctx) ?? "{}"))
                }
            }
        };
    }

    private static string BuildPlanPrompt(string title, string body, string context, string feedback)
    {
        var prompt = $"Generate a detailed implementation plan for the following GitHub issue:\n\n" +
                     $"**Title:** {title}\n" +
                     $"**Description:** {body}\n\n";

        if (!string.IsNullOrEmpty(context))
            prompt += $"**Context:** {context}\n\n";

        if (!string.IsNullOrEmpty(feedback))
            prompt += $"**Previous Feedback:** {feedback}\n\n";

        prompt += "Respond with a JSON object containing: summary, steps (array), " +
                  "filesToModify (array), filesToCreate (array), testStrategy, estimatedComplexity.";

        return prompt;
    }
}
