using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Activities.Blocker.Models;

namespace Tamma.Activities.Blocker;

/// <summary>
/// Classifies a blocker into one of 8 categories and determines severity.
/// Uses the AI diagnosis result plus collected signals to produce a classification.
/// Each blocker type has a recommended resolution strategy.
///
/// The 8 categories:
///   1. ConceptualMisunderstanding — doesn't understand the requirement
///   2. TechnicalKnowledgeGap — lacks specific technical skill
///   3. EnvironmentIssue — tooling, build, or environment problem
///   4. DesignDecisionParalysis — can't decide on approach
///   5. DebuggingStuck — can't find or fix a bug
///   6. IntegrationIssue — components don't work together
///   7. ExternalDependency — blocked by external team/API/service
///   8. PersonalBlocker — motivation, distraction, or capacity issue
/// </summary>
[Activity(
    "Tamma.Blocker",
    "Classify Blocker",
    "Categorize blocker type (8 categories) and determine severity",
    Kind = ActivityKind.Task
)]
public class ClassifyBlockerActivity : CodeActivity<BlockerDiagnosisResult>
{
    private readonly ILogger<ClassifyBlockerActivity>? _logger;

    /// <summary>Aggregated signals from parallel collection</summary>
    [Input(Description = "Aggregated signals from parallel signal collection")]
    public Input<AggregatedSignals> Signals { get; set; } = default!;

    /// <summary>AI diagnosis raw response (JSON string from LLM)</summary>
    [Input(Description = "AI diagnosis raw response from LLM call")]
    public Input<string?> AIDiagnosisResponse { get; set; } = default!;

    /// <summary>Junior developer's skill level (1-5)</summary>
    [Input(Description = "Junior skill level (1-5)", DefaultValue = 3)]
    public Input<int> SkillLevel { get; set; } = new(3);

    /// <summary>Additional blocker context (optional)</summary>
    [Input(Description = "Additional blocker context")]
    public Input<string?> BlockerContext { get; set; } = default!;

    [JsonConstructor]
    public ClassifyBlockerActivity() { }

    public ClassifyBlockerActivity(ILogger<ClassifyBlockerActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var signals = Signals.Get(context);
        var aiResponse = AIDiagnosisResponse.GetOrDefault(context);
        var skillLevel = Math.Clamp(SkillLevel.Get(context), 1, 5);
        var blockerContext = BlockerContext.GetOrDefault(context);

        _logger?.LogInformation("Classifying blocker from {SuccessfulCollectors}/{TotalCollectors} signals",
            signals.SuccessfulCollectors, signals.TotalCollectors);

        var result = new BlockerDiagnosisResult();

        try
        {
            // Try to parse AI diagnosis if available
            if (!string.IsNullOrEmpty(aiResponse))
            {
                result = ParseAIDiagnosis(aiResponse, signals, skillLevel);
            }
            else
            {
                // Fall back to rule-based classification from signals
                result = ClassifyFromSignals(signals, skillLevel, blockerContext);
            }

            _logger?.LogInformation(
                "Blocker classified: Type={BlockerType}, Severity={Severity}, Confidence={Confidence}",
                result.BlockerType, result.Severity, result.Confidence);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error during blocker classification — using fallback");
            result = ClassifyFromSignals(signals, skillLevel, blockerContext);
        }

        await ValueTask.CompletedTask;
        context.SetResult(result);
    }

    private BlockerDiagnosisResult ParseAIDiagnosis(
        string aiResponse,
        AggregatedSignals signals,
        int skillLevel)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(aiResponse);

            var blockerTypeStr = json.TryGetProperty("blocker_type", out var bt)
                ? bt.GetString() ?? ""
                : "";
            var confidence = json.TryGetProperty("confidence", out var conf)
                ? conf.GetDouble()
                : 0.5;
            var rootCause = json.TryGetProperty("root_cause", out var rc)
                ? rc.GetString() ?? ""
                : "";
            var approach = json.TryGetProperty("recommended_approach", out var ra)
                ? ra.GetString() ?? ""
                : json.TryGetProperty("recommended_intervention", out var ri)
                    ? ri.GetString() ?? ""
                    : "";
            var evidence = json.TryGetProperty("evidence", out var ev)
                ? JsonSerializer.Deserialize<List<string>>(ev.GetRawText()) ?? new()
                : new List<string>();

