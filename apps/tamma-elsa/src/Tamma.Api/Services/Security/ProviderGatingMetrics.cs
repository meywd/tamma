using System.Diagnostics.Metrics;

namespace Tamma.Api.Services.Security;

/// <summary>
/// Story 32-4 — OpenTelemetry surface for the SaaS provider gate
/// (<see cref="SaaSProviderGate"/>).
///
/// <list type="bullet">
///   <item><description><c>tamma.provider.gated</c> — monotonic counter of SaaS
///     gate denials, tagged with <c>provider</c>, <c>auth_model</c>
///     (<c>cli-token</c> | <c>unknown</c> | <c>api-key</c>) and <c>reason</c>
///     (<c>CLI_TOKEN_PROVIDER</c> | <c>PROVIDER_UNKNOWN</c> |
///     <c>TENANT_NOT_ENTITLED</c>). Incremented exactly once per SaaS denial;
///     never in single-user, never on an allow.</description></item>
/// </list>
///
/// <para>Self-registering meter (see <c>KekRotationMetrics</c> /
/// <c>AuditProjectionMetrics</c>): constructing a <see cref="Meter"/> with
/// <see cref="MeterName"/> makes the counter discoverable; registering this
/// class as a singleton in DI is sufficient. The plain <see cref="long"/>
/// tally (<see cref="GatedTotal"/>) lets unit tests assert "exactly one
/// increment" without subscribing a <see cref="MeterListener"/>.</para>
/// </summary>
public sealed class ProviderGatingMetrics : IDisposable
{
    /// <summary>Public meter name — pin so dashboards stay stable.</summary>
    public const string MeterName = "Tamma.ProviderGating";

    private readonly Meter _meter;
    private readonly Counter<long> _gated;
    private long _gatedTotal;

    public ProviderGatingMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");

        _gated = _meter.CreateCounter<long>(
            "tamma.provider.gated",
            unit: "{denial}",
            description: "SaaS call-LLM provider-gate denials, tagged by provider, "
                + "auth_model and reason (cli-token / unknown / not-entitled).");
    }

    /// <summary>In-process running total of gate denials since process start.</summary>
    public long GatedTotal => Interlocked.Read(ref _gatedTotal);

    /// <summary>
    /// Record exactly one SaaS gate denial. <paramref name="authModel"/> is the
    /// OTel-tag form (<c>cli-token</c> | <c>unknown</c> | <c>api-key</c>).
    /// </summary>
    public void RecordGated(string provider, string authModel, string reason)
    {
        Interlocked.Increment(ref _gatedTotal);
        _gated.Add(1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("auth_model", authModel),
            new KeyValuePair<string, object?>("reason", reason));
    }

    public void Dispose() => _meter.Dispose();
}
