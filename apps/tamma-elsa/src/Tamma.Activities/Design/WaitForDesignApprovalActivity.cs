using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.ADL;

namespace Tamma.Activities.Design;

/// <summary>
/// Story 3.7 — bookmark-based human gate: suspend the <c>design-proposal</c> workflow until
/// a reviewer approves or rejects the delivered design proposal, then surface the decision
/// as a typed outcome the flowchart branches on (Story-3.7 AC "Design reviews are automated
/// with stakeholder routing and feedback collection").
///
/// <para>Two resume paths are armed when the activity suspends:
/// <list type="bullet">
///   <item><description>an approval bookmark
///   (<c>design-approval-{tenant}-{session}</c>, see <see cref="ApprovalBookmarkName"/>)
///   resumed by the reviewer's decision via the secure resume endpoint → <c>Approved</c> or
///   <c>Rejected</c> depending on the injected <c>Approved</c> flag;</description></item>
///   <item><description>a DURABLE delay bookmark via
///   <see cref="Elsa.Extensions.DelayActivityExecutionContextExtensions.DelayFor(ActivityExecutionContext, System.TimeSpan, ExecuteActivityDelegate)"/>
///   at the review SLA (<c>Design:ReviewTimeoutMinutes</c>, default 4320 = 3 days) →
///   <c>Timeout</c>.</description></item>
/// </list>
/// The Delay bookmark is EF-persisted and re-armed by <c>Elsa.Scheduling</c>'s startup task
/// on rehydration, so a host restart mid-wait no longer drops the SLA (same hardening as
/// <see cref="Tamma.Activities.Clarify.WaitForClarifyingAnswersActivity"/>). Whichever path
/// resumes first completes the activity; Elsa burns the remaining bookmark, so there is no
/// orphaned timer / stale double-resume.</para>
///
/// <para><b>SECURITY (IDOR)</b> — the bookmark name folds in the tenant id (the
/// merge/deploy-gate posture, <see cref="WaitForMergeApprovalActivity.BookmarkName"/>). A
/// resume caller scoped to tenant A computes a name keyed by tenant A, so it can NEVER
/// resolve tenant B's gate; a cross-tenant attempt simply 404s (bookmark not found), it
/// never acts. This is table-free (no design-proposal row is minted — Story 3.7 is a
/// NON-MIGRATION change; the proposal lives in workflow state + DCB events) yet strictly
/// cross-tenant-safe. The session id is itself an unguessable 128-bit Guid, so within a
/// tenant the name is unguessable too.</para>
/// </summary>
[Activity(
    "Tamma.Design",
    "Wait For Design Approval",
    "Suspend workflow until a reviewer approves/rejects the design proposal or the SLA expires",
    Kind = ActivityKind.Task
)]
[FlowNode("Approved", "Rejected", "Timeout")]
public class WaitForDesignApprovalActivity : Activity
{
    private readonly ILogger<WaitForDesignApprovalActivity>? _logger;
    private readonly IConfiguration? _configuration;

