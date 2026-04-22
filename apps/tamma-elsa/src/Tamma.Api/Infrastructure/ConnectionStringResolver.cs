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
}
