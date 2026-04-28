using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Alerts;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Alerts;

/// <summary>
/// Story 5.6 (Wave C.1) — unit tests for <see cref="PostgresAlertSink"/>.
/// Uses EF InMemory so we exercise the write path + fan-out logic
/// without needing a Postgres container. The event repository and
/// rate limiter are recording doubles so we can assert emit +
/// drop-audit behaviour deterministically.
/// </summary>
[TestFixture]
public class PostgresAlertSinkTests
{
    private ControlPlaneDbContext _db = null!;
    private RecordingEventRepository _events = null!;
    private TestRateLimiter _rateLimiter = null!;
    private TestTimeProvider _time = null!;
    private PostgresAlertSink _sink = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new ControlPlaneDbContext(options);
        _events = new RecordingEventRepository();
        _rateLimiter = new TestRateLimiter(allow: true);
        _time = new TestTimeProvider(DateTimeOffset.Parse("2026-04-23T12:00:00Z"));
        _sink = new PostgresAlertSink(
            _db, _rateLimiter, _events, _time,
            NullLogger<PostgresAlertSink>.Instance);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    // ── Happy path ──────────────────────────────────────────────

    [Test]
    public async Task RaiseAsync_PersistsAlertAndFansOutToEveryMatchingChannel()
    {
        var platformChannel = SeedChannel(tenantId: null, name: "plat-slack");
        var tenantChannel = SeedChannel(
            tenantId: Guid.NewGuid(), name: "other-tenant-slack");
        await _db.SaveChangesAsync();

        var result = await _sink.RaiseAsync(new AlertPayload(
            Severity: AlertSeverity.Critical,
            Title: "Budget exhausted",
            Description: "tenant 1 hit $50 cap"));

        result.Delivered.Should().BeTrue();
        result.DroppedByRateLimit.Should().BeFalse();
        result.MatchedChannels.Should().Be(1,
            "platform-scoped alert matches only platform channels");

        var alert = await _db.Alerts.SingleAsync();
        alert.Severity.Should().Be("critical");
        alert.Title.Should().Be("Budget exhausted");
        alert.Status.Should().Be("active");
        alert.CreatedAt.Should().Be(_time.GetUtcNow().UtcDateTime);

        var attempts = await _db.AlertDeliveryAttempts.ToListAsync();
        attempts.Should().ContainSingle();
        attempts[0].ChannelId.Should().Be(platformChannel.Id);
        attempts[0].Status.Should().Be("pending");
        attempts[0].AttemptNumber.Should().Be(1);

        _events.Emitted.Should().ContainSingle();
        _events.Emitted[0].Type.Should().Be("ALERT.RAISED");
    }

    [Test]
    public async Task RaiseAsync_TenantScopedAlert_FansOutToTenantAndPlatformChannels()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var platformChannel = SeedChannel(tenantId: null, name: "plat");
        var tenantChannel = SeedChannel(tenantId: tenantId, name: "tenant");
        var otherChannel = SeedChannel(tenantId: otherTenantId, name: "other");
        await _db.SaveChangesAsync();

        var result = await _sink.RaiseAsync(new AlertPayload(
            Severity: AlertSeverity.Warning,
            Title: "Workflow retry storm",
            Description: "3 retries in 5 min",
            TenantId: tenantId));

        result.MatchedChannels.Should().Be(2,
            "tenant-scoped alerts fan out to their own tenant + platform channels");

