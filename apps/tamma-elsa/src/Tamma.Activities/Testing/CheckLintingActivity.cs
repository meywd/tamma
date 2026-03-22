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
/// ELSA activity that checks linting results against skill-level thresholds.
/// Evaluates lint warnings and errors against the configured maximum counts.
/// </summary>
[Activity(
    "Tamma.Testing",
    "Check Linting",
    "Evaluate linting results against skill-level-aware thresholds",
    Kind = ActivityKind.Task
)]
public class CheckLintingActivity : CodeActivity<LintCheckResult>
{
    private readonly ILogger<CheckLintingActivity>? _logger;

    /// <summary>CI results containing linting data</summary>
    [Input(Description = "CI pipeline results")]
    public Input<CIResultsPayload> CIResults { get; set; } = default!;

    /// <summary>Junior developer's skill level (1-5)</summary>
    [Input(Description = "Junior skill level", DefaultValue = 3)]
    public Input<int> SkillLevel { get; set; } = new(3);

    [JsonConstructor]
    public CheckLintingActivity()
    {
        _logger = null;
    }

    public CheckLintingActivity(ILogger<CheckLintingActivity> logger)
    {
        _logger = logger;
    }

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var ciResults = CIResults.Get(context);
        var skillLevel = Math.Clamp(SkillLevel.Get(context), 1, 5);

        var thresholds = QualityThresholds.ForSkillLevel(skillLevel);
        var warningsPassed = ciResults.LintWarnings <= thresholds.MaxLintWarnings;
        var errorsPassed = ciResults.LintErrors <= thresholds.MaxLintErrors;
        var passed = warningsPassed && errorsPassed;

        _logger?.LogInformation(
            "Lint check: warnings={Warnings} (max={MaxWarnings}), errors={Errors} (max={MaxErrors}), passed={Passed}",
            ciResults.LintWarnings, thresholds.MaxLintWarnings,
            ciResults.LintErrors, thresholds.MaxLintErrors, passed);

        var issues = new List<QualityIssue>();

        if (!errorsPassed)
        {
            issues.Add(new QualityIssue
            {
                Category = QualityIssueCategory.Lint,
                Severity = QualityIssueSeverity.Error,
                Message = $"{ciResults.LintErrors} lint error(s) found (max allowed: {thresholds.MaxLintErrors})",
                AutoFixable = true
            });
        }

        if (!warningsPassed)
        {
            issues.Add(new QualityIssue
            {
                Category = QualityIssueCategory.Lint,
                Severity = QualityIssueSeverity.Warning,
                Message = $"{ciResults.LintWarnings} lint warning(s) exceed threshold of {thresholds.MaxLintWarnings}",
                AutoFixable = true
            });
        }

        var result = new LintCheckResult
        {
            Passed = passed,
            WarningCount = ciResults.LintWarnings,
            ErrorCount = ciResults.LintErrors,
            MaxWarnings = thresholds.MaxLintWarnings,
            MaxErrors = thresholds.MaxLintErrors,
            Issues = issues,
            Message = passed
                ? $"Linting passed: {ciResults.LintWarnings} warning(s), {ciResults.LintErrors} error(s)"
                : $"Linting failed: {ciResults.LintWarnings} warning(s) (max {thresholds.MaxLintWarnings}), " +
                  $"{ciResults.LintErrors} error(s) (max {thresholds.MaxLintErrors})"
        };

        context.SetResult(result);
        return ValueTask.CompletedTask;
    }
}
