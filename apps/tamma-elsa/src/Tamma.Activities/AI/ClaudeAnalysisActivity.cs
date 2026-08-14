using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Activities.LlmCall;
using Tamma.Core.Enums;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.AI;

/// <summary>
/// ELSA activity to call Claude for intelligent analysis of code, responses, and situations.
/// Supports two modes:
///   1. Mediated LLM call (default — routes through POST /api/v1/llm/call)
///   2. Mock mode (when Anthropic:UseMock=true — for testing)
///
/// <para>Story 32-5 (AC9): the LLM call routes through the mediated call-LLM
/// endpoint (<see cref="MediatedLlmText"/>) — the engine holds NO provider key
/// and makes no direct <c>/v1/messages</c> call. The output contract
/// (<see cref="ClaudeAnalysisOutput"/>) is unchanged.</para>
/// </summary>
[Activity(
    "Tamma.AI",
    "Claude Analysis",
    "Call Claude API for intelligent analysis and insights",
    Kind = ActivityKind.Task
)]
public class ClaudeAnalysisActivity : CodeActivity<ClaudeAnalysisOutput>
{
    private readonly ILogger<ClaudeAnalysisActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Type of analysis to perform</summary>
    [Input(Description = "Analysis type: Assessment, CodeReview, BlockerDiagnosis, GuidanceGeneration")]
    public Input<AnalysisType> AnalysisType { get; set; } = default!;

    /// <summary>Content to analyze</summary>
    [Input(Description = "Content to analyze")]
    public Input<string> Content { get; set; } = default!;

    /// <summary>Additional context for the analysis</summary>
    [Input(Description = "Additional context")]
    public Input<string?> Context { get; set; } = default!;

    /// <summary>Junior developer's skill level (1-5)</summary>
    [Input(Description = "Junior skill level", DefaultValue = 3)]
    public Input<int> SkillLevel { get; set; } = new(3);

    [JsonConstructor]
    public ClaudeAnalysisActivity() { }

    public ClaudeAnalysisActivity(
        ILogger<ClaudeAnalysisActivity> logger,
        IMentorshipSessionRepository repository,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _logger = logger;
        _repository = repository;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var analysisType = AnalysisType.Get(context);
        var content = Content.Get(context);
        var additionalContext = Context.GetOrDefault(context);
        var skillLevel = Math.Clamp(SkillLevel.Get(context), 1, 5);

        _logger?.LogInformation(
            "Starting Claude analysis of type {AnalysisType} for session {SessionId}",
            analysisType, sessionId);

        try
        {
            var systemPrompt = GetSystemPrompt(analysisType, skillLevel);
            var userPrompt = GetUserPrompt(analysisType, content, additionalContext);

            var useMock = _configuration?.GetValue<bool>("Anthropic:UseMock") ?? false;

            // The composed system+user prompt is this legacy activity's authoritative
            // instruction; route it through the mediated call-LLM endpoint.
            var response = useMock
                ? SimulateClaudeResponse(analysisType)
                : await MediatedLlmText.CompleteAsync(
                    context, "senior_developer", $"{systemPrompt}\n\n{userPrompt}", context.CancellationToken,
                    action: "code-review");

            var result = ParseResponse(response, analysisType);

            await _repository!.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.AIAnalysis
            });

            _logger?.LogInformation(
                "Claude analysis completed for session {SessionId}: Confidence={Confidence}",
                sessionId, result.Confidence);

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during Claude analysis for session {SessionId}", sessionId);

