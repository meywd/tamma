using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Activities.Core;
using Tamma.Activities.LlmCall;
using Tamma.Activities.TDD.Models;

namespace Tamma.Activities.TDD;

/// <summary>
/// ELSA activity that generates the minimum implementation to make failing tests pass.
/// Part of the GREEN phase in the TDD cycle.
/// Calls LLM with role=implementer to write code that satisfies the tests.
///
/// <para>Story 32-5 (AC9): the LLM call routes through the mediated call-LLM
/// endpoint (<see cref="MediatedLlmText"/>) — no direct provider key/HTTP in the
/// engine. The mock path remains for tests; the output contract is unchanged.</para>
/// </summary>
[Activity(
    "Tamma.TDD",
    "Write Implementation",
    "Generate minimum implementation to pass tests (GREEN phase)",
    Kind = ActivityKind.Task
)]
public class WriteImplementationActivity : CodeActivity<ImplementationResult>
{
    private readonly ILogger<WriteImplementationActivity>? _logger;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Story identifier</summary>
    [Input(Description = "Story identifier")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Task description from the implementation plan</summary>
    [Input(Description = "Task description")]
    public Input<string> TaskDescription { get; set; } = default!;

    /// <summary>The test code that needs to pass</summary>
    [Input(Description = "Test code that needs to pass")]
    public Input<string> TestCode { get; set; } = default!;

    /// <summary>Test failure output from the RED phase</summary>
    [Input(Description = "Test failure output from RED phase")]
    public Input<string?> TestFailureOutput { get; set; } = default!;

    /// <summary>Existing code context</summary>
    [Input(Description = "Existing code context")]
    public Input<string?> CodeContext { get; set; } = default!;

    /// <summary>Junior developer's skill level (1-5)</summary>
    [Input(Description = "Junior skill level (1-5)", DefaultValue = 3)]
    public Input<int> SkillLevel { get; set; } = new(3);

    [JsonConstructor]
    public WriteImplementationActivity() { }

    public WriteImplementationActivity(
        ILogger<WriteImplementationActivity> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);
        var taskDescription = TaskDescription.Get(context);
        var testCode = TestCode.Get(context);
        var testFailureOutput = TestFailureOutput.Get(context);
        var codeContext = CodeContext.Get(context);
        var skillLevel = Math.Clamp(SkillLevel.Get(context), 1, 5);

        _logger?.LogInformation(
            "TDD GREEN phase: Writing implementation for task in story {StoryId}, session {SessionId}",
            storyId, sessionId);

        try
        {
            var prompt = BuildImplementationPrompt(taskDescription, testCode, testFailureOutput, codeContext, skillLevel);

            var useMock = _configuration?.GetValue<bool>("Anthropic:UseMock") ?? false;

            var response = useMock
                ? SimulateImplementation(taskDescription)
                : await MediatedLlmText.CompleteAsync(context, "implementer", prompt, context.CancellationToken);

            var result = ParseImplementationResponse(response);

            _logger?.LogInformation(
                "TDD GREEN phase: Generated implementation across {FileCount} files for session {SessionId}",
                result.ImplementationFiles.Count, sessionId);

            // Story 4-5 (AC1) — capture the code-file write as a DCB event. The
            // GREEN phase produced implementation code; emit CODE.GENERATED.SUCCESS
            // (or the loud CODE.GENERATED.FAILED when generation yielded no code) so
            // every code change is on the audit stream.
            TammaEventEmitter.Emit(context, this, _logger,
                CodeEvents.BuildGenerated(result.Success, storyId, sessionId,
                    CodeEvents.OperationImplementation, result.ImplementationFiles,
                    testCount: null, result.ErrorMessage));

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TDD GREEN phase: Error generating implementation for session {SessionId}", sessionId);

            var failed = new ImplementationResult
            {
                Success = false,
                ErrorMessage = $"Implementation generation failed: {ex.Message}"
            };

            // Story 4-5 (AC1) — the failure edge is auditable too (loud, error-status);
            // a failed code generation is never a silent non-event.
            TammaEventEmitter.Emit(context, this, _logger,
                CodeEvents.BuildGenerated(success: false, storyId, sessionId,
                    CodeEvents.OperationImplementation, failed.ImplementationFiles,
                    testCount: null, failed.ErrorMessage));

            context.SetResult(failed);
        }
    }

    private string BuildImplementationPrompt(
        string taskDescription,
        string testCode,
        string? testFailureOutput,
        string? codeContext,
        int skillLevel)
    {
        var guidance = SkillLevelPromptDetail.GetImplementationGuidance(skillLevel);
        var failureSection = !string.IsNullOrEmpty(testFailureOutput)
            ? $"\n\nTest failure output:\n```\n{testFailureOutput}\n```"
            : "";
        var contextSection = !string.IsNullOrEmpty(codeContext)
            ? $"\n\nExisting code context:\n{codeContext}"
            : "";

        return $@"Write the MINIMUM implementation needed to make ALL the following tests pass. Do not over-engineer and do not break any existing tests — write just enough simple, focused code to satisfy the tests, following the project's coding conventions.

Task: {taskDescription}

Tests to satisfy:
```
{testCode}
```
{failureSection}
{contextSection}

{guidance}

Respond with JSON: {{""implementationCode"": ""..."", ""implementationFiles"": [""...""]}}";
    }

    private static string SimulateImplementation(string taskDescription)
    {
        return JsonSerializer.Serialize(new
        {
            implementationCode = $"// Implementation for: {taskDescription}\n" +
                                 "export function implementation() {\n" +
                                 "  // Minimum implementation to pass tests\n" +
                                 "  return true;\n" +
                                 "}",
            implementationFiles = new[] { "src/implementation.ts" }
        });
    }

    private static ImplementationResult ParseImplementationResponse(string response)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(response);

            var code = json.TryGetProperty("implementationCode", out var ic)
                ? ic.GetString() ?? ""
                : "";
            var files = json.TryGetProperty("implementationFiles", out var imf)
                ? JsonSerializer.Deserialize<List<string>>(imf.GetRawText()) ?? new List<string>()
                : new List<string>();

            return new ImplementationResult
            {
                Success = !string.IsNullOrEmpty(code),
                ImplementationCode = code,
                ImplementationFiles = files
            };
        }
        catch (Exception ex)
        {
            return new ImplementationResult
            {
                Success = false,
                ErrorMessage = $"Failed to parse implementation response: {ex.Message}"
            };
        }
    }
}
