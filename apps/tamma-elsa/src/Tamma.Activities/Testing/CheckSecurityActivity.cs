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
/// ELSA activity that checks security scan results against skill-level thresholds.
/// Evaluates critical and high-severity vulnerabilities against configured limits.
/// </summary>
[Activity(
    "Tamma.Testing",
    "Check Security",
    "Evaluate security scan results against skill-level-aware thresholds",
    Kind = ActivityKind.Task
)]
public class CheckSecurityActivity : CodeActivity<SecurityCheckResult>
{
    private readonly ILogger<CheckSecurityActivity>? _logger;

    /// <summary>CI results containing security scan data</summary>
    [Input(Description = "CI pipeline results")]
    public Input<CIResultsPayload> CIResults { get; set; } = default!;

    /// <summary>Junior developer's skill level (1-5)</summary>
    [Input(Description = "Junior skill level", DefaultValue = 3)]
    public Input<int> SkillLevel { get; set; } = new(3);

    [JsonConstructor]
    public CheckSecurityActivity()
    {
        _logger = null;
    }

    public CheckSecurityActivity(ILogger<CheckSecurityActivity> logger)
    {
        _logger = logger;
    }

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var ciResults = CIResults.Get(context);
        var skillLevel = Math.Clamp(SkillLevel.Get(context), 1, 5);

        var thresholds = QualityThresholds.ForSkillLevel(skillLevel);

        var criticalCount = ciResults.SecurityIssues.Count(s => s.Severity == SecuritySeverity.Critical);
        var highCount = ciResults.SecurityIssues.Count(s => s.Severity == SecuritySeverity.High);
        var mediumCount = ciResults.SecurityIssues.Count(s => s.Severity == SecuritySeverity.Medium);
        var lowCount = ciResults.SecurityIssues.Count(s => s.Severity == SecuritySeverity.Low);

        var criticalPassed = criticalCount <= thresholds.MaxSecurityCritical;
        var highPassed = highCount <= thresholds.MaxSecurityHigh;
        var passed = criticalPassed && highPassed;

        _logger?.LogInformation(
            "Security check: critical={Critical} (max={MaxCritical}), high={High} (max={MaxHigh}), passed={Passed}",
            criticalCount, thresholds.MaxSecurityCritical,
            highCount, thresholds.MaxSecurityHigh, passed);

        var result = new SecurityCheckResult
        {
            Passed = passed,
            CriticalCount = criticalCount,
            HighCount = highCount,
            MediumCount = mediumCount,
            LowCount = lowCount,
            Vulnerabilities = ciResults.SecurityIssues,
            Message = passed
                ? $"Security scan passed: {criticalCount} critical, {highCount} high, {mediumCount} medium, {lowCount} low"
                : $"Security scan failed: {criticalCount} critical (max {thresholds.MaxSecurityCritical}), " +
                  $"{highCount} high (max {thresholds.MaxSecurityHigh})"
        };

        context.SetResult(result);
        return ValueTask.CompletedTask;
    }
}
