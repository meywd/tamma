using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Core.Enums;
using Tamma.Data.Abstractions;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Mentorship;

// =============================================================================
// FlowNode wrapper activities for mentorship states that need outcome-based
// routing in a Flowchart context.
//
// These activities use parameterless constructors and resolve dependencies via
// context.GetRequiredService<T>() at execution time. This is required because
// ELSA code-first workflows instantiate activities during Build() where DI is
// not available; ELSA resolves constructor dependencies only when using
// AddActivitiesFrom<T>() registration, not when directly newing up activities.
// =============================================================================

/// <summary>
/// Initializes a new mentorship session. Sets state to INIT_STORY_PROCESSING
/// and routes to "Done" on success or "Error" on failure.
/// </summary>
[Activity("Tamma.Mentorship", "Init Story Processing",
    "Initialize a new mentorship session and load story data",
    Kind = ActivityKind.Task)]
[FlowNode("Done", "Error")]
public class InitStoryProcessingActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "ID of the story to process")]
    public Input<string> StoryId { get; set; } = default!;

    [Input(Description = "ID of the junior developer")]
    public Input<string> JuniorId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<InitStoryProcessingActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);

        logger.LogInformation(
            "Initializing story processing for session {SessionId}, story {StoryId}",
            sessionId, storyId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.INIT_STORY_PROCESSING);

            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.SessionStarted,
                StateTo = MentorshipState.INIT_STORY_PROCESSING
            });

            await context.CompleteActivityWithOutcomesAsync("Done");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize story processing for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// Validates story requirements and context. Routes to "Valid", "Invalid", or
/// "BugIssue" (fast path for bug issues that skip straight to debugging).
/// </summary>
[Activity("Tamma.Mentorship", "Validate Story",
    "Validate story requirements, context, and detect bug issues",
    Kind = ActivityKind.Task)]
[FlowNode("Valid", "BugIssue", "Invalid", "Error")]
public class ValidateStoryActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "ID of the story to validate")]
    public Input<string> StoryId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<ValidateStoryActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);

        logger.LogInformation("Validating story {StoryId} for session {SessionId}", storyId, sessionId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.VALIDATE_STORY);

            var story = await repository.GetStoryByIdAsync(storyId);
            if (story == null)
            {
                logger.LogWarning("Story {StoryId} not found", storyId);
                await context.CompleteActivityWithOutcomesAsync("Invalid");
                return;
            }

            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.StoryValidated,
                StateFrom = MentorshipState.INIT_STORY_PROCESSING,
                StateTo = MentorshipState.VALIDATE_STORY
            });

            // Bug fast path: detect if story is a bug issue
            if (IsBugIssue(story))
            {
                logger.LogInformation("Story {StoryId} identified as bug issue, routing to fast path", storyId);
                await context.CompleteActivityWithOutcomesAsync("BugIssue");
                return;
            }

            var hasTitle = !string.IsNullOrWhiteSpace(story.Title);
            var hasDescription = !string.IsNullOrWhiteSpace(story.Description);

            if (!hasTitle || !hasDescription)
            {
                logger.LogWarning("Story {StoryId} missing required fields", storyId);
                await context.CompleteActivityWithOutcomesAsync("Invalid");
                return;
            }

            await context.CompleteActivityWithOutcomesAsync("Valid");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error validating story {StoryId}", storyId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }

    private static bool IsBugIssue(Tamma.Core.Entities.Story story)
    {
        var title = story.Title?.ToLowerInvariant() ?? "";
        var description = story.Description?.ToLowerInvariant() ?? "";
        return title.Contains("bug") || title.Contains("fix") || title.Contains("defect")
            || description.Contains("bug report") || description.Contains("steps to reproduce");
    }
}

/// <summary>
/// Clarifies requirements when partial understanding is detected.
/// Routes to "Clarified" (re-assess) or "MaxRetries" (escalate).
/// </summary>
[Activity("Tamma.Mentorship", "Clarify Requirements",
    "Clarify requirements with the junior developer",
    Kind = ActivityKind.Task)]
