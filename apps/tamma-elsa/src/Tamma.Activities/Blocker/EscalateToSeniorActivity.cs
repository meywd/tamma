using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.Blocker.Models;
using Tamma.Activities.LlmCall;

namespace Tamma.Activities.Blocker;

/// <summary>
/// Bookmark-based activity that compiles a full context dump and escalates to a senior developer.
/// The workflow suspends and waits for the senior to respond via API or Slack.
///
/// <para><b>Durable senior-response SLA timeout (completeness audit 2026-06-22, 7-1G AC2 /
/// §Missing #1+#2; hardened 2026-06-25 to the durable Delay primitive).</b> Two resume paths
/// are armed when the activity suspends: a custom escalation bookmark
/// (<c>blocker-escalation-{session}</c>) resumed by the senior → <see cref="Resolved"/>; and a
/// DURABLE delay bookmark via
/// <see cref="Elsa.Extensions.DelayActivityExecutionContextExtensions.DelayFor(ActivityExecutionContext, System.TimeSpan, ExecuteActivityDelegate)"/>
/// at the SLA (<c>BlockerDiagnosis:EscalationTimeoutMinutes</c>, default 1440 = 24h) →
/// <see cref="TimedOut"/>. The earlier build scheduled the SLA via
/// <c>IWorkflowScheduler.ScheduleAtAsync</c>, whose default in-memory backing is LOST on a
/// host restart — fatal here because the SLA defaults to 24h and a VPS restart inside that
/// window is routine. The Delay bookmark is EF-persisted and re-armed by
/// <c>Elsa.Scheduling</c>'s startup task on rehydration, so a restart mid-wait no longer
/// drops the SLA. Whichever path resumes first completes the activity; Elsa burns the
/// remaining bookmark, so there is no orphaned timer / stale double-resume.</para>
/// </summary>
[Activity(
    "Tamma.Blocker",
    "Escalate To Senior",
    "Compile context and notify senior developer for blocker resolution",
    Kind = ActivityKind.Task
)]
public class EscalateToSeniorActivity : Activity
{
    private readonly ILogger<EscalateToSeniorActivity>? _logger;
    private readonly IConfiguration? _configuration;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Story ID</summary>
    [Input(Description = "Story identifier")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Junior developer ID</summary>
    [Input(Description = "Junior developer identifier")]
    public Input<string> JuniorId { get; set; } = default!;

    /// <summary>Blocker type classification</summary>
    [Input(Description = "Classified blocker type")]
    public Input<string> BlockerType { get; set; } = default!;

    /// <summary>Blocker severity</summary>
    [Input(Description = "Blocker severity")]
    public Input<string> BlockerSeverity { get; set; } = default!;

    /// <summary>Diagnosis details from AI</summary>
    [Input(Description = "Diagnosis details")]
    public Input<string> DiagnosisDetails { get; set; } = default!;

    /// <summary>Previous resolution attempts</summary>
    [Input(Description = "Previous resolution attempts")]
    public Input<List<string>> PreviousAttempts { get; set; } = default!;

    /// <summary>Aggregated signals (optional)</summary>
    [Input(Description = "Aggregated signals from collection")]
    public Input<AggregatedSignals?> Signals { get; set; } = default!;

    /// <summary>Whether the escalation was resolved by the senior</summary>
    [Output(Description = "Whether the senior resolved the blocker")]
    public Output<bool> Resolved { get; set; } = default!;

    /// <summary>Whether the senior-response SLA expired with no response (durable timeout).</summary>
    [Output(Description = "Whether the senior-response SLA expired with no response")]
    public Output<bool> TimedOut { get; set; } = default!;

    /// <summary>Senior's response</summary>
    [Output(Description = "Senior's response")]
    public Output<string?> SeniorResponse { get; set; } = default!;

    [JsonConstructor]
    public EscalateToSeniorActivity() { }

    public EscalateToSeniorActivity(
        ILogger<EscalateToSeniorActivity> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// The SINGLE canonical escalation-bookmark name (<c>blocker-escalation-{session}</c>).
    /// Shared by the suspend side (<see cref="ExecuteAsync"/>) and the resume side
    /// (<c>BlockerResumeEndpoint</c>) so the two match byte-for-byte — the same
    /// suspend/resume-name-parity discipline as
    /// <c>WaitForMergeApprovalActivity.BookmarkName</c>. The name is keyed by the
    /// (globally-unique, unguessable) mentorship session id; the resume endpoint
    /// additionally verifies the caller's tenant OWNS that session before it ever
    /// resolves this name (IDOR guard on the Tamma.Api tier).
    /// </summary>
    public static string EscalationBookmarkName(Guid sessionId)
        => $"blocker-escalation-{sessionId}";

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);
        var juniorId = JuniorId.Get(context);
        var blockerType = BlockerType.Get(context);
        var severity = BlockerSeverity.Get(context);
        var diagnosisDetails = DiagnosisDetails.Get(context);
        var previousAttempts = PreviousAttempts.Get(context) ?? new List<string>();
        var signals = Signals.Get(context);

        _logger?.LogInformation(
            "Escalating blocker to senior: Session={SessionId}, Type={BlockerType}, Severity={Severity}",
            sessionId, blockerType, severity);

        // Compile context dump
        var escalationContext = new EscalationContext
        {
            SessionId = sessionId,
            StoryId = storyId,
            JuniorId = juniorId,
            BlockerType = Enum.TryParse<BlockerCategory>(blockerType, out var bt)
                ? bt
                : BlockerCategory.TechnicalKnowledgeGap,
            Severity = Enum.TryParse<BlockerDiagnosisSeverity>(severity, out var sev)
                ? sev
                : BlockerDiagnosisSeverity.High,
            DiagnosisDetails = diagnosisDetails,
            PreviousAttempts = previousAttempts,
            Signals = signals,
            EscalatedAt = DateTime.UtcNow
        };

        // Notify senior via configured channel
        await NotifySenior(context, escalationContext);

        // 1) Escalation bookmark — resumed by the senior's response (API/Slack). The workflow
        //    suspends here until this resumes OR the durable SLA delay below fires.
        context.CreateBookmark(new CreateBookmarkArgs
        {
            BookmarkName = EscalationBookmarkName(sessionId),
            Callback = OnResumeAsync,
            AutoBurn = true
        });

        // 2) Durable senior-response SLA — a DelayFor (Delay) bookmark that Elsa.Scheduling's
        //    startup background task RE-ARMS after a host restart (EF-persisted, not an
        //    in-memory timer). A never-answered escalation now terminates as a real Timeout
        //    even across a VPS restart inside the (default 24h) SLA window.
        var slaMinutes = _configuration?.GetValue<int?>("BlockerDiagnosis:EscalationTimeoutMinutes") ?? 1440;
        context.DelayFor(TimeSpan.FromMinutes(Math.Max(1, slaMinutes)), OnTimeoutAsync);

        _logger?.LogInformation(
            "Escalation awaiting senior; durable SLA timeout armed at +{SlaMinutes}min for session {SessionId}",
            slaMinutes, sessionId);
    }