    [Input(Description = "Design session id (bookmark scoping)")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Tenant id (GUID string or empty for single-user). Folded into the bookmark
    /// name so a cross-tenant resume can never resolve this gate.</summary>
    [Input(Description = "Tenant id (bookmark scoping — prevents cross-tenant resume/IDOR)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    /// <summary>Whether the reviewer approved (set on bookmark resume).</summary>
    [Output(Description = "Whether the reviewer approved the design")]
    public Output<bool> Approved { get; set; } = default!;

    /// <summary>The reviewer's feedback (set on bookmark resume).</summary>
    [Output(Description = "Reviewer feedback text")]
    public Output<string?> Feedback { get; set; } = default!;

    /// <summary>Whether the review SLA expired with no decision (durable timeout).</summary>
    [Output(Description = "Whether the review SLA expired with no decision")]
    public Output<bool> TimedOut { get; set; } = default!;

    [JsonConstructor]
    public WaitForDesignApprovalActivity() { }

    public WaitForDesignApprovalActivity(
        ILogger<WaitForDesignApprovalActivity> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// The SINGLE canonical approval-bookmark name (<c>design-approval-{tenant}-{session}</c>).
    /// Shared by the suspend side (<see cref="Execute"/>) and the resume side
    /// (<c>DesignResumeEndpoint</c>) so the two match byte-for-byte — the same
    /// suspend/resume-name-parity discipline as
    /// <see cref="WaitForMergeApprovalActivity.BookmarkName"/>. The tenant segment is
    /// normalised via the SAME <see cref="WaitForMergeApprovalActivity.NormalizeSegment"/>
    /// transform on both sides so the names always agree.
    /// </summary>
    public static string ApprovalBookmarkName(string? tenantId, Guid sessionId)
    {
        var tenant = WaitForMergeApprovalActivity.NormalizeSegment(tenantId);
        return $"design-approval-{tenant}-{sessionId}";
    }

    protected override void Execute(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var tenantId = TenantId.Get(context);
        var bookmarkName = ApprovalBookmarkName(tenantId, sessionId);

        _logger?.LogInformation(
            "Waiting for design approval: bookmark={BookmarkName} for session {SessionId}",
            bookmarkName, sessionId);

        // 1) Approval bookmark — resumed by the reviewer via the secure resume endpoint.
        context.CreateBookmark(new CreateBookmarkArgs
        {
            BookmarkName = bookmarkName,
            Callback = OnDecisionReceivedAsync,
            AutoBurn = true,
            IncludeActivityInstanceId = false,
        });

        // 2) Durable review SLA — a DelayFor (Delay) bookmark that Elsa.Scheduling's startup
        //    task re-arms after a host restart (EF-persisted, not an in-memory timer). A
        //    never-reviewed proposal terminates as a real Timeout even across a VPS restart
        //    inside the (default 3-day) SLA window.
        var slaMinutes = _configuration?.GetValue<int?>("Design:ReviewTimeoutMinutes") ?? 4320;
        context.DelayFor(TimeSpan.FromMinutes(Math.Max(1, slaMinutes)), OnTimeoutAsync);

        _logger?.LogInformation(
            "Design review wait armed; durable SLA timeout at +{SlaMinutes}min for session {SessionId}",
            slaMinutes, sessionId);
    }

    /// <summary>External resume path: the reviewer decided. Reads the decision from the
    /// resume input and completes with <c>Approved</c> or <c>Rejected</c> — Elsa burns the
    /// still-armed SLA Delay bookmark on completion (no orphaned timer).</summary>
    private async ValueTask OnDecisionReceivedAsync(ActivityExecutionContext context)
    {
        var (approved, feedback) = ReadDecision(context.WorkflowInput);

        _logger?.LogInformation(
            "Design review resumed (external): Approved={Approved}, feedbackLength={Length}",
            approved, feedback?.Length ?? 0);

        context.Set(Approved, approved);
        context.Set(Feedback, feedback);
        context.Set(TimedOut, false);

        await context.CompleteActivityWithOutcomesAsync(approved ? "Approved" : "Rejected");
    }

    /// <summary>Durable timeout path: the review SLA elapsed with no decision. Flags
    /// <see cref="TimedOut"/> (not <see cref="Approved"/>) so the workflow reports the real
    /// Timeout terminal instead of suspending forever — NEVER a silent false approval.</summary>
    private async ValueTask OnTimeoutAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        _logger?.LogWarning(
            "Design review SLA expired (durable timeout) for session {SessionId} — taking the Timeout terminal",
            sessionId);

        context.Set(Approved, false);
        context.Set(Feedback, null);
        context.Set(TimedOut, true);

        await context.CompleteActivityWithOutcomesAsync("Timeout");
    }

    /// <summary>
    /// Pure read-back of the reviewer decision from the bookmark resume input (exposed for
    /// unit testing). <c>Approved</c> is coerced via <see cref="ResumeInput.AsBool"/> so it
    /// is correct whether the runtime delivers the flag as a boxed <see cref="bool"/>
    /// (in-process) or as a <see cref="string"/> / <see cref="System.Text.Json.JsonElement"/>
    /// (serializing dispatcher) — the #15/#437 lesson: never a bare <c>is true</c> on resume
    /// input, which silently mis-branches (a rejection read as an approval) under
    /// serialization while still returning HTTP 200. <c>Feedback</c> is read via
    /// <c>.ToString()</c>, which is already serialization-tolerant.
    /// </summary>
    public static (bool Approved, string? Feedback) ReadDecision(IDictionary<string, object> input)
    {
        var approved = input.TryGetValue("Approved", out var a) && ResumeInput.AsBool(a);
        var feedback = input.TryGetValue("Feedback", out var f) ? f?.ToString() : null;
        return (approved, feedback);
    }
}