[FlowNode("Clarified", "MaxRetries", "Error")]
public class ClarifyRequirementsActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "Current clarification attempt number")]
    public Input<int> AttemptNumber { get; set; } = new(1);

    [Input(Description = "Maximum clarification attempts", DefaultValue = 3)]
    public Input<int> MaxAttempts { get; set; } = new(3);

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<ClarifyRequirementsActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);
        var attempt = AttemptNumber.Get(context);
        var maxAttempts = MaxAttempts.Get(context);

        logger.LogInformation(
            "Clarifying requirements for session {SessionId}, attempt {Attempt}/{Max}",
            sessionId, attempt, maxAttempts);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.CLARIFY_REQUIREMENTS);

            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.RequirementsClarified,
                StateFrom = MentorshipState.ASSESS_JUNIOR_CAPABILITY,
                StateTo = MentorshipState.CLARIFY_REQUIREMENTS
            });

            if (attempt >= maxAttempts)
            {
                logger.LogWarning("Max clarification attempts reached for session {SessionId}", sessionId);
                // Wave C.4 §3 — retry envelope exhausted. Emit
                // WORKFLOW.RETRY_EXCEEDED so the critical-severity rule
                // can fan out to PagerDuty/email.
                await WorkflowRetryEmitter.EmitAsync(
                    context.GetService<IAlertEventEmitter>(),
                    ReadMentorshipTenantId(context),
                    workflowDefinitionId: ExtractWorkflowDefinitionId(context),
                    workflowInstanceId: ExtractWorkflowInstanceId(context),
                    attempts: attempt,
                    maxAttempts: maxAttempts,
                    finalError: "clarify_requirements_max_attempts_reached",
                    activityId: context.Activity.Id,
                    ct: context.CancellationToken).ConfigureAwait(false);
                await context.CompleteActivityWithOutcomesAsync("MaxRetries");
                return;
            }

            await context.CompleteActivityWithOutcomesAsync("Clarified");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during requirements clarification for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }

    // Wave C.4 §3 — shared tenant-resolver helper for mentorship
    // FlowNode activities. The Mentorship workflows don't accept a
    // TenantId input today; the session repository knows it, but
    // resolving that at every retry-exhaustion site would mean a DB
    // hit per failure. We fall back to the workflow variable names
    // used across Tamma when present.
    internal static Guid? ReadMentorshipTenantId(ActivityExecutionContext context)
    {
        string?[] candidates =
        {
            context.GetVariable<string>("TenantId"),
            context.GetVariable<string>("tenantId"),
        };
        foreach (var s in candidates)
        {
            if (!string.IsNullOrWhiteSpace(s) && Guid.TryParse(s, out var g))
                return g;
        }
        return null;
    }

    /// <summary>
    /// Best-effort extraction of the workflow definition id as a Guid.
    /// Elsa stores definition ids as strings that are usually (but not
    /// always) Guid-parseable — returns Guid.Empty on non-Guid ids so
    /// the emission still fires with a predictable sentinel.
    /// </summary>
    internal static Guid ExtractWorkflowDefinitionId(ActivityExecutionContext context)
    {
        var id = context.WorkflowExecutionContext.Workflow.Identity.DefinitionId;
        return Guid.TryParse(id, out var g) ? g : Guid.Empty;
    }

    /// <summary>Similar best-effort for the instance id.</summary>
    internal static Guid ExtractWorkflowInstanceId(ActivityExecutionContext context) =>
        Guid.TryParse(context.WorkflowExecutionContext.Id, out var g) ? g : Guid.Empty;
}

/// <summary>
/// Re-explains story when misunderstanding is detected.
/// Routes to "Explained" (re-assess) or "MaxRetries" (escalate).
/// </summary>
[Activity("Tamma.Mentorship", "Re-Explain Story",
    "Re-explain story requirements to junior developer",
    Kind = ActivityKind.Task)]
[FlowNode("Explained", "MaxRetries", "Error")]
public class ReExplainStoryActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "Current explanation attempt number")]
    public Input<int> AttemptNumber { get; set; } = new(1);

    [Input(Description = "Maximum explanation attempts", DefaultValue = 3)]
    public Input<int> MaxAttempts { get; set; } = new(3);

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<ReExplainStoryActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);
        var attempt = AttemptNumber.Get(context);
        var maxAttempts = MaxAttempts.Get(context);

        logger.LogInformation(
            "Re-explaining story for session {SessionId}, attempt {Attempt}/{Max}",
            sessionId, attempt, maxAttempts);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.RE_EXPLAIN_STORY);

            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.StoryReExplained,
                StateFrom = MentorshipState.ASSESS_JUNIOR_CAPABILITY,
                StateTo = MentorshipState.RE_EXPLAIN_STORY
            });

            if (attempt >= maxAttempts)
            {
                logger.LogWarning("Max re-explanation attempts reached for session {SessionId}", sessionId);
                // Wave C.4 §3 — retry envelope exhausted.
                await WorkflowRetryEmitter.EmitAsync(
                    context.GetService<IAlertEventEmitter>(),
                    ClarifyRequirementsActivity.ReadMentorshipTenantId(context),
                    workflowDefinitionId:
                        ClarifyRequirementsActivity.ExtractWorkflowDefinitionId(context),
                    workflowInstanceId:
                        ClarifyRequirementsActivity.ExtractWorkflowInstanceId(context),
                    attempts: attempt,
                    maxAttempts: maxAttempts,
                    finalError: "re_explain_story_max_attempts_reached",
                    activityId: context.Activity.Id,
                    ct: context.CancellationToken).ConfigureAwait(false);
                await context.CompleteActivityWithOutcomesAsync("MaxRetries");
                return;
            }

            await context.CompleteActivityWithOutcomesAsync("Explained");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during story re-explanation for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// Breaks down story into smaller tasks for planning.