        var attempts = await _db.AlertDeliveryAttempts.ToListAsync();
        attempts.Should().HaveCount(2);
        attempts.Select(a => a.ChannelId).Should()
            .BeEquivalentTo(new[] { platformChannel.Id, tenantChannel.Id });
        attempts.Select(a => a.ChannelId).Should()
            .NotContain(otherChannel.Id,
            "other tenant's channels must never receive this tenant's alert");
    }

    [Test]
    public async Task RaiseAsync_DisabledChannel_IsSkipped()
    {
        SeedChannel(tenantId: null, name: "off", enabled: false);
        SeedChannel(tenantId: null, name: "on", enabled: true);
        await _db.SaveChangesAsync();

        var result = await _sink.RaiseAsync(new AlertPayload(
            Severity: AlertSeverity.Info,
            Title: "hello",
            Description: "."));

        result.MatchedChannels.Should().Be(1);
    }

    // ── Validation ──────────────────────────────────────────────

    [Test]
    public void RaiseAsync_InvalidSeverity_Throws()
    {
        var act = () => _sink.RaiseAsync(new AlertPayload(
            Severity: "spicy",
            Title: "x",
            Description: "y"));
        act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*severity*");
    }

    [Test]
    public void RaiseAsync_EmptyTitle_Throws()
    {
        var act = () => _sink.RaiseAsync(new AlertPayload(
            Severity: AlertSeverity.Info,
            Title: "",
            Description: "y"));
        act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Title*");
    }

    [Test]
    public void RaiseAsync_EmptyDescription_Throws()
    {
        var act = () => _sink.RaiseAsync(new AlertPayload(
            Severity: AlertSeverity.Info,
            Title: "x",
            Description: ""));
        act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Description*");
    }

    [Test]
    public void RaiseAsync_TitleOver512Chars_Throws()
    {
        var act = () => _sink.RaiseAsync(new AlertPayload(
            Severity: AlertSeverity.Info,
            Title: new string('x', 513),
            Description: "y"));
        act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*512*");
    }

    // ── Rate-limit drop ─────────────────────────────────────────

    [Test]
    public async Task RaiseAsync_RateLimitDrop_WritesNoAlertAndEmitsDroppedEvent()
    {
        SeedChannel(tenantId: null, name: "plat");
        await _db.SaveChangesAsync();
        var ruleId = Guid.NewGuid();
        _rateLimiter.NextAllow = false;

        var result = await _sink.RaiseAsync(new AlertPayload(
            Severity: AlertSeverity.Warning,
            Title: "throttled",
            Description: "rate-limited",
            RuleId: ruleId));

        result.Delivered.Should().BeFalse();
        result.DroppedByRateLimit.Should().BeTrue();
        result.AlertId.Should().Be(Guid.Empty);
        result.MatchedChannels.Should().Be(0);

        (await _db.Alerts.CountAsync()).Should().Be(0,
            "dropped alerts must NOT persist an alert row");
        (await _db.AlertDeliveryAttempts.CountAsync()).Should().Be(0,
            "dropped alerts write no delivery rows");

        _events.Emitted.Should().ContainSingle();
        _events.Emitted[0].Type.Should().Be("ALERT.DELIVERY_DROPPED");
    }

    // ── Helpers ─────────────────────────────────────────────────

    private AlertChannel SeedChannel(
        Guid? tenantId, string name, bool enabled = true)
    {
        var channel = new AlertChannel
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            ChannelType = AlertChannelType.Slack,
            IsEnabled = enabled,
            Config = "{}",
            CredentialsSecretId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.AlertChannels.Add(channel);
        return channel;
    }

    private sealed class TestRateLimiter : IAlertRateLimiter
    {
        public bool NextAllow { get; set; }

        public TestRateLimiter(bool allow) { NextAllow = allow; }

        public bool TryConsume(Guid? ruleId) => NextAllow;
    }

    internal sealed class RecordingEventRepository : IEventRepository
    {
        public List<DomainEvent> Emitted { get; } = new();

        public Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
            Emitted.Add(evt);
            return Task.FromResult(evt);
        }

        public Task<DomainEvent?> GetByIdAsync(Guid id) =>
            Task.FromResult<DomainEvent?>(null);

        public Task<List<DomainEvent>> QueryAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit) =>
            Task.FromResult(new List<DomainEvent>());

        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) =>
            Task.FromResult<DomainEvent?>(null);

        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;

        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset) =>
            Task.FromResult<(IReadOnlyList<DomainEvent>, int)>(
                (Array.Empty<DomainEvent>(), 0));
    }
}
