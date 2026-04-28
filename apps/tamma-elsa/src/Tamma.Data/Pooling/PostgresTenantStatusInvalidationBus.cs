using Microsoft.Extensions.Logging;
using Npgsql;
using Tamma.Data.Abstractions;

namespace Tamma.Data.Pooling;

/// <summary>
/// Postgres LISTEN/NOTIFY-backed <see cref="ITenantStatusInvalidationBus"/>.
/// Publishes a <c>pg_notify</c> on
/// <see cref="ChannelName"/> with the tenant id as the payload (formatted
/// as a Guid in <c>"D"</c> format — 36 characters with hyphens).
///
/// <para><b>Why <c>pg_notify(text, text)</c> instead of raw
/// <c>NOTIFY channel, payload</c></b>: <c>pg_notify</c> takes the channel
/// + payload as proper parameters, so they're never re-interpolated
/// into the SQL grammar. Raw <c>NOTIFY</c> requires inline literals,
/// which would force us to do our own quoting/escaping.</para>
///
/// <para><b>Best-effort delivery</b>: every call opens a short-lived
/// connection from the data source's pool, fires <c>SELECT pg_notify(...)</c>,
/// and returns. Failures are logged at WARN and swallowed — the
/// publishing pod has already done its local invalidation; cluster
/// fan-out is a freshness optimisation, not a correctness boundary.
/// Postgres NOTIFY is delivered transactionally: the message is queued
/// at COMMIT and Postgres handles redistribution to active LISTENers.
/// Because we're publishing in autocommit mode (no explicit
/// <c>BEGIN</c>), the COMMIT happens implicitly when the
/// <c>pg_notify(...)</c> SELECT returns.</para>
///
/// <para>The bus shares the central CP <see cref="NpgsqlDataSource"/>
/// rather than minting its own data source, so connection pool
/// observability (existing CP metrics) covers publish traffic too.</para>
/// </summary>
public sealed class PostgresTenantStatusInvalidationBus : ITenantStatusInvalidationBus
{
    /// <summary>
    /// Postgres LISTEN/NOTIFY channel used to broadcast tenant-status
    /// invalidations. Lower-case + underscored — Postgres lower-cases
    /// unquoted channel identifiers, so we keep the canonical name
    /// lower-case to avoid quoting confusion across publish/subscribe.
    /// </summary>
    public const string ChannelName = "tamma_tenant_status_changed";

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresTenantStatusInvalidationBus> _logger;

    public PostgresTenantStatusInvalidationBus(
        NpgsqlDataSource dataSource,
        ILogger<PostgresTenantStatusInvalidationBus> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);
        _dataSource = dataSource;
        _logger = logger;
    }

    public async ValueTask PublishAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Format the Guid as a 36-character hyphenated string. The
            // listener parses it back via Guid.TryParse — a strict
            // round-trip with no ambiguity vs alternate Guid formats
            // (N/B/P/X), and matches what every other tenant-id audit
            // payload in the system uses.
            var payload = tenantId.ToString("D");

            await using var conn = await _dataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_notify(@channel, @payload)";
            cmd.Parameters.AddWithValue("channel", ChannelName);
            cmd.Parameters.AddWithValue("payload", payload);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogDebug(
                "Published tenant-status invalidation for {TenantId} on channel {Channel}",
                tenantId, ChannelName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller requested cancellation — surface it.
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort: the local pod has already invalidated its
            // own cache. A transient publish failure means siblings
            // converge after the TTL elapses (default 10s), not faster.
            _logger.LogWarning(
                ex,
                "Failed to publish tenant-status invalidation for {TenantId} on channel {Channel}. "
                + "Local cache already invalidated; cluster will converge after TTL.",
                tenantId, ChannelName);
        }
    }
}