            var blockerType = MapStringToBlockerCategory(blockerTypeStr);
            var severity = DetermineSeverity(signals, skillLevel, blockerType);

            return new BlockerDiagnosisResult
            {
                BlockerType = blockerType,
                Severity = severity,
                RootCauseHypothesis = rootCause,
                RecommendedApproach = approach,
                Confidence = confidence,
                Evidence = evidence
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse AI diagnosis response — using signal-based fallback");
            return ClassifyFromSignals(signals, skillLevel, null);
        }
    }

    private BlockerDiagnosisResult ClassifyFromSignals(
        AggregatedSignals signals,
        int skillLevel,
        string? blockerContext)
    {
        // Rule-based classification from signals

        // 1. Build failure → EnvironmentIssue or TechnicalKnowledgeGap
        if (signals.CIStatus?.CollectionSucceeded == true
            && signals.CIStatus.BuildStatus == "Failed")
        {
            var isSyntax = signals.CIStatus.BuildError?.Contains("CS") == true
                || signals.CIStatus.BuildError?.Contains("syntax") == true;

            return new BlockerDiagnosisResult
            {
                BlockerType = isSyntax
                    ? BlockerCategory.TechnicalKnowledgeGap
                    : BlockerCategory.EnvironmentIssue,
                Severity = DetermineSeverity(signals, skillLevel,
                    isSyntax ? BlockerCategory.TechnicalKnowledgeGap : BlockerCategory.EnvironmentIssue),
                RootCauseHypothesis = isSyntax
                    ? "Syntax or compilation error — possible knowledge gap"
                    : "Build configuration or environment issue",
                RecommendedApproach = isSyntax ? "Guidance" : "Assistance",
                Confidence = 0.7,
                Evidence = new List<string> { $"Build failed: {signals.CIStatus.BuildError}" }
            };
        }

        // 2. Multiple test failures → DebuggingStuck
        if (signals.CIStatus?.CollectionSucceeded == true
            && signals.CIStatus.FailedTests > 3)
        {
            return new BlockerDiagnosisResult
            {
                BlockerType = BlockerCategory.DebuggingStuck,
                Severity = DetermineSeverity(signals, skillLevel, BlockerCategory.DebuggingStuck),
                RootCauseHypothesis = "Multiple test failures indicate debugging difficulty",
                RecommendedApproach = "Guidance",
                Confidence = 0.65,
                Evidence = signals.CIStatus.FailingTestNames
                    .Take(5)
                    .Select(t => $"Failing test: {t}")
                    .ToList()
            };
        }

        // 3. Prolonged inactivity → ConceptualMisunderstanding or PersonalBlocker
        if (signals.Inactivity?.CollectionSucceeded == true
            && signals.Inactivity.IsInactive
            && signals.Inactivity.TimeSinceLastActivity.TotalMinutes > 60)
        {
            // If no communication either, might be personal blocker
            var noComms = signals.Communication?.CollectionSucceeded != true
                || !signals.Communication.HasRecentCommunication;

            return new BlockerDiagnosisResult
            {
                BlockerType = noComms
                    ? BlockerCategory.PersonalBlocker
                    : BlockerCategory.ConceptualMisunderstanding,
                Severity = DetermineSeverity(signals, skillLevel,
                    noComms ? BlockerCategory.PersonalBlocker : BlockerCategory.ConceptualMisunderstanding),
                RootCauseHypothesis = noComms
                    ? "Prolonged inactivity with no communication — possible personal blocker"
                    : "Prolonged inactivity — may not understand requirements",
                RecommendedApproach = "Hint",
                Confidence = 0.5,
                Evidence = new List<string>
                {
                    $"Inactive for {signals.Inactivity.TimeSinceLastActivity.TotalMinutes:F0} minutes"
                }
            };
        }

        // 4. No file changes but commits exist → DesignDecisionParalysis
        if (signals.GitActivity?.CollectionSucceeded == true
            && signals.GitActivity.RecentCommitCount > 0
            && signals.GitActivity.FilesChanged == 0)
        {
            return new BlockerDiagnosisResult
            {
                BlockerType = BlockerCategory.DesignDecisionParalysis,
                Severity = DetermineSeverity(signals, skillLevel, BlockerCategory.DesignDecisionParalysis),
                RootCauseHypothesis = "Commits without meaningful file changes suggest indecision",
                RecommendedApproach = "Guidance",
                Confidence = 0.5,
                Evidence = new List<string>
                {
                    $"{signals.GitActivity.RecentCommitCount} commits but no file changes"
                }
            };
        }

        // 5. Check additional context keywords
        if (!string.IsNullOrEmpty(blockerContext))
        {
            var contextLower = blockerContext.ToLower();

            if (contextLower.Contains("dependency") || contextLower.Contains("api") || contextLower.Contains("service"))
            {
                return new BlockerDiagnosisResult
                {
                    BlockerType = BlockerCategory.ExternalDependency,
                    Severity = BlockerDiagnosisSeverity.Medium,
                    RootCauseHypothesis = "External dependency or service issue",
                    RecommendedApproach = "Escalation",
                    Confidence = 0.6,
                    Evidence = new List<string> { $"Context mentions: {blockerContext}" }
                };
            }

            if (contextLower.Contains("integrate") || contextLower.Contains("connect") || contextLower.Contains("together"))
            {
                return new BlockerDiagnosisResult
                {
                    BlockerType = BlockerCategory.IntegrationIssue,
                    Severity = BlockerDiagnosisSeverity.Medium,
                    RootCauseHypothesis = "Components not working together",
                    RecommendedApproach = "Assistance",
                    Confidence = 0.6,
                    Evidence = new List<string> { $"Context mentions: {blockerContext}" }
                };
            }
        }

        // 6. Default: TechnicalKnowledgeGap (most common for juniors)
        return new BlockerDiagnosisResult
        {
            BlockerType = BlockerCategory.TechnicalKnowledgeGap,
            Severity = DetermineSeverity(signals, skillLevel, BlockerCategory.TechnicalKnowledgeGap),
            RootCauseHypothesis = "Insufficient diagnostic data — defaulting to technical knowledge gap",
            RecommendedApproach = "Hint",
            Confidence = 0.3,
            Evidence = new List<string> { "Default classification — insufficient signals" }
        };
    }

    private static BlockerDiagnosisSeverity DetermineSeverity(
        AggregatedSignals signals,
        int skillLevel,
        BlockerCategory blockerType)
    {
        // Severity factors: time stuck, impact on timeline, skill level mismatch
        var inactiveMinutes = signals.Inactivity?.TimeSinceLastActivity.TotalMinutes ?? 0;

        // Base severity from inactivity duration
        var baseSeverity = inactiveMinutes switch
        {
            > 120 => BlockerDiagnosisSeverity.Critical,
            > 60 => BlockerDiagnosisSeverity.High,
            > 30 => BlockerDiagnosisSeverity.Medium,
            _ => BlockerDiagnosisSeverity.Low
        };

        // Upgrade severity for low skill levels
        if (skillLevel <= 2 && baseSeverity < BlockerDiagnosisSeverity.High)
        {
            baseSeverity = (BlockerDiagnosisSeverity)((int)baseSeverity + 1);
        }

        // Certain blocker types are inherently more severe
        if (blockerType == BlockerCategory.ExternalDependency
            || blockerType == BlockerCategory.PersonalBlocker)
        {
            if (baseSeverity < BlockerDiagnosisSeverity.Medium)
                baseSeverity = BlockerDiagnosisSeverity.Medium;
        }

        return baseSeverity;
    }

    private static BlockerCategory MapStringToBlockerCategory(string value)
    {
        var normalized = value.Replace("_", "").Replace("-", "").Replace(" ", "").ToLower();

        return normalized switch
        {
            "conceptualmisunderstanding" or "requirementsunclear" => BlockerCategory.ConceptualMisunderstanding,
            "technicalknowledgegap" => BlockerCategory.TechnicalKnowledgeGap,
            "environmentissue" => BlockerCategory.EnvironmentIssue,
            "designdecisionparalysis" or "architectureconfusion" => BlockerCategory.DesignDecisionParalysis,
            "debuggingstuck" or "testingchallenge" => BlockerCategory.DebuggingStuck,
            "integrationissue" or "dependencyissue" => BlockerCategory.IntegrationIssue,
            "externaldependency" => BlockerCategory.ExternalDependency,
            "personalblocker" or "motivationissue" => BlockerCategory.PersonalBlocker,
            _ => BlockerCategory.TechnicalKnowledgeGap
        };
    }
}
