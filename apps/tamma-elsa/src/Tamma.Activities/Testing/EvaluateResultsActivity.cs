using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Testing.Models;

namespace Tamma.Activities.Testing;

/// <summary>
/// ELSA activity that evaluates CI results against skill-level-aware quality thresholds.
/// Routes to one of four outcomes: AllPass, MinorIssues, MajorIssues, Critical.
/// Scoring weights: Coverage 40%, Lint 25%, Security 25%, Build 10%.
/// </summary>
[Activity(
    "Tamma.Testing",
    "Evaluate Results",
    "Evaluate CI results against quality thresholds with skill-level-aware routing",
    Kind = ActivityKind.Task
)]
[FlowNode("AllPass", "MinorIssues", "MajorIssues", "Critical")]
public class EvaluateResultsActivity : Activity
{
    private readonly ILogger<EvaluateResultsActivity>? _logger;

    /// <summary>CI results to evaluate</summary>
    [Input(Description = "CI pipeline results to evaluate")]
    public Input<CIResultsPayload> CIResults { get; set; } = default!;

    /// <summary>Junior developer's skill level (1-5)</summary>
    [Input(Description = "Junior skill level for threshold calculation", DefaultValue = 3)]
    public Input<int> SkillLevel { get; set; } = new(3);

    /// <summary>Number of consecutive passes (for progressive quality)</summary>
    [Input(Description = "Consecutive pass count for progressive threshold tightening", DefaultValue = 0)]
    public Input<int> ConsecutivePassCount { get; set; } = new(0);

    /// <summary>Evaluation result output</summary>
    [Output(Description = "Quality gate evaluation result")]
    public Output<QualityGateResult> EvaluationResult { get; set; } = default!;

    [JsonConstructor]
    public EvaluateResultsActivity()
    {
        _logger = null;
    }

    public EvaluateResultsActivity(ILogger<EvaluateResultsActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var ciResults = CIResults.Get(context);
        var skillLevel = Math.Clamp(SkillLevel.Get(context), 1, 5);
        var consecutivePassCount = ConsecutivePassCount.Get(context);

        _logger?.LogInformation(
            "Evaluating CI results for run {RunId}, skill level {SkillLevel}, consecutive passes {ConsecutivePassCount}",
            ciResults.RunId, skillLevel, consecutivePassCount);

        // Generate thresholds based on skill level
        var thresholds = QualityThresholds.ForSkillLevel(skillLevel);

        // Progressive quality: tighten thresholds after 3+ consecutive passes
        if (consecutivePassCount >= 3)
        {
            thresholds = thresholds.Tighten();
            _logger?.LogInformation(
                "Tightened thresholds after {Count} consecutive passes: coverage={Coverage}%, lint warnings={LintWarnings}",
                consecutivePassCount, thresholds.MinCoveragePercent, thresholds.MaxLintWarnings);
        }

        var issues = new List<QualityIssue>();

        // Evaluate build
        var buildScore = EvaluateBuild(ciResults, thresholds, issues);

        // Evaluate tests
        EvaluateTests(ciResults, thresholds, issues);

        // Evaluate coverage
        var coverageScore = EvaluateCoverage(ciResults, thresholds, issues);

        // Evaluate linting
        var lintScore = EvaluateLinting(ciResults, thresholds, issues);

        // Evaluate security
        var securityScore = EvaluateSecurity(ciResults, thresholds, issues);

        // Calculate overall score: Coverage 40%, Lint 25%, Security 25%, Build 10%
        var overallScore = (coverageScore * 0.40) + (lintScore * 0.25) + (securityScore * 0.25) + (buildScore * 0.10);

        // Determine outcome
        var outcome = DetermineOutcome(issues, ciResults, thresholds);

        var result = new QualityGateResult
        {
            Outcome = outcome,
            OverallScore = Math.Round(overallScore, 2),
            CoverageScore = Math.Round(coverageScore, 2),
            LintScore = Math.Round(lintScore, 2),
            SecurityScore = Math.Round(securityScore, 2),
            BuildScore = Math.Round(buildScore, 2),
            Issues = issues,
            Summary = GenerateSummary(outcome, overallScore, issues),
            AutoFixable = issues.Any(i => i.AutoFixable),
            SkillLevel = skillLevel,
            AppliedThresholds = thresholds
        };

        EvaluationResult.Set(context, result);

        _logger?.LogInformation(
            "Evaluation complete: Outcome={Outcome}, Score={Score}, Issues={IssueCount}",
            outcome, overallScore, issues.Count);

        // Route to the appropriate outcome
        var outcomeName = outcome.ToString();
        await context.CompleteActivityWithOutcomesAsync(outcomeName);
    }

