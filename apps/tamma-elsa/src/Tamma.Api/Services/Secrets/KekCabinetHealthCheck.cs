using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Tamma.Data;

namespace Tamma.Api.Services.Secrets;

/// <summary>
/// R2-H13: startup health check that refuses to mark the API "ready"
/// if there are tenant rows whose <c>KekVersion</c> is more than
/// <see cref="KekProvider.RetainedHistorySize"/> behind the active
/// primary. This forces operators to re-encrypt before retiring keys —
/// otherwise a row two rotations behind would have its KEK pruned out
/// of the cabinet and become permanently undecryptable.
///
/// <para>The check reports <see cref="HealthStatus.Unhealthy"/> with a
/// remediation message ("rotate the laggard rows under runbook
/// kek-rotation.md before retiring more keys"). It runs on the
/// "ready" probe; the liveness probe (no DB dependency) ignores it.</para>
///
/// <para>Phase 0 made <c>KekVersion</c> <c>NOT NULL</c> in the schema,
/// so the legacy "NULL = version 0" state can never exist and has been
/// removed from this check.</para>
/// </summary>
public sealed class KekCabinetHealthCheck : IHealthCheck
{
    private readonly KekProvider _kekProvider;
    private readonly IDbContextFactory<ControlPlaneDbContext>? _dbContextFactory;
    private readonly ILogger<KekCabinetHealthCheck> _logger;

    public KekCabinetHealthCheck(
        KekProvider kekProvider,
        ILogger<KekCabinetHealthCheck> logger,
        IDbContextFactory<ControlPlaneDbContext>? dbContextFactory = null)
    {
        ArgumentNullException.ThrowIfNull(kekProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _kekProvider = kekProvider;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var activeVersion = _kekProvider.GetActiveVersion();
        var retainedHistorySize = _kekProvider.RetainedHistorySize;

        // The minimum decryptable version is (activeVersion -
        // retainedHistorySize). Rows tagged below this number are
        // un-decryptable because the KEK has been pruned out of the
        // cabinet ring. We refuse to start when any such row exists.
        var minDecryptableVersion = activeVersion - retainedHistorySize;

        if (_dbContextFactory is null)
        {
            // No CP DbContext factory wired — common in dev/test where
            // the resolver is the stub. Surface as Healthy with a note;
            // there are no tenant rows to laggard-check against.
            return HealthCheckResult.Healthy(
                "KEK cabinet ready (no CP DbContext factory wired — dev mode).");
        }

        try
        {
            await using var ctx = await _dbContextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            // Legacy "KekVersion IS NULL = version 0" detection removed — Phase 0 made
            // the column NOT NULL, so the state cannot exist.

            // Find the lowest KekVersion across non-deleted tenant rows
            // that actually carry an encrypted connection string.
            var minRow = await ctx.Tenants
                .IgnoreQueryFilters()
                .Where(t => t.DeletedAt == null)
                .Where(t => EF.Property<byte[]?>(t, "EncryptedConnectionString") != null)
                .Select(t => (int?)EF.Property<short>(t, "KekVersion"))
                .MinAsync(cancellationToken)
                .ConfigureAwait(false);

            if (minRow is null)
            {
                return HealthCheckResult.Healthy(
                    "KEK cabinet ready (no encrypted tenant rows yet).");
            }

            if (minRow.Value < minDecryptableVersion)
            {
                var msg =
                    $"KEK cabinet has tenant rows at version {minRow.Value} "
                    + $"but the cabinet only retains versions back to "
                    + $"{minDecryptableVersion} (active={activeVersion}, "
                    + $"retainedHistorySize={retainedHistorySize}). "
                    + "Run the KEK rotation runbook to re-encrypt the laggards "
                    + "before retiring more keys.";
                _logger.LogError("{Message}", msg);
                return HealthCheckResult.Unhealthy(msg);
            }

            return HealthCheckResult.Healthy(
                $"KEK cabinet ready (minTenantVersion={minRow}, active={activeVersion}, "
                + $"retainedHistorySize={retainedHistorySize}).");
        }
        catch (Exception ex)
        {
            // Postgres unreachable, model not yet migrated, etc. We
            // don't want this health check to take down readiness for
            // unrelated reasons — degrade to Degraded rather than
            // Unhealthy.
            _logger.LogWarning(ex,
                "KekCabinetHealthCheck could not query tenants table — degraded.");
            return HealthCheckResult.Degraded(
                $"KekCabinetHealthCheck inconclusive: {ex.GetType().Name}",
                ex);
        }
    }
}
