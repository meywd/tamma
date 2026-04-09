using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Activities.Assessment.Models;
using Tamma.Core.Enums;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Assessment;

/// <summary>
/// Updates the junior developer's skill profile with assessment results.
/// Tracks: assessment result, confidence, gaps, strengths, timestamp, story context,
/// and computes a running average confidence across assessments.
/// </summary>
[Activity(
    "Tamma.Assessment",
    "Update Skill Profile",
    "Update junior's skill profile with assessment results",
    Kind = ActivityKind.Task
)]
public class UpdateSkillProfileActivity : CodeActivity
{
    private readonly ILogger<UpdateSkillProfileActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Junior developer ID</summary>
    [Input(Description = "Junior developer ID")]
    public Input<string> JuniorId { get; set; } = default!;

    /// <summary>Story ID being assessed</summary>
    [Input(Description = "Story ID")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Assessment outcome status</summary>
    [Input(Description = "Assessment status")]
    public Input<AssessmentOutcomeStatus> Status { get; set; } = default!;

    /// <summary>Confidence score (0.0-1.0)</summary>
    [Input(Description = "Confidence score")]
    public Input<decimal> Confidence { get; set; } = default!;

    /// <summary>Identified knowledge gaps (JSON array)</summary>
    [Input(Description = "Knowledge gaps (JSON array)")]
    public Input<string> GapsJson { get; set; } = default!;

    /// <summary>Identified strengths (JSON array)</summary>
    [Input(Description = "Strengths (JSON array)")]
    public Input<string> StrengthsJson { get; set; } = default!;

    [JsonConstructor]
    public UpdateSkillProfileActivity() { }

    public UpdateSkillProfileActivity(
        ILogger<UpdateSkillProfileActivity> logger,
        IMentorshipSessionRepository repository)
    {
        _logger = logger;
        _repository = repository;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var juniorId = JuniorId.Get(context);
        var storyId = StoryId.Get(context) ?? string.Empty;
        var status = Status.Get(context);
        var confidence = Confidence.Get(context);
        var gapsJson = GapsJson.Get(context) ?? "[]";
        var strengthsJson = StrengthsJson.Get(context) ?? "[]";

        _logger?.LogInformation(
            "Updating skill profile for junior {JuniorId}, session {SessionId}: Status={Status}, Confidence={Confidence}",
            juniorId, sessionId, status, confidence);

        try
        {
            var junior = await _repository!.GetJuniorByIdAsync(juniorId);
            if (junior == null)
            {
                _logger?.LogWarning("Junior {JuniorId} not found, skipping skill profile update", juniorId);
                return;
            }

            // Parse gaps and strengths
            List<string> gaps;
            List<string> strengths;
            try
            {
                gaps = JsonSerializer.Deserialize<List<string>>(gapsJson) ?? new List<string>();
            }
            catch
            {
                gaps = new List<string>();
            }
            try
            {
                strengths = JsonSerializer.Deserialize<List<string>>(strengthsJson) ?? new List<string>();
            }
            catch
            {
                strengths = new List<string>();
            }

            // Compute running average confidence
            // Load existing profile data from LearningPatterns JSON
            var existingAssessments = new List<SkillProfileUpdate>();
            if (junior.LearningPatterns != null)
            {
                try
                {
                    existingAssessments = JsonSerializer.Deserialize<List<SkillProfileUpdate>>(
                        junior.LearningPatterns.RootElement.GetRawText()) ?? new List<SkillProfileUpdate>();
                }
                catch
                {
                    existingAssessments = new List<SkillProfileUpdate>();
                }
            }

            // Calculate running average
            var totalConfidence = existingAssessments.Sum(a => a.Confidence) + confidence;
            var totalCount = existingAssessments.Count + 1;
            var runningAverage = totalConfidence / totalCount;

            // Create the new assessment entry
            var profileUpdate = new SkillProfileUpdate
            {
                Status = status,
                Confidence = confidence,
                Gaps = gaps,
                Strengths = strengths,
                StoryId = storyId,
                RunningAverageConfidence = runningAverage,
                AssessedAt = DateTime.UtcNow
            };

            existingAssessments.Add(profileUpdate);

            // Update the junior's learning patterns
            junior.LearningPatterns = JsonDocument.Parse(
                JsonSerializer.Serialize(existingAssessments));
            junior.UpdatedAt = DateTime.UtcNow;

            await _repository.UpdateJuniorAsync(junior);

            // Log the skill profile update event
            await _repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.SkillLevelUpdated,
                Trigger = "assessment_skill_profile_update"
            });

            _logger?.LogInformation(
                "Skill profile updated for junior {JuniorId}: RunningAvgConfidence={RunningAvg}, TotalAssessments={Count}",
                juniorId, runningAverage, totalCount);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Error updating skill profile for junior {JuniorId}, session {SessionId}",
                juniorId, sessionId);
        }
    }
}