    private static double EvaluateBuild(CIResultsPayload ciResults, QualityThresholds thresholds, List<QualityIssue> issues)
    {
        if (!ciResults.BuildPassed && thresholds.RequireBuildPass)
        {
            issues.Add(new QualityIssue
            {
                Category = QualityIssueCategory.Build,
                Severity = QualityIssueSeverity.Critical,
                Message = "Build failed",
                AutoFixable = false
            });
            return 0;
        }

        return ciResults.BuildPassed ? 100 : 0;
    }

    private static void EvaluateTests(CIResultsPayload ciResults, QualityThresholds thresholds, List<QualityIssue> issues)
    {
        if (ciResults.FailedTests > 0 && thresholds.RequireAllTestsPass)
        {
            issues.Add(new QualityIssue
            {
                Category = QualityIssueCategory.Test,
                Severity = ciResults.FailedTests > 5
                    ? QualityIssueSeverity.Critical
                    : QualityIssueSeverity.Error,
                Message = $"{ciResults.FailedTests} test(s) failed out of {ciResults.TotalTests}",
                AutoFixable = false
            });

            foreach (var failedTest in ciResults.FailedTestDetails)
            {
                issues.Add(new QualityIssue
                {
                    Category = QualityIssueCategory.Test,
                    Severity = QualityIssueSeverity.Error,
                    Message = $"Test failed: {failedTest.TestName}",
                    Details = failedTest.ErrorMessage,
                    AutoFixable = false
                });
            }
        }
    }

    private static double EvaluateCoverage(CIResultsPayload ciResults, QualityThresholds thresholds, List<QualityIssue> issues)
    {
        var coverage = ciResults.CoveragePercentage;

        if (coverage < thresholds.MinCoveragePercent)
        {
            var gap = thresholds.MinCoveragePercent - coverage;
            var severity = gap > 20
                ? QualityIssueSeverity.Critical
                : gap > 10
                    ? QualityIssueSeverity.Error
                    : QualityIssueSeverity.Warning;

            issues.Add(new QualityIssue
            {
                Category = QualityIssueCategory.Coverage,
                Severity = severity,
                Message = $"Coverage {coverage:F1}% is below threshold {thresholds.MinCoveragePercent:F1}% (gap: {gap:F1}%)",
                AutoFixable = false
            });
        }

        // Score: 100 if at or above threshold, proportional below
        return coverage >= thresholds.MinCoveragePercent
            ? 100
            : Math.Max(0, (coverage / thresholds.MinCoveragePercent) * 100);
    }

    private static double EvaluateLinting(CIResultsPayload ciResults, QualityThresholds thresholds, List<QualityIssue> issues)
    {
        if (ciResults.LintErrors > thresholds.MaxLintErrors)
        {
            issues.Add(new QualityIssue
            {
                Category = QualityIssueCategory.Lint,
                Severity = QualityIssueSeverity.Error,
                Message = $"{ciResults.LintErrors} lint error(s) found (max allowed: {thresholds.MaxLintErrors})",
                AutoFixable = true
            });
        }

        if (ciResults.LintWarnings > thresholds.MaxLintWarnings)
        {
            issues.Add(new QualityIssue
            {
                Category = QualityIssueCategory.Lint,
                Severity = QualityIssueSeverity.Warning,
                Message = $"{ciResults.LintWarnings} lint warning(s) found (max allowed: {thresholds.MaxLintWarnings})",
                AutoFixable = true
            });
        }

        // Score based on total lint issues vs threshold
        var totalLintIssues = ciResults.LintErrors + ciResults.LintWarnings;
        var maxAllowed = thresholds.MaxLintErrors + thresholds.MaxLintWarnings;

        if (totalLintIssues == 0) return 100;
        if (maxAllowed == 0) return totalLintIssues == 0 ? 100 : Math.Max(0, 100 - totalLintIssues * 10);

        return totalLintIssues <= maxAllowed
            ? 100
            : Math.Max(0, 100 - ((totalLintIssues - maxAllowed) * 10));
    }

