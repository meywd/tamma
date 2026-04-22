namespace Tamma.Activities.SecretsRotation.Contracts;

/// <summary>
/// Story 29-6 AC3 — discriminated result of
/// <see cref="IRotationHandler.ProbeAsync"/>. Mirrors a sealed-discriminated-
/// union (SCS=struct / Healthy / Unhealthy) without the boilerplate by
/// keeping a single record + a status enum.
///
/// <para>The reason string is short, machine-readable, and redacted of
/// plaintext values — handlers include it in the rotation-failure event
/// so dashboards surface the failure class (<c>connection_refused</c>,
/// <c>auth_failed</c>, <c>app_not_running</c>) without leaking secrets.</para>
/// </summary>
/// <param name="Status">Healthy / Unhealthy.</param>
/// <param name="Reason">Short machine-readable reason when
/// <see cref="Status"/> is <see cref="ProbeStatus.Unhealthy"/>; empty
/// string when healthy.</param>
/// <param name="DurationMs">How long the probe took (informational).</param>
public sealed record ProbeResult(
    ProbeStatus Status,
    string Reason,
    long DurationMs)
{
    public bool IsHealthy => Status == ProbeStatus.Healthy;

    public static ProbeResult Healthy(long durationMs) =>
        new(ProbeStatus.Healthy, string.Empty, durationMs);

    public static ProbeResult Unhealthy(string reason, long durationMs) =>
        new(ProbeStatus.Unhealthy, reason, durationMs);
}

public enum ProbeStatus
{
    Healthy,
    Unhealthy
}
