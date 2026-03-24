using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Testing.Models;

namespace Tamma.Activities.Testing;

/// <summary>
/// ELSA activity that checks code coverage against skill-level thresholds.
/// Extracts coverage data from CI results and evaluates against the required minimum.
/// </summary>
[Activity(
    "Tamma.Testing",
    "Check Coverage",
    "Evaluate code coverage against skill-level-aware thresholds",
    Kind = ActivityKind.Task
)]
public class CheckCoverageActivity : CodeActivity<CoverageCheckResult>
{
    private readonly ILogger<CheckCoverageActivity>? _logger;

    /// <summary>CI results containing coverage data</summary>
    [Input(Description = "CI pipeline results")]
    public Input<CIResultsPayload> CIResults { get; set; } = default!;

    /// <summary>Junior developer's skill level (1-5)</summary>
    [Input(Description = "Junior skill level", DefaultValue = 3)]
    public Input<int> SkillLevel { get; set; } = new(3);

    /// <summary>Override minimum coverage (optional, overrides skill-level default)</summary>
    [Input(Description = "Override minimum coverage percentage")]
    public Input<double?> MinCoverageOverride { get; set; } = default!;

    [JsonConstructor]
    public CheckCoverageActivity()
    {
        _logger = null;
    }

    public CheckCoverageActivity(ILogger<CheckCoverageActivity> logger)
    {
        _logger = logger;
    }

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var ciResults = CIResults.Get(context);
        var skillLevel = Math.Clamp(SkillLevel.Get(context), 1, 5);
        var coverageOverride = MinCoverageOverride.Get(context);

        var thresholds = QualityThresholds.ForSkillLevel(skillLevel);
        var requiredCoverage = coverageOverride ?? thresholds.MinCoveragePercent;
        var actualCoverage = ciResults.CoveragePercentage;
        var gap = requiredCoverage - actualCoverage;
        var passed = actualCoverage >= requiredCoverage;

        _logger?.LogInformation(
            "Coverage check: actual={Actual}%, required={Required}%, passed={Passed}",
            actualCoverage, requiredCoverage, passed);

        var result = new CoverageCheckResult
        {
            Passed = passed,
            ActualCoverage = actualCoverage,
            RequiredCoverage = requiredCoverage,
            Gap = passed ? 0 : gap,
            Message = passed
                ? $"Coverage {actualCoverage:F1}% meets the required {requiredCoverage:F1}%"
                : $"Coverage {actualCoverage:F1}% is below the required {requiredCoverage:F1}% (gap: {gap:F1}%)"
        };

        context.SetResult(result);
        return ValueTask.CompletedTask;
    }
}
