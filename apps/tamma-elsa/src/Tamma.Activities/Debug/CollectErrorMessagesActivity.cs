using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.Debug.Models;

namespace Tamma.Activities.Debug;

/// <summary>
/// Gathers stack traces, log output, and test failure messages for debugging.
/// Part of the parallel debug context gathering (Fork).
/// </summary>
[Activity(
    "Tamma.Debug",
    "Collect Error Messages",
    "Gather stack traces, logs, and test output",
    Kind = ActivityKind.Task
)]
public class CollectErrorMessagesActivity : CodeActivity<ErrorMessages>
{
    private readonly ILogger<CollectErrorMessagesActivity>? _logger;

    /// <summary>Raw error output (stack traces, test output, etc.)</summary>
    [Input(Description = "Raw error output from the failing operation")]
    public Input<string> ErrorOutput { get; set; } = default!;

    /// <summary>Debug context mode — determines emphasis</summary>
    [Input(Description = "Debug context mode")]
    public Input<string> DebugContextMode { get; set; } = default!;

    /// <summary>Repository URL for log retrieval</summary>
    [Input(Description = "Repository URL")]
    public Input<string> RepositoryUrl { get; set; } = default!;

    /// <summary>Branch name</summary>
    [Input(Description = "Branch name")]
    public Input<string> BranchName { get; set; } = default!;

    [JsonConstructor]
    public CollectErrorMessagesActivity() { }

    public CollectErrorMessagesActivity(ILogger<CollectErrorMessagesActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var errorOutput = ErrorOutput.Get(context) ?? string.Empty;
        var mode = DebugContextMode.Get(context);

        _logger?.LogInformation(
            "Collecting error messages for debug mode {Mode}, error length={Length}",
            mode, errorOutput.Length);

        try
        {
            var errors = new List<string>();
            var stackTraces = new List<string>();
            var logLines = new List<string>();

            // Parse error output into structured components
            var lines = errorOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var currentStackTrace = new List<string>();
            var inStackTrace = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("at ") || trimmed.StartsWith("   at "))
                {
                    inStackTrace = true;
                    currentStackTrace.Add(trimmed);
                }
                else if (inStackTrace)
                {
                    // End of stack trace block
                    if (currentStackTrace.Count > 0)
                    {
                        stackTraces.Add(string.Join("\n", currentStackTrace));
                        currentStackTrace.Clear();
                    }
                    inStackTrace = false;

                    if (IsErrorLine(trimmed))
                        errors.Add(trimmed);
                    else
                        logLines.Add(trimmed);
                }
                else if (IsErrorLine(trimmed))
                {
                    errors.Add(trimmed);
                }
                else if (IsLogLine(trimmed))
                {
                    logLines.Add(trimmed);
                }
            }

            // Flush any remaining stack trace
            if (currentStackTrace.Count > 0)
                stackTraces.Add(string.Join("\n", currentStackTrace));

            // For RuntimeError mode, emphasize stack traces
            if (mode == "RuntimeError" && stackTraces.Count == 0 && !string.IsNullOrWhiteSpace(errorOutput))
            {
                // Treat entire output as relevant
                stackTraces.Add(errorOutput);
            }

            var result = new ErrorMessages
            {
                RawOutput = errorOutput,
                Errors = errors,
                StackTraces = stackTraces,
                RelevantLogLines = logLines.Take(50).ToList()
            };

            _logger?.LogInformation(
                "Collected {ErrorCount} errors, {TraceCount} stack traces, {LogCount} log lines",
                result.Errors.Count, result.StackTraces.Count, result.RelevantLogLines.Count);

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to collect error messages");
            context.SetResult(new ErrorMessages
            {
                RawOutput = errorOutput,
                Errors = new List<string> { $"Error collection failed: {ex.Message}" }
            });
        }

        await ValueTask.CompletedTask;
    }

    private static bool IsErrorLine(string line)
    {
        return line.Contains("Error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Exception", StringComparison.OrdinalIgnoreCase)
            || line.Contains("FAIL", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Assert.", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("E ", StringComparison.Ordinal)
            || line.Contains("error CS", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLogLine(string line)
    {
        return !string.IsNullOrWhiteSpace(line) && line.Length > 5;
    }
}
