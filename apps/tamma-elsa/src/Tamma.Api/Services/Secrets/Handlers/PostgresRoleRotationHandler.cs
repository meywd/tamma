using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Tamma.Activities.SecretsRotation.Contracts;

namespace Tamma.Api.Services.Secrets.Handlers;

/// <summary>
/// Story 29-7 — concrete <see cref="IRotationHandler"/> that rotates a
/// Postgres role's password. Resolved by the rotation workflow when a
/// secret's first <c>ConsumerRef</c> is
/// <c>{ system: "postgres", identifier: "role=&lt;role&gt;;db=&lt;db&gt;" }</c>.
///
/// <para>Flow:</para>
/// <list type="number">
///   <item><description><b>PushAsync</b>: validate the role name
///     against <see cref="RoleWhitelist"/>; run
///     <c>ALTER ROLE "&lt;role&gt;" WITH PASSWORD '&lt;new&gt;'</c>
///     on the admin connection string (see
///     <see cref="ResolveAdminConnectionString"/>).</description></item>
///   <item><description><b>ProbeAsync</b>: open a fresh
///     <see cref="NpgsqlDataSource"/> with the new password, run
///     <c>SELECT 1</c>, return <see cref="ProbeResult.Healthy"/> on
///     success.</description></item>
///   <item><description><b>RollbackAsync</b>: fetch the previous
///     active version's plaintext from the gateway, ALTER ROLE back
///     to it; if no prior exists, set the role's password to NULL
///     (disable) and emit <c>ROLLBACK.ROLE_DISABLED</c>.</description></item>
///   <item><description><b>RevokeOldAsync</b>: drain the Npgsql pool
///     keyed by the old-password connection string so in-flight
///     connections can close cleanly.</description></item>
/// </list>
///
/// <para>Dry-run mode (<see cref="RotationContext.DryRun"/>=true)
/// short-circuits PushAsync with a log-only preview — used by the
/// admin-UI's "preview rotation" button (Story 29-4).</para>
/// </summary>
public sealed class PostgresRoleRotationHandler : IRotationHandler
{
    public string System => "postgres";

    private readonly IPostgresRotationExecutor _executor;
    private readonly ISecretRotationGateway _gateway;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PostgresRoleRotationHandler> _logger;

