using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Extensions;
using Tamma.Api.Services.Diagnostics;
using Tamma.Data;

namespace Tamma.Api.Tests.Diagnostics;

/// <summary>
/// Wraps <see cref="DiagnosticsSetUpFixture.Factory"/> with
/// <c>ConfigureTestServices</c> to add the diagnostics DI registrations.
/// Keeps <c>Program.cs</c> untouched (the parent orchestration owns the real
/// wiring) while making the tests hermetic.
/// </summary>
/// <remarks>
/// The resulting <see cref="WebApplicationFactory{TEntryPoint}"/> is cached
/// so every test class in this folder reuses the same host and the same
/// in-memory recent-events cache, which matters because
/// <see cref="IDiagnosticsService"/> is registered as a singleton.
/// </remarks>
internal static class DiagnosticsTestHarness
{
    private static readonly object _lock = new();
    private static WebApplicationFactory<Program>? _factory;

    /// <summary>
    /// Header a test request sets to bind a concrete active tenant. The
    /// dev-mode <c>AllowAnonymous</c> fixture resolves no tenant (the real
    /// <see cref="Tamma.Api.Middleware.TenantContextMiddleware"/> only binds
    /// from a JWT/API-key principal), so <c>ITenantContext.TenantId</c> is
    /// null by default — which the Story 23-6 fail-closed guard now rejects.
    /// This header lets the tenant-scoped integration tests act as a concrete
    /// tenant; omitting it exercises the null-tenant fail-closed path.
    /// </summary>
    public const string TenantHeader = "X-Test-Tenant-Id";

    public static WebApplicationFactory<Program> Factory
    {
        get
        {
            if (_factory is not null) return _factory;
            lock (_lock)
            {
                _factory ??= DiagnosticsSetUpFixture.Factory.WithWebHostBuilder(builder =>
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.AddDiagnosticsServices();

                        // Header-driven ITenantContext so tenant-scoped HTTP
                        // tests can present a concrete tenant. Scoped + read
                        // from the current request header ⇒ parallel-safe.
                        services.AddHttpContextAccessor();
                        services.RemoveAll<ITenantContext>();
                        services.AddScoped<ITenantContext>(sp =>
                        {
                            var ctx = new TenantContext();
                            var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
                            if (http is not null
                                && http.Request.Headers.TryGetValue(TenantHeader, out var vals)
                                && Guid.TryParse(vals.ToString(), out var tid))
                            {
                                ctx.SetTenantId(tid);
                            }
                            return ctx;
                        });
                    });
                });
            }
            return _factory;
        }
    }

    public static HttpClient CreateClient() => Factory.CreateClient();

    public static IServiceScope CreateScope() => Factory.Services.CreateScope();
}
