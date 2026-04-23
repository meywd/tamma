using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Alerts.Rules;
using Tamma.Data;

namespace Tamma.Api.Tests.Alerts.Rules;

/// <summary>
/// Story 5.6 (Wave C.2) — seeder idempotency + surgical update
/// semantics:
/// <list type="bullet">
///   <item><description>first run inserts all 5 built-ins</description></item>
///   <item><description>re-run on unchanged DB is a no-op</description></item>
///   <item><description>drift on description/event_type/predicate/throttle
///     triggers a surgical update</description></item>
///   <item><description>admin overrides on is_enabled / channel_ids /
///     severity survive re-run</description></item>
///   <item><description>re-run does not insert duplicates</description></item>
/// </list>
/// </summary>
[TestFixture]
public class BuiltInAlertRuleSeederTests
{
    private ServiceProvider _sp = null!;
    private TestTimeProvider _time = null!;
    private BuiltInAlertRuleSeeder _seeder = null!;

    [SetUp]
    public void SetUp()
    {
        // One shared DbContextOptions keeps all scope-resolved
        // contexts pointed at the same EF InMemory database root.
        // AddDbContext with an inline lambda creates a fresh options
        // instance per resolve, which under some EF versions fans out
        // into independent in-memory roots even when the database
        // name matches — hence the explicit shared options.
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId
                    .TransactionIgnoredWarning))
            .Options;
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddScoped<ControlPlaneDbContext>(_ =>
            new ControlPlaneDbContext(options));
        _sp = services.BuildServiceProvider();
        _time = new TestTimeProvider(DateTimeOffset.Parse("2026-04-23T10:00:00Z"));
        _seeder = new BuiltInAlertRuleSeeder(
            _sp, _time, NullLogger<BuiltInAlertRuleSeeder>.Instance);
    }

    [TearDown]
    public void TearDown() => _sp.Dispose();

    [Test]
    public async Task FirstRun_InsertsAllBuiltIns()
    {
        var result = await _seeder.SeedAsync(default);
        result.Inserted.Should().Be(BuiltInAlertRules.All.Count);
        result.Updated.Should().Be(0);
        result.Unchanged.Should().Be(0);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var rows = await db.AlertRules.ToListAsync();
        rows.Should().HaveCount(BuiltInAlertRules.All.Count);
        rows.Should().AllSatisfy(r =>
        {
            r.IsBuiltIn.Should().BeTrue();
            r.BuiltInKey.Should().NotBeNullOrEmpty();
        });
    }

    [Test]
    public async Task RerunOnUnchangedDb_IsNoOp()
    {
        await _seeder.SeedAsync(default);
        var second = await _seeder.SeedAsync(default);
        second.Inserted.Should().Be(0);
        second.Updated.Should().Be(0);
        second.Unchanged.Should().Be(BuiltInAlertRules.All.Count);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        (await db.AlertRules.CountAsync())
            .Should().Be(BuiltInAlertRules.All.Count,
                "re-run must not duplicate");
    }

    [Test]
    public async Task DescriptionDrift_TriggersSurgicalUpdate()
    {
        await _seeder.SeedAsync(default);

        // Simulate a deploy where the in-code description changed.
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var row = await db.AlertRules
                .FirstAsync(r => r.BuiltInKey == "budget-exhausted");
            row.Description = "stale description from earlier release";
            await db.SaveChangesAsync();
        }

        var result = await _seeder.SeedAsync(default);
        result.Updated.Should().Be(1);

        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var row = await db.AlertRules
                .FirstAsync(r => r.BuiltInKey == "budget-exhausted");
            row.Description.Should().NotBe("stale description from earlier release");
            row.Description.Should().Be(
                BuiltInAlertRules.All
                    .First(s => s.BuiltInKey == "budget-exhausted").Description);
        }
    }

    [Test]
    public async Task AdminOverrides_SurviveRerun()
    {
        await _seeder.SeedAsync(default);
        var customChannel = Guid.NewGuid();

        // Admin disables the rule and links a channel.
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var row = await db.AlertRules
                .FirstAsync(r => r.BuiltInKey == "workflow-retry-exceeded");
            row.IsEnabled = false;
            row.ChannelIds = new[] { customChannel };
            row.Severity = "warning";  // admin demoted from critical
            await db.SaveChangesAsync();
        }

        // Re-run seeder — should keep admin overrides untouched.
        await _seeder.SeedAsync(default);

        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var row = await db.AlertRules
                .FirstAsync(r => r.BuiltInKey == "workflow-retry-exceeded");
            row.IsEnabled.Should().BeFalse("admin override preserved");
            row.ChannelIds.Should().Equal(customChannel);
            row.Severity.Should().Be("warning");
        }
    }

    [Test]
    public async Task PredicateDrift_TriggersSurgicalUpdate()
    {
        await _seeder.SeedAsync(default);

        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var row = await db.AlertRules
                .FirstAsync(r => r.BuiltInKey == "agent-dispatch-failed-3x-5min");
            row.Predicate =
                """{"op":"count_gte","window_seconds":60,"threshold":10}""";
            await db.SaveChangesAsync();
        }

        await _seeder.SeedAsync(default);

        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var row = await db.AlertRules
                .FirstAsync(r => r.BuiltInKey == "agent-dispatch-failed-3x-5min");
            row.Predicate.Should().Contain("window_seconds\":300");
            row.Predicate.Should().Contain("threshold\":3");
        }
    }

    [Test]
    public async Task SeederResult_IsIdempotentAcrossMultipleCalls()
    {
        await _seeder.SeedAsync(default);
        await _seeder.SeedAsync(default);
        var third = await _seeder.SeedAsync(default);
        third.Inserted.Should().Be(0);
        third.Updated.Should().Be(0);
    }
}
