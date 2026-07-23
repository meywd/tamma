using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ADL;
using Tamma.Activities.Core;
using Tamma.Core;
using Tamma.Core.Documents.Policy;

namespace Tamma.Activities.Documents;

/// <summary>
/// Story 39-8 (AC4) — the ONE generic document-decision gate. Suspends any
/// lifecycle on a tenant-folded, unguessable bookmark until the orchestrator (or
/// the human the orchestrator assigned) supplies a decision, then surfaces that
/// decision — mapped onto 39-5's <see cref="AcceptanceDecision"/> — as a typed
/// outcome the flowchart branches on. It generalizes the five proven per-workflow
/// resume gates (<c>WaitForDesignApprovalActivity</c> et al.) into a single reuse:
/// the 39-6 ACCEPT stage registers THIS gate regardless of who the orchestrator
/// routes the decision to (self-decision and assigned-human decision resume it
/// identically — the decider varies, the gate does not).
///
/// <para><b>SECURITY (IDOR)</b> — the bookmark name folds in the tenant id (the
/// design/merge-gate posture, <see cref="DecisionBookmarkName"/> →
/// <see cref="WaitForMergeApprovalActivity.NormalizeSegment"/>). A resume caller
/// scoped to tenant A computes a name keyed by tenant A, so it can NEVER resolve
/// tenant B's gate; a cross-tenant attempt simply 404s. The session id is itself an
/// unguessable 128-bit decision-session Guid (39-6 mints it at Init), so within a
/// tenant the name is unguessable too.</para>
///
/// <para><b>D8 — no SLA timer.</b> Unlike the design/escalation gates, NO
/// <c>DelayFor</c> timeout is armed: suspended-on-bookmark is a legal resumability
/// posture (39-10), the orchestrator/Task-View liveness policy belongs to
/// 39-17/39-19, and a silent timeout would be a decision nobody took. The
/// <c>DelayFor</c> pattern (see <c>WaitForDesignApprovalActivity</c>) is the
/// retrofit seam if that policy ever arrives.</para>
///
/// <para><b>Events (D3).</b> The gate emits both <c>APPROVAL.*</c> events itself,
/// via <see cref="TammaEventEmitter"/> → the durable <c>EventDrain</c>:
/// <c>APPROVAL.REQUESTED</c> at <see cref="Execute"/> (request+suspend is one
/// atomic site), <c>APPROVAL.PROVIDED</c> at the resume callback. Emitting inside
/// the engine (not the endpoint) keeps the events on the durable drain with full
/// workflow tag context and keeps the endpoint a thin resume seam.</para>
/// </summary>
[Activity(
    "Tamma.Documents",
    "Wait For Document Decision",
    "Suspend any lifecycle until a document decision (accept/request-revision/reject/escalate) is injected on the tenant-folded bookmark",
    Kind = ActivityKind.Task
)]
[FlowNode("Accept", "RequestRevision", "Reject", "Escalate")]
public class WaitForDocumentDecisionActivity : Activity
{
    private readonly ILogger<WaitForDocumentDecisionActivity>? _logger;

