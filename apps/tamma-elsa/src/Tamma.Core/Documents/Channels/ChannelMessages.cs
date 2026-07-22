using System.Text.Json.Serialization;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Documents.Channels;

/// <summary>
/// Story 39-18 (AC1, Design Decision D3) — the CLOSED, drift-tested channel
/// message set carried over the two SignalR channels. Serialized polymorphically
/// with a <c>kind</c> discriminator through <see cref="DocumentJson.Options"/>; the
/// derived set is closed and count-pinned (exactly 8 kinds) by
/// <c>ChannelMessageContractTests</c>. Every wire property carries an explicit
/// <c>[JsonPropertyName]</c> (39-2's D8 discipline).
///
/// <para>39-5's canonical <see cref="AcceptanceRequest"/> / <see cref="AcceptanceDecision"/>
/// are REUSED by reference (wrapped in <see cref="AcceptanceRequested"/> /
/// <see cref="DecisionProvided"/>), never redefined — one record, one home.</para>
///
/// <para>The transport is never the source of truth (AC6): every request/escalation/
/// guidance/task message is persisted to <c>channel_outbox</c> BEFORE any hub send,
/// and decisions land ONLY through 39-8's idempotent resume surface.</para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AcceptanceRequested), "acceptance-request")]
[JsonDerivedType(typeof(DecisionProvided), "acceptance-decision")]
[JsonDerivedType(typeof(TaskAssigned), "task-assigned")]
[JsonDerivedType(typeof(EscalationRaised), "escalation-raised")]
[JsonDerivedType(typeof(EscalationDisposition), "escalation-disposition")]
[JsonDerivedType(typeof(GuidanceQuery), "guidance-query")]
[JsonDerivedType(typeof(GuidanceReply), "guidance-reply")]
[JsonDerivedType(typeof(AgentConversationMessage), "agent-conversation")]
public abstract record ChannelMessage;

/// <summary>
/// The accept stage's <see cref="AcceptanceRequest"/> published on the
/// workflow↔orchestrator channel (wraps 39-5's canonical record by reference).
/// </summary>
public sealed record AcceptanceRequested(
    [property: JsonPropertyName("request")] AcceptanceRequest Request) : ChannelMessage;

/// <summary>
/// A decision the orchestrator/human answered with (wraps 39-5's canonical
/// <see cref="AcceptanceDecision"/> + the server-derived decider + the rules
/// version reference the decision was made under). The hub NEVER applies this
/// itself — decisions land through the 39-8 resume surface (D7).
/// </summary>
public sealed record DecisionProvided(
    [property: JsonPropertyName("decision")] AcceptanceDecision Decision,
    [property: JsonPropertyName("decider")] string Decider,
    [property: JsonPropertyName("rulesReference")] string? RulesReference) : ChannelMessage;

/// <summary>
/// The orchestrator's assignment notification on the user channel. Role-addressed
/// (design review 2026-07-21): the payload carries the ASSIGNED ROLE, never a single
/// assignee. Delivery is per-recipient via outbox fan-out — the actual recipient is
/// the outbox row's <c>RecipientUserId</c> (one row per audience member from
/// <c>ITaskAudienceResolver</c>), NOT a field in this payload.
/// </summary>
public sealed record TaskAssigned(
    [property: JsonPropertyName("taskId")] Guid TaskId,
    [property: JsonPropertyName("decisionSessionId")] Guid DecisionSessionId,
    [property: JsonPropertyName("assignedRole")] string AssignedRole,
    [property: JsonPropertyName("basis")] string Basis /* AssignmentBasis wire */,
    [property: JsonPropertyName("documentTypeKey")] string DocumentTypeKey,
    [property: JsonPropertyName("documentId")] Guid DocumentId,
    [property: JsonPropertyName("issueId")] string IssueId,
    [property: JsonPropertyName("autonomyLevel")] int AutonomyLevel,
    [property: JsonPropertyName("rulesReference")] string? RulesReference) : ChannelMessage;

/// <summary>
/// The 39-8 escalation lineage payload — one shape, shared with the
/// <c>ESCALATION.TRIGGERED</c> event data. <see cref="LineageJson"/> carries the
/// serialized document lineage (never a bare failure string).
/// </summary>
public sealed record EscalationRaised(
    [property: JsonPropertyName("escalationId")] string EscalationId,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("lineageJson")] string LineageJson,
    [property: JsonPropertyName("issueId")] string IssueId,
    [property: JsonPropertyName("rulesReference")] string? RulesReference) : ChannelMessage;

