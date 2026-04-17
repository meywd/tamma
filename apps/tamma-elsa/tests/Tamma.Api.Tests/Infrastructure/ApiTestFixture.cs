using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Tamma.Api.Tests;

/// <summary>
/// Shared in-memory web-app fixture for Api integration tests.
///
/// The fixture spins up <see cref="Program"/> via <see cref="WebApplicationFactory{TEntryPoint}"/>
/// with Development-mode config and an in-memory connection string that is
/// never dialled (endpoints under test do not hit the database). If a test
/// needs a real database it should layer its own Testcontainers setup on top.
/// </summary>
public sealed class ApiTestFixture : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"]
                    = "Host=127.0.0.1;Port=1;Database=tamma_test;Username=test;Password=test",
                ["OpenSearch:Enabled"] = "false"
            });
        });
    }
}
