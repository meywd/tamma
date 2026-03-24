using System.Text.Json.Serialization;
using Tamma.Core.Enums;

namespace Tamma.Activities.Assessment.Models;

/// <summary>
/// Status of an assessment evaluation
/// </summary>
public enum AssessmentOutcomeStatus
{
    /// <summary>Junior correctly understands requirements (confidence >= 0.7)</summary>
    Correct,

    /// <summary>Junior has partial understanding (confidence >= 0.4)</summary>
    Partial,

    /// <summary>Junior misunderstood requirements (confidence &lt; 0.4)</summary>
    Incorrect,

    /// <summary>No response received within timeout window</summary>
    Timeout
}

/// <summary>
/// Complete result of an assessment workflow execution
/// </summary>
public class AssessmentResult
{
    /// <summary>Assessment outcome status</summary>
    public AssessmentOutcomeStatus Status { get; set; }

    /// <summary>AI confidence in the classification (0.0-1.0)</summary>
    public decimal Confidence { get; set; }

    /// <summary>Identified knowledge gaps</summary>
    public List<string> Gaps { get; set; } = new();

    /// <summary>Identified strengths</summary>
    public List<string> Strengths { get; set; } = new();

    /// <summary>Recommended next mentorship state</summary>
    public MentorshipState NextState { get; set; }

    /// <summary>Questions that were asked</summary>
    public List<string> Questions { get; set; } = new();

    /// <summary>The junior's response text</summary>
    public string JuniorResponse { get; set; } = string.Empty;

    /// <summary>AI's reasoning for the classification</summary>
    public string AnalysisRationale { get; set; } = string.Empty;
}

/// <summary>
/// A set of generated assessment questions
/// </summary>
public class QuestionSet
{
    /// <summary>Generated questions</summary>
    public List<string> Questions { get; set; } = new();

    /// <summary>Skill level the questions were targeted at</summary>
    public int TargetSkillLevel { get; set; }

    /// <summary>Whether this is a retry with gap-targeted questions</summary>
    public bool IsRetry { get; set; }

    /// <summary>Context summary provided alongside questions</summary>
    public string ContextSummary { get; set; } = string.Empty;
}

/// <summary>
/// Result from delivering questions to the junior
/// </summary>
public class DeliveryResult
{
    /// <summary>Whether delivery was successful</summary>
    public bool Success { get; set; }

    /// <summary>Channel used for delivery (slack, api, email)</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>Delivery message or error detail</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Timestamp of delivery</summary>
    public DateTime DeliveredAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Result from AI analysis of the junior's response
/// </summary>
public class AnalysisResult
{
    /// <summary>Classification status from AI</summary>
    public AssessmentOutcomeStatus Status { get; set; }

    /// <summary>Confidence score (0.0-1.0)</summary>
    public decimal Confidence { get; set; }

    /// <summary>Identified knowledge gaps</summary>
    public List<string> Gaps { get; set; } = new();

    /// <summary>Identified strengths</summary>
    public List<string> Strengths { get; set; } = new();

    /// <summary>AI's reasoning for the classification</summary>
    public string Rationale { get; set; } = string.Empty;

    /// <summary>Summary of what the junior understood</summary>
    public string UnderstandingSummary { get; set; } = string.Empty;
}

/// <summary>
/// Payload for the WaitForResponse bookmark
/// </summary>
public class AssessmentBookmarkPayload
{
    /// <summary>Mentorship session ID</summary>
    public Guid SessionId { get; set; }

    /// <summary>Attempt number for this assessment</summary>
    public int AttemptNumber { get; set; }

    /// <summary>Bookmark name for identification</summary>
    public string BookmarkName { get; set; } = string.Empty;
}

/// <summary>
/// Data provided when resuming the WaitForResponse bookmark
/// </summary>
public class AssessmentResponseInput
{
    /// <summary>The junior's response text</summary>
    public string Response { get; set; } = string.Empty;

    /// <summary>Timestamp when the response was submitted</summary>
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Skill profile update entry for tracking assessment history
/// </summary>
public class SkillProfileUpdate
{
    /// <summary>Assessment outcome status</summary>
    public AssessmentOutcomeStatus Status { get; set; }

    /// <summary>Confidence score from this assessment</summary>
    public decimal Confidence { get; set; }

    /// <summary>Gaps identified in this assessment</summary>
    public List<string> Gaps { get; set; } = new();

    /// <summary>Strengths identified in this assessment</summary>
    public List<string> Strengths { get; set; } = new();

    /// <summary>Story ID being assessed</summary>
    public string StoryId { get; set; } = string.Empty;

    /// <summary>Running average confidence across assessments</summary>
    public decimal RunningAverageConfidence { get; set; }

    /// <summary>When this assessment was performed</summary>
    public DateTime AssessedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Previous attempt context passed to retry assessments
/// </summary>
public class PreviousAttempt
{
    /// <summary>Previous assessment status</summary>
    public AssessmentOutcomeStatus Status { get; set; }

    /// <summary>Previous confidence</summary>
    public decimal Confidence { get; set; }

    /// <summary>Previously identified gaps to target</summary>
    public List<string> Gaps { get; set; } = new();

    /// <summary>Previous questions asked</summary>
    public List<string> PreviousQuestions { get; set; } = new();

    /// <summary>Attempt number</summary>
    public int AttemptNumber { get; set; }
}
