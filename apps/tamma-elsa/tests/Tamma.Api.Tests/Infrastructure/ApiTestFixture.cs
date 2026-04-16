using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tamma.Api.Services.PromptStore;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Infrastructure;

/// <summary>
/// Shared test fixture that boots the ASP.NET host via <see cref="WebApplicationFactory{TEntryPoint}"/>.
/// Uses EF Core InMemory provider instead of Postgres because the sandbox lacks Docker;
/// this keeps integration tests deterministic and fast. Individual endpoint tests
/// exercise the full HTTP pipeline: routing, middleware, DI, repositories, and services.
///
/// Registers prompt-store services so endpoints requiring them can be hit. When the
/// parent wires DI globally in <c>Program.cs</c> after merging, this fixture can be
/// simplified.
/// </summary>
public sealed class ApiTestFixture : WebApplicationFactory<Program>, IDisposable
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    /// <summary>Stable user id assigned to every authenticated test request.</summary>
    public Guid TestUserId { get; } = Guid.NewGuid();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Jwt:Secret", ""); // force Development permissive branch
        builder.UseSetting("OpenSearch:Enabled", "false");

        builder.ConfigureServices(services =>
        {
            // Drop the production Npgsql DbContext options and swap to InMemory.
            // We replace every registration touching DbContextOptions<TammaDbContext>
            // and the context itself so the test resolves a TestDbContext that is
            // InMemory-safe (ignores mentorship entities with JsonDocument props).
            var descriptors = services.Where(d =>
                d.ServiceType == typeof(DbContextOptions<TammaDbContext>) ||
                d.ServiceType == typeof(DbContextOptions) ||
                d.ServiceType == typeof(TammaDbContext)).ToList();
            foreach (var d in descriptors) services.Remove(d);

            // Register DbContextOptions<TammaDbContext> so TestDbContext (which
            // inherits and takes that options type) constructs cleanly.
            services.AddSingleton(_ =>
            {
                var builder = new DbContextOptionsBuilder<TammaDbContext>()
                    .UseInMemoryDatabase(_dbName)
                    .ConfigureWarnings(w => w.Ignore(
                        Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning));
                return builder.Options;
            });

            services.AddScoped<TammaDbContext>(sp =>
            {
                var opts = sp.GetRequiredService<DbContextOptions<TammaDbContext>>();
                var tenantCtx = sp.GetRequiredService<ITenantContext>();
                return new TestDbContext(opts, tenantCtx);
            });

            // Register prompt-store services (parent will eventually wire these via
            // AddPromptStoreServices() in Program.cs — for now we do it here).
            services.AddScoped<PromptStoreService>();
            services.AddScoped<PromptEventsService>();

            // Install a lightweight test auth handler so endpoint handlers see a
            // stable user identity. Development's AllowAnonymous handler runs
            // without claims, which breaks per-user override testing.
            services.AddAuthentication(TestAuthHandler.Scheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.Scheme, _ => { });
            services.AddSingleton(this); // handler resolves TestUserId off us
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(TestAuthHandler.Scheme);
        return client;
    }

    /// <summary>
    /// Runs the given delegate inside a fresh scope with a live <see cref="TammaDbContext"/>.
    /// Useful for seeding or asserting database state from tests.
    /// </summary>
    public async Task WithDbAsync(Func<TammaDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
        await action(db);
    }

    public async Task<T> WithDbAsync<T>(Func<TammaDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
        return await action(db);
    }

    public IPromptRepository Prompts()
    {
        var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IPromptRepository>();
    }

    public IEventRepository Events()
    {
        var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IEventRepository>();
    }
}
