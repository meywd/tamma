using System.Diagnostics.Metrics;

namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Story 28-12 (AC5 residual) — OpenTelemetry metric surface for the
/// platform-wide KEK rotation flow driven by
/// <see cref="KekRotationCoordinator"/>.
///
/// <list type="bullet">
///   <item><description><c>tamma.kek_rotation.remaining</c> — observable
///     gauge of the number of tenants that still need their connection
///     string re-encrypted under the new KEK. Read live from the
///     coordinator's <see cref="KekRotationStatus"/> snapshot
///     (<c>TotalTenants - ReencryptedTenants - FailedTenants</c>). The
///     gauge reports <c>0</c> when no rotation is in flight (Idle /
///     Completed) so a steady stream of <c>0</c> samples means "all
///     tenants are on the current KEK".</description></item>
/// </list>
///
/// <para>The meter is self-registering: constructing a
/// <see cref="Meter"/> with <see cref="MeterName"/> makes the gauge
/// discoverable by any <c>MeterProvider</c> wired to that name. The
/// codebase does not maintain an explicit <c>AddMeter</c> allow-list
/// (see <see cref="Tamma.Data.Pooling.TenantConnectionPoolMetrics"/>),
/// so registering this class as a singleton in DI is sufficient to make
/// the gauge observable.</para>
/// </summary>
public sealed class KekRotationMetrics : IDisposable
{
    /// <summary>Public meter name — pin so dashboards stay stable.</summary>
    public const string MeterName = "Tamma.KekRotation";

    private readonly Meter _meter;
    private readonly KekRotationCoordinator _coordinator;

    public KekRotationMetrics(KekRotationCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;

        _meter = new Meter(MeterName, "1.0.0");

        _meter.CreateObservableGauge(
            "tamma.kek_rotation.remaining",
            ObserveRemaining,
            unit: "{tenant}",
            description: "Tenants still needing connection-string re-encryption "
                + "under the new KEK during an in-flight rotation (0 when idle).");
    }

    /// <summary>
    /// Live count of tenants still awaiting re-encryption. Cheap: reads
    /// the coordinator's in-memory status snapshot. Tenants that have
    /// already been re-encrypted OR have failed are both "done" from the
    /// remaining-work perspective, so they are subtracted from the total.
    /// Clamped at <c>0</c> to guard against any transient
    /// status-update interleaving where the partial counts briefly
    /// exceed the (later-set) total.
    /// </summary>
    public long RemainingTenants
    {
        get
        {
            var status = _coordinator.GetStatus();
            var remaining = (long)status.TotalTenants
                - status.ReencryptedTenants
                - status.FailedTenants;
            return remaining < 0 ? 0 : remaining;
        }
    }

    private long ObserveRemaining() => RemainingTenants;

    public void Dispose() => _meter.Dispose();
}
