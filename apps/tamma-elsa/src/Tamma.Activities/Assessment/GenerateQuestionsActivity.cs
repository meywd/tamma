using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Activities.Assessment.Models;

namespace Tamma.Activities.Assessment;

/// <summary>
/// Generates assessment questions adapted to the junior's skill level.
/// Uses story context and optional previous attempt data to produce targeted questions.
/// In production, delegates to the LLM Call sub-workflow (7-1B) via RunWorkflow.
/// </summary>
[Activity(
    "Tamma.Assessment",
    "Generate Questions",
    "Generate assessment questions adapted to skill level",
    Kind = ActivityKind.Task
)]
public class GenerateQuestionsActivity : CodeActivity<QuestionSet>
{
    private readonly ILogger<GenerateQuestionsActivity>? _logger;
    private readonly IConfiguration? _configuration;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Story ID being assessed</summary>
    [Input(Description = "Story ID being assessed")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Junior developer's current skill level (1-5)</summary>
    [Input(Description = "Junior skill level (1-5)")]
    public Input<int> SkillLevel { get; set; } = new(3);

    /// <summary>Story context gathered from Context Gathering workflow</summary>
    [Input(Description = "Story context from context gathering")]
    public Input<string> StoryContext { get; set; } = default!;

    /// <summary>Previous attempt data for retry assessments (JSON or null)</summary>
    [Input(Description = "Previous attempt data for retry context (optional)")]
    public Input<string?> PreviousAttemptJson { get; set; } = default!;

    [JsonConstructor]
    public GenerateQuestionsActivity() { }

    public GenerateQuestionsActivity(
        ILogger<GenerateQuestionsActivity> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);
        var skillLevel = Math.Clamp(SkillLevel.Get(context), 1, 5);
        var storyContext = StoryContext.Get(context) ?? string.Empty;
        var previousAttemptJson = PreviousAttemptJson.Get(context);

        _logger?.LogInformation(
            "Generating assessment questions for session {SessionId}, skill level {SkillLevel}",
            sessionId, skillLevel);

        PreviousAttempt? previousAttempt = null;
        if (!string.IsNullOrEmpty(previousAttemptJson))
        {
            try
            {
                previousAttempt = JsonSerializer.Deserialize<PreviousAttempt>(previousAttemptJson);
            }
            catch (JsonException ex)
            {
                _logger?.LogWarning(ex, "Failed to deserialize previous attempt data");
            }
        }

        var questionCount = GetQuestionCount(skillLevel);
        var questions = BuildQuestions(skillLevel, storyContext, previousAttempt);

        var contextSummary = storyContext.Length > 500
            ? storyContext[..500] + "..."
            : storyContext;

        var result = new QuestionSet
        {
            Questions = questions,
            TargetSkillLevel = skillLevel,
            IsRetry = previousAttempt != null,
            ContextSummary = contextSummary
        };

        _logger?.LogInformation(
            "Generated {QuestionCount} questions for session {SessionId} (retry={IsRetry})",
            result.Questions.Count, sessionId, result.IsRetry);

        context.SetResult(result);

        await ValueTask.CompletedTask;
    }

    /// <summary>
    /// Get the target question count based on skill level.
    /// Reads from configuration or uses defaults.
    /// </summary>
    private int GetQuestionCount(int skillLevel)
    {
        var configKey = $"Assessment:QuestionsPerLevel:{skillLevel}";
        var configValue = _configuration?[configKey];
        if (int.TryParse(configValue, out var configured))
        {
            return configured;
        }

        return skillLevel switch
        {
            1 => 2,
            2 => 3,
            3 => 4,
            4 => 4,
            5 => 5,
            _ => 3
        };
    }

    /// <summary>
    /// Build assessment questions based on skill level and context.
    /// In production, this would delegate to the LLM Call workflow for AI-generated questions.
    /// </summary>
    private List<string> BuildQuestions(int skillLevel, string storyContext, PreviousAttempt? previousAttempt)
    {
        var questions = new List<string>();

        // If retrying, target previously identified gaps
        if (previousAttempt != null && previousAttempt.Gaps.Count > 0)
        {
            foreach (var gap in previousAttempt.Gaps.Take(GetQuestionCount(skillLevel)))
            {
                questions.Add($"Regarding {gap}: Can you explain your understanding and how you would address this aspect of the requirements?");
            }

            // Fill remaining slots with skill-level questions
            var remaining = GetQuestionCount(skillLevel) - questions.Count;
            if (remaining > 0)
            {
                questions.AddRange(GetSkillLevelQuestions(skillLevel).Take(remaining));
            }

            return questions;
        }

        // Standard questions based on skill level
        questions.AddRange(GetSkillLevelQuestions(skillLevel));

        return questions;
    }

    /// <summary>
    /// Get standard questions adapted to skill level
    /// </summary>
    private List<string> GetSkillLevelQuestions(int skillLevel)
    {
        var questions = new List<string>();

        // Level 1-2: Simple comprehension questions
        questions.Add("In your own words, describe what this story requires you to build and why it matters.");
        questions.Add("What are the key acceptance criteria, and how will you verify each one is met?");

        if (skillLevel <= 2)
        {
            if (GetQuestionCount(skillLevel) >= 3)
            {
                questions.Add("What existing code or documentation will you need to reference to complete this task?");
            }
            return questions.Take(GetQuestionCount(skillLevel)).ToList();
        }

        // Level 3: Include design considerations
        questions.Add("What design decisions do you need to make, and what trade-offs are involved?");
        questions.Add("How will you structure your implementation to keep the code maintainable and testable?");

        if (skillLevel == 3)
        {
            return questions.Take(GetQuestionCount(skillLevel)).ToList();
        }

        // Level 4-5: Include edge cases and architectural implications
        questions.Add("What edge cases or error scenarios should you handle, and how would you approach them?");
        questions.Add("How does this change affect the broader system architecture, and what dependencies are involved?");
        questions.Add("What performance or security considerations apply to this implementation?");

        return questions.Take(GetQuestionCount(skillLevel)).ToList();
    }
}
