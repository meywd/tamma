using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.Debug.Models;

namespace Tamma.Activities.Debug;

/// <summary>
/// Extracts reproduction steps from the issue description (BugInvestigation mode only).
/// Parses expected behavior, actual behavior, and environment details.
/// Part of the parallel Fork.
/// </summary>
[Activity(
    "Tamma.Debug",
    "Collect Reproduction Steps",
    "Extract bug reproduction steps from issue description (BugInvestigation only)",
    Kind = ActivityKind.Task
)]
public class CollectReproductionStepsActivity : CodeActivity<ReproductionSteps>
{
    private readonly ILogger<CollectReproductionStepsActivity>? _logger;

    /// <summary>Issue description containing reproduction steps</summary>
    [Input(Description = "Issue description for bug investigation")]
    public Input<string> IssueDescription { get; set; } = default!;

    /// <summary>Debug context mode</summary>
    [Input(Description = "Debug context mode")]
    public Input<string> DebugContextMode { get; set; } = default!;

    [JsonConstructor]
    public CollectReproductionStepsActivity() { }

    public CollectReproductionStepsActivity(ILogger<CollectReproductionStepsActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var issueDescription = IssueDescription.Get(context) ?? string.Empty;
        var mode = DebugContextMode.Get(context);

        _logger?.LogInformation(
            "Collecting reproduction steps, mode={Mode}, description length={Length}",
            mode, issueDescription.Length);

        try
        {
            var result = new ReproductionSteps();

            if (mode != "BugInvestigation" || string.IsNullOrWhiteSpace(issueDescription))
            {
                // Not BugInvestigation mode or no description — return empty
                _logger?.LogInformation(
                    "Skipping reproduction steps (mode={Mode})", mode);
                context.SetResult(result);
                return;
            }

            // Parse the issue description for common bug report sections
            var lines = issueDescription.Split('\n');
            var currentSection = "description";
            var stepLines = new List<string>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                var lower = trimmed.ToLower();

                // Detect section headers
                if (lower.Contains("steps to reproduce") || lower.Contains("reproduction steps")
                    || lower.Contains("how to reproduce"))
                {
                    currentSection = "steps";
                    continue;
                }
                if (lower.Contains("expected behavior") || lower.Contains("expected result")
                    || lower.Contains("expected:"))
                {
                    currentSection = "expected";
                    continue;
                }
                if (lower.Contains("actual behavior") || lower.Contains("actual result")
                    || lower.Contains("actual:"))
                {
                    currentSection = "actual";
                    continue;
                }
                if (lower.Contains("environment") || lower.Contains("system info")
                    || lower.Contains("version"))
                {
                    currentSection = "environment";
                    continue;
                }

                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                switch (currentSection)
                {
                    case "steps":
                        // Strip common list prefixes
                        var step = trimmed.TrimStart('-', '*', ' ');
                        if (step.Length > 0 && char.IsDigit(step[0]))
                            step = step.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ')', ' ');
                        if (!string.IsNullOrWhiteSpace(step))
                            result.Steps.Add(step);
                        break;

                    case "expected":
                        result.ExpectedBehavior += (result.ExpectedBehavior.Length > 0 ? "\n" : "") + trimmed;
                        break;

                    case "actual":
                        result.ActualBehavior += (result.ActualBehavior.Length > 0 ? "\n" : "") + trimmed;
                        break;

                    case "environment":
                        result.Environment += (result.Environment.Length > 0 ? "\n" : "") + trimmed;
                        break;

                    default:
                        // Default section lines go to steps as fallback
                        stepLines.Add(trimmed);
                        break;
                }
            }

            // If no structured steps found, use fallback lines
            if (result.Steps.Count == 0 && stepLines.Count > 0)
            {
                result.Steps = stepLines;
            }

            // If still no structured info, put the whole description as actual behavior
            if (result.Steps.Count == 0 && string.IsNullOrEmpty(result.ActualBehavior))
            {
                result.ActualBehavior = issueDescription;
            }

            _logger?.LogInformation(
                "Collected {StepCount} reproduction steps, expected: {HasExpected}, actual: {HasActual}",
                result.Steps.Count,
                !string.IsNullOrEmpty(result.ExpectedBehavior),
                !string.IsNullOrEmpty(result.ActualBehavior));

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to collect reproduction steps");
            context.SetResult(new ReproductionSteps
            {
                ActualBehavior = issueDescription
            });
        }

        await ValueTask.CompletedTask;
    }
}