            context.SetResult(new ClaudeAnalysisOutput
            {
                Success = false,
                AnalysisType = analysisType,
                Message = $"Analysis failed: {ex.Message}",
                Confidence = 0,
                FallbackUsed = true
            });
        }
    }

    private string GetSystemPrompt(AnalysisType type, int skillLevel)
    {
        var basePrompt = "You are Tamma, an AI mentorship assistant for junior developers. " +
            "Be constructive, focus on learning opportunities, and adapt your language complexity to the developer's skill level.";

        var skillDescription = skillLevel switch
        {
            1 => "The developer is a complete beginner. Use simple terms and explain concepts thoroughly.",
            2 => "The developer has basic knowledge. You can assume familiarity with fundamental concepts.",
            3 => "The developer has intermediate skills. You can use standard technical terminology.",
            4 => "The developer is advanced. You can discuss complex patterns and optimizations.",
            5 => "The developer is highly skilled. Focus on nuanced improvements and best practices.",
            _ => "The developer has intermediate skills."
        };

        var typeSpecificPrompt = type switch
        {
            AI.AnalysisType.Assessment =>
                "Evaluate whether the junior developer understands the story/task requirements correctly, partially, or incorrectly.",

            AI.AnalysisType.CodeReview =>
                "Review the code submitted by the junior developer and provide constructive feedback that helps them learn.",

            AI.AnalysisType.BlockerDiagnosis =>
                "Diagnose why the junior developer is stuck: identify the type and root cause of the blocker.",

            AI.AnalysisType.GuidanceGeneration =>
                "Generate clear, actionable guidance that helps the stuck developer learn while solving their immediate problem. " +
                "Use the Socratic method when appropriate — guide them to the answer rather than just telling them.",

            _ => ""
        };

        return $"{basePrompt}\n\n{skillDescription}\n\n{typeSpecificPrompt}";
    }

    private string GetUserPrompt(AnalysisType type, string content, string? additionalContext)
    {
        var contextSection = string.IsNullOrEmpty(additionalContext)
            ? ""
            : $"\n\nAdditional Context:\n{additionalContext}";

        return type switch
        {
            AI.AnalysisType.Assessment => $@"
Please analyze this response from a junior developer about their understanding of a task:

---
{content}
---
{contextSection}

Provide your analysis in the following JSON format:
{{
    ""status"": ""Correct|Partial|Incorrect"",
    ""confidence"": 0.0-1.0,
    ""understanding_summary"": ""brief summary of their understanding"",
    ""gaps"": [""list of knowledge gaps if any""],
    ""strengths"": [""positive aspects of their response""],
    ""recommended_action"": ""what should happen next""
}}",

            AI.AnalysisType.CodeReview => $@"
Please review this code:

```
{content}
```
{contextSection}

Provide your review in the following JSON format:
{{
    ""overall_quality"": ""Good|Acceptable|NeedsWork"",
    ""score"": 0-100,
    ""issues"": [
        {{
            ""severity"": ""Critical|Major|Minor|Suggestion"",
            ""location"": ""file:line or description"",
            ""issue"": ""what's wrong"",
            ""suggestion"": ""how to fix it""
        }}
    ],
    ""positives"": [""good things about the code""],
    ""learning_opportunities"": [""concepts they could learn more about""]
}}",

            AI.AnalysisType.BlockerDiagnosis => $@"
Analyze this situation where a junior developer appears to be stuck:

---
{content}
---
{contextSection}

Provide your diagnosis in the following JSON format:
{{
    ""blocker_type"": ""RequirementsUnclear|TechnicalKnowledgeGap|EnvironmentIssue|ArchitectureConfusion|TestingChallenge|MotivationIssue|Other"",
    ""confidence"": 0.0-1.0,
    ""root_cause"": ""the underlying reason for the blocker"",
    ""evidence"": [""observations that support this diagnosis""],
    ""recommended_intervention"": ""Hint|Guidance|DirectAssistance|Escalation"",
    ""immediate_action"": ""what to do right now""
}}",

            AI.AnalysisType.GuidanceGeneration => $@"
Generate helpful guidance for this junior developer situation:

---
{content}
---
{contextSection}

Provide guidance in the following JSON format:
{{
    ""main_guidance"": ""the key message or guidance"",
    ""steps"": [""step 1"", ""step 2"", ...],
    ""examples"": [""relevant examples if helpful""],
    ""questions_to_ask_themselves"": [""Socratic questions""],
    ""resources"": [""helpful resources or documentation""],
    ""encouragement"": ""a motivating message""
}}",

            _ => content
        };
    }

    private static string SimulateAssessmentStatus()
    {
        var r = Random.Shared.Next(100);
        return r < 60 ? "Correct" : r < 88 ? "Partial" : "Incorrect";
    }

    private string SimulateClaudeResponse(AnalysisType type)
    {
        return type switch
        {
            AI.AnalysisType.Assessment => JsonSerializer.Serialize(new
            {
                status = SimulateAssessmentStatus(),
                confidence = 0.7 + (Random.Shared.NextDouble() * 0.25),
                understanding_summary = "The developer shows a good grasp of the core requirements",
                gaps = new[] { "Edge case handling not mentioned", "Testing approach unclear" },
                strengths = new[] { "Clear understanding of main user flow", "Good technical vocabulary" },
                recommended_action = "Proceed with minor clarifications needed"
            }),

            AI.AnalysisType.CodeReview => JsonSerializer.Serialize(new
            {
                overall_quality = "Acceptable",
                score = 75 + Random.Shared.Next(20),
                issues = new[]
                {
                    new
                    {
                        severity = "Minor",
                        location = "line 45",
                        issue = "Variable could have a more descriptive name",
                        suggestion = "Rename 'x' to 'userCount'"
                    }
                },
                positives = new[] { "Good code structure", "Follows naming conventions" },
                learning_opportunities = new[] { "Error handling patterns", "Unit testing" }
            }),

            AI.AnalysisType.BlockerDiagnosis => JsonSerializer.Serialize(new
            {
                blocker_type = "TechnicalKnowledgeGap",
                confidence = 0.8,
                root_cause = "Unfamiliarity with async/await patterns",
                evidence = new[] { "Multiple attempts at same code pattern", "Error messages related to promises" },
                recommended_intervention = "Guidance",
                immediate_action = "Explain async/await fundamentals with a simple example"
            }),

            AI.AnalysisType.GuidanceGeneration => JsonSerializer.Serialize(new
            {
                main_guidance = "Let's break this problem down into smaller pieces",
                steps = new[]
                {
                    "First, identify what data you need",
                    "Then, write a simple function to fetch that data",
                    "Finally, use the data in your component"
                },
                examples = new[] { "Here's a similar pattern from the codebase: UserService.GetUser()" },
                questions_to_ask_themselves = new[]
                {
                    "What should this function return?",
                    "What happens if the data isn't available?"
                },
                resources = new[] { "MDN async/await guide", "Project wiki: API patterns" },
                encouragement = "You're making great progress! This concept clicks once you see it in action."
            }),

            _ => "{}"
        };
    }

    private ClaudeAnalysisOutput ParseResponse(string response, AnalysisType type)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(response);

            var output = new ClaudeAnalysisOutput
            {
                Success = true,
                AnalysisType = type,
                RawResponse = response,
                FallbackUsed = false
            };

            switch (type)
            {
                case AI.AnalysisType.Assessment:
                    output.AssessmentStatus = json.GetProperty("status").GetString();
                    output.Confidence = json.GetProperty("confidence").GetDouble();
                    output.Summary = json.GetProperty("understanding_summary").GetString();
                    output.Gaps = JsonSerializer.Deserialize<List<string>>(
                        json.GetProperty("gaps").GetRawText()) ?? new();
                    output.Strengths = JsonSerializer.Deserialize<List<string>>(
                        json.GetProperty("strengths").GetRawText()) ?? new();
                    output.RecommendedAction = json.GetProperty("recommended_action").GetString();
                    break;

                case AI.AnalysisType.CodeReview:
                    output.OverallQuality = json.GetProperty("overall_quality").GetString();
                    output.Score = json.GetProperty("score").GetInt32();
                    output.CodeReviewIssues = JsonSerializer.Deserialize<List<CodeReviewIssue>>(
                        json.GetProperty("issues").GetRawText()) ?? new();
                    output.Positives = JsonSerializer.Deserialize<List<string>>(
                        json.GetProperty("positives").GetRawText()) ?? new();
                    output.LearningOpportunities = JsonSerializer.Deserialize<List<string>>(
                        json.GetProperty("learning_opportunities").GetRawText()) ?? new();
                    output.Confidence = output.Score / 100.0;
                    break;

                case AI.AnalysisType.BlockerDiagnosis:
                    output.DiagnosedBlockerType = json.GetProperty("blocker_type").GetString();
                    output.Confidence = json.GetProperty("confidence").GetDouble();
                    output.RootCause = json.GetProperty("root_cause").GetString();
                    output.Evidence = JsonSerializer.Deserialize<List<string>>(
                        json.GetProperty("evidence").GetRawText()) ?? new();
                    output.RecommendedIntervention = json.GetProperty("recommended_intervention").GetString();
                    output.ImmediateAction = json.GetProperty("immediate_action").GetString();
                    break;

                case AI.AnalysisType.GuidanceGeneration:
                    output.MainGuidance = json.GetProperty("main_guidance").GetString();
                    output.Steps = JsonSerializer.Deserialize<List<string>>(
                        json.GetProperty("steps").GetRawText()) ?? new();
                    output.Examples = JsonSerializer.Deserialize<List<string>>(
                        json.GetProperty("examples").GetRawText()) ?? new();
                    output.SocraticQuestions = JsonSerializer.Deserialize<List<string>>(
                        json.GetProperty("questions_to_ask_themselves").GetRawText()) ?? new();
                    output.Resources = JsonSerializer.Deserialize<List<string>>(
                        json.GetProperty("resources").GetRawText()) ?? new();
                    output.Encouragement = json.GetProperty("encouragement").GetString();
                    output.Confidence = 0.9;
                    break;
            }

            return output;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse Claude response");

            return new ClaudeAnalysisOutput
            {
                Success = false,
                AnalysisType = type,
                RawResponse = response,
                Message = $"Failed to parse response: {ex.Message}",
                Confidence = 0,
                FallbackUsed = true
            };
        }
    }
}

