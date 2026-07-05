using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.ADL;

namespace Tamma.Activities.Clarify;

/// <summary>
/// Story 3.5 — bookmark-based human gate: suspend the <c>clarifying-questions</c>
/// workflow until the stakeholder answers the delivered questions, then surface the
/// answers as a typed outcome the flowchart branches on.
///
/// <para>Two resume paths are armed when the activity suspends:
/// <list type="bullet">
///   <item><description>an answer bookmark
///   (<c>clarify-answers-{tenant}-{session}</c>, see <see cref="AnswersBookmarkName"/>)
///   resumed by the stakeholder's answers via the secure resume endpoint →
///   <c>Answered</c>;</description></item>
///   <item><description>a DURABLE delay bookmark via
///   <see cref="Elsa.Extensions.DelayActivityExecutionContextExtensions.DelayFor(ActivityExecutionContext, System.TimeSpan, ExecuteActivityDelegate)"/>
///   at the answer SLA (<c>Clarify:AnswerTimeoutMinutes</c>, default 4320 = 3 days)
///   → <c>Timeout</c>.</description></item>
/// </list>
/// The Delay bookmark is EF-persisted and re-armed by <c>Elsa.Scheduling</c>'s startup
/// task on rehydration, so a host restart mid-wait no longer drops the SLA (same
/// hardening as <see cref="Tamma.Activities.Blocker.EscalateToSeniorActivity"/>).
/// Whichever path resumes first completes the activity; Elsa burns the remaining
/// bookmark, so there is no orphaned timer / stale double-resume.</para>
///
/// <para><b>SECURITY (IDOR)</b> — the bookmark name folds in the tenant id (the
/// merge/deploy-gate posture, <see cref="WaitForMergeApprovalActivity.BookmarkName"/>).
/// A resume caller scoped to tenant A computes a name keyed by tenant A, so it can NEVER
/// resolve tenant B's gate; a cross-tenant attempt simply 404s (bookmark not found), it
/// never acts. This is table-free (no clarify-session row is minted — Story 3.5 is a
/// NON-MIGRATION change) yet strictly cross-tenant-safe. The session id is itself an
/// unguessable 128-bit Guid, so within a tenant the name is unguessable too.</para>
/// </summary>
[Activity(
    "Tamma.Clarify",
    "Wait For Clarifying Answers",
    "Suspend workflow until the stakeholder answers the clarifying questions or the SLA expires",
    Kind = ActivityKind.Task
)]
[FlowNode("Answered", "Timeout")]
public class WaitForClarifyingAnswersActivity : Activity
{
    private readonly ILogger<WaitForClarifyingAnswersActivity>? _logger;
    private readonly IConfiguration? _configuration;

