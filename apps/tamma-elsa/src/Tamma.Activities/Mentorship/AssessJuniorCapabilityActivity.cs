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
/// ELSA activity to assess junior developer's understanding of story requirements.
/// This is the first assessment phase where we evaluate if the junior correctly
/// understands what needs to be built.
/// </summary>
[Activity(
    "Tamma.Mentorship",
    "Assess Junior Capability",
    "Evaluate junior developer's understanding of story requirements",
    Kind = ActivityKind.Task
)]
public class AssessJuniorCapabilityActivity : CodeActivity<AssessmentOutput>
{
    private readonly ILogger<AssessJuniorCapabilityActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;
    private readonly IIntegrationService? _integrationService;

    /// <summary>ID of the story to assess</summary>
    [Input(Description = "ID of the story to assess")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>ID of the junior developer</summary>
    [Input(Description = "ID of the junior developer")]
    public Input<string> JuniorId { get; set; } = default!;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Timeout in minutes for the assessment</summary>
    [Input(Description = "Timeout in minutes", DefaultValue = 5)]
    public Input<int> TimeoutMinutes { get; set; } = new(5);

    [JsonConstructor]
    public AssessJuniorCapabilityActivity() { }

    public AssessJuniorCapabilityActivity(
        ILogger<AssessJuniorCapabilityActivity> logger,
        IMentorshipSessionRepository repository,
        IIntegrationService integrationService)
    {
        _logger = logger;
        _repository = repository;
        _integrationService = integrationService;
    }

    /// <summary>
    /// Execute the assessment activity
    /// </summary>
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var storyId = StoryId.Get(context);
        var juniorId = JuniorId.Get(context);
        var sessionId = SessionId.Get(context);
        var timeoutMinutes = TimeoutMinutes.Get(context);

        _logger?.LogInformation(
            "Starting capability assessment for junior {JuniorId} on story {StoryId}",
            juniorId, storyId);

        try
        {
            // Get story and junior information
            var story = await _repository!.GetStoryByIdAsync(storyId);
            var junior = await _repository.GetJuniorByIdAsync(juniorId);

            if (story == null)
            {
                _logger?.LogError("Story {StoryId} not found", storyId);
                context.SetResult(new AssessmentOutput
                {
                    Status = AssessmentStatus.Error,
                    NextState = MentorshipState.FAILED,
                    Message = $"Story {storyId} not found"
                });
                return;
            }

            if (junior == null)
            {
                _logger?.LogError("Junior developer {JuniorId} not found", juniorId);
                context.SetResult(new AssessmentOutput
                {
                    Status = AssessmentStatus.Error,
                    NextState = MentorshipState.FAILED,
                    Message = $"Junior developer {juniorId} not found"
                });
                return;
            }

            // Update session state
            await _repository.UpdateStateAsync(sessionId, MentorshipState.ASSESS_JUNIOR_CAPABILITY);

            // Build assessment questions based on story complexity
            var questions = BuildAssessmentQuestions(story.Complexity);

            // Send assessment via Slack (if configured)
            if (!string.IsNullOrEmpty(junior.SlackId))
            {
                var message = FormatAssessmentMessage(story.Title, story.Description ?? "", questions);
                await _integrationService!.SendSlackDirectMessageAsync(junior.SlackId, message);
            }

            // Log the assessment event
            await _repository.LogEventAsync(new Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Core.Entities.EventTypes.AssessmentCompleted,
                StateFrom = MentorshipState.INIT_STORY_PROCESSING,
                StateTo = MentorshipState.ASSESS_JUNIOR_CAPABILITY
            });

            // For now, simulate assessment result based on junior's skill level
            // In production, this would wait for actual response from the junior
            var result = SimulateAssessmentResult(junior.SkillLevel, story.Complexity);

            _logger?.LogInformation(
                "Assessment completed for junior {JuniorId}: Status={Status}, Confidence={Confidence}",
                juniorId, result.Status, result.Confidence);

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during capability assessment for session {SessionId}", sessionId);

            await _repository!.LogEventAsync(new Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Core.Entities.EventTypes.Error
            });

            context.SetResult(new AssessmentOutput
            {
                Status = AssessmentStatus.Error,
                NextState = MentorshipState.DIAGNOSE_BLOCKER,
                Message = ex.Message
            });
        }
    }

    private List<string> BuildAssessmentQuestions(int complexity)
    {
        var questions = new List<string>
        {
            "What do you understand needs to be built?",
            "What are the key requirements from the acceptance criteria?"
        };

        if (complexity >= 3)
        {
            questions.Add("What technical challenges do you foresee?");
            questions.Add("What's your planned approach?");
        }

        if (complexity >= 4)
        {
            questions.Add("What technologies will you use?");
            questions.Add("How will you handle edge cases?");
        }

        return questions;
    }

    private string FormatAssessmentMessage(string storyTitle, string description, List<string> questions)
    {
        return $@"**Tamma Mentorship Assessment**

*Story: {storyTitle}*
{description}

Please answer the following questions to demonstrate your understanding:

{string.Join("\n", questions.Select((q, i) => $"{i + 1}. {q}"))}

Reply to this message with your answers.";
    }

    private AssessmentOutput SimulateAssessmentResult(int skillLevel, int complexity)
    {
        // Simulate assessment based on skill level vs complexity
        var successChance = (skillLevel * 20) - (complexity * 10) + 50;
        var roll = Random.Shared.Next(100);

        if (roll < successChance)
        {
            return new AssessmentOutput
            {
                Status = AssessmentStatus.Correct,
                Confidence = 0.8 + (Random.Shared.NextDouble() * 0.2),
                NextState = MentorshipState.PLAN_DECOMPOSITION,
                Message = "Junior demonstrates good understanding of requirements"
            };
        }
        else if (roll < successChance + 25)
        {
            return new AssessmentOutput
            {
                Status = AssessmentStatus.Partial,
                Confidence = 0.5 + (Random.Shared.NextDouble() * 0.3),
                NextState = MentorshipState.CLARIFY_REQUIREMENTS,
                Message = "Junior has partial understanding, needs clarification",
                Gaps = new List<string> { "Technical approach unclear", "Edge cases not considered" }
            };
        }
        else
        {
            return new AssessmentOutput
            {
                Status = AssessmentStatus.Incorrect,
                Confidence = 0.2 + (Random.Shared.NextDouble() * 0.3),
                NextState = MentorshipState.RE_EXPLAIN_STORY,
                Message = "Junior misunderstood requirements, needs re-explanation",
                Gaps = new List<string> { "Core requirements misunderstood", "Acceptance criteria unclear" }
            };
        }
    }
}

/// <summary>
/// Output model for assessment activity
/// </summary>
public class AssessmentOutput
{
    public AssessmentStatus Status { get; set; }
    public double Confidence { get; set; }
    public MentorshipState NextState { get; set; }
    public string? Message { get; set; }
    public List<string> Gaps { get; set; } = new();
}