/// <summary>
/// A human disposition of an escalation relayed onto the channel. <see cref="Disposition"/>
/// is an <c>EscalationDisposition</c> wire string (resolved/overridden/abandoned).
/// </summary>
public sealed record EscalationDisposition(
    [property: JsonPropertyName("escalationId")] string EscalationId,
    [property: JsonPropertyName("disposition")] string Disposition,
    [property: JsonPropertyName("note")] string? Note) : ChannelMessage;

/// <summary>An orchestrator/workflow guidance question. Recorded via <c>GUIDANCE.REQUESTED</c> (D8).</summary>
public sealed record GuidanceQuery(
    [property: JsonPropertyName("queryId")] Guid QueryId,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("contextJson")] string? ContextJson) : ChannelMessage;

/// <summary>The paired reply to a <see cref="GuidanceQuery"/>. Recorded via <c>GUIDANCE.PROVIDED</c> (D8).</summary>
public sealed record GuidanceReply(
    [property: JsonPropertyName("queryId")] Guid QueryId,
    [property: JsonPropertyName("reply")] string Reply) : ChannelMessage;

/// <summary>
/// One turn of user↔agent conversation. <see cref="Direction"/> is
/// <c>"user-&gt;agent"</c> or <c>"agent-&gt;user"</c>. Conversation kinds are minted
/// ONLY by 39-19's chat service (which records <c>CHAT.*</c>); the outbox refuses a
/// direct conversation enqueue outside that path (D8).
/// </summary>
public sealed record AgentConversationMessage(
    [property: JsonPropertyName("conversationId")] Guid ConversationId,
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("text")] string Text) : ChannelMessage;

/// <summary>
/// The transport envelope. <see cref="MessageId"/> is a UUID v7 (time-ordered
/// replay without a sequence column); <see cref="Message"/> is the polymorphic
/// payload; <see cref="RecipientUserId"/> is <c>null</c> for the tenant's
/// orchestrator agent and a specific user for a fanned-out user-audience row.
/// </summary>
public sealed record ChannelEnvelope(
    [property: JsonPropertyName("messageId")] Guid MessageId,
    [property: JsonPropertyName("tenantId")] Guid TenantId,
    [property: JsonPropertyName("audience")] ChannelAudience Audience,
    [property: JsonPropertyName("recipientUserId")] Guid? RecipientUserId,
    [property: JsonPropertyName("message")] ChannelMessage Message,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

/// <summary>
/// Story 39-18 — the canonical <c>kind</c> ↔ <see cref="ChannelAudience"/> pairing
/// (server-derived, never trusted from a payload). <c>ChannelOutboxService</c> uses
/// this to fail-loud (<c>CHANNEL.MESSAGE.INVALID</c>) on a mismatched pairing and to
/// refuse a direct conversation-kind enqueue (chat is 39-19's, D8).
/// </summary>
public static class ChannelMessageKinds
{
    public const string AcceptanceRequest = "acceptance-request";
    public const string AcceptanceDecision = "acceptance-decision";
    public const string TaskAssigned = "task-assigned";
    public const string EscalationRaised = "escalation-raised";
    public const string EscalationDisposition = "escalation-disposition";
    public const string GuidanceQuery = "guidance-query";
    public const string GuidanceReply = "guidance-reply";
    public const string AgentConversation = "agent-conversation";

    /// <summary>The discriminator <c>kind</c> for a concrete <see cref="ChannelMessage"/>.</summary>
    public static string KindOf(ChannelMessage message) => message switch
    {
        Channels.AcceptanceRequested => AcceptanceRequest,
        Channels.DecisionProvided => AcceptanceDecision,
        Channels.TaskAssigned => TaskAssigned,
        Channels.EscalationRaised => EscalationRaised,
        Channels.EscalationDisposition => EscalationDisposition,
        Channels.GuidanceQuery => GuidanceQuery,
        Channels.GuidanceReply => GuidanceReply,
        Channels.AgentConversationMessage => AgentConversation,
        _ => throw new ArgumentOutOfRangeException(nameof(message), message.GetType().Name, "Unknown channel message kind."),
    };

    /// <summary>
    /// The canonical audience a kind travels on, or <c>null</c> when the kind is not
    /// a direct-enqueue kind (conversation kinds are minted only by 39-19's chat
    /// service, so a direct enqueue of them is refused).
    /// </summary>
    public static ChannelAudience? AudienceFor(string kind) => kind switch
    {
        AcceptanceRequest => ChannelAudience.Orchestrator,
        AcceptanceDecision => ChannelAudience.Orchestrator,
        EscalationRaised => ChannelAudience.Orchestrator,
        EscalationDisposition => ChannelAudience.Orchestrator,
        GuidanceQuery => ChannelAudience.Orchestrator,
        GuidanceReply => ChannelAudience.Orchestrator,
        TaskAssigned => ChannelAudience.User,
        // agent-conversation is NOT a direct-enqueue kind (D8).
        _ => null,
    };
}
