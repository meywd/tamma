using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Tamma.Api.Extensions;
using Tamma.Api.Services.Diagnostics;

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
                    });
                });
            }
            return _factory;
        }
    }

    public static HttpClient CreateClient() => Factory.CreateClient();

    public static IServiceScope CreateScope() => Factory.Services.CreateScope();
}
