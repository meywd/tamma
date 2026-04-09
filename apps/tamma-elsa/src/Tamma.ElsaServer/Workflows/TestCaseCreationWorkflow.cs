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
/// Test Case Creation — generates test cases from task plans for the TDD red phase.
/// Dispatches llm-call with role=tester, action=write-tests.
///
/// Validates the output contains test cases. Retries once on invalid.
///
/// Flow:
///   Init → Generate Tests (llm-call) → Extract & Validate → Valid?
///     ├─ Yes → Output → Finish
///     └─ No → Retry? → Yes → Generate Tests
///                      → No → Error Output → Finish
///
/// Inputs: repository, branchName, tasksJson, contextIds
/// Outputs: testCasesJson
/// </summary>
public class TestCaseCreationWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Test Case Creation";
        builder.DefinitionId = "test-case-creation";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Generate test cases from task plans and commit to PR branch";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var branchName = builder.WithVariable<string>("BranchName", "");
        var tasksJson = builder.WithVariable<string>("TasksJson", "[]");
        var contextIds = builder.WithVariable<string>("ContextIds", "[]");

        var testCasesJson = builder.WithVariable<string>("TestCasesJson", "[]");
        var testsValid = builder.WithVariable<bool>("TestsValid", false);
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
                branchName.Set(ctx, ctx.GetInput<string>("branchName") ?? "");
                tasksJson.Set(ctx, ctx.GetInput<string>("tasksJson") ?? "[]");
                contextIds.Set(ctx, ctx.GetInput<string>("contextIds") ?? "[]");
                var inputMaxRetries = ctx.GetInput<int?>("maxRetries");
                if (inputMaxRetries.HasValue) maxRetries.Set(ctx, inputMaxRetries.Value);
                return (object)repo;
            })
        };
        init.SetDisplayText("Initialize");

        // ================================================================
        // 2. Generate Test Cases (via LlmCallWorkflow)
        // ================================================================
        var generateTests = new DispatchWorkflow
        {
            Id = "GenerateTests", Name = "Generate Tests",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = "tester",
                ["action"] = "write-tests",
                ["variables"] = new Dictionary<string, object>
                {
                    ["tasksJson"] = tasksJson.Get(ctx),
                    ["contextIds"] = contextIds.Get(ctx),
                    ["repository"] = repository.Get(ctx),
                    ["branchName"] = branchName.Get(ctx),
                    ["validationErrors"] = validationErrors.Get(ctx),
                },
                ["enableTools"] = true,
            }),
            WaitForCompletion = new(true),
            Result = new(llmResult),
        };
        generateTests.SetDisplayText("Generate Tests");

        // ================================================================
        // 3. Extract + Validate
        // ================================================================
        var extractAndValidate = new SetVariable
        {
            Id = "ExtractValidate", Name = "Extract & Validate",
            Variable = testCasesJson,
            Value = new Input<object?>(ctx =>
            {
                var result = llmResult.Get(ctx);
                var output = "";
                if (result != null && result.TryGetValue("llmResponse", out var r))
                    output = r?.ToString() ?? "";

                // Extract JSON — look for array or object
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
                    errors.Add("Empty test cases output");
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
                                errors.Add("Test cases array is empty");
                        }
                        else if (root.ValueKind == JsonValueKind.Object)
                        {
                            // Accept object with testCases, tests, or similar key
                            if (root.TryGetProperty("testCases", out var tc))
                                extracted = tc.GetRawText();
                            else if (root.TryGetProperty("tests", out var t))
                                extracted = t.GetRawText();
                            else
                                errors.Add("Missing 'testCases' or 'tests' property");
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

                testsValid.Set(ctx, errors.Count == 0);
                validationErrors.Set(ctx, string.Join("; ", errors));
                return (object)extracted;
            })
        };
        extractAndValidate.SetDisplayText("Extract & Validate");

        // ================================================================
        // 4. Valid?
        // ================================================================
        var isValid = new FlowDecision(ctx => testsValid.Get(ctx))
        { Id = "TestsValid", Name = "Tests Valid?" };
        isValid.SetDisplayText("Tests Valid?");

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
        { Id = "OutTestCases", OutputName = new("testCasesJson"), OutputValue = new(ctx => (object)testCasesJson.Get(ctx)) };
        setOutputs.SetDisplayText("Output Test Cases");

        var setErrorOutputs = new Sequence
        {
            Id = "SetErrorOutputs", Name = "Error Outputs",
            Activities =
            {
                new SetOutput { Id = "OutErrTests", OutputName = new("testCasesJson"), OutputValue = new(_ => (object)"[]") },
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
            Id = "TestCaseCreationFlowchart",
            Start = init,
            Activities =
            {
                init, generateTests, extractAndValidate, isValid,
                incrementRetry, canRetry,
                setOutputs, setErrorOutputs, finish,
            },
            Connections =
            {
                Connect(init, generateTests),
                Connect(generateTests, extractAndValidate),
                Connect(extractAndValidate, isValid),

                ConnectOutcome(isValid, "True", setOutputs),
                Connect(setOutputs, finish),

                ConnectOutcome(isValid, "False", incrementRetry),
                Connect(incrementRetry, canRetry),

                ConnectOutcome(canRetry, "True", generateTests), // retry
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