/// Routes to "Planned" on success.
/// </summary>
[Activity("Tamma.Mentorship", "Plan Decomposition",
    "Break down story into implementation tasks",
    Kind = ActivityKind.Task)]
[FlowNode("Planned", "Error")]
public class PlanDecompositionActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "ID of the story to decompose")]
    public Input<string> StoryId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<PlanDecompositionActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);

        logger.LogInformation("Planning decomposition for story {StoryId}", storyId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.PLAN_DECOMPOSITION);

            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.PlanCreated,
                StateFrom = MentorshipState.ASSESS_JUNIOR_CAPABILITY,
                StateTo = MentorshipState.PLAN_DECOMPOSITION
            });

            await context.CompleteActivityWithOutcomesAsync("Planned");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during plan decomposition for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// Reviews and approves the implementation plan.
/// Routes to "Approved", "NeedsAdjustment", or "MaxRetries".
/// </summary>
[Activity("Tamma.Mentorship", "Review Plan",
    "Review and approve the implementation plan",
    Kind = ActivityKind.Task)]
[FlowNode("Approved", "NeedsAdjustment", "MaxRetries", "Error")]
public class ReviewPlanActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "Current review iteration")]
    public Input<int> Iteration { get; set; } = new(1);

    [Input(Description = "Maximum plan review iterations", DefaultValue = 2)]
    public Input<int> MaxIterations { get; set; } = new(2);

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<ReviewPlanActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);
        var iteration = Iteration.Get(context);
        var maxIterations = MaxIterations.Get(context);

        logger.LogInformation(
            "Reviewing plan for session {SessionId}, iteration {Iteration}/{Max}",
            sessionId, iteration, maxIterations);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.REVIEW_PLAN);

            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.PlanReviewed,
                StateFrom = MentorshipState.PLAN_DECOMPOSITION,
                StateTo = MentorshipState.REVIEW_PLAN
            });

            // Simulate review (in production, this would use Claude AI or human review)
            var roll = Random.Shared.Next(100);
            if (roll < 70) // 70% approval
            {
                await context.CompleteActivityWithOutcomesAsync("Approved");
            }
            else if (iteration >= maxIterations)
            {
                await context.CompleteActivityWithOutcomesAsync("MaxRetries");
            }
            else
            {
                await context.CompleteActivityWithOutcomesAsync("NeedsAdjustment");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during plan review for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// Adjusts implementation plan based on review feedback.
/// Routes to "Adjusted" (back to review).
/// </summary>
[Activity("Tamma.Mentorship", "Adjust Plan",
    "Adjust plan based on review feedback",
    Kind = ActivityKind.Task)]
[FlowNode("Adjusted", "Error")]
public class AdjustPlanActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<AdjustPlanActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);

        logger.LogInformation("Adjusting plan for session {SessionId}", sessionId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.ADJUST_PLAN);

            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.PlanAdjusted,
                StateFrom = MentorshipState.REVIEW_PLAN,
                StateTo = MentorshipState.ADJUST_PLAN
            });

            await context.CompleteActivityWithOutcomesAsync("Adjusted");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during plan adjustment for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// Starts the implementation phase. Routes to "Started".
/// </summary>
[Activity("Tamma.Mentorship", "Start Implementation",
    "Begin implementation work on the story",
    Kind = ActivityKind.Task)]
[FlowNode("Started", "Error")]
public class StartImplementationActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "ID of the story")]
    public Input<string> StoryId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<StartImplementationActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);

        logger.LogInformation("Starting implementation for session {SessionId}, story {StoryId}", sessionId, storyId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.START_IMPLEMENTATION);

            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.ImplementationStarted,
                StateFrom = MentorshipState.REVIEW_PLAN,
                StateTo = MentorshipState.START_IMPLEMENTATION
            });

            await context.CompleteActivityWithOutcomesAsync("Started");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error starting implementation for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// Detects behavioral patterns in junior's work.
/// Routes to "PatternFound" or "NoPattern".
/// </summary>
[Activity("Tamma.Mentorship", "Detect Pattern",
    "Detect behavioral patterns in junior developer's work",
    Kind = ActivityKind.Task)]
