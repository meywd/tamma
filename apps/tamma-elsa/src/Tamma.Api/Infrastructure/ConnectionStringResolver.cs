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
}
