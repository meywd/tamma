using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Api.Endpoints;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;

namespace Tamma.Api.Services.Documents;

/// <summary>
/// Story 39-8 / 39-18 (D7) — the SHARED document-decision submission path. The
/// decision-build + kind validation + channel-carried resume logic lives HERE, in
/// ONE place, so BOTH the public REST endpoint (<c>DocumentDecisionEndpoints.ResumeDecision</c>)
/// AND the workflow↔orchestrator hub (<c>OrchestratorChannelHub.SubmitDecision</c>)
/// drive the SAME 39-8 idempotent resume surface — the hub never applies anything
/// itself (canon: "decisions land only via the idempotent resume surface").
///
/// <para>The service is constructed with an <see cref="IElsaWorkflowService"/> so the
/// hub can resolve it from DI; the REST endpoint constructs it inline with the
/// <see cref="IElsaWorkflowService"/> passed to the handler, preserving the endpoint's
/// existing signature (and its tests). Channel derivation stays server-side at the
/// call site (<c>ApprovalChannels.Derive</c>); this service never reads a channel or
/// decider from a client body.</para>
/// </summary>
public sealed class DocumentDecisionSubmissionService
{
    /// <summary>The decision kinds the gate accepts (the closed 39-5 discriminator set).</summary>
    public static readonly IReadOnlySet<string> AllowedDecisionKinds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "accept", "request-revision", "reject", "escalate" };

    private static readonly JsonSerializerOptions DecisionJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IElsaWorkflowService _elsa;

    public DocumentDecisionSubmissionService(IElsaWorkflowService elsa)
    {
        _elsa = elsa ?? throw new ArgumentNullException(nameof(elsa));
    }

    /// <summary>Whether <paramref name="kind"/> is one of the closed decision kinds.</summary>
    public static bool IsAllowedKind(string? kind) =>
        !string.IsNullOrWhiteSpace(kind) && AllowedDecisionKinds.Contains(kind.Trim());

    /// <summary>
    /// Build the 39-5 <see cref="AcceptanceDecision"/> from the validated public
    /// request. A human REJECT passes through (it clamps to Escalate only on the
    /// orchestrator channel, which 39-6's guardrail applies); an escalate reason wire
    /// is parsed, defaulting to <c>AcceptorJudgment</c> when a human simply chose to
    /// escalate.
    /// </summary>
    public static AcceptanceDecision BuildDecision(DocumentDecisionEndpoints.DecisionRequest req) =>
        req.Kind.Trim().ToLowerInvariant() switch
        {
            "accept" => new AcceptanceDecision.Accept(),
            "request-revision" => new AcceptanceDecision.RequestRevision(req.Notes ?? req.Feedback ?? string.Empty),
            "reject" => new AcceptanceDecision.Reject(req.Reason ?? req.Feedback ?? string.Empty),
            _ => new AcceptanceDecision.Escalate(
                ParseEscalationReason(req.Reason),
                req.Detail ?? req.Feedback ?? req.Reason ?? string.Empty),
        };

    /// <summary>Serialize a 39-5 decision to the canonical decision JSON.</summary>
    public static string SerializeDecision(AcceptanceDecision decision) =>
        JsonSerializer.Serialize(decision, DecisionJsonOptions);

    /// <summary>
    /// Submit a decision built from the typed public request (the REST path). Builds
    /// the 39-5 decision JSON server-side, then forwards to the 39-8 resume surface.
    /// </summary>
    public Task<DecisionSubmitResult> SubmitAsync(
        Guid sessionId, DocumentDecisionEndpoints.DecisionRequest req,
        Guid? tenantId, string deciderId, ApprovalChannel channel)
    {
        var decision = BuildDecision(req);
        var decisionJson = SerializeDecision(decision);
        var kind = req.Kind.Trim().ToLowerInvariant();
        return ResumeAsync(sessionId, decisionJson, req.Feedback, tenantId, deciderId, channel, kind);
    }

    /// <summary>
    /// The ONE place a decision reaches 39-8's idempotent resume surface. Callers
    /// supply an already-built <paramref name="decisionJson"/> (the hub path) or use
    /// <see cref="SubmitAsync(Guid, DocumentDecisionEndpoints.DecisionRequest, Guid?, string, ApprovalChannel)"/>
    /// which builds it. Returns the resume result verbatim (incl. the 404/409
    /// gate-not-waiting discipline) so the caller can surface idempotency outcomes.
    /// </summary>
    public async Task<DecisionSubmitResult> ResumeAsync(
        Guid sessionId, string decisionJson, string? feedback,
        Guid? tenantId, string deciderId, ApprovalChannel channel, string kind)
    {
        var result = await _elsa.ResumeDocumentDecisionAsync(
            sessionId, tenantId?.ToString(), decisionJson, feedback,
            deciderId, deciderId, channel.ToWire(), rulesReference: null);

        return new DecisionSubmitResult(
            result.Resumed, result.GateNotFound, result.WorkflowInstanceId, kind, channel.ToWire());
    }

    private static AcceptanceEscalationReason ParseEscalationReason(string? wire)
    {
        if (!string.IsNullOrWhiteSpace(wire))
        {
            foreach (var reason in Enum.GetValues<AcceptanceEscalationReason>())
                if (string.Equals(reason.ToWire(), wire, StringComparison.OrdinalIgnoreCase))
                    return reason;
        }
        return AcceptanceEscalationReason.AcceptorJudgment;
    }
}

/// <summary>
/// Result of a document-decision submission through the shared service — the 39-8
/// resume outcome plus the server-derived kind/channel for the caller's response.
/// </summary>
public sealed record DecisionSubmitResult(
    bool Resumed, bool GateNotFound, string? WorkflowInstanceId, string Kind, string Channel);