[FlowNode("PatternFound", "NoPattern", "Error")]
public class DetectPatternActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "ID of the junior developer")]
    public Input<string> JuniorId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<DetectPatternActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);
        var juniorId = JuniorId.Get(context);

        logger.LogInformation("Detecting patterns for junior {JuniorId} in session {SessionId}", juniorId, sessionId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.DETECT_PATTERN);

            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.PatternDetected,
                StateTo = MentorshipState.DETECT_PATTERN
            });

            // Simulate pattern detection
            var hasPattern = Random.Shared.Next(100) < 60;
            await context.CompleteActivityWithOutcomesAsync(hasPattern ? "PatternFound" : "NoPattern");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error detecting patterns for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// Auto-fixes quality issues found by the quality gate.
/// Routes to "Fixed" (retry quality gate) or "ManualFixNeeded".
/// </summary>
[Activity("Tamma.Mentorship", "Auto Fix Issues",
    "Attempt automatic fix for quality gate failures",
    Kind = ActivityKind.Task)]
[FlowNode("Fixed", "ManualFixNeeded", "Error")]
public class AutoFixIssuesActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "Current auto-fix attempt")]
    public Input<int> AttemptNumber { get; set; } = new(1);

    [Input(Description = "Maximum auto-fix attempts", DefaultValue = 3)]
    public Input<int> MaxAttempts { get; set; } = new(3);

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<AutoFixIssuesActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);
        var attempt = AttemptNumber.Get(context);
        var maxAttempts = MaxAttempts.Get(context);

        logger.LogInformation(
            "Auto-fixing issues for session {SessionId}, attempt {Attempt}/{Max}",
            sessionId, attempt, maxAttempts);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.AUTO_FIX_ISSUES);

            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.AutoFixAttempted,
                StateFrom = MentorshipState.QUALITY_GATE_CHECK,
                StateTo = MentorshipState.AUTO_FIX_ISSUES
            });

            // Simulate auto-fix result
            var fixSucceeded = Random.Shared.Next(100) < 70;

            if (fixSucceeded)
            {
                await context.CompleteActivityWithOutcomesAsync("Fixed");
            }
            else if (attempt >= maxAttempts)
            {
                // Wave C.4 §3 — auto-fix retry envelope exhausted;
                // downstream outcome routes to manual fix. Emit the
                // alert event so operators see the escalation.
                await WorkflowRetryEmitter.EmitAsync(
                    context.GetService<IAlertEventEmitter>(),
                    ClarifyRequirementsActivity.ReadMentorshipTenantId(context),
                    workflowDefinitionId:
                        ClarifyRequirementsActivity.ExtractWorkflowDefinitionId(context),
                    workflowInstanceId:
                        ClarifyRequirementsActivity.ExtractWorkflowInstanceId(context),
                    attempts: attempt,
                    maxAttempts: maxAttempts,
                    finalError: "auto_fix_max_attempts_reached",
                    activityId: context.Activity.Id,
                    ct: context.CancellationToken).ConfigureAwait(false);
                await context.CompleteActivityWithOutcomesAsync("ManualFixNeeded");
            }
            else
            {
                await context.CompleteActivityWithOutcomesAsync("Fixed");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during auto-fix for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// Manual fix required for complex issues that auto-fix cannot handle.
/// Routes to "FixApplied" (back to quality gate) or "NeedHelp" (to guidance).
/// </summary>
[Activity("Tamma.Mentorship", "Manual Fix Required",
    "Guide junior through manual fix for quality issues",
    Kind = ActivityKind.Task)]
[FlowNode("FixApplied", "NeedHelp", "Error")]
public class ManualFixRequiredActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<ManualFixRequiredActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);

        logger.LogInformation("Manual fix required for session {SessionId}", sessionId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.MANUAL_FIX_REQUIRED);

            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.ManualFixRequired,
                StateFrom = MentorshipState.AUTO_FIX_ISSUES,
                StateTo = MentorshipState.MANUAL_FIX_REQUIRED
            });

            var fixApplied = Random.Shared.Next(100) < 70;
            await context.CompleteActivityWithOutcomesAsync(fixApplied ? "FixApplied" : "NeedHelp");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during manual fix for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// Re-requests code review after fixes are applied.
/// Routes to "ReviewRequested" or "MaxRetries".
/// </summary>
[Activity("Tamma.Mentorship", "Re-Request Review",
    "Re-request code review after applying fixes",
    Kind = ActivityKind.Task)]