/// <summary>
/// Types of analysis Claude can perform
/// </summary>
public enum AnalysisType
{
    /// <summary>Assess junior's understanding of requirements</summary>
    Assessment,

    /// <summary>Review submitted code</summary>
    CodeReview,

    /// <summary>Diagnose why junior is stuck</summary>
    BlockerDiagnosis,

    /// <summary>Generate helpful guidance</summary>
    GuidanceGeneration
}

/// <summary>
/// Code review issue from Claude analysis
/// </summary>
public class CodeReviewIssue
{
    public string Severity { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Issue { get; set; } = string.Empty;
    public string Suggestion { get; set; } = string.Empty;
}

/// <summary>
/// Output model for Claude analysis activity
/// </summary>
public class ClaudeAnalysisOutput
{
    public bool Success { get; set; }
    public AnalysisType AnalysisType { get; set; }
    public double Confidence { get; set; }
    public string? RawResponse { get; set; }
    public string? Message { get; set; }
    public bool FallbackUsed { get; set; }

    // Assessment-specific outputs
    public string? AssessmentStatus { get; set; }
    public string? Summary { get; set; }
    public List<string> Gaps { get; set; } = new();
    public List<string> Strengths { get; set; } = new();
    public string? RecommendedAction { get; set; }

    // Code Review-specific outputs
    public string? OverallQuality { get; set; }
    public int Score { get; set; }
    public List<CodeReviewIssue> CodeReviewIssues { get; set; } = new();
    public List<string> Positives { get; set; } = new();
    public List<string> LearningOpportunities { get; set; } = new();

    // Blocker Diagnosis-specific outputs
    public string? DiagnosedBlockerType { get; set; }
    public string? RootCause { get; set; }
    public List<string> Evidence { get; set; } = new();
    public string? RecommendedIntervention { get; set; }
    public string? ImmediateAction { get; set; }

    // Guidance Generation-specific outputs
    public string? MainGuidance { get; set; }
    public List<string> Steps { get; set; } = new();
    public List<string> Examples { get; set; } = new();
    public List<string> SocraticQuestions { get; set; } = new();
    public List<string> Resources { get; set; } = new();
    public string? Encouragement { get; set; }
}
