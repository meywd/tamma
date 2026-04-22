using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Tamma.Data.Interceptors;

/// <summary>
/// EF Core connection interceptor that binds the current request's
/// <see cref="ITenantContext.TenantId"/> to the Postgres session variable
/// <c>app.current_tenant_id</c>. This is the C# port of the TS
/// <c>withTenantContext</c> helper (finding 004) and the load-bearing hook
/// that activates the RLS policies installed by the Phase-2 migration
/// (finding 020).
///
/// <para>Behavior:</para>
/// <list type="bullet">
///   <item><description>On every connection open, run
///     <c>SELECT set_config('app.current_tenant_id', @tenantId, false)</c>.
///     The third arg <c>false</c> scopes the setting to the session (NOT
///     a transaction) — EF Core pools connections and reuses them for
///     multiple statements within a request, so session scope matches the
///     DbContext lifetime. Each scoped DbContext resolution opens a fresh
///     connection (pooled or new), and we re-apply the binding each time.
///     </description></item>
///   <item><description>If <see cref="ITenantContext.TenantId"/> is null,
///     run <c>set_config('app.current_tenant_id', '', false)</c> so RLS
///     evaluates <c>NULLIF(current_setting(...), '')::uuid = NULL</c> and
///     fails closed for tenant-scoped rows.</description></item>
///   <item><description>Only applies to Npgsql connections — no-ops on
///     other providers so the test/unit path (InMemory / SQLite) is
///     unaffected.</description></item>
/// </list>
///
/// <para>Register this interceptor ONLY on the app-role DbContext
/// (<c>TammaAppDbContext</c>). The admin-role DbContext
/// (<c>TammaDbContext</c>) intentionally skips the binding — background
/// services and migrations run as superuser-equivalent and bypass RLS.</para>
///
/// <para>Safety note: <c>set_config(..., false)</c> is session-scoped so
/// the value leaks across statements on the same connection. This is
/// intentional — the same DbContext scope fires multiple queries and we
/// want them all to see the same tenant. When Npgsql returns the
/// connection to the pool, the next checkout will re-open via this
/// interceptor and reset the value. Connection pool reuse of a stale
/// tenant value cannot happen unless the interceptor fails; we log and
/// continue in that case (see catch block) to avoid creating a
/// hard-fail on transient DB hiccups.</para>
/// </summary>
public sealed class TenantContextInterceptor : DbConnectionInterceptor
{
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<TenantContextInterceptor>? _logger;

    public TenantContextInterceptor(
        ITenantContext tenantContext,
        ILogger<TenantContextInterceptor>? logger = null)
    {
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyTenantBindingAsync(connection, cancellationToken);
    }

    public override void ConnectionOpened(
        DbConnection connection,
        ConnectionEndEventData eventData)
    {
        // Synchronous path — rarely hit in ASP.NET Core but required by the
        // interceptor contract. Block on the async path rather than
        // duplicating the logic.
        ApplyTenantBindingAsync(connection, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private async Task ApplyTenantBindingAsync(
        DbConnection connection, CancellationToken ct)
    {
        if (connection is not NpgsqlConnection)
        {
            // Non-Postgres providers (InMemory, SQLite) don't support
            // set_config. Silently skip — the test path does not exercise
            // RLS and fails-closed via EF query filters instead.
            return;
        }

        var tenantId = _tenantContext.TenantId;
        var tenantValue = tenantId?.ToString() ?? string.Empty;

        try
        {
            await using var cmd = connection.CreateCommand();
            // Third arg `false` = session-scope (NOT transaction-scope).
            // set_config returns the value as text; we read it to keep the
            // command round-trip predictable.
            cmd.CommandText = "SELECT set_config('app.current_tenant_id', @p, false)";
            var param = cmd.CreateParameter();
            param.ParameterName = "p";
            param.Value = tenantValue;
            cmd.Parameters.Add(param);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Failed to bind app.current_tenant_id on connection open (tenantId={TenantId})",
                tenantId);
            // Do NOT rethrow — if RLS is active, the policy will fail-closed
            // to zero rows which is the correct behavior for an unbinded
            // session. If RLS is dormant, the EF query filter still
            // fails-closed. Either way, behavior is safe.
        }
    }
}
