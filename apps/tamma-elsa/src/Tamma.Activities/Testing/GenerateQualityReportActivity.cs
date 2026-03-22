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
/// ELSA activity that generates a comprehensive quality report from all check results.
/// Scoring weights: Coverage 40%, Lint 25%, Security 25%, Build 10%.
/// Includes teaching feedback appropriate to the developer's skill level.
/// </summary>
[Activity(
    "Tamma.Testing",
    "Generate Quality Report",
    "Generate comprehensive quality report with scoring and teaching feedback",
    Kind = ActivityKind.Task
)]
public class GenerateQualityReportActivity : CodeActivity<QualityReport>
{
    private readonly ILogger<GenerateQualityReportActivity>? _logger;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>CI results from the pipeline</summary>
    [Input(Description = "CI pipeline results")]
    public Input<CIResultsPayload> CIResults { get; set; } = default!;

    /// <summary>Coverage check result</summary>
    [Input(Description = "Coverage check result")]
    public Input<CoverageCheckResult> CoverageResult { get; set; } = default!;

    /// <summary>Linting check result</summary>
    [Input(Description = "Linting check result")]
    public Input<LintCheckResult> LintResult { get; set; } = default!;

    /// <summary>Security check result</summary>
    [Input(Description = "Security check result")]
    public Input<SecurityCheckResult> SecurityResult { get; set; } = default!;

    /// <summary>Junior developer's skill level (1-5)</summary>
    [Input(Description = "Junior skill level", DefaultValue = 3)]
    public Input<int> SkillLevel { get; set; } = new(3);

    /// <summary>Number of consecutive passes before this run</summary>
    [Input(Description = "Consecutive pass count", DefaultValue = 0)]
    public Input<int> ConsecutivePassCount { get; set; } = new(0);

    [JsonConstructor]
    public GenerateQualityReportActivity()
    {
        _logger = null;
    }

    public GenerateQualityReportActivity(ILogger<GenerateQualityReportActivity> logger)
    {
        _logger = logger;
    }

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var ciResults = CIResults.Get(context);
        var coverageResult = CoverageResult.Get(context);
        var lintResult = LintResult.Get(context);
        var securityResult = SecurityResult.Get(context);
        var skillLevel = Math.Clamp(SkillLevel.Get(context), 1, 5);
        var consecutivePassCount = ConsecutivePassCount.Get(context);

        var thresholds = QualityThresholds.ForSkillLevel(skillLevel);
        var thresholdsTightened = consecutivePassCount >= 3;
        if (thresholdsTightened)
        {
            thresholds = thresholds.Tighten();
        }

        // Calculate individual scores (0-100)
        var coverageScore = coverageResult.Passed
            ? 100.0
            : Math.Max(0, (coverageResult.ActualCoverage / coverageResult.RequiredCoverage) * 100);

        var lintScore = CalculateLintScore(lintResult);
        var securityScore = CalculateSecurityScore(securityResult);
        var buildScore = ciResults.BuildPassed ? 100.0 : 0.0;

        // Weighted overall score: Coverage 40%, Lint 25%, Security 25%, Build 10%
        var overallScore = (coverageScore * 0.40) + (lintScore * 0.25) + (securityScore * 0.25) + (buildScore * 0.10);

        // Aggregate all issues
        var allIssues = new List<QualityIssue>();
        allIssues.AddRange(lintResult.Issues);

        if (!coverageResult.Passed)
        {
            allIssues.Add(new QualityIssue
            {
                Category = QualityIssueCategory.Coverage,
                Severity = coverageResult.Gap > 20
                    ? QualityIssueSeverity.Critical
                    : coverageResult.Gap > 10
                        ? QualityIssueSeverity.Error
                        : QualityIssueSeverity.Warning,
                Message = coverageResult.Message,
                AutoFixable = false
            });
        }

