using System.Text.Json.Serialization;

namespace Tamma.Activities.Testing.Models;

// ============================================
// CI Trigger & Results
// ============================================

/// <summary>
/// Result of triggering a CI pipeline run.
/// </summary>
public class CITriggerResult
{
    public bool Success { get; set; }
    public string RunId { get; set; } = string.Empty;
    public string PipelineUrl { get; set; } = string.Empty;
    public string? Error { get; set; }
    public DateTime TriggeredAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Full CI results received when the pipeline completes (via bookmark resume).
/// </summary>
public class CIResultsPayload
{
    public string RunId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // Success, Failed, Cancelled
    public bool BuildPassed { get; set; }
    public int TotalTests { get; set; }
    public int PassedTests { get; set; }
    public int FailedTests { get; set; }
    public int SkippedTests { get; set; }
    public double CoveragePercentage { get; set; }
    public int LintWarnings { get; set; }
    public int LintErrors { get; set; }
    public List<SecurityVulnerability> SecurityIssues { get; set; } = new();
    public List<FailedTestDetail> FailedTestDetails { get; set; } = new();
    public DateTime CompletedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public string? ArtifactUrl { get; set; }
}

/// <summary>
/// Detail of a failed test case.
/// </summary>
public class FailedTestDetail
{
    public string TestName { get; set; } = string.Empty;
    public string? TestSuite { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Security vulnerability found during scanning.
/// </summary>
public class SecurityVulnerability
{
    public string Id { get; set; } = string.Empty;
    public SecuritySeverity Severity { get; set; }
    public string Package { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? FixVersion { get; set; }
    public string? CveId { get; set; }
}

/// <summary>
/// Severity levels for security vulnerabilities.
/// </summary>
public enum SecuritySeverity
{
    Info,
    Low,
    Medium,
    High,
    Critical
}

// ============================================
// Quality Gate Evaluation
// ============================================

/// <summary>
/// Result from evaluating all CI results against quality thresholds.
/// </summary>
public class QualityGateResult
{
    public EvaluationOutcome Outcome { get; set; }
    public double OverallScore { get; set; }
    public double CoverageScore { get; set; }
    public double LintScore { get; set; }
    public double SecurityScore { get; set; }
    public double BuildScore { get; set; }
    public List<QualityIssue> Issues { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
    public bool AutoFixable { get; set; }
    public int SkillLevel { get; set; }
    public QualityThresholds AppliedThresholds { get; set; } = new();
}

/// <summary>
/// Evaluation outcome for routing in the flowchart.
/// </summary>
public enum EvaluationOutcome
{
    AllPass,
    MinorIssues,
    MajorIssues,
    Critical
}

/// <summary>
/// Individual quality issue found during evaluation.
/// </summary>
public class QualityIssue
{
    public QualityIssueCategory Category { get; set; }
    public QualityIssueSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? FilePath { get; set; }
    public int? LineNumber { get; set; }
    public bool AutoFixable { get; set; }
}

/// <summary>
/// Categories for quality issues.
/// </summary>
public enum QualityIssueCategory
{
    Coverage,
    Lint,
    Security,
    Build,
    Test
}

/// <summary>
/// Severity for quality issues.
/// </summary>
public enum QualityIssueSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

// ============================================
// Quality Thresholds (skill-level-aware)
// ============================================

/// <summary>
/// Quality thresholds that vary by skill level.
/// Level 1 = 60% coverage / 10 lint, Level 5 = 90% coverage / 0 lint.
/// </summary>
public class QualityThresholds
{
    public int SkillLevel { get; set; }
    public double MinCoveragePercent { get; set; }
    public int MaxLintWarnings { get; set; }
    public int MaxLintErrors { get; set; }
    public int MaxSecurityHigh { get; set; }
    public int MaxSecurityCritical { get; set; }
    public bool RequireBuildPass { get; set; } = true;
    public bool RequireAllTestsPass { get; set; } = true;

    /// <summary>
    /// Generate thresholds based on skill level (1-5).
    /// Level 1 is most lenient, Level 5 is strictest.
    /// </summary>
    public static QualityThresholds ForSkillLevel(int skillLevel)
    {
        var level = Math.Clamp(skillLevel, 1, 5);
        return new QualityThresholds
        {
            SkillLevel = level,
            // Level 1=60%, Level 2=67.5%, Level 3=75%, Level 4=82.5%, Level 5=90%
            MinCoveragePercent = 60 + (level - 1) * 7.5,
            // Level 1=10, Level 2=7, Level 3=5, Level 4=2, Level 5=0
            MaxLintWarnings = Math.Max(0, 10 - (level - 1) * 3 + (level == 5 ? -1 : 0)),
            // All levels: 0 lint errors
            MaxLintErrors = 0,
            // Level 1=2, Level 2=1, Level 3=0, Level 4=0, Level 5=0
            MaxSecurityHigh = Math.Max(0, 2 - (level - 1)),
            // All levels: 0 critical security issues
            MaxSecurityCritical = 0,
            RequireBuildPass = true,
            RequireAllTestsPass = true
        };
    }

    /// <summary>
    /// Tighten thresholds by one notch (for progressive quality).
    /// </summary>
    public QualityThresholds Tighten()
    {
        return new QualityThresholds
        {
            SkillLevel = SkillLevel,
            MinCoveragePercent = Math.Min(95, MinCoveragePercent + 5),
            MaxLintWarnings = Math.Max(0, MaxLintWarnings - 1),
            MaxLintErrors = 0,
            MaxSecurityHigh = Math.Max(0, MaxSecurityHigh - 1),
            MaxSecurityCritical = 0,
            RequireBuildPass = true,
            RequireAllTestsPass = true
        };
    }
}

// ============================================
// Coverage, Lint, Security Check Outputs
// ============================================

/// <summary>
/// Output from coverage check activity.
/// </summary>
public class CoverageCheckResult
{
    public bool Passed { get; set; }
    public double ActualCoverage { get; set; }
    public double RequiredCoverage { get; set; }
    public double Gap { get; set; }
    public List<string> UncoveredFiles { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Output from linting check activity.
/// </summary>
public class LintCheckResult
{
    public bool Passed { get; set; }
    public int WarningCount { get; set; }
    public int ErrorCount { get; set; }
    public int MaxWarnings { get; set; }
    public int MaxErrors { get; set; }
    public List<QualityIssue> Issues { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Output from security check activity.
/// </summary>
public class SecurityCheckResult
{
    public bool Passed { get; set; }
    public int CriticalCount { get; set; }
    public int HighCount { get; set; }
    public int MediumCount { get; set; }
    public int LowCount { get; set; }
    public List<SecurityVulnerability> Vulnerabilities { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

// ============================================
// Quality Report
// ============================================

/// <summary>
/// Comprehensive quality report generated after all checks.
/// Scoring weights: Coverage 40%, Lint 25%, Security 25%, Build 10%.
/// </summary>
public class QualityReport
{
    public Guid SessionId { get; set; }
    public string RunId { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public double OverallScore { get; set; }
    public string Grade { get; set; } = string.Empty; // A, B, C, D, F
    public bool Passed { get; set; }
    public int SkillLevel { get; set; }
    public QualityThresholds AppliedThresholds { get; set; } = new();
    public CoverageCheckResult Coverage { get; set; } = new();
    public LintCheckResult Linting { get; set; } = new();
    public SecurityCheckResult Security { get; set; } = new();
    public bool BuildPassed { get; set; }
    public bool AllTestsPassed { get; set; }
    public int TotalTests { get; set; }
    public int PassedTests { get; set; }
    public int FailedTests { get; set; }
    public List<QualityIssue> AllIssues { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
    public string TeachingFeedback { get; set; } = string.Empty;
    public int ConsecutivePassCount { get; set; }
    public bool ThresholdsTightened { get; set; }
}

// ============================================
// Commit Fix
// ============================================

/// <summary>
/// Result of an auto-fix commit attempt.
/// </summary>
public class CommitFixResult
{
    public bool Success { get; set; }
    public string? CommitSha { get; set; }
    public string? CommitMessage { get; set; }
    public int FilesChanged { get; set; }
    public int AttemptNumber { get; set; }
    public int MaxAttempts { get; set; }
    public bool ShouldRetry { get; set; }
    public string? Error { get; set; }
    public List<string> FixedIssues { get; set; } = new();
}

// ============================================
// Bookmark Payloads
// ============================================

/// <summary>
/// Bookmark payload for WaitForCIResults activity.
/// The bookmark ID follows the pattern: ci-result-{sessionId}-{runId}
///
/// <para><b>Epic 31 P3 (DG-5).</b> The payload now also carries the
/// <see cref="Repository"/> (<c>owner/repo</c>) and the ambient
/// <see cref="TenantId"/>, so the CI completion poller can resolve the
/// tenant's platform driver and poll THIS run's status without any
/// out-of-band state. In-flight bookmarks serialized before this change
/// simply deserialize with an empty repository — the poller skips them
/// (they end via the timeout edge exactly as before; rollout-safe).</para>
/// </summary>
public class CIResultBookmarkPayload
{
    [JsonConstructor]
    public CIResultBookmarkPayload() { }

    public CIResultBookmarkPayload(Guid sessionId, string runId, string? repository = null, string? tenantId = null)
    {
        SessionId = sessionId;
        RunId = runId;
        BookmarkId = $"ci-result-{sessionId}-{runId}";
        Repository = repository ?? string.Empty;
        TenantId = tenantId;
    }

    public Guid SessionId { get; set; }
    public string RunId { get; set; } = string.Empty;
    public string BookmarkId { get; set; } = string.Empty;

    /// <summary>The <c>owner/repo</c> full name the run belongs to (poller scope).</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>The acting tenant (Guid string) or null in single-user/platform scope.</summary>
    public string? TenantId { get; set; }
}
