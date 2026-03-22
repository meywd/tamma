using System.Text.Json;
using Tamma.Core.Enums;

namespace Tamma.Core.Entities;

/// <summary>
/// Represents an event that occurred during a mentorship session
/// </summary>
public class MentorshipEvent
{
    /// <summary>Unique identifier for the event</summary>
    public Guid Id { get; set; }

    /// <summary>Session this event belongs to</summary>
    public Guid SessionId { get; set; }

    /// <summary>Type of event</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Event data payload (JSON)</summary>
    public JsonDocument? EventData { get; set; }

    /// <summary>State before the event (if state transition)</summary>
    public MentorshipState? StateFrom { get; set; }

    /// <summary>State after the event (if state transition)</summary>
    public MentorshipState? StateTo { get; set; }

    /// <summary>What triggered this event</summary>
    public string? Trigger { get; set; }

    /// <summary>When the event occurred</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation property
    public virtual MentorshipSession? Session { get; set; }
}

/// <summary>
/// Common event types in the mentorship workflow
/// </summary>
public static class EventTypes
{
    public const string StateTransition = "state_transition";
    public const string AssessmentCompleted = "assessment_completed";
    public const string AssessmentTimeout = "assessment_timeout";
    public const string BlockerDetected = "blocker_detected";
    public const string BlockerResolved = "blocker_resolved";
    public const string QualityGateRun = "quality_gate_run";
    public const string CodeReviewSubmitted = "code_review_submitted";
    public const string CodeReviewApproved = "code_review_approved";
    public const string CodeReviewChangesRequested = "code_review_changes_requested";
    public const string ProgressUpdate = "progress_update";
    public const string GuidanceProvided = "guidance_provided";
    public const string HintProvided = "hint_provided";
    public const string EscalationTriggered = "escalation_triggered";
    public const string SessionPaused = "session_paused";
    public const string SessionResumed = "session_resumed";
    public const string SessionCompleted = "session_completed";
    public const string SessionFailed = "session_failed";
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Info = "info";

    // Phase 2 event types
    public const string BlockerDiagnosed = "blocker_diagnosed";
    public const string CodeReviewPrepared = "code_review_prepared";
    public const string CodeReviewMonitored = "code_review_monitored";
    public const string CodeReviewUpdate = "code_review_update";
    public const string AIAnalysis = "ai_analysis";
    public const string SuggestionsGenerated = "suggestions_generated";
    public const string ContextGathered = "context_gathered";
    public const string MergeCompleted = "merge_completed";
    public const string SkillLevelUpdated = "skill_level_updated";

    // Phase 3 event types (Story 7-1A workflow states)
    public const string SessionStarted = "session_started";
    public const string StoryValidated = "story_validated";
    public const string RequirementsClarified = "requirements_clarified";
    public const string StoryReExplained = "story_re_explained";
    public const string PlanCreated = "plan_created";
    public const string PlanReviewed = "plan_reviewed";
    public const string PlanAdjusted = "plan_adjusted";
    public const string ImplementationStarted = "implementation_started";
    public const string PatternDetected = "pattern_detected";
    public const string AutoFixAttempted = "auto_fix_attempted";
    public const string ManualFixRequired = "manual_fix_required";
    public const string ReviewReRequested = "review_re_requested";
    public const string EscalatedToSenior = "escalated_to_senior";
    public const string SessionCancelled = "session_cancelled";
    public const string SessionTimedOut = "session_timed_out";
    public const string ReportGenerated = "report_generated";
    public const string SkillProfileUpdated = "skill_profile_updated";
}