[FlowNode("ReviewRequested", "MaxRetries", "Error")]
public class ReRequestReviewActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "Current review iteration")]
    public Input<int> Iteration { get; set; } = new(1);

    [Input(Description = "Maximum review iterations", DefaultValue = 5)]
    public Input<int> MaxIterations { get; set; } = new(5);

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<ReRequestReviewActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);
        var iteration = Iteration.Get(context);
        var maxIterations = MaxIterations.Get(context);

        logger.LogInformation(
            "Re-requesting review for session {SessionId}, iteration {Iteration}/{Max}",
            sessionId, iteration, maxIterations);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.RE_REQUEST_REVIEW);

            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.ReviewReRequested,
                StateFrom = MentorshipState.GUIDE_FIXES,
                StateTo = MentorshipState.RE_REQUEST_REVIEW
            });

            if (iteration >= maxIterations)
            {
                logger.LogWarning("Max review iterations reached for session {SessionId}", sessionId);
                await context.CompleteActivityWithOutcomesAsync("MaxRetries");
                return;
            }

            await context.CompleteActivityWithOutcomesAsync("ReviewRequested");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error re-requesting review for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// Escalates the issue to a senior developer or lead.
/// Routes to "Escalated".
/// </summary>
[Activity("Tamma.Mentorship", "Escalate To Senior",
    "Escalate the issue to a senior developer",
    Kind = ActivityKind.Task)]
[FlowNode("Escalated", "Error")]
public class EscalateToSeniorActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "Reason for escalation")]
    public Input<string?> Reason { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<EscalateToSeniorActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);
        var reason = Reason.Get(context);

        logger.LogWarning(
            "Escalating session {SessionId} to senior developer. Reason: {Reason}",
            sessionId, reason ?? "Unspecified");

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.ESCALATE_TO_SENIOR);

            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.EscalatedToSenior,
                StateTo = MentorshipState.ESCALATE_TO_SENIOR
            });

            await context.CompleteActivityWithOutcomesAsync("Escalated");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error escalating session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// Terminal completed state. Routes to "Done".
/// </summary>
[Activity("Tamma.Mentorship", "Completed",
    "Mark mentorship session as completed",
    Kind = ActivityKind.Task)]
[FlowNode("Done")]
public class CompletedActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<CompletedActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);

        logger.LogInformation("Mentorship session {SessionId} completed", sessionId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.COMPLETED);

            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.SessionCompleted,
                StateTo = MentorshipState.COMPLETED
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error marking session {SessionId} as completed", sessionId);
        }

        await context.CompleteActivityWithOutcomesAsync("Done");
    }
}

// =============================================================================
// Exception State Activities (reachable from any active state)
// =============================================================================

/// <summary>
/// Pauses the mentorship session. Routes to "Paused".
/// </summary>
[Activity("Tamma.Mentorship", "Pause Session",
    "Pause the mentorship session",
    Kind = ActivityKind.Task)]
[FlowNode("Paused")]
public class PauseSessionActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "Reason for pausing")]
    public Input<string?> Reason { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<PauseSessionActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);

        logger.LogInformation("Pausing session {SessionId}", sessionId);

        await repository.UpdateStateAsync(sessionId, MentorshipState.PAUSED);
        await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
        {
            SessionId = sessionId,
            EventType = Tamma.Core.Entities.EventTypes.SessionPaused,
            StateTo = MentorshipState.PAUSED
        });

        await context.CompleteActivityWithOutcomesAsync("Paused");
    }
}

/// <summary>
/// Cancels the mentorship session. Routes to "Cancelled".
/// </summary>
[Activity("Tamma.Mentorship", "Cancel Session",
    "Cancel the mentorship session",
    Kind = ActivityKind.Task)]
[FlowNode("Cancelled")]
public class CancelSessionActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "Reason for cancellation")]
    public Input<string?> Reason { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<CancelSessionActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);

        logger.LogInformation("Cancelling session {SessionId}", sessionId);

        await repository.UpdateStateAsync(sessionId, MentorshipState.CANCELLED);
        await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
        {
            SessionId = sessionId,
            EventType = Tamma.Core.Entities.EventTypes.SessionCancelled,
            StateTo = MentorshipState.CANCELLED
        });

        await context.CompleteActivityWithOutcomesAsync("Cancelled");
    }
}

/// <summary>
/// Marks the session as failed. Routes to "Failed".
/// </summary>
[Activity("Tamma.Mentorship", "Fail Session",
    "Mark the mentorship session as failed",
    Kind = ActivityKind.Task)]
