using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Activities.Debug.Models;

namespace Tamma.Activities.Debug;

/// <summary>
/// Selects the highest-confidence untried hypothesis from the list.
/// Returns null (empty) if all hypotheses have been tried — signals loop exit.
/// </summary>
[Activity(
    "Tamma.Debug",
    "Select Hypothesis",
    "Pick highest-confidence untried hypothesis for next fix attempt",
    Kind = ActivityKind.Task
)]
public class SelectHypothesisActivity : CodeActivity<Hypothesis?>
{
    private readonly ILogger<SelectHypothesisActivity>? _logger;

    /// <summary>All hypotheses (JSON array)</summary>
    [Input(Description = "All hypotheses as JSON")]
    public Input<string> HypothesesJson { get; set; } = default!;

    /// <summary>Current iteration number</summary>
    [Input(Description = "Current iteration number (1-based)")]
    public Input<int> CurrentIteration { get; set; } = default!;

    /// <summary>Maximum iterations allowed</summary>
    [Input(Description = "Maximum iterations", DefaultValue = 5)]
    public Input<int> MaxIterations { get; set; } = new(5);

    [JsonConstructor]
    public SelectHypothesisActivity() { }

    public SelectHypothesisActivity(ILogger<SelectHypothesisActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var hypothesesJson = HypothesesJson.Get(context) ?? "[]";
        var currentIteration = CurrentIteration.Get(context);
        var maxIterations = MaxIterations.Get(context);

        _logger?.LogInformation(
            "Selecting hypothesis for iteration {Iteration}/{Max}",
            currentIteration, maxIterations);

        try
        {
            var hypotheses = JsonSerializer.Deserialize<List<Hypothesis>>(hypothesesJson)
                ?? new List<Hypothesis>();

            // Check if we've exceeded max iterations
            if (currentIteration > maxIterations)
            {
                _logger?.LogInformation(
                    "Max iterations ({Max}) exceeded — no hypothesis selected",
                    maxIterations);
                context.SetResult(null);
                return;
            }

            // Find highest-confidence untried hypothesis
            var selected = hypotheses
                .Where(h => h.Outcome == HypothesisOutcome.Untried)
                .OrderByDescending(h => h.Confidence)
                .FirstOrDefault();

            if (selected == null)
            {
                _logger?.LogInformation(
                    "No untried hypotheses remaining — all {Count} have been attempted",
                    hypotheses.Count);
                context.SetResult(null);
                return;
            }

            _logger?.LogInformation(
                "Selected hypothesis #{Rank}: {Description} (confidence={Confidence:F2})",
                selected.Rank, selected.Description, selected.Confidence);

            context.SetResult(selected);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to select hypothesis");
            context.SetResult(null);
        }

        await ValueTask.CompletedTask;
    }
}
