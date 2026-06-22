using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Security;
using Tamma.Data;
using Tamma.Data.Entities;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Security;

/// <summary>
/// Story 32-4 / 34-11 — <see cref="EntityProviderAuthLookup"/> over a real
/// Postgres testcontainer. Proves the entity-backed read of
/// <c>Provider.AuthModel</c> resolves <c>api-key</c> / <c>cli-token</c> rows,
/// returns <c>null</c> for an unknown key (SaaS fail-closed), and matches
/// case-insensitively / trimmed — the production default behind
/// <see cref="IProviderAuthLookup"/>.
/// </summary>
[TestFixture]
public class EntityProviderAuthLookupTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;
    private ServiceProvider _sp = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("db_authlookup_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();

        var services = new ServiceCollection();
        services.AddDbContext<ControlPlaneDbContext>(o => o.UseNpgsql(_cs));
        _sp = services.BuildServiceProvider();

        await using var ctx = NewContext();
        await ctx.Database.MigrateAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_sp is not null) await _sp.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SetUp]
    public async Task SetUp()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(
            "TRUNCATE provider_model_prices, providers CASCADE;");

        var now = DateTime.UtcNow;
        ctx.Providers.AddRange(
            new Provider
            {
                Id = Guid.NewGuid(), Key = "anthropic", DisplayName = "Anthropic",
                AuthModel = "api-key", Status = "active", CreatedAt = now, UpdatedAt = now,
            },
            new Provider
            {
                Id = Guid.NewGuid(), Key = "claude-code", DisplayName = "Claude Code",
                AuthModel = "cli-token", Status = "active", CreatedAt = now, UpdatedAt = now,
            },
            new Provider
            {
                // A retired api-key provider — must NOT classify as ApiKey (fail-closed).
                Id = Guid.NewGuid(), Key = "retired-openai", DisplayName = "Retired OpenAI",
                AuthModel = "api-key", Status = "retired", CreatedAt = now, UpdatedAt = now,
            });
        await ctx.SaveChangesAsync();
    }

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    private EntityProviderAuthLookup NewLookup() =>
        new(_sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EntityProviderAuthLookup>.Instance);

    [Test]
    public async Task Reads_api_key_provider_as_ApiKey()
    {
        var model = await NewLookup().AuthModelAsync("anthropic");
        model.Should().Be(ProviderAuthModel.ApiKey);
    }

    [Test]
    public async Task Reads_cli_token_provider_as_CliToken()
    {
        var model = await NewLookup().AuthModelAsync("claude-code");
        model.Should().Be(ProviderAuthModel.CliToken);
    }

    [Test]
    public async Task Unknown_key_resolves_to_null_failclosed()
    {
        var model = await NewLookup().AuthModelAsync("not-a-provider");
        model.Should().BeNull();
    }

    [Test]
    public async Task Retired_provider_resolves_to_null_failclosed()
    {
        // A retired api-key provider must resolve to null (unknown → SaaS deny),
        // never classify as ApiKey — the active-status filter (matching 34-11's
        // DbProviderPricingService Status == "active") is load-bearing here.
        var model = await NewLookup().AuthModelAsync("retired-openai");
        model.Should().BeNull();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task Blank_key_resolves_to_null(string? key)
    {
        var model = await NewLookup().AuthModelAsync(key);
        model.Should().BeNull();
    }

    [TestCase("ANTHROPIC", ProviderAuthModel.ApiKey)]
    [TestCase("  claude-code ", ProviderAuthModel.CliToken)]
    [TestCase("Claude-Code", ProviderAuthModel.CliToken)]
    public async Task Matching_is_case_insensitive_and_trimmed(string key, ProviderAuthModel expected)
    {
        var model = await NewLookup().AuthModelAsync(key);
        model.Should().Be(expected);
    }
}