[FlowNode("Failed")]
public class FailSessionActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "Failure reason")]
    public Input<string?> Reason { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<FailSessionActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);
        var reason = Reason.Get(context);

        logger.LogError("Session {SessionId} failed. Reason: {Reason}", sessionId, reason ?? "Unknown");

        await repository.UpdateStateAsync(sessionId, MentorshipState.FAILED);
        await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
        {
            SessionId = sessionId,
            EventType = Tamma.Core.Entities.EventTypes.SessionFailed,
            StateTo = MentorshipState.FAILED
        });

        await context.CompleteActivityWithOutcomesAsync("Failed");
    }
}

/// <summary>
/// Marks the session as timed out. Routes to "TimedOut".
/// </summary>
[Activity("Tamma.Mentorship", "Timeout Session",
    "Mark the mentorship session as timed out",
    Kind = ActivityKind.Task)]
[FlowNode("TimedOut")]
public class TimeoutSessionActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<TimeoutSessionActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);

        logger.LogWarning("Session {SessionId} timed out", sessionId);

        await repository.UpdateStateAsync(sessionId, MentorshipState.TIMEOUT);
        await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
        {
            SessionId = sessionId,
            EventType = Tamma.Core.Entities.EventTypes.SessionTimedOut,
            StateTo = MentorshipState.TIMEOUT
        });

        await context.CompleteActivityWithOutcomesAsync("TimedOut");
    }
}

// =============================================================================
// FlowNode wrapper activities for existing CodeActivity-based activities.
// These produce named outcomes for Flowchart routing, resolving dependencies
// from the execution context at runtime.
// =============================================================================

/// <summary>
/// FlowNode wrapper for AssessJuniorCapabilityActivity.
/// Routes to "Correct", "Partial", "Incorrect", or "Error" based on assessment result.
/// </summary>
[Activity("Tamma.Mentorship", "Assess Junior (Flow)",
    "Assess junior's understanding with flowchart routing",
    Kind = ActivityKind.Task)]
[FlowNode("Correct", "Partial", "Incorrect", "Error")]
public class AssessJuniorFlowActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "ID of the story to assess")]
    public Input<string> StoryId { get; set; } = default!;

    [Input(Description = "ID of the junior developer")]
    public Input<string> JuniorId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<AssessJuniorFlowActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);
        var juniorId = JuniorId.Get(context);

        logger.LogInformation(
            "Assessing junior {JuniorId} on story {StoryId} (flow)", juniorId, storyId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.ASSESS_JUNIOR_CAPABILITY);

            var story = await repository.GetStoryByIdAsync(storyId);
            var junior = await repository.GetJuniorByIdAsync(juniorId);

            if (story == null || junior == null)
            {
                await context.CompleteActivityWithOutcomesAsync("Error");
                return;
            }

            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.AssessmentCompleted,
                StateFrom = MentorshipState.VALIDATE_STORY,
                StateTo = MentorshipState.ASSESS_JUNIOR_CAPABILITY
            });

            // Simulate assessment based on skill level vs complexity
            var successChance = (junior.SkillLevel * 20) - (story.Complexity * 10) + 50;
            var roll = Random.Shared.Next(100);

            if (roll < successChance)
                await context.CompleteActivityWithOutcomesAsync("Correct");
            else if (roll < successChance + 25)
                await context.CompleteActivityWithOutcomesAsync("Partial");
            else
                await context.CompleteActivityWithOutcomesAsync("Incorrect");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during assessment for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// FlowNode wrapper for MonitorImplementationActivity.
/// Routes to "Steady", "Complete", "Stalled", "Circular", "Slowing", or "Error".
/// </summary>
[Activity("Tamma.Mentorship", "Monitor Progress (Flow)",
    "Monitor implementation progress with flowchart routing",
    Kind = ActivityKind.Task)]
[FlowNode("Steady", "Complete", "Stalled", "Circular", "Slowing", "Error")]
public class MonitorProgressFlowActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "ID of the story")]
    public Input<string> StoryId { get; set; } = default!;

    [Input(Description = "ID of the junior developer")]
    public Input<string> JuniorId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<MonitorProgressFlowActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);

        logger.LogInformation("Monitoring progress for session {SessionId} (flow)", sessionId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.MONITOR_PROGRESS);

            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.ProgressUpdate,
                StateTo = MentorshipState.MONITOR_PROGRESS
            });

            // Simulate progress check
            var roll = Random.Shared.Next(100);
            if (roll < 30)
                await context.CompleteActivityWithOutcomesAsync("Complete");
            else if (roll < 60)
                await context.CompleteActivityWithOutcomesAsync("Steady");
            else if (roll < 75)
                await context.CompleteActivityWithOutcomesAsync("Slowing");
            else if (roll < 90)
                await context.CompleteActivityWithOutcomesAsync("Stalled");
            else
                await context.CompleteActivityWithOutcomesAsync("Circular");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error monitoring progress for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// FlowNode wrapper for DiagnoseBlockerActivity.
