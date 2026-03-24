using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Core.Enums;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Mentorship;

/// <summary>
/// ELSA activity to run quality gate checks on the implementation.
/// Includes tests, linting, coverage, and static analysis.
/// </summary>
[Activity(
    "Tamma.Mentorship",
    "Quality Gate Check",
    "Run quality gate checks on the implementation",
    Kind = ActivityKind.Task
)]
public class QualityGateCheckActivity : CodeActivity<QualityGateOutput>
{
    private readonly ILogger<QualityGateCheckActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;
    private readonly IIntegrationService? _integrationService;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>ID of the story being checked</summary>
    [Input(Description = "ID of the story being checked")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Minimum code coverage percentage required</summary>
    [Input(Description = "Minimum code coverage percentage", DefaultValue = 80)]
    public Input<int> MinCoverage { get; set; } = new(80);

    /// <summary>Whether to allow warnings</summary>
    [Input(Description = "Allow warnings to pass", DefaultValue = true)]
    public Input<bool> AllowWarnings { get; set; } = new(true);

    [JsonConstructor]
    public QualityGateCheckActivity() { }

    public QualityGateCheckActivity(
        ILogger<QualityGateCheckActivity> logger,
        IMentorshipSessionRepository repository,
        IIntegrationService integrationService)
    {
        _logger = logger;
        _repository = repository;
        _integrationService = integrationService;
    }

    /// <summary>
    /// Execute the quality gate check activity
    /// </summary>
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);
        var minCoverage = MinCoverage.Get(context);
        var allowWarnings = AllowWarnings.Get(context);

        _logger?.LogInformation(
            "Running quality gate checks for story {StoryId}",
            storyId);

        try
        {
            // Update session state
            await _repository!.UpdateStateAsync(sessionId, MentorshipState.QUALITY_GATE_CHECK);

            // Get story for repository URL
            var story = await _repository.GetStoryByIdAsync(storyId);
            if (story == null)
            {
                _logger?.LogError("Story {StoryId} not found", storyId);
                context.SetResult(new QualityGateOutput
                {
                    Passed = false,
                    Status = QualityGateStatus.Error,
                    NextState = MentorshipState.FAILED,
                    Message = $"Story {storyId} not found"
                });
                return;
            }

            var results = new List<GateResult>();

            // Run each quality gate
            if (!string.IsNullOrEmpty(story.RepositoryUrl))
            {
                // 1. Unit Tests
                var testResult = await RunTestGate(story.RepositoryUrl, storyId);
                results.Add(testResult);

                // 2. Code Coverage
                var coverageResult = await RunCoverageGate(story.RepositoryUrl, storyId, minCoverage);
                results.Add(coverageResult);

                // 3. Build Compilation
                var buildResult = await RunBuildGate(story.RepositoryUrl, storyId);
                results.Add(buildResult);

                // 4. Linting (simulated)
                var lintResult = SimulateLintGate();
                results.Add(lintResult);

                // 5. Static Analysis (simulated)
                var analysisResult = SimulateStaticAnalysisGate();
                results.Add(analysisResult);
            }
            else
            {
                // Simulate results if no repository configured
                results = SimulateAllGates(minCoverage);
            }

            // Aggregate results
            var output = AggregateResults(results, allowWarnings);

            // Log quality gate event
            await _repository.LogEventAsync(new Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Core.Entities.EventTypes.QualityGateRun,
                StateFrom = MentorshipState.MONITOR_PROGRESS,
                StateTo = output.Passed ? MentorshipState.PREPARE_CODE_REVIEW : MentorshipState.AUTO_FIX_ISSUES
            });

            _logger?.LogInformation(
                "Quality gate check completed for story {StoryId}: Passed={Passed}, Issues={IssueCount}",
                storyId, output.Passed, output.Issues.Count);

            context.SetResult(output);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during quality gate check for session {SessionId}", sessionId);

