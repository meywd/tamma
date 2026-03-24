using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Activities.Assessment.Models;
using Tamma.Core.Enums;

namespace Tamma.Activities.Assessment;

/// <summary>
/// Routes the assessment based on the analysis confidence score.
/// Produces one of four outcomes:
///   - Correct (confidence >= 0.7): nextState = PLAN_DECOMPOSITION
///   - Partial (confidence >= 0.4): nextState = CLARIFY_REQUIREMENTS
///   - Incorrect (confidence &lt; 0.4): nextState = RE_EXPLAIN_STORY
///   - Timeout: nextState = DIAGNOSE_BLOCKER
///
/// Thresholds are configurable via appsettings.json.
/// </summary>
[Activity(
    "Tamma.Assessment",
    "Classify Result",
    "Route based on analysis confidence level",
    Kind = ActivityKind.Task
)]
[FlowNode("Correct", "Partial", "Incorrect", "Timeout")]
public class ClassifyResultActivity : Activity
{
    private readonly ILogger<ClassifyResultActivity>? _logger;
    private readonly IConfiguration? _configuration;

    /// <summary>Analysis result (JSON serialized AnalysisResult)</summary>
    [Input(Description = "Analysis result (JSON)")]
    public Input<string> AnalysisResultJson { get; set; } = default!;

    /// <summary>Whether the response was received (false = timeout)</summary>
    [Input(Description = "Whether a response was received")]
    public Input<bool> ResponseReceived { get; set; } = default!;

    /// <summary>The classified assessment outcome status</summary>
    [Output(Description = "Assessment outcome status")]
    public Output<AssessmentOutcomeStatus> Status { get; set; } = default!;

    /// <summary>The confidence score</summary>
    [Output(Description = "Confidence score (0.0-1.0)")]
    public Output<decimal> Confidence { get; set; } = default!;

    /// <summary>The recommended next mentorship state</summary>
    [Output(Description = "Recommended next mentorship state")]
    public Output<MentorshipState> NextState { get; set; } = default!;

    [JsonConstructor]
    public ClassifyResultActivity() { }

    public ClassifyResultActivity(
        ILogger<ClassifyResultActivity> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var responseReceived = ResponseReceived.Get(context);

        // Handle timeout case
        if (!responseReceived)
        {
            _logger?.LogInformation("Classifying assessment result: Timeout (no response received)");

            Status.Set(context, AssessmentOutcomeStatus.Timeout);
            Confidence.Set(context, 0m);
            NextState.Set(context, MentorshipState.DIAGNOSE_BLOCKER);

            await context.CompleteActivityWithOutcomesAsync("Timeout");
            return;
        }

        // Parse analysis result
        var analysisJson = AnalysisResultJson.Get(context) ?? "{}";
        AnalysisResult analysis;
        try
        {
            analysis = JsonSerializer.Deserialize<AnalysisResult>(analysisJson)
                ?? new AnalysisResult();
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Failed to deserialize analysis result, defaulting to Incorrect");
            analysis = new AnalysisResult
            {
                Status = AssessmentOutcomeStatus.Incorrect,
                Confidence = 0m
            };
        }

        // Get configurable thresholds
        var correctThreshold = GetThreshold("Correct", 0.7m);
        var partialThreshold = GetThreshold("Partial", 0.4m);

        // Classify based on confidence
        AssessmentOutcomeStatus status;
        MentorshipState nextState;
        string outcome;

        if (analysis.Confidence >= correctThreshold)
        {
            status = AssessmentOutcomeStatus.Correct;
            nextState = MentorshipState.PLAN_DECOMPOSITION;
            outcome = "Correct";
        }
        else if (analysis.Confidence >= partialThreshold)
        {
            status = AssessmentOutcomeStatus.Partial;
            nextState = MentorshipState.CLARIFY_REQUIREMENTS;
            outcome = "Partial";
        }
        else
        {
            status = AssessmentOutcomeStatus.Incorrect;
            nextState = MentorshipState.RE_EXPLAIN_STORY;
            outcome = "Incorrect";
        }

        _logger?.LogInformation(
            "Assessment classified: Status={Status}, Confidence={Confidence}, NextState={NextState}",
            status, analysis.Confidence, nextState);

        Status.Set(context, status);
        Confidence.Set(context, analysis.Confidence);
        NextState.Set(context, nextState);

        await context.CompleteActivityWithOutcomesAsync(outcome);
    }

    /// <summary>
    /// Get configurable confidence threshold from appsettings.json
    /// </summary>
    private decimal GetThreshold(string level, decimal defaultValue)
    {
        var configKey = $"Assessment:ConfidenceThresholds:{level}";
        var configValue = _configuration?[configKey];
        if (decimal.TryParse(configValue, out var configured))
        {
            return configured;
        }
        return defaultValue;
    }
}