/// Routes to "Hint", "Guidance", "Assistance", or "Escalate" based on diagnosis.
/// </summary>
[Activity("Tamma.Mentorship", "Diagnose Blocker (Flow)",
    "Diagnose blocker with flowchart routing",
    Kind = ActivityKind.Task)]
[FlowNode("Hint", "Guidance", "Assistance", "Escalate", "Error")]
public class DiagnoseBlockerFlowActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "ID of the story")]
    public Input<string> StoryId { get; set; } = default!;

    [Input(Description = "ID of the junior developer")]
    public Input<string> JuniorId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<DiagnoseBlockerFlowActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);

        logger.LogInformation("Diagnosing blocker for session {SessionId} (flow)", sessionId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.DIAGNOSE_BLOCKER);

            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.BlockerDiagnosed,
                StateTo = MentorshipState.DIAGNOSE_BLOCKER
            });

            // Route based on simulated severity
            var roll = Random.Shared.Next(100);
            if (roll < 30)
                await context.CompleteActivityWithOutcomesAsync("Hint");
            else if (roll < 60)
                await context.CompleteActivityWithOutcomesAsync("Guidance");
            else if (roll < 85)
                await context.CompleteActivityWithOutcomesAsync("Assistance");
            else
                await context.CompleteActivityWithOutcomesAsync("Escalate");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error diagnosing blocker for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// FlowNode wrapper for hint-level guidance. Routes to "Done".
/// </summary>
[Activity("Tamma.Mentorship", "Provide Hint (Flow)",
    "Provide hint-level guidance with flowchart routing",
    Kind = ActivityKind.Task)]
[FlowNode("Done", "Error")]
public class ProvideHintFlowActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<ProvideHintFlowActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);

        logger.LogInformation("Providing hint for session {SessionId}", sessionId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.PROVIDE_HINT);
            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.HintProvided,
                StateTo = MentorshipState.PROVIDE_HINT
            });

            await context.CompleteActivityWithOutcomesAsync("Done");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error providing hint for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// FlowNode wrapper for guidance-level support. Routes to "Done".
/// </summary>
[Activity("Tamma.Mentorship", "Provide Guidance (Flow)",
    "Provide guidance-level support with flowchart routing",
    Kind = ActivityKind.Task)]
[FlowNode("Done", "Error")]
public class ProvideGuidanceFlowActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<ProvideGuidanceFlowActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);

        logger.LogInformation("Providing guidance for session {SessionId}", sessionId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.PROVIDE_GUIDANCE);
            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.GuidanceProvided,
                StateTo = MentorshipState.PROVIDE_GUIDANCE
            });

            await context.CompleteActivityWithOutcomesAsync("Done");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error providing guidance for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// FlowNode wrapper for direct assistance. Routes to "Done".
/// </summary>
[Activity("Tamma.Mentorship", "Provide Assistance (Flow)",
    "Provide direct assistance with flowchart routing",
    Kind = ActivityKind.Task)]
[FlowNode("Done", "Error")]
public class ProvideAssistanceFlowActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<ProvideAssistanceFlowActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);

        logger.LogInformation("Providing assistance for session {SessionId}", sessionId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.PROVIDE_ASSISTANCE);
            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.GuidanceProvided,
                StateTo = MentorshipState.PROVIDE_ASSISTANCE
            });

            await context.CompleteActivityWithOutcomesAsync("Done");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error providing assistance for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// FlowNode wrapper for QualityGateCheckActivity.
/// Routes to "Passed", "Failed", or "Error".
/// </summary>
[Activity("Tamma.Mentorship", "Quality Gate (Flow)",
    "Run quality gate checks with flowchart routing",
    Kind = ActivityKind.Task)]
[FlowNode("Passed", "Failed", "Error")]
public class QualityGateFlowActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "ID of the story")]
    public Input<string> StoryId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<QualityGateFlowActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);

        logger.LogInformation("Running quality gate for session {SessionId} (flow)", sessionId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.QUALITY_GATE_CHECK);

            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.QualityGateRun,
                StateTo = MentorshipState.QUALITY_GATE_CHECK
            });

            // Simulate quality gate result
            var passed = Random.Shared.Next(100) < 75;
            await context.CompleteActivityWithOutcomesAsync(passed ? "Passed" : "Failed");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error running quality gate for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// FlowNode wrapper for preparing code review. Routes to "Prepared".
/// </summary>
[Activity("Tamma.Mentorship", "Prepare Code Review (Flow)",
    "Prepare and create PR for code review",
    Kind = ActivityKind.Task)]