        if (!securityResult.Passed)
        {
            foreach (var vuln in securityResult.Vulnerabilities.Where(v =>
                v.Severity is SecuritySeverity.Critical or SecuritySeverity.High))
            {
                allIssues.Add(new QualityIssue
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
        }

        if (!ciResults.BuildPassed)
        {
            allIssues.Add(new QualityIssue
            {
                Category = QualityIssueCategory.Build,
                Severity = QualityIssueSeverity.Critical,
                Message = "Build failed",
                AutoFixable = false
            });
        }

        // Determine pass/fail
        var allTestsPassed = ciResults.FailedTests == 0;
        var passed = coverageResult.Passed && lintResult.Passed
                     && securityResult.Passed && ciResults.BuildPassed && allTestsPassed;

        // Generate grade
        var grade = overallScore switch
        {
            >= 90 => "A",
            >= 80 => "B",
            >= 70 => "C",
            >= 60 => "D",
            _ => "F"
        };

        var newConsecutivePassCount = passed ? consecutivePassCount + 1 : 0;

        var report = new QualityReport
        {
            SessionId = sessionId,
            RunId = ciResults.RunId,
            GeneratedAt = DateTime.UtcNow,
            OverallScore = Math.Round(overallScore, 2),
            Grade = grade,
            Passed = passed,
            SkillLevel = skillLevel,
            AppliedThresholds = thresholds,
            Coverage = coverageResult,
            Linting = lintResult,
            Security = securityResult,
            BuildPassed = ciResults.BuildPassed,
            AllTestsPassed = allTestsPassed,
            TotalTests = ciResults.TotalTests,
            PassedTests = ciResults.PassedTests,
            FailedTests = ciResults.FailedTests,
            AllIssues = allIssues,
            Recommendations = GenerateRecommendations(coverageResult, lintResult, securityResult, ciResults, skillLevel),
            TeachingFeedback = GenerateTeachingFeedback(passed, overallScore, skillLevel, allIssues),
            ConsecutivePassCount = newConsecutivePassCount,
            ThresholdsTightened = thresholdsTightened
        };

        _logger?.LogInformation(
            "Quality report generated for session {SessionId}: Grade={Grade}, Score={Score}, Passed={Passed}",
            sessionId, grade, overallScore, passed);

        context.SetResult(report);
        return ValueTask.CompletedTask;
    }

    private static double CalculateLintScore(LintCheckResult lintResult)
    {
        if (lintResult.ErrorCount == 0 && lintResult.WarningCount == 0)
            return 100;

        var totalIssues = lintResult.ErrorCount + lintResult.WarningCount;
        var maxAllowed = lintResult.MaxErrors + lintResult.MaxWarnings;

        if (maxAllowed == 0)
            return totalIssues == 0 ? 100 : Math.Max(0, 100 - totalIssues * 10);

        return totalIssues <= maxAllowed
            ? 100
            : Math.Max(0, 100 - ((totalIssues - maxAllowed) * 10));
    }

    private static double CalculateSecurityScore(SecurityCheckResult securityResult)
    {
        var score = 100.0;
        score -= securityResult.CriticalCount * 30;
        score -= securityResult.HighCount * 15;
        score -= securityResult.MediumCount * 5;
        return Math.Max(0, score);
    }

    private static List<string> GenerateRecommendations(
        CoverageCheckResult coverage,
        LintCheckResult lint,
        SecurityCheckResult security,
        CIResultsPayload ciResults,
        int skillLevel)
    {
        var recommendations = new List<string>();

        if (!ciResults.BuildPassed)
        {
            recommendations.Add("Fix build errors before addressing other issues - a passing build is the foundation.");
        }

        if (ciResults.FailedTests > 0)
        {
            recommendations.Add(
                $"Fix {ciResults.FailedTests} failing test(s). Review error messages and stack traces for clues.");
        }

        if (!coverage.Passed)
        {
            var suggestion = skillLevel <= 2
                ? "Add tests for the main code paths first. Focus on the 'happy path' before edge cases."
                : "Increase test coverage by adding tests for uncovered branches and edge cases.";
            recommendations.Add($"Coverage gap of {coverage.Gap:F1}%: {suggestion}");
        }

        if (!lint.Passed)
        {
            if (lint.ErrorCount > 0)
            {
                recommendations.Add(
                    "Fix lint errors first - these often indicate real bugs or code issues.");
            }

            if (lint.WarningCount > lint.MaxWarnings)
            {
                recommendations.Add(
                    "Run your linter's auto-fix command to resolve formatting warnings automatically.");
            }
        }

        if (!security.Passed)
        {
            if (security.CriticalCount > 0)
            {
                recommendations.Add(
                    "Critical security vulnerabilities must be resolved immediately. Update affected packages.");
            }

            if (security.HighCount > 0)
            {
                recommendations.Add(
                    "High-severity vulnerabilities should be addressed. Check for available package updates.");
            }
        }

        if (recommendations.Count == 0)
        {
            recommendations.Add("Excellent work! All quality gates passed. Keep up the great coding practices.");
        }

        return recommendations;
    }

    private static string GenerateTeachingFeedback(
        bool passed, double score, int skillLevel, List<QualityIssue> issues)
    {
        if (passed)
        {
            return skillLevel switch
            {
                1 or 2 => $"Great job! Your code passed all quality checks with a score of {score:F0}/100. " +
                           "Quality gates help catch problems early - keep writing tests alongside your code!",
                3 => $"Well done! Score: {score:F0}/100. Your code quality is solid. " +
                     "Consider exploring more advanced testing patterns like mocking and integration tests.",
                4 or 5 => $"Excellent work! Score: {score:F0}/100. Your quality standards are high. " +
                          "As thresholds tighten with your skill growth, keep pushing for comprehensive test coverage.",
                _ => $"Quality checks passed with score {score:F0}/100."
            };
        }

        var primaryIssue = issues
            .OrderByDescending(i => i.Severity)
            .FirstOrDefault();

        var issueContext = primaryIssue?.Category switch
        {
            QualityIssueCategory.Build =>
                "A failing build is the first thing to fix. Read the error messages carefully - they usually point directly to the problem.",
            QualityIssueCategory.Test =>
                "Failing tests indicate something changed in the expected behavior. Compare what the test expects vs what your code produces.",
            QualityIssueCategory.Coverage =>
                "Low coverage means parts of your code aren't tested. Think about what could go wrong in untested paths.",
            QualityIssueCategory.Lint =>
                "Lint issues are often quick to fix. Many can be auto-fixed, and they help maintain consistent, readable code.",
            QualityIssueCategory.Security =>
                "Security vulnerabilities in dependencies need prompt attention. Check if newer versions of affected packages are available.",
            _ => "Review the issues listed and address them from most to least severe."
        };

        return skillLevel switch
        {
            1 or 2 => $"Don't worry - getting quality checks to pass is a learning process! " +
                       $"Score: {score:F0}/100. Focus on the most important issue first: {issueContext}",
            3 => $"Some issues need attention (score: {score:F0}/100). {issueContext} " +
                 "Try to fix issues systematically - start with critical ones.",
            4 or 5 => $"Quality check needs attention (score: {score:F0}/100). {issueContext} " +
                      "At your skill level, these should be quick fixes.",
            _ => $"Score: {score:F0}/100. {issueContext}"
        };
    }
}