    private static double EvaluateSecurity(CIResultsPayload ciResults, QualityThresholds thresholds, List<QualityIssue> issues)
    {
        var criticalCount = ciResults.SecurityIssues.Count(s => s.Severity == SecuritySeverity.Critical);
        var highCount = ciResults.SecurityIssues.Count(s => s.Severity == SecuritySeverity.High);

        if (criticalCount > thresholds.MaxSecurityCritical)
        {
            issues.Add(new QualityIssue
            {
                Category = QualityIssueCategory.Security,
                Severity = QualityIssueSeverity.Critical,
                Message = $"{criticalCount} critical security vulnerability(s) found",
                AutoFixable = false
            });
        }

        if (highCount > thresholds.MaxSecurityHigh)
        {
            issues.Add(new QualityIssue
            {
                Category = QualityIssueCategory.Security,
                Severity = QualityIssueSeverity.Error,
                Message = $"{highCount} high security vulnerability(s) found (max allowed: {thresholds.MaxSecurityHigh})",
                AutoFixable = false
            });
        }

        foreach (var vuln in ciResults.SecurityIssues.Where(s =>
            s.Severity is SecuritySeverity.Critical or SecuritySeverity.High))
        {
            issues.Add(new QualityIssue
            {
                Category = QualityIssueCategory.Security,
                Severity = vuln.Severity == SecuritySeverity.Critical
                    ? QualityIssueSeverity.Critical
                    : QualityIssueSeverity.Error,
                Message = $"[{vuln.Severity}] {vuln.Package}: {vuln.Description}",
                Details = vuln.CveId,
                AutoFixable = !string.IsNullOrEmpty(vuln.FixVersion)
            });
        }

        // Score: deduct per severity
        var score = 100.0;
        score -= criticalCount * 30;
        score -= highCount * 15;
        score -= ciResults.SecurityIssues.Count(s => s.Severity == SecuritySeverity.Medium) * 5;

        return Math.Max(0, score);
    }

    private static EvaluationOutcome DetermineOutcome(
        List<QualityIssue> issues, CIResultsPayload ciResults, QualityThresholds thresholds)
    {
        var hasCritical = issues.Any(i => i.Severity == QualityIssueSeverity.Critical);
        var hasErrors = issues.Any(i => i.Severity == QualityIssueSeverity.Error);
        var hasWarnings = issues.Any(i => i.Severity == QualityIssueSeverity.Warning);

        if (hasCritical || (!ciResults.BuildPassed && thresholds.RequireBuildPass))
            return EvaluationOutcome.Critical;

        if (hasErrors)
            return EvaluationOutcome.MajorIssues;

        if (hasWarnings)
            return EvaluationOutcome.MinorIssues;

        return EvaluationOutcome.AllPass;
    }

    private static string GenerateSummary(EvaluationOutcome outcome, double score, List<QualityIssue> issues)
    {
        var issueBreakdown = issues
            .GroupBy(i => i.Category)
            .Select(g => $"{g.Key}: {g.Count()} issue(s)")
            .ToList();

        var breakdown = issueBreakdown.Count > 0
            ? string.Join(", ", issueBreakdown)
            : "No issues found";

        return outcome switch
        {
            EvaluationOutcome.AllPass =>
                $"All quality gates passed with score {score:F0}/100. {breakdown}",
            EvaluationOutcome.MinorIssues =>
                $"Quality gates passed with minor issues (score: {score:F0}/100). {breakdown}",
            EvaluationOutcome.MajorIssues =>
                $"Quality gates failed with major issues (score: {score:F0}/100). {breakdown}",
            EvaluationOutcome.Critical =>
                $"Critical quality gate failures (score: {score:F0}/100). {breakdown}",
            _ => $"Evaluation complete (score: {score:F0}/100). {breakdown}"
        };
    }
}
