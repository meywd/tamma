using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Stopgap;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Api.Tests.TestDoubles;

namespace Tamma.Api.Tests.Secrets.Migration;

/// <summary>
/// TDD harness for Story 29-9's
/// <see cref="StopgapSecretMigrator"/>. Pins idempotency, audit-event
/// emission, and the config-or-env source resolution order.
///
/// <para><b>Parallelism</b>: marked <see cref="NonParallelizableAttribute"/>
/// because <see cref="RunAsync_ProbesEnvFallback_WhenConfigMissing"/> mutates
/// a process-wide environment variable (<c>TAMMA_SHARED_SECRET</c>) that is
/// also probed by every other test's migrator via
/// <see cref="StopgapSecretDescriptor.ResolveFromConfig"/>. Running
/// concurrently with sibling fixtures (or with other methods in this fixture
/// via <c>ParallelScope.Children</c>) would inject a spurious second
/// <c>MigratedSuccess</c> event and break the
/// <see cref="RunAsync_EmitsMigratedSuccessAuditEvent"/> assertion.</para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class StopgapSecretMigratorTests
{
    private SecretsDbContextFactoryDouble _factory = null!;
    private InMemorySecretStoreBackend _backend = null!;
    private RecordingSecretAccessAuditor _auditor = null!;
    private TimeProvider _time = null!;
    private static readonly Guid OperatorId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-111111111111");

    [SetUp]
    public void SetUp()
    {
        _factory = new SecretsDbContextFactoryDouble(Guid.NewGuid().ToString());
        _backend = new InMemorySecretStoreBackend();
        _auditor = new RecordingSecretAccessAuditor();
        _time = new FixedTimeProvider(
            new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero));
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private StopgapSecretMigrator NewMigrator(IConfiguration cfg) =>
        new(_factory, _backend, _auditor, cfg, _time,
            NullLogger<StopgapSecretMigrator>.Instance);

    [Test]
    public async Task RunAsync_ImportsEntriesWithSourceValue()
    {
        var cfg = Cfg(("Anthropic:ApiKey", "sk-ant-abc"),
                      ("GitHub:Token", "ghp_xyz"),
                      ("Cranl:ApiKey", "cranl_sk_123"));

        var migrator = NewMigrator(cfg);

        var report = await migrator.RunAsync(OperatorId);

        // Each entry with a source value should end up as Imported.
        report.Results
            .Where(r => r.Outcome == StopgapMigrationOutcome.Imported)
            .Select(r => r.CabinetName)
            .Should().BeEquivalentTo(new[]
            {
                StopgapSecretMap.PlatformAnthropicApiKey,
                StopgapSecretMap.PlatformGitHubToken,
                StopgapSecretMap.PlatformCranlApiKey,
            });

        // Parent rows exist in the cabinet DB.
        await using var ctx = await _factory.CreateDbContextAsync();
        var rows = await ctx.Secrets.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(r => r.Scope == "platform"
                                       && r.ActiveVersionNumber == 1);

        // Backend holds the plaintext.
        var anthropicRow = rows.Single(r => r.Name == StopgapSecretMap.PlatformAnthropicApiKey);
        (await _backend.GetVersionPlaintextAsync(anthropicRow.Id, 1))
            .Should().Be("sk-ant-abc");
    }

    [Test]
    public async Task RunAsync_EmitsMigratedSuccessAuditEvent()
    {
        var cfg = Cfg(("Anthropic:ApiKey", "sk-ant-abc"));
        var migrator = NewMigrator(cfg);

        await migrator.RunAsync(OperatorId);

        _auditor.Events
            .Where(e => e.EventType == SecretAuditEventTypes.MigratedSuccess)
            .Should().ContainSingle()
            .Which.Reference.Name.Should().Be(StopgapSecretMap.PlatformAnthropicApiKey);
    }

    [Test]
    public async Task RunAsync_EmitsMigratedFailedForMissingSource()
    {
        // Empty config: every entry lacks a source value.
        var cfg = Cfg();
        var migrator = NewMigrator(cfg);

        var report = await migrator.RunAsync(OperatorId);

        report.ImportedCount.Should().Be(0);
        report.NoSourceCount.Should().Be(StopgapSecretMap.Platform.Count);
        _auditor.Events
            .Where(e => e.EventType == SecretAuditEventTypes.MigratedFailed)
            .Should().HaveCount(StopgapSecretMap.Platform.Count);
    }

    [Test]
    public async Task RunAsync_IsIdempotent()
    {
        var cfg = Cfg(("Anthropic:ApiKey", "sk-ant-1"),
                      ("GitHub:Token", "ghp_1"));

        var migrator = NewMigrator(cfg);

        var first = await migrator.RunAsync(OperatorId);
        var second = await migrator.RunAsync(OperatorId);

        first.ImportedCount.Should().Be(2);
        // Second run: no new imports. The two already-present rows
        // roll to Skipped; the rest stay NoSourceValue.
        second.ImportedCount.Should().Be(0);
        second.SkippedCount.Should().Be(2);

        // DB still has exactly one row per imported secret.
        await using var ctx = await _factory.CreateDbContextAsync();
        (await ctx.Secrets.AsNoTracking().CountAsync())
            .Should().Be(2);

        // Second run emitted Skipped audit events for the already-present rows.
        _auditor.Events
            .Where(e => e.EventType == SecretAuditEventTypes.MigratedSkipped)
            .Select(e => e.Reference.Name)
            .Should().BeEquivalentTo(new[]
            {
                StopgapSecretMap.PlatformAnthropicApiKey,
                StopgapSecretMap.PlatformGitHubToken,
            });
    }

    [Test]
    public async Task RunAsync_SetsLastRotatedAt_ToNow()
    {
        var cfg = Cfg(("Anthropic:ApiKey", "sk-ant-abc"));
        var migrator = NewMigrator(cfg);

        await migrator.RunAsync(OperatorId);

        await using var ctx = await _factory.CreateDbContextAsync();
        var row = await ctx.Secrets.AsNoTracking().FirstAsync();
        row.LastRotatedAt.Should().Be(_time.GetUtcNow().UtcDateTime);
    }

    [Test]
    public async Task RunAsync_ProbesEnvFallback_WhenConfigMissing()
    {
        Environment.SetEnvironmentVariable(
            "TAMMA_SHARED_SECRET", "hmac-from-env");
        try
        {
            var cfg = Cfg();
            var migrator = NewMigrator(cfg);
            var report = await migrator.RunAsync(OperatorId);

            report.Results
                .Single(r => r.CabinetName == StopgapSecretMap.PlatformTenantSharedSecret)
                .Outcome.Should().Be(StopgapMigrationOutcome.Imported);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAMMA_SHARED_SECRET", null);
        }
    }

    private static IConfiguration Cfg(params (string Key, string Value)[] entries)
    {
        var dict = entries.ToDictionary<(string Key, string Value), string, string?>(
            e => e.Key, e => e.Value);
        return new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();
    }


    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
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
