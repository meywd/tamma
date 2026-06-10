using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Story 28-12 AC1+AC2 (2026-05-30 residual #3) — runtime least-privilege
/// assertion for the control-plane API's database role.
///
/// <para>The three-role split in <c>scripts/db/postgres-roles.sql</c>
/// gives the runtime API the <c>tamma_app</c> login role, which has
/// SELECT/INSERT/UPDATE/DELETE on the CP tables but <b>cannot</b>
/// <c>CREATE DATABASE</c> or <c>CREATE ROLE</c>. The security property:
/// a SQL-injection or logic bug in request handling can't escalate to
/// provisioning databases / roles. The gap this closes: nothing enforced
/// that the API actually connects as <c>tamma_app</c> — a pod accidentally
/// configured with the <c>tamma_provisioner</c> or <c>tamma_admin</c> URL
/// would silently run with escalated privileges.</para>
///
/// <para>This health check runs <c>SELECT current_user</c> against the
/// app connection (<c>ConnectionStrings:TammaAppDb</c>) on the "ready"
/// probe and asserts the result is NOT <c>tamma_provisioner</c> and NOT
/// <c>tamma_admin</c>.</para>
///
/// <para><b>Gating</b> (see <see cref="Evaluate"/>): in Production a
/// privileged role is <see cref="HealthStatus.Unhealthy"/> (fail fast —
/// readiness never flips green). In Development / Test — where the split
/// may not exist and everything runs as the default <c>postgres</c> user
/// — a privileged role is only a WARN log and the check reports
/// Healthy. This keeps the 2664-test suite (single default role,
/// <c>UseEnvironment("Development")</c>) green.</para>
///
/// <para>The <see cref="IsForbiddenAppUser"/> / <see cref="Evaluate"/>
/// decision core is pure and unit-tested
/// (<c>DbRoleLeastPrivilegeCheckTests</c>). The live
/// <c>SELECT current_user</c> probe is the integration boundary.</para>
/// </summary>
public sealed class DbRoleLeastPrivilegeCheck : IHealthCheck
{
    /// <summary>Roles the runtime API must never connect as.</summary>
    private static readonly string[] ForbiddenRoles =
    {
        "tamma_provisioner",
        "tamma_admin",
    };

    private readonly string? _appConnectionString;
    private readonly bool _isProduction;
    private readonly ILogger<DbRoleLeastPrivilegeCheck> _logger;

    public DbRoleLeastPrivilegeCheck(
        string? appConnectionString,
        bool isProduction,
        ILogger<DbRoleLeastPrivilegeCheck> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _appConnectionString = appConnectionString;
        _isProduction = isProduction;
        _logger = logger;
    }

    /// <summary>
    /// Pure decision: is <paramref name="currentUser"/> a Postgres role
    /// the runtime API must never run as? Case-insensitive (Postgres
    /// folds unquoted identifiers to lower-case, but a probe may surface
    /// mixed case). A null/empty user is inconclusive, not forbidden.
    /// </summary>
    public static bool IsForbiddenAppUser(string? currentUser)
    {
        if (string.IsNullOrWhiteSpace(currentUser))
            return false;

        foreach (var role in ForbiddenRoles)
        {
            if (string.Equals(currentUser.Trim(), role, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Pure gating decision combining the environment with the observed
    /// role. Production + forbidden role → <see cref="DbRoleLeastPrivilegeOutcome.Fail"/>.
    /// Non-Production + forbidden role → <see cref="DbRoleLeastPrivilegeOutcome.WarnOnly"/>.
    /// Otherwise <see cref="DbRoleLeastPrivilegeOutcome.Ok"/>.
    /// </summary>
    public static DbRoleLeastPrivilegeOutcome Evaluate(bool isProduction, string? currentUser)
    {
        if (!IsForbiddenAppUser(currentUser))
            return DbRoleLeastPrivilegeOutcome.Ok;

        return isProduction
            ? DbRoleLeastPrivilegeOutcome.Fail
            : DbRoleLeastPrivilegeOutcome.WarnOnly;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_appConnectionString))
        {
            // No app connection wired — common in dev/test where the
            // fixture leaves TammaAppDb unset and the API falls back to
            // the admin connection. Nothing to probe; report Healthy.
            return HealthCheckResult.Healthy(
                "DB role check skipped (no app connection string configured).");
        }

        string? currentUser;
        try
        {
            await using var conn = new NpgsqlConnection(_appConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand("SELECT current_user", conn);
            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            currentUser = result as string;
        }
        catch (Exception ex)
        {
            // Postgres unreachable / connection error — don't take down
            // readiness for an unrelated reason. Degrade rather than fail.
            _logger.LogWarning(ex,
                "DbRoleLeastPrivilegeCheck could not probe current_user — degraded.");
            return HealthCheckResult.Degraded(
                $"DbRoleLeastPrivilegeCheck inconclusive: {ex.GetType().Name}", ex);
        }

        var outcome = Evaluate(_isProduction, currentUser);
        switch (outcome)
        {
            case DbRoleLeastPrivilegeOutcome.Fail:
                var failMsg =
                    $"API is connected to Postgres as '{currentUser}', a privileged "
                    + "role (tamma_provisioner / tamma_admin). The runtime API MUST "
                    + "connect as tamma_app (least privilege — no CREATE DATABASE / "
                    + "CREATE ROLE). Fix ConnectionStrings:TammaAppDb to use the "
                    + "tamma_app role. See scripts/db/postgres-roles.sql and "
                    + "story 28-12 AC1/AC2.";
                _logger.LogError("{Message}", failMsg);
                return HealthCheckResult.Unhealthy(failMsg);

            case DbRoleLeastPrivilegeOutcome.WarnOnly:
                _logger.LogWarning(
                    "API is connected to Postgres as '{CurrentUser}', a privileged "
                    + "role (tamma_provisioner / tamma_admin). This is a hard failure "
                    + "in Production but only a warning outside it (the three-role "
                    + "split may not exist in dev/test). Set ConnectionStrings:TammaAppDb "
                    + "to the tamma_app role to match the production posture.",
                    currentUser);
                return HealthCheckResult.Healthy(
                    $"DB role check: running as '{currentUser}' (privileged) — warning "
                    + "only outside Production.");

            default:
                return HealthCheckResult.Healthy(
                    $"DB role check: running as least-privilege role '{currentUser}'.");
        }
    }
}

/// <summary>
/// Outcome of the least-privilege gating decision. See
/// <see cref="DbRoleLeastPrivilegeCheck.Evaluate"/>.
/// </summary>
public enum DbRoleLeastPrivilegeOutcome
{
    /// <summary>Running as an acceptable (least-privilege) role.</summary>
    Ok,

    /// <summary>Privileged role, but outside Production — warn, don't fail.</summary>
    WarnOnly,

    /// <summary>Privileged role in Production — fail fast.</summary>
    Fail,
}
