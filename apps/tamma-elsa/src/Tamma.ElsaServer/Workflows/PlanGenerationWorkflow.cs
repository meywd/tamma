using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using Tamma.Api.Services.Agents;
using Tamma.ElsaServer.Workflows.Helpers;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Plan Generation — architect LLM produces an implementation blueprint.
///
/// Prompts come from the prompt registry (role=architect, action=plan-system-design).
/// No inline prompts. No approval step (approval is in SingleIssueCycle).
///
/// Validates the plan has required fields. Retries on invalid (max 2).
///
/// Flow:
///   Init → Generate Plan (llm-call) → Validate → Valid?
///     ├─ Yes → Output → Finish
///     └─ No → Retry? → Yes → feed errors back → Generate Plan
///                     → No → Error Output → Finish
/// </summary>
public class PlanGenerationWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Plan Generation";
        builder.DefinitionId = "plan-generation";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Architect LLM generates implementation plan via prompt registry";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var issueNumber = builder.WithVariable<int>("IssueNumber", 0);
        var poSummary = builder.WithVariable<string>("POSummary", "");
        var contextIds = builder.WithVariable<string>("ContextIds", "[]");
        var workItemJson = builder.WithVariable<string>("WorkItemJson", "");
        var reviewNotes = builder.WithVariable<string>("ReviewNotes", "");
        var revisionNumber = builder.WithVariable<int>("RevisionNumber", 0);
        var tenantId = builder.WithVariable<string>("TenantId", "");

        var planJson = builder.WithVariable<string>("PlanJson", "");
        var planValid = builder.WithVariable<bool>("PlanValid", false);
        var validationErrors = builder.WithVariable<string>("ValidationErrors", "");
        var retryCount = builder.WithVariable<int>("RetryCount", 0);
        var maxRetries = builder.WithVariable<int>("MaxRetries", 2);

        var llmResult = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // 1. Init
        // ================================================================
        var init = new SetVariable
        {
            Id = "Init", Name = "Initialize",
            Variable = repository,
            Value = new Input<object?>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                issueNumber.Set(ctx, ctx.GetInput<int>("issueNumber"));
                poSummary.Set(ctx, ctx.GetInput<string>("poSummary") ?? "");
                contextIds.Set(ctx, ctx.GetInput<string>("contextIds") ?? "[]");
                workItemJson.Set(ctx, ctx.GetInput<string>("workItemJson") ?? "");
                reviewNotes.Set(ctx, ctx.GetInput<string>("reviewNotes") ?? "");
                revisionNumber.Set(ctx, ctx.GetInput<int>("revisionNumber"));
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                var inputMaxRetries = ctx.GetInput<int?>("maxRetries");
                if (inputMaxRetries.HasValue) maxRetries.Set(ctx, inputMaxRetries.Value);
                return (object)repo;
            })
        };
        init.SetDisplayText("Initialize");

        // ================================================================
        // 2. Generate Plan (via LlmCallWorkflow — prompt from registry)
        // ================================================================
        var generatePlan = new DispatchWorkflow
        {
            Id = "GeneratePlan", Name = "Generate Plan",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = AgentRole.Architect.ToWire(),
                ["action"] = AgentAction.PlanSystemDesign.ToWire(),
                ["tenantId"] = tenantId.Get(ctx),
                ["variables"] = new Dictionary<string, object>
                {
                    ["workItemJson"] = workItemJson.Get(ctx),
                    // Retry feedback is merged INTO contextFindings — a variable the
                    // Plan-family template actually declares ({{contextFindings}}).
                    // A separate "validationErrors" key is undeclared in the template
                    // and was silently dropped at render, so retries re-prompted
                    // blind. First attempt (no errors) passes poSummary unchanged.
                    ["contextFindings"] = ValidationFeedbackHelper.AppendFeedback(
                        poSummary.Get(ctx), validationErrors.Get(ctx)),
                    ["poSummary"] = poSummary.Get(ctx),
                    ["contextIds"] = contextIds.Get(ctx),
                    ["repository"] = repository.Get(ctx),
                    ["reviewNotes"] = reviewNotes.Get(ctx),
                    ["revisionNumber"] = revisionNumber.Get(ctx),
                },
                ["enableTools"] = true,
            }),
            WaitForCompletion = new(true),
            Result = new(llmResult),
        };
        generatePlan.SetDisplayText("Generate Plan");

        // ================================================================
        // 3. Extract + Validate
        // ================================================================
        var extractAndValidate = new SetVariable
        {
            Id = "ExtractValidate", Name = "Extract & Validate",
            Variable = planJson,
            Value = new Input<object?>(ctx =>
            {
                var result = llmResult.Get(ctx);
                var output = "";
                if (result != null && result.TryGetValue("llmResponse", out var r))
                    output = r?.ToString() ?? "";

                var (json, isValid, errors) = PlanValidationHelper.ValidatePlan(output);
                planValid.Set(ctx, isValid);
                validationErrors.Set(ctx, errors);
                return (object)json;
            })
        };
        extractAndValidate.SetDisplayText("Extract & Validate");

        // ================================================================
        // 4. Valid?
        // ================================================================
        var isValid = new FlowDecision(ctx => planValid.Get(ctx))
        { Id = "PlanValid", Name = "Plan Valid?" };
        isValid.SetDisplayText("Plan Valid?");

        // ================================================================
        // 5. Retry logic
        // ================================================================
        var incrementRetry = new SetVariable
        {
            Id = "IncrRetry", Name = "Increment Retry",
            Variable = retryCount,
            Value = new Input<object?>(ctx => (object)(retryCount.Get(ctx) + 1))
        };
        incrementRetry.SetDisplayText("Increment Retry");

        var canRetry = new FlowDecision(ctx => retryCount.Get(ctx) < maxRetries.Get(ctx))
        { Id = "CanRetry", Name = "Can Retry?" };
        canRetry.SetDisplayText("Can Retry?");

        // ================================================================
        // 6. Outputs
        // ================================================================
        var setOutputs = new SetOutput
        { Id = "OutPlan", OutputName = new("planJson"), OutputValue = new(ctx => (object)planJson.Get(ctx)) };
        setOutputs.SetDisplayText("Output Plan");

        var setErrorOutputs = new Sequence
        {
            Id = "SetErrorOutputs", Name = "Error Outputs",
            Activities =
            {
                new SetOutput { Id = "OutErrPlan", OutputName = new("planJson"), OutputValue = new(_ => (object)"") },
                new SetOutput { Id = "OutErr", OutputName = new("error"), OutputValue = new(ctx => (object)validationErrors.Get(ctx)) },
            }
        };
        setErrorOutputs.SetDisplayText("Error Outputs");

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "PlanGenerationFlowchart",
            Start = init,
            Activities =
            {
                init, generatePlan, extractAndValidate, isValid,
                incrementRetry, canRetry,
                setOutputs, setErrorOutputs, finish,
            },
            Connections =
            {
                Connect(init, generatePlan),
                Connect(generatePlan, extractAndValidate),
                Connect(extractAndValidate, isValid),

                ConnectOutcome(isValid, "True", setOutputs),
                Connect(setOutputs, finish),

                ConnectOutcome(isValid, "False", incrementRetry),
                Connect(incrementRetry, canRetry),

                ConnectOutcome(canRetry, "True", generatePlan), // retry
                ConnectOutcome(canRetry, "False", setErrorOutputs), // give up
                Connect(setErrorOutputs, finish),
            }
        };
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
