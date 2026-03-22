using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Activities.Assessment.Models;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Assessment;

/// <summary>
/// Analyzes the junior's assessment response using AI (LLM Call sub-workflow 7-1B).
/// Sends questions, response, story context, and skill level to the LLM for structured analysis.
/// The analysis prompt instructs the LLM to be encouraging but honest.
/// </summary>
[Activity(
    "Tamma.Assessment",
    "Analyze Response",
    "AI analysis of junior's assessment response",
    Kind = ActivityKind.Task
)]
public class AnalyzeResponseActivity : CodeActivity<AnalysisResult>
{
    private readonly ILogger<AnalyzeResponseActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;
    private readonly IConfiguration? _configuration;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Junior's skill level (1-5)</summary>
    [Input(Description = "Junior skill level (1-5)", DefaultValue = 3)]
    public Input<int> SkillLevel { get; set; } = new(3);

    /// <summary>Questions that were asked (JSON)</summary>
    [Input(Description = "Questions asked (JSON array)")]
    public Input<string> QuestionsJson { get; set; } = default!;

    /// <summary>Junior's response text</summary>
    [Input(Description = "Junior's response text")]
    public Input<string> JuniorResponse { get; set; } = default!;

    /// <summary>Story context for evaluation</summary>
    [Input(Description = "Story context for evaluation")]
    public Input<string> StoryContext { get; set; } = default!;

    [JsonConstructor]
    public AnalyzeResponseActivity() { }

    public AnalyzeResponseActivity(
        ILogger<AnalyzeResponseActivity> logger,
        IMentorshipSessionRepository repository,
        IConfiguration configuration)
    {
        _logger = logger;
        _repository = repository;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var skillLevel = Math.Clamp(SkillLevel.Get(context), 1, 5);
        var questionsJson = QuestionsJson.Get(context) ?? "[]";
        var juniorResponse = JuniorResponse.Get(context) ?? string.Empty;
        var storyContext = StoryContext.Get(context) ?? string.Empty;

        _logger?.LogInformation(
            "Analyzing assessment response for session {SessionId}, response length={ResponseLength}",
            sessionId, juniorResponse.Length);

        var startTime = DateTime.UtcNow;

        try
        {
            // In production, this delegates to the LLM Call sub-workflow (7-1B)
            // with role=analyst and AnalysisType=Assessment.
            // For now, perform a heuristic analysis.
            var result = PerformAnalysis(questionsJson, juniorResponse, storyContext, skillLevel);

            var analysisTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _logger?.LogInformation(
                "Response analysis completed for session {SessionId}: Status={Status}, Confidence={Confidence}, Duration={Duration}ms",
                sessionId, result.Status, result.Confidence, analysisTime);

            // Log the analysis event
            await _repository!.LogEventAsync(new Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Core.Entities.EventTypes.AIAnalysis,
                Trigger = "assessment_response_analysis"
            });

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error analyzing response for session {SessionId}", sessionId);

            context.SetResult(new AnalysisResult
            {
                Status = AssessmentOutcomeStatus.Incorrect,
                Confidence = 0m,
                Gaps = new List<string> { "Analysis failed - unable to evaluate response" },
                Strengths = new List<string>(),
                Rationale = $"Analysis error: {ex.Message}",
                UnderstandingSummary = "Could not analyze response"
            });
        }
    }

    /// <summary>
    /// Perform heuristic analysis of the junior's response.
    /// In production, this would be replaced by LLM-based analysis via RunWorkflow.
    /// </summary>
    private static AnalysisResult PerformAnalysis(
        string questionsJson, string juniorResponse, string storyContext, int skillLevel)
    {
        // Parse questions for evaluation
        List<string> questions;
        try
        {
            var questionSet = JsonSerializer.Deserialize<QuestionSet>(questionsJson);
            questions = questionSet?.Questions ?? new List<string>();
        }
        catch
        {
            try
            {
                questions = JsonSerializer.Deserialize<List<string>>(questionsJson) ?? new List<string>();
            }
            catch
            {
                questions = new List<string>();
            }
        }

        // Heuristic scoring based on response characteristics
        var responseLength = juniorResponse.Length;
        var questionCount = questions.Count > 0 ? questions.Count : 1;
        var avgResponsePerQuestion = responseLength / questionCount;

        var gaps = new List<string>();
        var strengths = new List<string>();
        decimal confidence;

        // Score based on response length relative to questions
        if (avgResponsePerQuestion >= 200)
        {
            confidence = 0.8m;
            strengths.Add("Detailed and thorough responses");
        }
        else if (avgResponsePerQuestion >= 100)
        {
            confidence = 0.6m;
            strengths.Add("Reasonable level of detail in responses");
        }
        else if (avgResponsePerQuestion >= 30)
        {
            confidence = 0.4m;
            gaps.Add("Responses could be more detailed");
        }
        else
        {
            confidence = 0.2m;
            gaps.Add("Very brief responses indicate possible misunderstanding");
        }

        // Check for technical terminology usage
        var technicalTerms = new[] { "implementation", "architecture", "api", "database", "test",
            "requirement", "acceptance criteria", "edge case", "design", "pattern" };
        var termsUsed = technicalTerms.Count(t =>
            juniorResponse.Contains(t, StringComparison.OrdinalIgnoreCase));

        if (termsUsed >= 3)
        {
            confidence += 0.1m;
            strengths.Add("Good use of technical vocabulary");
        }

        // Check for structured thinking
        if (juniorResponse.Contains("first", StringComparison.OrdinalIgnoreCase) ||
            juniorResponse.Contains("then", StringComparison.OrdinalIgnoreCase) ||
            juniorResponse.Contains("finally", StringComparison.OrdinalIgnoreCase) ||
            juniorResponse.Contains("step", StringComparison.OrdinalIgnoreCase))
        {
            confidence += 0.05m;
            strengths.Add("Shows structured thinking approach");
        }

        // Clamp confidence
        confidence = Math.Clamp(confidence, 0m, 1m);

        // Determine status based on thresholds
        AssessmentOutcomeStatus status;
        if (confidence >= 0.7m)
        {
            status = AssessmentOutcomeStatus.Correct;
            if (gaps.Count == 0) strengths.Add("Demonstrates strong understanding of requirements");
        }
        else if (confidence >= 0.4m)
        {
            status = AssessmentOutcomeStatus.Partial;
            gaps.Add("Some aspects of the requirements may need clarification");
        }
        else
        {
            status = AssessmentOutcomeStatus.Incorrect;
            gaps.Add("Fundamental understanding of requirements needs improvement");
        }

        return new AnalysisResult
        {
            Status = status,
            Confidence = confidence,
            Gaps = gaps,
            Strengths = strengths,
            Rationale = $"Heuristic analysis: response length={responseLength}, technical terms={termsUsed}, confidence={confidence:F2}",
            UnderstandingSummary = status switch
            {
                AssessmentOutcomeStatus.Correct => "Junior demonstrates good understanding of the requirements",
                AssessmentOutcomeStatus.Partial => "Junior has partial understanding but some gaps exist",
                AssessmentOutcomeStatus.Incorrect => "Junior shows limited understanding of the requirements",
                _ => "Unable to determine understanding level"
            }
        };
    }
}
