using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Alerts;
using Tamma.Api.Services.Alerts.Rules;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Alerts.Rules;

/// <summary>
/// Story 5.6 (Wave C.2) — registry semantics:
/// <list type="bullet">
///   <item><description>bucket-indexes by event type</description></item>
///   <item><description>only exposes enabled rules</description></item>
///   <item><description>hot-reload swaps the snapshot</description></item>
///   <item><description>malformed predicate skipped; other rules still loaded</description></item>
///   <item><description>wildcard rules merge into every per-type query</description></item>
/// </list>
/// </summary>
[TestFixture]
public class AlertRuleRegistryTests
{
    private ServiceProvider _sp = null!;
    private AlertRuleRegistry _registry = null!;
    private string _dbName = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(_dbName)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId
                    .TransactionIgnoredWarning))
            .Options;
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddScoped<ControlPlaneDbContext>(_ =>
            new ControlPlaneDbContext(options));
        _sp = services.BuildServiceProvider();
        _registry = new AlertRuleRegistry(
            _sp, NullLogger<AlertRuleRegistry>.Instance);
    }

    [TearDown]
    public void TearDown() => _sp.Dispose();

    private static AlertRule MakeRow(
        string eventType, string name,
        string predicate = """{"op":"always"}""",
        bool enabled = true,
        string severity = AlertSeverity.Warning)
    {
        return new AlertRule
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = "d",
            IsEnabled = enabled,
            Severity = severity,
            EventType = eventType,
            Predicate = predicate,
            ThrottleSeconds = 0,
            ChannelIds = Array.Empty<Guid>(),
            IsBuiltIn = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    [Test]
    public async Task Refresh_LoadsEnabledRulesIndexedByEventType()
    {
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            db.AlertRules.Add(MakeRow("A.X", "ax"));
            db.AlertRules.Add(MakeRow("B.Y", "by"));
            await db.SaveChangesAsync();
        }

        await _registry.RefreshAsync(default);

        _registry.Count.Should().Be(2);
        _registry.GetRulesForEventType("A.X").Should().ContainSingle();
        _registry.GetRulesForEventType("B.Y").Should().ContainSingle();
        _registry.GetRulesForEventType("UNKNOWN").Should().BeEmpty();
    }

    [Test]
    public async Task Refresh_SkipsDisabledRows()
    {
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            db.AlertRules.Add(MakeRow("A.X", "enabled", enabled: true));
            db.AlertRules.Add(MakeRow("A.X", "disabled", enabled: false));
            await db.SaveChangesAsync();
        }

        await _registry.RefreshAsync(default);

        _registry.GetRulesForEventType("A.X")
            .Should().ContainSingle()
            .Which.Name.Should().Be("enabled");
    }

    [Test]
    public async Task Refresh_BadPredicate_SkipsRowAndContinues()
    {
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            db.AlertRules.Add(MakeRow("A.X", "good"));
            db.AlertRules.Add(MakeRow("A.X", "bad",
                predicate: """{"op":"nonsense"}"""));
            await db.SaveChangesAsync();
        }

        await _registry.RefreshAsync(default);

        _registry.Count.Should().Be(1);
        _registry.GetRulesForEventType("A.X")
            .Should().ContainSingle()
            .Which.Name.Should().Be("good");
    }

    [Test]
    public async Task Refresh_WildcardRule_MergedIntoEveryQuery()
    {
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            db.AlertRules.Add(MakeRow("*", "catch-all"));
            db.AlertRules.Add(MakeRow("A.X", "ax"));
            await db.SaveChangesAsync();
        }

        await _registry.RefreshAsync(default);

        _registry.GetRulesForEventType("A.X")
            .Select(r => r.Name)
            .Should().Contain(new[] { "ax", "catch-all" });
        // Wildcard alone when no specific rule matches.
        _registry.GetRulesForEventType("Z.Z")
            .Select(r => r.Name).Should().Equal("catch-all");
    }

    [Test]
    public async Task Refresh_HotSwapsSnapshot()
    {
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            db.AlertRules.Add(MakeRow("A.X", "first"));
            await db.SaveChangesAsync();
        }
        await _registry.RefreshAsync(default);
        _registry.Count.Should().Be(1);

        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            db.AlertRules.Add(MakeRow("B.Y", "second"));
            await db.SaveChangesAsync();
        }
        await _registry.RefreshAsync(default);
        _registry.Count.Should().Be(2);
    }
}