[FlowNode("Prepared", "Error")]
public class PrepareCodeReviewFlowActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<PrepareCodeReviewFlowActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);

        logger.LogInformation("Preparing code review for session {SessionId}", sessionId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.PREPARE_CODE_REVIEW);
            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.CodeReviewPrepared,
                StateTo = MentorshipState.PREPARE_CODE_REVIEW
            });

            await context.CompleteActivityWithOutcomesAsync("Prepared");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error preparing code review for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// FlowNode wrapper for monitoring review status.
/// Routes to "Approved", "ChangesRequested", "Pending", or "Error".
/// </summary>
[Activity("Tamma.Mentorship", "Monitor Review (Flow)",
    "Monitor code review status with flowchart routing",
    Kind = ActivityKind.Task)]
[FlowNode("Approved", "ChangesRequested", "Pending", "Error")]
public class MonitorReviewFlowActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<MonitorReviewFlowActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);

        logger.LogInformation("Monitoring review for session {SessionId}", sessionId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.MONITOR_REVIEW);
            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.CodeReviewMonitored,
                StateTo = MentorshipState.MONITOR_REVIEW
            });

            // Simulate review status
            var roll = Random.Shared.Next(100);
            if (roll < 50)
                await context.CompleteActivityWithOutcomesAsync("Approved");
            else if (roll < 80)
                await context.CompleteActivityWithOutcomesAsync("ChangesRequested");
            else
                await context.CompleteActivityWithOutcomesAsync("Pending");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error monitoring review for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// FlowNode wrapper for guiding review fixes. Routes to "Guided".
/// </summary>
[Activity("Tamma.Mentorship", "Guide Fixes (Flow)",
    "Guide junior through review fixes with flowchart routing",
    Kind = ActivityKind.Task)]
[FlowNode("Guided", "Error")]
public class GuideFixesFlowActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<GuideFixesFlowActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);

        logger.LogInformation("Guiding fixes for session {SessionId}", sessionId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.GUIDE_FIXES);
            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.CodeReviewUpdate,
                StateTo = MentorshipState.GUIDE_FIXES
            });

            await context.CompleteActivityWithOutcomesAsync("Guided");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error guiding fixes for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// FlowNode wrapper for merge and complete. Routes to "Merged".
/// </summary>
[Activity("Tamma.Mentorship", "Merge And Complete (Flow)",
    "Merge PR and begin completion with flowchart routing",
    Kind = ActivityKind.Task)]
[FlowNode("Merged", "Error")]
public class MergeAndCompleteFlowActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<MergeAndCompleteFlowActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);

        logger.LogInformation("Merging and completing session {SessionId}", sessionId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.MERGE_AND_COMPLETE);
            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.MergeCompleted,
                StateFrom = MentorshipState.MONITOR_REVIEW,
                StateTo = MentorshipState.MERGE_AND_COMPLETE
            });

            await context.CompleteActivityWithOutcomesAsync("Merged");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error merging session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// FlowNode wrapper for generating session report. Routes to "Generated".
/// </summary>
[Activity("Tamma.Mentorship", "Generate Report (Flow)",
    "Generate session report with flowchart routing",
    Kind = ActivityKind.Task)]
[FlowNode("Generated", "Error")]
public class GenerateReportFlowActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<GenerateReportFlowActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);

        logger.LogInformation("Generating report for session {SessionId}", sessionId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.GENERATE_REPORT);
            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.ReportGenerated,
                StateFrom = MentorshipState.MERGE_AND_COMPLETE,
                StateTo = MentorshipState.GENERATE_REPORT
            });

            await context.CompleteActivityWithOutcomesAsync("Generated");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating report for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}

/// <summary>
/// FlowNode wrapper for updating junior skill profile. Routes to "Updated".
/// </summary>
[Activity("Tamma.Mentorship", "Update Skill Profile (Flow)",
    "Update junior skill profile with flowchart routing",
    Kind = ActivityKind.Task)]
[FlowNode("Updated", "Error")]
public class UpdateSkillProfileFlowActivity : Activity
{
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<UpdateSkillProfileFlowActivity>>();
        var repository = context.GetRequiredService<IMentorshipSessionRepository>();
        var sessionId = SessionId.Get(context);

        logger.LogInformation("Updating skill profile for session {SessionId}", sessionId);

        try
        {
            await repository.UpdateStateAsync(sessionId, MentorshipState.UPDATE_SKILL_PROFILE);
            await repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.SkillProfileUpdated,
                StateFrom = MentorshipState.GENERATE_REPORT,
                StateTo = MentorshipState.UPDATE_SKILL_PROFILE
            });

            await context.CompleteActivityWithOutcomesAsync("Updated");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating skill profile for session {SessionId}", sessionId);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }
}