    /// <summary>Decision-session id (39-6 mints it at Init) — the unguessable bookmark scope.</summary>
    [Input(Description = "Decision-session id (bookmark scoping)")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Tenant id (GUID string or empty for single-user). Folded into the bookmark
    /// name so a cross-tenant resume can never resolve this gate.</summary>
    [Input(Description = "Tenant id (bookmark scoping — prevents cross-tenant resume/IDOR)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    /// <summary>D4 — ISO-8601 timestamp the accept stage stamped when it built the request;
    /// REQUIRED. The callback runs on a rehydrated activity (Execute-time locals are gone),
    /// so <c>durationMs</c> is computed off this re-read input, not activity-instance state.</summary>
    [Input(Description = "ISO-8601 UTC time the acceptance request was published (durationMs basis)")]
    public Input<string> RequestedAtUtc { get; set; } = default!;

    /// <summary>D7 — the resolved-rules summary (source+version+autonomy) the decision is made
    /// under; stamped by the server into both REQUESTED and PROVIDED so orchestrator decisions
    /// are auditable without trusting the orchestrator's self-report.</summary>
    [Input(Description = "Resolved acceptance-rules reference (source+version+autonomy summary)")]
    public Input<string?> RulesReference { get; set; } = new((string?)null);

    [Input(Description = "Issue / requirement id (tag threading)")]
    public Input<string?> IssueId { get; set; } = new((string?)null);

    [Input(Description = "Document id under decision (tag threading)")]
    public Input<string?> DocumentId { get; set; } = new((string?)null);

    [Input(Description = "Document type key (tag threading)")]
    public Input<string?> DocumentType { get; set; } = new((string?)null);

    [Input(Description = "Correlation id (tag threading)")]
    public Input<string?> CorrelationId { get; set; } = new((string?)null);

    /// <summary>The serialized 39-5 <see cref="AcceptanceDecision"/> (canonical form).</summary>
    [Output(Description = "Serialized AcceptanceDecision (39-5)")]
    public Output<string> DecisionJson { get; set; } = default!;

    [Output(Description = "Decider feedback / notes")]
    public Output<string?> Feedback { get; set; } = default!;

    [Output(Description = "Server-derived decider identity")]
    public Output<string?> DeciderId { get; set; } = default!;

    [Output(Description = "Transport channel the decision arrived on (orchestrator|user|api)")]
    public Output<string> Channel { get; set; } = default!;

    [Output(Description = "Time-to-decision in milliseconds (denormalized)")]
    public Output<long> DurationMs { get; set; } = default!;

    [JsonConstructor]
    public WaitForDocumentDecisionActivity() { }

    public WaitForDocumentDecisionActivity(ILogger<WaitForDocumentDecisionActivity> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// D2 — the SINGLE canonical decision-bookmark name
    /// (<c>document-decision-{tenant}-{session}</c>). Shared by the suspend side
    /// (<see cref="Execute"/>) and the resume side
    /// (<c>DocumentDecisionResumeEndpoint</c>) so the two match byte-for-byte. The
    /// tenant segment is normalised via the SAME
    /// <see cref="WaitForMergeApprovalActivity.NormalizeSegment"/> transform on both
    /// sides so the names always agree.
    /// </summary>
    public static string DecisionBookmarkName(string? tenantId, Guid sessionId)
        => LifecycleBookmarks.ForDecisionSession(tenantId, sessionId);

    protected override void Execute(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var tenantId = TenantId.Get(context);
        var requestedAtUtc = RequestedAtUtc.Get(context);

        // D4 — fail LOUD if the durationMs basis is missing/unparseable, rather than
        // silently emitting a meaningless duration on resume.
        if (string.IsNullOrWhiteSpace(requestedAtUtc) || !TryParseIso(requestedAtUtc, out _))
        {
            throw new TammaError(
                "DOCUMENT.DECISION.MISSING_REQUESTED_AT",
                $"WaitForDocumentDecisionActivity requires a valid ISO-8601 RequestedAtUtc; got '{requestedAtUtc}'.",
                new Dictionary<string, object?> { ["requestedAtUtc"] = requestedAtUtc, ["sessionId"] = sessionId },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        var bookmarkName = DecisionBookmarkName(tenantId, sessionId);

        // APPROVAL.REQUESTED — request+suspend is one atomic site (D3). channel is always
        // orchestrator (D6 — every request routes to the orchestrator).
        TammaEventEmitter.Emit(context, this, _logger, BuildRequestedEvent(
            sessionId, tenantId,
            IssueId.Get(context), DocumentId.Get(context), DocumentType.Get(context),
            CorrelationId.Get(context), RulesReference.Get(context), requestedAtUtc));

        _logger?.LogInformation(
            "Waiting for document decision: bookmark={BookmarkName} for session {SessionId}",
            bookmarkName, sessionId);

        context.CreateBookmark(new CreateBookmarkArgs
        {
            BookmarkName = bookmarkName,
            Callback = OnDecisionReceivedAsync,
            AutoBurn = true,
            IncludeActivityInstanceId = false,
        });
    }

    /// <summary>External resume path: the decision was injected. Reads it (fail-closed on
    /// garbage, D5), computes <c>durationMs</c> off the re-read <see cref="RequestedAtUtc"/>,
    /// emits <c>APPROVAL.PROVIDED</c>, sets the outputs, and completes on the decision-kind
    /// outcome.</summary>
    private async ValueTask OnDecisionReceivedAsync(ActivityExecutionContext context)
    {
        var read = ReadDecision(context.WorkflowInput);

        var requestedAtUtc = RequestedAtUtc.Get(context);
        var durationMs = ComputeDurationMs(requestedAtUtc);

        // D7 — the authoritative rules reference is the server-stamped activity input, not the
        // resume echo, so orchestrator decisions stay auditable.
        var rulesReference = RulesReference.Get(context);

        _logger?.LogInformation(
            "Document decision resumed: kind={Kind} channel={Channel} decider={Decider} durationMs={Duration}",
            read.DecisionKind, read.Channel, read.DeciderId ?? "<none>", durationMs);

        TammaEventEmitter.Emit(context, this, _logger, BuildProvidedEvent(
            SessionId.Get(context), TenantId.Get(context),
            IssueId.Get(context), DocumentId.Get(context), DocumentType.Get(context),
            CorrelationId.Get(context), rulesReference, read, durationMs));

        context.Set(DecisionJson, read.DecisionJson);
        context.Set(Feedback, read.Feedback);
        context.Set(DeciderId, read.DeciderId);
        context.Set(Channel, read.Channel);
        context.Set(DurationMs, durationMs);

        await context.CompleteActivityWithOutcomesAsync(read.Outcome);
    }

    // -----------------------------------------------------------------------
    // Pure read-back + mapping (exposed for unit testing)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Pure read-back of the injected decision from the bookmark resume input (D5). The
    /// serialized 39-5 <see cref="AcceptanceDecision"/> is deserialized polymorphically;
    /// an unparseable / missing <c>DecisionJson</c> FAIL-CLOSES to
    /// <c>Escalate(AcceptorJudgment, "unreadable decision payload")</c> — the
    /// serializing-dispatcher lesson (#15/#437): never mis-branch on garbage while returning
    /// 200. Scalar fields are read via <c>.ToString()</c> (serialization-tolerant).
    /// </summary>
    public static DecisionReadResult ReadDecision(IDictionary<string, object> input)
    {
        var decisionJson = input.TryGetValue("DecisionJson", out var dj) ? dj?.ToString() : null;
        var feedback = input.TryGetValue("Feedback", out var f) ? f?.ToString() : null;
        var deciderId = input.TryGetValue("DeciderId", out var di) ? di?.ToString() : null;
        var deciderDisplay = input.TryGetValue("DeciderDisplay", out var dd) ? dd?.ToString() : null;
        var channel = input.TryGetValue("Channel", out var ch) ? ch?.ToString() : null;
        var rulesReference = input.TryGetValue("RulesReference", out var rr) ? rr?.ToString() : null;

        var decision = ParseDecisionFailClosed(decisionJson);
        var outcome = OutcomeFor(decision);
        var kind = KindWireFor(decision);

        // Re-serialize the (possibly fail-closed) decision to the canonical form so the
        // downstream 39-6 guardrail always reads a valid AcceptanceDecision, never the raw
        // client string.
        var canonicalJson = JsonSerializer.Serialize<AcceptanceDecision>(decision, SerializerOptions);

        return new DecisionReadResult(
            Decision: decision,
            DecisionJson: canonicalJson,
            Outcome: outcome,
            DecisionKind: kind,
            Feedback: feedback,
            DeciderId: deciderId,
            DeciderDisplay: deciderDisplay,
            Channel: string.IsNullOrWhiteSpace(channel) ? "orchestrator" : channel,
            RulesReference: rulesReference);
    }

    /// <summary>Deserialize the injected decision; ANY failure (null/empty/garbage/unknown
    /// kind) fail-closes to <c>Escalate(AcceptorJudgment)</c> (D5).</summary>
    private static AcceptanceDecision ParseDecisionFailClosed(string? decisionJson)
    {
        if (!string.IsNullOrWhiteSpace(decisionJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<AcceptanceDecision>(decisionJson, SerializerOptions);
                if (parsed is not null) return parsed;
            }
            catch (JsonException)
            {
                // fall through to fail-closed escalation
            }
        }

        return new AcceptanceDecision.Escalate(
            AcceptanceEscalationReason.AcceptorJudgment,
            "unreadable decision payload");
    }

    /// <summary>Map an <see cref="AcceptanceDecision"/> onto its flowchart outcome edge.</summary>
    public static string OutcomeFor(AcceptanceDecision decision) => decision switch
    {
        AcceptanceDecision.Accept => "Accept",
        AcceptanceDecision.RequestRevision => "RequestRevision",
        AcceptanceDecision.Reject => "Reject",
        AcceptanceDecision.Escalate => "Escalate",
        _ => "Escalate",
    };

    /// <summary>The wire <c>kind</c> discriminator for an <see cref="AcceptanceDecision"/>.</summary>
    public static string KindWireFor(AcceptanceDecision decision) => decision switch
    {
        AcceptanceDecision.Accept => "accept",
        AcceptanceDecision.RequestRevision => "request-revision",
        AcceptanceDecision.Reject => "reject",
        AcceptanceDecision.Escalate => "escalate",
        _ => "escalate",
    };

    private long ComputeDurationMs(string? requestedAtUtc)
    {
        if (TryParseIso(requestedAtUtc, out var requestedAt))
        {
            var ms = (long)(DateTimeOffset.UtcNow - requestedAt).TotalMilliseconds;
            return ms < 0 ? 0 : ms;
        }
        return 0;
    }

    private static bool TryParseIso(string? value, out DateTimeOffset parsed)
        => DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed);

    // -----------------------------------------------------------------------
    // Pure event builders (exposed for unit testing the tag/data mapping)
    // -----------------------------------------------------------------------

    public static TammaEvent BuildRequestedEvent(
        Guid sessionId, string? tenantId,
        string? issueId, string? documentId, string? documentType,
        string? correlationId, string? rulesReference, string requestedAtUtc)
    {
        var tags = BuildApprovalTags(sessionId, tenantId, issueId, documentId, documentType, correlationId);

        var data = new Dictionary<string, object?>
        {
            ["channel"] = "orchestrator",
            ["requestedAtUtc"] = requestedAtUtc,
        };
        if (!string.IsNullOrWhiteSpace(rulesReference)) data["rulesReference"] = rulesReference;

        return new TammaEvent
        {
            EventType = ApprovalEvents.Requested,
            Status = ApprovalEvents.StatusForEvent(ApprovalEvents.Requested),
            Tags = tags,
            Data = data,
        };
    }

    public static TammaEvent BuildProvidedEvent(
        Guid sessionId, string? tenantId,
        string? issueId, string? documentId, string? documentType,
        string? correlationId, string? rulesReference,
        DecisionReadResult read, long durationMs)
    {
        var tags = BuildApprovalTags(sessionId, tenantId, issueId, documentId, documentType, correlationId);

        var data = new Dictionary<string, object?>
        {
            ["channel"] = read.Channel,
            ["decisionKind"] = read.DecisionKind,
            ["durationMs"] = durationMs,
        };
        if (!string.IsNullOrWhiteSpace(read.DeciderId)) data["deciderId"] = read.DeciderId;
        if (!string.IsNullOrWhiteSpace(read.DeciderDisplay)) data["deciderDisplay"] = read.DeciderDisplay;
        if (!string.IsNullOrWhiteSpace(read.Feedback)) data["feedback"] = read.Feedback;
        if (!string.IsNullOrWhiteSpace(rulesReference)) data["rulesReference"] = rulesReference;

        return new TammaEvent
        {
            EventType = ApprovalEvents.Provided,
            Status = ApprovalEvents.StatusForEvent(ApprovalEvents.Provided),
            Tags = tags,
            Data = data,
        };
    }

    private static Dictionary<string, object?> BuildApprovalTags(
        Guid sessionId, string? tenantId,
        string? issueId, string? documentId, string? documentType, string? correlationId)
    {
        var tags = new Dictionary<string, object?> { ["sessionId"] = sessionId.ToString() };
        if (!string.IsNullOrWhiteSpace(issueId)) tags["issueId"] = issueId;
        if (!string.IsNullOrWhiteSpace(documentId)) tags["documentId"] = documentId;
        if (!string.IsNullOrWhiteSpace(documentType)) tags["documentType"] = documentType;
        if (!string.IsNullOrWhiteSpace(correlationId)) tags["correlationId"] = correlationId;
        if (ApprovalEvents.ParseTenantId(tenantId) is Guid t) tags["tenantId"] = t.ToString("D");
        return tags;
    }

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Pure result of <see cref="ReadDecision"/> — the parsed decision plus the
    /// server-derived passthrough fields the callback stamps into the audit trail.</summary>
    public sealed record DecisionReadResult(
        AcceptanceDecision Decision,
        string DecisionJson,
        string Outcome,
        string DecisionKind,
        string? Feedback,
        string? DeciderId,
        string? DeciderDisplay,
        string Channel,
        string? RulesReference);
}