    public PostgresRoleRotationHandler(
        IPostgresRotationExecutor executor,
        ISecretRotationGateway gateway,
        IConfiguration configuration,
        ILogger<PostgresRoleRotationHandler> logger)
    {
        _executor = executor;
        _gateway = gateway;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task PushAsync(
        RotationTarget target,
        string newPlaintext,
        RotationContext ctx,
        CancellationToken ct)
    {
        var parsed = PostgresConsumerIdentifier.Parse(target.ConsumerIdentifier);
        EnsureAllowed(target, parsed);

        if (!PostgresPasswordGenerator.IsSafe(newPlaintext))
            throw new ArgumentException(
                "Supplied plaintext is not in the Postgres-safe alphabet. " +
                "Use PostgresPasswordGenerator.Generate() for the value.",
                nameof(newPlaintext));

        if (ctx.DryRun)
        {
            _logger.LogInformation(
                "[dry-run] Would execute ALTER ROLE \"{Role}\" WITH PASSWORD '<redacted, {Length} chars>' rotation={Correlation}",
                parsed.Role, newPlaintext.Length, ctx.RotationCorrelationId);
            return;
        }

        var admin = ResolveAdminConnectionString(ctx);
        await _executor.AlterRolePasswordAsync(admin, parsed.Role, newPlaintext, ct).ConfigureAwait(false);
    }

    public async Task<ProbeResult> ProbeAsync(
        RotationTarget target,
        RotationContext ctx,
        CancellationToken ct)
    {
        var parsed = PostgresConsumerIdentifier.Parse(target.ConsumerIdentifier);

        var admin = ResolveAdminConnectionString(ctx);
        var newPlaintext = await _gateway.GetVersionPlaintextAsync(
                target.SecretId, target.NewVersionNumber, ct)
            .ConfigureAwait(false);
        if (newPlaintext is null)
            return ProbeResult.Unhealthy("new_plaintext_missing", 0);

        var probeConnString = BuildProbeConnectionString(admin, parsed, newPlaintext);
        try
        {
            var ms = await _executor.ProbeRoleAsync(probeConnString, ct).ConfigureAwait(false);
            return ProbeResult.Healthy(ms);
        }
        catch (NpgsqlException ex)
        {
            return ProbeResult.Unhealthy(
                $"npgsql_{ex.SqlState ?? "unknown"}:{ex.GetType().Name}", 0);
        }
        catch (Exception ex)
        {
            return ProbeResult.Unhealthy(ex.GetType().Name, 0);
        }
    }

    public async Task RollbackAsync(
        RotationTarget target,
        string newPlaintext,
        RotationContext ctx,
        CancellationToken ct)
    {
        var parsed = PostgresConsumerIdentifier.Parse(target.ConsumerIdentifier);
        EnsureAllowed(target, parsed);

        var admin = ResolveAdminConnectionString(ctx);
        string? previousPlaintext = null;
        if (target.PreviousVersionNumber > 0)
        {
            previousPlaintext = await _gateway.GetVersionPlaintextAsync(
                    target.SecretId, target.PreviousVersionNumber, ct)
                .ConfigureAwait(false);
        }

        if (previousPlaintext is not null && PostgresPasswordGenerator.IsSafe(previousPlaintext))
        {
            await _executor.AlterRolePasswordAsync(admin, parsed.Role, previousPlaintext, ct)
                .ConfigureAwait(false);
        }
        else
        {
            _logger.LogWarning(
                "No previous plaintext available for {Role}; disabling role password.",
                parsed.Role);
            await _executor.SetRolePasswordNullAsync(admin, parsed.Role, ct).ConfigureAwait(false);
        }
    }

    public Task RevokeOldAsync(
        RotationTarget target,
        string oldPlaintext,
        RotationContext ctx,
        CancellationToken ct)
    {
        try
        {
            var parsed = PostgresConsumerIdentifier.Parse(target.ConsumerIdentifier);
            var admin = ResolveAdminConnectionString(ctx);
            var oldConnString = BuildProbeConnectionString(admin, parsed, oldPlaintext);
            _executor.DrainPool(oldConnString);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Pool drain failed for secret {Secret} — leaving connections to age out.",
                target.Name);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolve the admin connection string. Order:
    /// 1) <c>RotationContext.HandlerOptions["AdminConnectionString"]</c>
    ///    (lets 29-4's UI inject a one-off credential);
    /// 2) <c>ConnectionStrings:TammaAdmin</c>;
    /// 3) <c>ConnectionStrings:TenantAdmin</c>;
    /// 4) <c>ConnectionStrings:DefaultConnection</c> — last-resort
    ///    fallback.
    /// </summary>
    private string ResolveAdminConnectionString(RotationContext ctx)
    {
        var fromCtx = ctx.GetOption("AdminConnectionString", string.Empty);
        if (!string.IsNullOrWhiteSpace(fromCtx)) return fromCtx;
        var cs = _configuration.GetConnectionString("TammaAdmin")
            ?? _configuration.GetConnectionString("TenantAdmin")
            ?? _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                "No admin connection string configured. Set ConnectionStrings:TammaAdmin " +
                "or pass HandlerOptions.AdminConnectionString.");
        return cs;
    }

    /// <summary>
    /// Build the probe connection string. Re-uses the admin string's
    /// host/port/SSL and overrides Username/Password/Database with the
    /// role under rotation + the new plaintext.
    /// </summary>
    internal static string BuildProbeConnectionString(
        string adminConnectionString,
        PostgresConsumerIdentifier parsed,
        string newPlaintext)
    {
        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Username = parsed.Role,
            Password = newPlaintext,
            ApplicationName = "tamma-rotation-probe",
        };
        if (parsed.Db is not null)
            builder.Database = parsed.Db;
        return builder.ConnectionString;
    }

    private static void EnsureAllowed(RotationTarget target, PostgresConsumerIdentifier parsed)
    {
        var isTenantScope = target.TenantId is not null;
        if (!RoleWhitelist.IsAllowed(parsed.Role, isTenantScope))
            throw new InvalidOperationException(
                $"Role '{parsed.Role}' is not on the Tamma rotation whitelist " +
                $"(scope={(isTenantScope ? "tenant" : "platform")}). " +
                "Refusing to rotate.");
    }
}
