using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;
using Tamma.Core;

namespace Tamma.Core.Documents.Channels;

/// <summary>
/// Story 39-18 (Design Decision D3) — the two real-time channels a
/// <see cref="ChannelEnvelope"/> can travel on. Deliberately DISTINCT from 39-8's
/// three-member <see cref="ApprovalChannel"/> (which classifies who RESUMED a
/// decision — orchestrator/user/api); this two-member enum names WHERE a message
/// travels:
/// <list type="bullet">
///   <item><c>orchestrator</c> — the workflow↔orchestrator channel (engine/agent
///     traffic, service-principal/orchestrator-only, tenant-partitioned).</item>
///   <item><c>user</c> — the user↔orchestrator/platform channel (dashboard users,
///     tenant + per-user folded groups derived server-side from the principal).</item>
/// </list>
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<ChannelAudience>))]
public enum ChannelAudience
{
    [Wire("orchestrator")] Orchestrator,
    [Wire("user")]         User,
}

/// <summary><see cref="ChannelAudience"/> wire helper.</summary>
public static class ChannelAudienceExtensions
{
    /// <summary>The canonical wire string for <paramref name="audience"/>.</summary>
    public static string ToWire(this ChannelAudience audience) => EnumWire<ChannelAudience>.ToWire(audience);

    /// <summary>
    /// Case-sensitive parse of a <see cref="ChannelAudience"/> wire string.
    /// Throws <see cref="TammaError"/> (<c>CHANNEL.AUDIENCE.INVALID</c>) on an
    /// unknown token — a bad audience fails loud on the boundary.
    /// </summary>
    public static ChannelAudience ParseAudience(string wire)
    {
        if (EnumWire<ChannelAudience>.TryParse(wire, out var value))
            return value;
        throw new TammaError(
            "CHANNEL.AUDIENCE.INVALID",
            $"Unknown channel audience wire value '{wire}'.",
            new Dictionary<string, object?> { ["audience"] = wire },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }
}
