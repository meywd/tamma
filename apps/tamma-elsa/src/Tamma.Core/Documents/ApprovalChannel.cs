using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;

namespace Tamma.Core.Documents;

/// <summary>
/// The transport channel a decision arrives on (Story 39-5 step 3). A NEW enum
/// OWNED BY 39-5: <see cref="Policy.AcceptanceGuardrails"/> types its gate
/// context's decider channel on it (Tamma.Core cannot reference a type from
/// 39-8's scope, so 39-5 defines it here). 39-8 CONSUMES it — it maps its
/// server-derived resume principal onto these three values; it never redefines
/// the type.
///
/// <list type="bullet">
/// <item><c>orchestrator</c> — the 39-17 agent decided itself (the autonomous path).</item>
/// <item><c>user</c> — a human decided via the 39-19 Task View / conversational surface.</item>
/// <item><c>api</c> — a decision arrived over the programmatic API.</item>
/// </list>
///
/// <para>The distinction is load-bearing for the guardrails: a <c>Reject</c> is
/// human-only, so a reject arriving on the <c>orchestrator</c> channel is clamped
/// to <c>Escalate(RejectRequiresHuman)</c>.</para>
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<ApprovalChannel>))]
public enum ApprovalChannel
{
    [Wire("orchestrator")] Orchestrator,
    [Wire("user")]         User,
    [Wire("api")]          Api,
}

/// <summary><see cref="ApprovalChannel"/> wire helper.</summary>
public static class ApprovalChannelExtensions
{
    public static string ToWire(this ApprovalChannel channel) => EnumWire<ApprovalChannel>.ToWire(channel);
}