    [Input(Description = "Clarify session id (bookmark scoping)")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Tenant id (GUID string or empty for single-user). Folded into the
    /// bookmark name so a cross-tenant resume can never resolve this gate.</summary>
    [Input(Description = "Tenant id (bookmark scoping — prevents cross-tenant resume/IDOR)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    /// <summary>The stakeholder's answers (set on bookmark resume).</summary>
    [Output(Description = "Stakeholder's answers text")]
    public Output<string?> Answers { get; set; } = default!;

    /// <summary>Whether answers were received (vs a timeout).</summary>
    [Output(Description = "Whether answers were received")]
    public Output<bool> Answered { get; set; } = default!;

    /// <summary>Whether the answer SLA expired with no response (durable timeout).</summary>
    [Output(Description = "Whether the answer SLA expired with no response")]
    public Output<bool> TimedOut { get; set; } = default!;

    [JsonConstructor]
    public WaitForClarifyingAnswersActivity() { }

    public WaitForClarifyingAnswersActivity(
        ILogger<WaitForClarifyingAnswersActivity> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// The SINGLE canonical answer-bookmark name (<c>clarify-answers-{tenant}-{session}</c>).
    /// Shared by the suspend side (<see cref="ExecuteAsync"/>) and the resume side
    /// (<c>ClarifyResumeEndpoint</c>) so the two match byte-for-byte — the same
    /// suspend/resume-name-parity discipline as
    /// <see cref="WaitForMergeApprovalActivity.BookmarkName"/>. The tenant segment is
    /// normalised via the SAME <see cref="WaitForMergeApprovalActivity.NormalizeSegment"/>
    /// transform on both sides so the names always agree.
    /// </summary>
    public static string AnswersBookmarkName(string? tenantId, Guid sessionId)
    {
        var tenant = WaitForMergeApprovalActivity.NormalizeSegment(tenantId);
        return $"clarify-answers-{tenant}-{sessionId}";
    }

    protected override void Execute(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var tenantId = TenantId.Get(context);
        var bookmarkName = AnswersBookmarkName(tenantId, sessionId);

        _logger?.LogInformation(
            "Waiting for clarifying answers: bookmark={BookmarkName} for session {SessionId}",
            bookmarkName, sessionId);

        // 1) Answer bookmark — resumed by the stakeholder via the secure resume endpoint.
        context.CreateBookmark(new CreateBookmarkArgs
        {
            BookmarkName = bookmarkName,
            Callback = OnAnswersReceivedAsync,
            AutoBurn = true,
            IncludeActivityInstanceId = false,
        });

        // 2) Durable answer SLA — a DelayFor (Delay) bookmark that Elsa.Scheduling's
        //    startup task re-arms after a host restart (EF-persisted, not an in-memory
        //    timer). A never-answered clarification terminates as a real Timeout even
        //    across a VPS restart inside the (default 3-day) SLA window.
        var slaMinutes = _configuration?.GetValue<int?>("Clarify:AnswerTimeoutMinutes") ?? 4320;
        context.DelayFor(TimeSpan.FromMinutes(Math.Max(1, slaMinutes)), OnTimeoutAsync);

        _logger?.LogInformation(
            "Clarify answer wait armed; durable SLA timeout at +{SlaMinutes}min for session {SessionId}",
            slaMinutes, sessionId);
    }

    /// <summary>External resume path: the stakeholder answered. Reads the answers from
    /// the resume input and completes — Elsa burns the still-armed SLA Delay bookmark on
    /// completion (no orphaned timer).</summary>
    private async ValueTask OnAnswersReceivedAsync(ActivityExecutionContext context)
    {
        var (answered, answers) = ReadAnswers(context.WorkflowInput);

        _logger?.LogInformation(
            "Clarify answers resumed (external): Answered={Answered}, length={Length}",
            answered, answers?.Length ?? 0);

        context.Set(Answers, answers);
        context.Set(Answered, answered);
        context.Set(TimedOut, false);

        await context.CompleteActivityWithOutcomesAsync("Answered");
    }

    /// <summary>Durable timeout path: the answer SLA elapsed with no response. Flags
    /// <see cref="TimedOut"/> (not <see cref="Answered"/>) so the workflow reports the
    /// real Timeout terminal instead of suspending forever.</summary>
    private async ValueTask OnTimeoutAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        _logger?.LogWarning(
            "Clarify answer SLA expired (durable timeout) for session {SessionId} — taking the Timeout terminal",
            sessionId);

        context.Set(Answers, null);
        context.Set(Answered, false);
        context.Set(TimedOut, true);

        await context.CompleteActivityWithOutcomesAsync("Timeout");
    }

    /// <summary>
    /// Pure read-back of the stakeholder outcome from the bookmark resume input
    /// (exposed for unit testing). <c>Answered</c> is coerced via
    /// <see cref="ResumeInput.AsBool"/> so it is correct whether the runtime delivers the
    /// flag as a boxed <see cref="bool"/> (in-process) or as a <see cref="string"/> /
    /// <see cref="System.Text.Json.JsonElement"/> (serializing dispatcher) — the #15/#437
    /// lesson: never a bare <c>is true</c> on resume input. <c>Answers</c> is read via
    /// <c>.ToString()</c>, which is already serialization-tolerant.
    /// </summary>
    public static (bool Answered, string? Answers) ReadAnswers(IDictionary<string, object> input)
    {
        var answered = input.TryGetValue("Answered", out var a) && ResumeInput.AsBool(a);
        var answers = input.TryGetValue("Answers", out var ans) ? ans?.ToString() : null;
        return (answered, answers);
    }
}