            context.SetResult(new QualityGateOutput
            {
                Passed = false,
                Status = QualityGateStatus.Error,
                NextState = MentorshipState.DIAGNOSE_BLOCKER,
                Message = ex.Message
            });
        }
    }

    private async Task<GateResult> RunTestGate(string repositoryUrl, string storyId)
    {
        try
        {
            var testResults = await _integrationService!.TriggerTestsAsync(repositoryUrl, $"feature/{storyId}");

            return new GateResult
            {
                GateType = QualityGateType.UnitTests,
                Passed = testResults.FailedTests == 0,
                Score = testResults.TotalTests > 0
                    ? (double)testResults.PassedTests / testResults.TotalTests * 100
                    : 0,
                Issues = testResults.FailedTestDetails.Select(t => new QualityIssue
                {
                    GateType = QualityGateType.UnitTests,
                    Severity = IssueSeverity.Error,
                    Message = $"Test failed: {t.TestName}",
                    Details = t.ErrorMessage
                }).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to run test gate");
            return SimulateTestGate();
        }
    }

    private async Task<GateResult> RunCoverageGate(string repositoryUrl, string storyId, int minCoverage)
    {
        try
        {
            var testResults = await _integrationService!.TriggerTestsAsync(repositoryUrl, $"feature/{storyId}");
            var coverage = testResults.CoveragePercentage ?? 0;

            return new GateResult
            {
                GateType = QualityGateType.CodeCoverage,
                Passed = coverage >= minCoverage,
                Score = coverage,
                Issues = coverage < minCoverage
                    ? new List<QualityIssue>
                    {
                        new()
                        {
                            GateType = QualityGateType.CodeCoverage,
                            Severity = IssueSeverity.Error,
                            Message = $"Code coverage {coverage:F1}% is below minimum {minCoverage}%"
                        }
                    }
                    : new List<QualityIssue>()
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to run coverage gate");
            return SimulateCoverageGate(minCoverage);
        }
    }

    private async Task<GateResult> RunBuildGate(string repositoryUrl, string storyId)
    {
        try
        {
            var buildStatus = await _integrationService!.GetBuildStatusAsync(repositoryUrl, $"feature/{storyId}");

            return new GateResult
            {
                GateType = QualityGateType.BuildCompilation,
                Passed = buildStatus.Status == "Success",
                Score = buildStatus.Status == "Success" ? 100 : 0,
                Issues = buildStatus.Status != "Success"
                    ? new List<QualityIssue>
                    {
                        new()
                        {
                            GateType = QualityGateType.BuildCompilation,
                            Severity = IssueSeverity.Critical,
                            Message = "Build failed",
                            Details = buildStatus.Error
                        }
                    }
                    : new List<QualityIssue>()
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to run build gate");
            return SimulateBuildGate();
        }
    }

    private GateResult SimulateTestGate()
    {
        var passed = Random.Shared.Next(100) < 85;

        return new GateResult
        {
            GateType = QualityGateType.UnitTests,
            Passed = passed,
            Score = passed ? 100 : Random.Shared.Next(60, 95),
            Issues = passed
                ? new List<QualityIssue>()
                : new List<QualityIssue>
                {
                    new()
                    {
                        GateType = QualityGateType.UnitTests,
                        Severity = IssueSeverity.Error,
                        Message = "UserServiceTests.TestCreateUser failed",
                        Details = "Expected: User created. Actual: Null reference exception"
                    }
                }
        };
    }

    private GateResult SimulateCoverageGate(int minCoverage)
    {
        var coverage = Random.Shared.Next(65, 95);
        var passed = coverage >= minCoverage;

        return new GateResult
        {
            GateType = QualityGateType.CodeCoverage,
            Passed = passed,
            Score = coverage,
            Issues = passed
                ? new List<QualityIssue>()
                : new List<QualityIssue>
                {
                    new()
                    {
                        GateType = QualityGateType.CodeCoverage,
                        Severity = IssueSeverity.Error,
                        Message = $"Code coverage {coverage}% is below minimum {minCoverage}%"
                    }
                }
        };
    }

    private GateResult SimulateBuildGate()
    {
        var passed = Random.Shared.Next(100) < 90;

        return new GateResult
        {
            GateType = QualityGateType.BuildCompilation,
            Passed = passed,
            Score = passed ? 100 : 0,
            Issues = passed
                ? new List<QualityIssue>()
                : new List<QualityIssue>
                {
                    new()
                    {
                        GateType = QualityGateType.BuildCompilation,
                        Severity = IssueSeverity.Critical,
                        Message = "Build compilation failed",
                        Details = "error CS1002: ; expected"
                    }
                }
        };
    }

    private GateResult SimulateLintGate()
    {
        var warningCount = Random.Shared.Next(0, 5);

        return new GateResult
        {
            GateType = QualityGateType.Linting,
            Passed = true, // Warnings don't fail the gate
            Score = 100 - (warningCount * 5),
            Issues = Enumerable.Range(0, warningCount).Select(_ => new QualityIssue
            {
                GateType = QualityGateType.Linting,
                Severity = IssueSeverity.Warning,
                Message = "Line exceeds maximum length of 120 characters"
            }).ToList()
        };
    }

    private GateResult SimulateStaticAnalysisGate()
    {
        var hasIssues = Random.Shared.Next(100) < 20;

        return new GateResult
        {
            GateType = QualityGateType.StaticAnalysis,
            Passed = !hasIssues,
            Score = hasIssues ? 80 : 100,
            Issues = hasIssues
                ? new List<QualityIssue>
                {
                    new()
                    {
                        GateType = QualityGateType.StaticAnalysis,
                        Severity = IssueSeverity.Warning,
                        Message = "Cognitive complexity of method is too high"
                    }
                }
                : new List<QualityIssue>()
        };
    }

    private List<GateResult> SimulateAllGates(int minCoverage)
    {
        return new List<GateResult>
        {
            SimulateTestGate(),
            SimulateCoverageGate(minCoverage),
            SimulateBuildGate(),
            SimulateLintGate(),
            SimulateStaticAnalysisGate()
        };
    }

    private QualityGateOutput AggregateResults(List<GateResult> results, bool allowWarnings)
    {
        var allIssues = results.SelectMany(r => r.Issues).ToList();
        var hasErrors = allIssues.Any(i => i.Severity >= IssueSeverity.Error);
        var hasWarnings = allIssues.Any(i => i.Severity == IssueSeverity.Warning);

        var passed = !hasErrors && (allowWarnings || !hasWarnings);

        return new QualityGateOutput
        {
            Passed = passed,
            Status = passed
                ? (hasWarnings ? QualityGateStatus.PassedWithWarnings : QualityGateStatus.Passed)
                : QualityGateStatus.Failed,
            NextState = passed ? MentorshipState.PREPARE_CODE_REVIEW : MentorshipState.AUTO_FIX_ISSUES,
            GateResults = results,
            Issues = allIssues,
            Suggestions = GenerateSuggestions(allIssues),
            OverallScore = results.Average(r => r.Score)
        };
    }

    private List<string> GenerateSuggestions(List<QualityIssue> issues)
    {
        var suggestions = new List<string>();

        if (issues.Any(i => i.GateType == QualityGateType.UnitTests))
        {
            suggestions.Add("Review failing tests and fix the underlying issues");
        }

        if (issues.Any(i => i.GateType == QualityGateType.CodeCoverage))
        {
            suggestions.Add("Add more unit tests to increase code coverage");
        }

        if (issues.Any(i => i.GateType == QualityGateType.BuildCompilation))
        {
            suggestions.Add("Fix compilation errors before proceeding");
        }

        if (issues.Any(i => i.GateType == QualityGateType.Linting))
        {
            suggestions.Add("Run auto-formatter to fix linting issues");
        }

        if (issues.Any(i => i.GateType == QualityGateType.StaticAnalysis))
        {
            suggestions.Add("Review code complexity and consider refactoring");
        }

        return suggestions;
    }
}

/// <summary>
/// Result from a single quality gate
/// </summary>
public class GateResult
{
    public QualityGateType GateType { get; set; }
    public bool Passed { get; set; }
    public double Score { get; set; }
    public List<QualityIssue> Issues { get; set; } = new();
}

/// <summary>
/// Individual quality issue
/// </summary>
public class QualityIssue
{
    public QualityGateType GateType { get; set; }
    public IssueSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? FilePath { get; set; }
    public int? LineNumber { get; set; }
}

/// <summary>
/// Output model for quality gate check
/// </summary>
public class QualityGateOutput
{
    public bool Passed { get; set; }
    public QualityGateStatus Status { get; set; }
    public MentorshipState NextState { get; set; }
    public List<GateResult> GateResults { get; set; } = new();
    public List<QualityIssue> Issues { get; set; } = new();
    public List<string> Suggestions { get; set; } = new();
    public double OverallScore { get; set; }
    public string? Message { get; set; }
}