    private async Task NotifySenior(ActivityExecutionContext context, EscalationContext escalation)
    {
        var escalationChannel = _configuration?["BlockerDiagnosis:EscalationChannel"] ?? "slack";
        var seniorChannel = _configuration?["BlockerDiagnosis:SeniorNotificationChannel"] ?? "#mentorship-escalations";

        var message = $@"**Tamma: Blocker Escalation**

A junior developer needs senior help with a blocker that could not be resolved through automated guidance.

*Session:* {escalation.SessionId}
*Story:* {escalation.StoryId}
*Junior:* {escalation.JuniorId}
*Blocker Type:* {escalation.BlockerType}
*Severity:* {escalation.Severity}

*Diagnosis:*
{escalation.DiagnosisDetails}

*Previous Attempts ({escalation.PreviousAttempts.Count}):*
{string.Join("\n", escalation.PreviousAttempts.Select((a, i) => $"{i + 1}. {a}"))}

Please respond to this escalation via the Tamma API or reply in this thread.";

        try
        {
            if (escalationChannel == "slack")
            {
                // Story 38-3b — enqueue the senior-channel post via the API seam
                // (engine holds no Slack credential); fire-and-forget, fail-soft so
                // the escalation bookmark is still created if the notification fails.
                await MediatedSlack.QueueChannelMessageAsync(
                    context, seniorChannel, message, "Warning", "SendNotification", context.CancellationToken);
            }

            _logger?.LogInformation("Senior notification sent for session {SessionId}", escalation.SessionId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to send senior notification — bookmark still created");
        }
    }

    /// <summary>
    /// External resume path: the senior responded. Reads the senior's outcome from the resume
    /// input and completes — Elsa burns the still-armed SLA Delay bookmark on completion (no
    /// orphaned timer).
    /// </summary>
    private async ValueTask OnResumeAsync(ActivityExecutionContext context)
    {
        var (resolved, seniorResponse) = ReadSeniorOutcome(context.WorkflowInput);

        _logger?.LogInformation("Senior escalation resumed (external): Resolved={Resolved}", resolved);

        context.Set(Resolved, resolved);
        context.Set(TimedOut, false);
        context.Set(SeniorResponse, seniorResponse);

        await context.CompleteActivityAsync();
    }

    /// <summary>
    /// Pure read-back of the senior outcome from the bookmark resume input (exposed for unit
    /// testing). <c>Resolved</c> is coerced via <see cref="BlockerResumeInput.AsBool"/> so it is
    /// correct whether the runtime delivers the flag as a boxed <see cref="bool"/> (in-process)
    /// or as a <see cref="string"/> / <see cref="System.Text.Json.JsonElement"/> (serializing
    /// dispatcher). <c>SeniorResponse</c> is a string read via <c>.ToString()</c>, which is
    /// already serialization-tolerant.
    /// </summary>
    internal static (bool Resolved, string? SeniorResponse) ReadSeniorOutcome(IDictionary<string, object> input)
    {
        var resolved = input.TryGetValue("Resolved", out var r) && BlockerResumeInput.AsBool(r);
        var seniorResponse = input.TryGetValue("SeniorResponse", out var sr) ? sr?.ToString() : null;
        return (resolved, seniorResponse);
    }

    /// <summary>
    /// Durable timeout path: the senior-response SLA elapsed with no response. The Delay
    /// bookmark scheduler resumes the activity here (and re-arms across a host restart). Flags
    /// <see cref="TimedOut"/> (not <see cref="Resolved"/>) so the workflow reports the real
    /// Timeout terminal instead of suspending forever; the still-armed escalation bookmark is
    /// burned on completion.
    /// </summary>
    private async ValueTask OnTimeoutAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);

        _logger?.LogWarning(
            "Senior-response SLA expired (durable timeout) for session {SessionId} — taking the Timeout terminal",
            sessionId);

        context.Set(Resolved, false);
        context.Set(TimedOut, true);
        context.Set(SeniorResponse, null);

        await context.CompleteActivityAsync();
    }
}
