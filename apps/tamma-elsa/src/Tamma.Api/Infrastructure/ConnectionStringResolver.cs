using Microsoft.Extensions.Configuration;

namespace Tamma.Api.Infrastructure;

internal static class ConnectionStringResolver
{
    public static string ResolveAdmin(IConfiguration cfg)
    {
        var tamma = cfg.GetConnectionString("TammaDb");
        if (!string.IsNullOrWhiteSpace(tamma))
            return tamma;

        var legacy = cfg.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(legacy))
            return legacy;

        throw new InvalidOperationException(
            "No admin database connection configured. Set ConnectionStrings:TammaDb "
            + "(or the legacy ConnectionStrings:DefaultConnection).");
    }

    public static string? ResolveApp(IConfiguration cfg)
    {
        var app = cfg.GetConnectionString("TammaAppDb");
        return string.IsNullOrWhiteSpace(app) ? null : app;
    }

    /// <summary>
    /// Epic 28 control-plane connection string. Production:
    /// <c>tamma_control</c> Postgres. Dev: falls back to the admin
    /// connection so a single-DB local Postgres keeps working until the
    /// new context is wired into actual handlers (Story 28-2 endpoint
    /// cutover and Story 28-5 provisioning workflow).
    /// </summary>
    public static string? ResolveControlPlane(IConfiguration cfg)
    {
        var cp = cfg.GetConnectionString("ControlPlane");
        return string.IsNullOrWhiteSpace(cp) ? null : cp;
    }

    /// <summary>
    /// Epic 29 (review fix) — resolve the connection the secret cabinet
    /// should ride on, mirroring how the ControlPlane DbContext actually
    /// binds at runtime.
    ///
    /// <para>Order: dedicated <c>ConnectionStrings:SecretStore</c> →
    /// <c>ConnectionStrings:ControlPlane</c> → the admin connection
    /// (<c>TammaDb</c> / legacy <c>DefaultConnection</c>). The final admin
    /// fallback matches <c>AddTammaData</c>: when <c>ControlPlane</c> is
    /// unset the ControlPlaneDbContext runs on the admin connection, so the
    /// secret store must see that SAME real connection — never an empty
    /// string that would make a Production host silently fall through to
    /// volatile in-memory secrets.</para>
    ///
    /// <para>Empty / whitespace strings are coerced to null at every step
    /// (an appsettings default of <c>""</c> must not mask a missing
    /// override), and — unlike <see cref="ResolveAdmin"/> — this returns
    /// <c>null</c> rather than throwing when nothing resolves, so the caller
    /// can make a fail-closed vs in-memory decision.</para>
    /// </summary>
    public static string? ResolveSecretStore(IConfiguration cfg)
    {
        var secretStore = cfg.GetConnectionString("SecretStore");
        if (!string.IsNullOrWhiteSpace(secretStore))
            return secretStore;

        var controlPlane = ResolveControlPlane(cfg);
        if (!string.IsNullOrWhiteSpace(controlPlane))
            return controlPlane;

        // Admin fallback — the same connection the ControlPlaneDbContext
        // uses when ConnectionStrings:ControlPlane is unset. Non-throwing:
        // when neither admin key resolves we return null so the caller
        // can fail closed (Production) rather than silently boot in-memory.
        var admin = cfg.GetConnectionString("TammaDb");
        if (!string.IsNullOrWhiteSpace(admin))
            return admin;

        var legacy = cfg.GetConnectionString("DefaultConnection");
        return string.IsNullOrWhiteSpace(legacy) ? null : legacy;
    }
}
