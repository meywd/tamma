using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Stopgap;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Secrets.Migration;

/// <summary>
/// Tests for <see cref="RuntimeSecretResolver"/>. Pins the Story 29-9
/// cabinet-first-with-env-fallback contract AND the Story 29-10
/// fail-fast mode.
/// </summary>
[TestFixture]
public class RuntimeSecretResolverTests
{
    private SecretsDbContextFactoryDouble _factory = null!;
    private InMemorySecretStoreBackend _backend = null!;
    private TimeProvider _time = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new SecretsDbContextFactoryDouble(Guid.NewGuid().ToString());
        _backend = new InMemorySecretStoreBackend();
        _time = TimeProvider.System;
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private RuntimeSecretResolver New(
        IConfiguration cfg, bool allowEnvFallback, TimeSpan? ttl = null) =>
        new(_factory, _backend, cfg,
            NullLogger<RuntimeSecretResolver>.Instance,
            _time, allowEnvFallback, ttl);

    [Test]
    public async Task GetAsync_ReturnsValueFromCabinet_WhenPresent()
    {
        var cabinetName = StopgapSecretMap.PlatformAnthropicApiKey;
        var secretId = await SeedCabinetRowAsync(cabinetName, "sk-from-cabinet");
        _ = secretId;

        var resolver = New(Cfg(), allowEnvFallback: true);

        var v = await resolver.GetAsync(cabinetName);

        v.Should().Be("sk-from-cabinet");
    }

    [Test]
    public async Task GetAsync_FallsBackToConfig_WhenCabinetEmpty()
    {
        var cabinetName = StopgapSecretMap.PlatformAnthropicApiKey;
        var cfg = Cfg(("Anthropic:ApiKey", "sk-from-config"));

        var resolver = New(cfg, allowEnvFallback: true);

        var v = await resolver.GetAsync(cabinetName);

        v.Should().Be("sk-from-config");
    }

    [Test]
    public async Task GetAsync_ReturnsNull_WhenFallbackEmptyAndCabinetEmpty()
    {
        var resolver = New(Cfg(), allowEnvFallback: true);

        var v = await resolver.GetAsync(
            StopgapSecretMap.PlatformAnthropicApiKey);

        v.Should().BeNull();
    }

    [Test]
    public async Task GetAsync_ThrowsMissingSecret_WhenFallbackDisabled_AndCabinetEmpty()
    {
        var resolver = New(Cfg(), allowEnvFallback: false);

        Func<Task> act = () => resolver.GetAsync(
            StopgapSecretMap.PlatformAnthropicApiKey);

        await act.Should()
            .ThrowAsync<MissingSecretException>()
            .Where(e => e.CabinetName == StopgapSecretMap.PlatformAnthropicApiKey);
    }

    [Test]
    public async Task GetAsync_CachesValue_AcrossCalls()
    {
        var cabinetName = StopgapSecretMap.PlatformElsaApiKey;
        var secretId = await SeedCabinetRowAsync(cabinetName, "elsa-original");

        var resolver = New(Cfg(), allowEnvFallback: false,
            ttl: TimeSpan.FromMinutes(5));

        (await resolver.GetAsync(cabinetName)).Should().Be("elsa-original");

        // Mutate backend but NOT cache — still returns cached value.
        await _backend.DeleteVersionAsync(secretId, 1);
        await _backend.PutVersionAsync(secretId, 1, "elsa-rotated");

        (await resolver.GetAsync(cabinetName)).Should().Be("elsa-original");

        // After invalidation, the new value wins.
        resolver.Invalidate(cabinetName);
        (await resolver.GetAsync(cabinetName)).Should().Be("elsa-rotated");
    }

    [Test]
    public async Task GetAsync_Throws_OnEmptyCabinetName()
    {
        var resolver = New(Cfg(), allowEnvFallback: true);
        Func<Task> act = () => resolver.GetAsync("");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    private async Task<Guid> SeedCabinetRowAsync(string name, string plaintext)
    {
        var secretId = Guid.NewGuid();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            ctx.Secrets.Add(new SecretRow
            {
                Id = secretId,
                Name = name,
                Scope = "platform",
                TenantId = null,
                Purpose = "ApiKey",
                ActiveVersionNumber = 1,
                OwnerUserId = Guid.NewGuid(),
                LastRotatedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                ConsumerRefsJson = "[]",
                RotationScheduleJson = "{\"Kind\":\"None\"}",
            });
            await ctx.SaveChangesAsync();
        }
        await _backend.PutVersionAsync(secretId, 1, plaintext);
        return secretId;
    }

    private static IConfiguration Cfg(params (string Key, string Value)[] entries)
    {
        var dict = entries.ToDictionary<(string Key, string Value), string, string?>(
            e => e.Key, e => e.Value);
        return new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();
    }

    private sealed class SecretsDbContextFactoryDouble
        : IDbContextFactory<SecretsDbContext>, IDisposable
    {
        private readonly string _dbName;
        private SecretsDbContext? _tracking;

        public SecretsDbContextFactoryDouble(string dbName)
        {
            _dbName = dbName;
            _tracking = CreateDbContext();
            _tracking.Database.EnsureCreated();
        }

        public SecretsDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<SecretsDbContext>()
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics
                        .InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new SecretsDbContext(options);
        }

        public Task<SecretsDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());

        public void Dispose()
        {
            _tracking?.Dispose();
            _tracking = null;
        }
    }
}
