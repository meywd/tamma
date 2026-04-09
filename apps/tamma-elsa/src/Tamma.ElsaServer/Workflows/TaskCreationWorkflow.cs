using System.Text.Json;
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

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Task Creation — Senior dev LLM breaks the approved plan into detailed implementation tasks.
/// Each task includes: files to modify, code changes, test approach, and dependencies (DAG).
///
/// Validates output has a 'tasks' array. Retries on invalid (max 2).
///
/// Flow:
///   Init → Generate Tasks (llm-call) → Extract & Validate → Valid?
///     ├─ Yes → Output → Finish
///     └─ No → Retry? → Yes → feed errors back → Generate Tasks
///                      → No → Error Output → Finish
///
/// Inputs: repository, issueNumber, planJson, contextIds
/// Outputs: tasksJson (JSON array of detailed task plans)
/// </summary>
public class TaskCreationWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Task Creation";
        builder.DefinitionId = "task-creation";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Senior dev LLM breaks plan into deep implementation task plans";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var issueNumber = builder.WithVariable<int>("IssueNumber", 0);
        var planJson = builder.WithVariable<string>("PlanJson", "");
        var contextIds = builder.WithVariable<string>("ContextIds", "[]");
        var workItemJson = builder.WithVariable<string>("WorkItemJson", "");

        var tasksJson = builder.WithVariable<string>("TasksJson", "[]");
        var tasksValid = builder.WithVariable<bool>("TasksValid", false);
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
                planJson.Set(ctx, ctx.GetInput<string>("planJson") ?? "");
                contextIds.Set(ctx, ctx.GetInput<string>("contextIds") ?? "[]");
                workItemJson.Set(ctx, ctx.GetInput<string>("workItemJson") ?? "");
                var inputMaxRetries = ctx.GetInput<int?>("maxRetries");
                if (inputMaxRetries.HasValue) maxRetries.Set(ctx, inputMaxRetries.Value);
                return (object)repo;
            })
        };
        init.SetDisplayText("Initialize");

        // ================================================================
        // 2. Generate Tasks (via LlmCallWorkflow — prompt from registry)
        // ================================================================
        var generateTasks = new DispatchWorkflow
        {
            Id = "GenerateTasks", Name = "Generate Tasks",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = "senior_developer",
                ["action"] = "create-tasks",
                ["variables"] = new Dictionary<string, object>
                {
                    ["planJson"] = planJson.Get(ctx),
                    ["contextIds"] = contextIds.Get(ctx),
                    ["workItemJson"] = workItemJson.Get(ctx),
                    ["repository"] = repository.Get(ctx),
                    ["validationErrors"] = validationErrors.Get(ctx),
                },
                ["enableTools"] = true,
            }),
            WaitForCompletion = new(true),
            Result = new(llmResult),
        };
        generateTasks.SetDisplayText("Generate Tasks");

        // ================================================================
        // 3. Extract + Validate
        // ================================================================
        var extractAndValidate = new SetVariable
        {
            Id = "ExtractValidate", Name = "Extract & Validate",
            Variable = tasksJson,
            Value = new Input<object?>(ctx =>
            {
                var result = llmResult.Get(ctx);
                var output = "";
                if (result != null && result.TryGetValue("llmResponse", out var r))
                    output = r?.ToString() ?? "";

                // Extract JSON — look for array first, then object
                var extracted = "";
                var arrayStart = output.IndexOf('[');
                var arrayEnd = output.LastIndexOf(']');
                if (arrayStart >= 0 && arrayEnd > arrayStart)
                {
                    extracted = output[arrayStart..(arrayEnd + 1)];
                }
                else
                {
                    var jsonStart = output.IndexOf('{');
                    var jsonEnd = output.LastIndexOf('}');
                    if (jsonStart >= 0 && jsonEnd > jsonStart)
                        extracted = output[jsonStart..(jsonEnd + 1)];
                }

                // Validate
                var errors = new List<string>();
                if (string.IsNullOrWhiteSpace(extracted))
                {
                    errors.Add("Empty tasks output");
                }
                else
                {
                    try
                    {
                        var doc = JsonDocument.Parse(extracted);
                        var root = doc.RootElement;

                        if (root.ValueKind == JsonValueKind.Array)
                        {
                            if (root.GetArrayLength() == 0)
                                errors.Add("Tasks array is empty");
                        }
                        else if (root.ValueKind == JsonValueKind.Object)
                        {
                            if (root.TryGetProperty("tasks", out var tasksArr))
                            {
                                if (tasksArr.ValueKind != JsonValueKind.Array || tasksArr.GetArrayLength() == 0)
                                    errors.Add("'tasks' property is empty or not an array");
                                else
                                    extracted = tasksArr.GetRawText(); // normalize to array
                            }
                            else
                            {
                                errors.Add("Missing 'tasks' array in response");
                            }
                        }
                        else
                        {
                            errors.Add("Response is not a JSON array or object");
                        }
                    }
                    catch (JsonException ex)
                    {
                        errors.Add($"Invalid JSON: {ex.Message}");
                    }
                }

                tasksValid.Set(ctx, errors.Count == 0);
                validationErrors.Set(ctx, string.Join("; ", errors));
                return (object)extracted;
            })
        };
        extractAndValidate.SetDisplayText("Extract & Validate");

        // ================================================================
        // 4. Valid?
        // ================================================================
        var isValid = new FlowDecision(ctx => tasksValid.Get(ctx))
        { Id = "TasksValid", Name = "Tasks Valid?" };
        isValid.SetDisplayText("Tasks Valid?");

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
        { Id = "OutTasks", OutputName = new("tasksJson"), OutputValue = new(ctx => (object)tasksJson.Get(ctx)) };
        setOutputs.SetDisplayText("Output Tasks");

        var setErrorOutputs = new Sequence
        {
            Id = "SetErrorOutputs", Name = "Error Outputs",
            Activities =
            {
                new SetOutput { Id = "OutErrTasks", OutputName = new("tasksJson"), OutputValue = new(_ => (object)"[]") },
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
            Id = "TaskCreationFlowchart",
            Start = init,
            Activities =
            {
                init, generateTasks, extractAndValidate, isValid,
                incrementRetry, canRetry,
                setOutputs, setErrorOutputs, finish,
            },
            Connections =
            {
                Connect(init, generateTasks),
                Connect(generateTasks, extractAndValidate),
                Connect(extractAndValidate, isValid),

                ConnectOutcome(isValid, "True", setOutputs),
                Connect(setOutputs, finish),

                ConnectOutcome(isValid, "False", incrementRetry),
                Connect(incrementRetry, canRetry),

                ConnectOutcome(canRetry, "True", generateTasks), // retry
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
