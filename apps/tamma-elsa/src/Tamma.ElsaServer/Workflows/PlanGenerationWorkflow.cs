using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.ADL;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Plan Generation sub-workflow: generates an AI implementation plan
/// and waits for human approval via bookmark.
///
/// Flow: InitVars → PlanApprovalLoop (While: Generate → Extract → WaitApproval → Check) → OutputApproved → OutputPlanJson
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
        var editedPlanJsonVar = builder.WithVariable<string>("EditedPlanJson", "");
        var llmResultVar = builder.WithVariable<IDictionary<string, object>?>();
        var planLoopVar = builder.WithVariable<bool>("PlanLoop", true);
        var feedbackVar = builder.WithVariable<string>("Feedback", "");

        var initVars = new SetVariable
        {
            Id = "InitPlanVars", Name = "Init Variables",
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
        initVars.SetDisplayText("Init Variables");

        var planLoop = new While(ctx => planLoopVar.Get(ctx))
        {
            Id = "PlanApprovalLoop", Name = "Plan Approval Loop",
            Body = new Sequence
            {
                Id = "PlanLoopBody", Name = "Plan Loop Body",
                Activities =
                {
                    WithLabel(new DispatchWorkflow
                    {
                        Id = "DispatchPlanGeneration", Name = "Generate Plan via LLM",
                        WorkflowDefinitionId = new("llm-call"),
                        Input = new(ctx => new Dictionary<string, object>
                        {
                            ["agentRole"] = "analyst",
                            ["taskPrompt"] = BuildPlanPrompt(issueTitleVar.Get(ctx), issueBodyVar.Get(ctx), contextJsonVar.Get(ctx), feedbackVar.Get(ctx)),
                            ["sessionId"] = $"adl-plan-{issueNumberVar.Get(ctx)}"
                        }),
                        WaitForCompletion = new(true),
                        Result = new(llmResultVar)
                    }, "Generate Plan via LLM"),
                    WithLabel(new SetVariable
                    {
                        Id = "ExtractPlan", Name = "Extract Plan",
                        Variable = planJsonVar,
                        Value = new Input<object?>(ctx =>
                        {
                            var result = llmResultVar.Get(ctx);
                            if (result != null && result.TryGetValue("llmResponse", out var resp))
                                return resp?.ToString() ?? "{}";
                            return "{}";
                        })
                    }, "Extract Plan"),
                    WithLabel(new WaitForPlanApprovalActivity
                    {
                        Id = "WaitPlanApproval", Name = "Wait for Plan Approval",
                        IssueNumber = new Input<int>(ctx => issueNumberVar.Get(ctx)),
                        PlanJson = new Input<string>(ctx => planJsonVar.Get(ctx)),
                        ApprovalResultJson = new Output<string?>(new Variable<string>()),
                        EditedPlanJson = new Output<string?>(editedPlanJsonVar)
                    }, "Wait for Plan Approval"),
                    WithLabel(new SetVariable
                    {
                        Id = "CheckApprovalDecision", Name = "Check Approval Decision",
                        Variable = planLoopVar,
                        Value = new Input<object?>(ctx =>
                        {
                            var edited = editedPlanJsonVar.Get(ctx);
                            if (!string.IsNullOrEmpty(edited))
                            {
                                feedbackVar.Set(ctx, $"User requested edits. Previous plan feedback: {edited}");
                                return (object)true;
                            }
                            planLoopVar.Set(ctx, false);
                            return (object)false;
                        })
                    }, "Check Approval Decision")
                }
            }
        };
        planLoop.SetDisplayText("Plan Approval Loop");

        var outputApproved = new SetOutput { Id = "OutputApproved", Name = "Output Approved", OutputName = new("approved"), OutputValue = new(ctx => (object)(!string.IsNullOrEmpty(planJsonVar.Get(ctx)))) };
        outputApproved.SetDisplayText("Output Approved");
        var outputPlanJson = new SetOutput { Id = "OutputPlanJson", Name = "Output Plan JSON", OutputName = new("planJson"), OutputValue = new(ctx => (object)(planJsonVar.Get(ctx) ?? "{}")) };
        outputPlanJson.SetDisplayText("Output Plan JSON");

        builder.Root = new Flowchart
        {
            Id = "PlanGenerationFlowchart",
            Start = initVars,
            Activities = { initVars, planLoop, outputApproved, outputPlanJson },
            Connections =
            {
                Connect(initVars, planLoop),
                Connect(planLoop, outputApproved),
                Connect(outputApproved, outputPlanJson)
            }
        };
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

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
