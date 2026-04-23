using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Alerts;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Alerts;

/// <summary>
/// Story 5.6 / 1.5-37 (Wave C.1) — unit tests for
/// <see cref="NotificationDispatcher"/>. Exercises:
/// <list type="bullet">
///   <item><description>Drains <c>pending</c> rows in a single tick</description></item>
///   <item><description>Success → flips row to <c>success</c> + emits DELIVERY_SUCCESS</description></item>
///   <item><description>Failure → flips row to <c>failed</c> + schedules NextAttemptAt per backoff</description></item>
///   <item><description>After MaxAttempts failures → NextAttemptAt is DateTime.MaxValue (permanent skip)</description></item>
///   <item><description>Disabled channel → recorded as a failure (no infinite poll)</description></item>
///   <item><description>Unknown channel type → failure with descriptive error</description></item>
/// </list>
/// </summary>
[TestFixture]
public class NotificationDispatcherTests
{
    private ServiceProvider _sp = null!;
    private StubChannel _stubChannel = null!;
    private TestTimeProvider _time = null!;
    private PostgresAlertSinkTests.RecordingEventRepository _events = null!;

    [SetUp]
    public void SetUp()
    {
        _stubChannel = new StubChannel("slack");
        _time = new TestTimeProvider(DateTimeOffset.Parse("2026-04-23T12:00:00Z"));
        _events = new PostgresAlertSinkTests.RecordingEventRepository();

        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<ControlPlaneDbContext>(opts =>
            opts.UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
        services.AddSingleton<IAlertChannel>(_stubChannel);
        services.AddSingleton<IAlertChannelRegistry>(
            new AlertChannelRegistry(new[] { (IAlertChannel)_stubChannel }));
        services.AddSingleton<IEventRepository>(_events);
        _sp = services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown() => _sp.Dispose();

    private NotificationDispatcher NewDispatcher(
        NotificationDispatcherOptions? options = null) =>
        new(_sp, options ?? DefaultOptions(), _time,
            NullLogger<NotificationDispatcher>.Instance);

    private static NotificationDispatcherOptions DefaultOptions() => new()
    {
        PollInterval = TimeSpan.FromSeconds(10),
        MaxAttempts = 5,
        BackoffSchedule = new[]
        {
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(30),
        },
        BatchSize = 100,
    };

    private async Task<AlertDeliveryAttempt> ReadAttemptAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<ControlPlaneDbContext>();
        return await db.AlertDeliveryAttempts.AsNoTracking().FirstAsync();
    }

    // ── Tests ───────────────────────────────────────────────────

    [Test]
    public async Task DispatchOnceAsync_PendingRow_OnSuccess_FlipsToSuccess()
    {
        await SeedPendingAttemptAsync();
        _stubChannel.NextResult = new DeliveryResult(Success: true, Error: null);

        var processed = await NewDispatcher().DispatchOnceAsync(default);

        processed.Should().Be(1);
        var refreshed = await ReadAttemptAsync();
        refreshed.Status.Should().Be("success");
        refreshed.Error.Should().BeNull();
        refreshed.DeliveredAt.Should().NotBeNull();

        _events.Emitted.Should().ContainSingle(e =>
            e.Type == "ALERT.DELIVERY_SUCCESS");
    }

    [Test]
    public async Task DispatchOnceAsync_PendingRow_OnFailure_SchedulesNextAttemptAt30s()
    {
        await SeedPendingAttemptAsync();
        _stubChannel.NextResult = new DeliveryResult(false, "upstream 502");

        await NewDispatcher().DispatchOnceAsync(default);

        var refreshed = await ReadAttemptAsync();
        refreshed.Status.Should().Be("failed");
        refreshed.AttemptNumber.Should().Be(2);
        refreshed.Error.Should().Contain("upstream 502");
        refreshed.NextAttemptAt.Should().NotBeNull();
        refreshed.NextAttemptAt!.Value
            .Should().BeCloseTo(_time.GetUtcNow().UtcDateTime.AddSeconds(30),
                TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task DispatchOnceAsync_AfterMaxAttempts_NextAttemptAt_IsMaxValue()
    {
        var attemptId = await SeedPendingAttemptAsync();
        // Arrange: put the row at attempt 4 so the next failure is the 5th
        // (terminal) and the eligibility gate still lets it through
        // (AttemptNumber < MaxAttempts, i.e. 4 < 5).
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var row = await db.AlertDeliveryAttempts.FirstAsync(a => a.Id == attemptId);
            row.AttemptNumber = 4;
            row.Status = "failed";
            row.NextAttemptAt = _time.GetUtcNow().UtcDateTime.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        _stubChannel.NextResult = new DeliveryResult(false, "still broken");
        await NewDispatcher().DispatchOnceAsync(default);

        var refreshed = await ReadAttemptAsync();
        refreshed.AttemptNumber.Should().Be(5);
        refreshed.Status.Should().Be("failed");
        refreshed.NextAttemptAt.Should().Be(DateTime.MaxValue,
            "terminal attempts must never be picked up again by the poll query");
    }

    [Test]
    public async Task DispatchOnceAsync_RowAtMaxAttempts_IsNotPickedUp()
    {
        var attemptId = await SeedPendingAttemptAsync();
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var row = await db.AlertDeliveryAttempts.FirstAsync(a => a.Id == attemptId);
            row.AttemptNumber = 5;
            row.Status = "failed";
            row.NextAttemptAt = DateTime.MaxValue;
            await db.SaveChangesAsync();
        }

        _stubChannel.NextResult = new DeliveryResult(true, null);
        var processed = await NewDispatcher().DispatchOnceAsync(default);

        processed.Should().Be(0, "terminal rows must be skipped by the poll query");
        _stubChannel.Invocations.Should().Be(0);
    }

    [Test]
    public async Task DispatchOnceAsync_DisabledChannel_RecordsFailureAndSkipsSend()
    {
        var attemptId = await SeedPendingAttemptAsync();
        // Disable the channel between seed and dispatch.
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var attempt = await db.AlertDeliveryAttempts.FirstAsync(a => a.Id == attemptId);
            var channel = await db.AlertChannels.FirstAsync(c => c.Id == attempt.ChannelId);
            channel.IsEnabled = false;
            await db.SaveChangesAsync();
        }

        await NewDispatcher().DispatchOnceAsync(default);

        var refreshed = await ReadAttemptAsync();
        refreshed.Status.Should().Be("failed");
        refreshed.Error.Should().Contain("disabled");
        _stubChannel.Invocations.Should().Be(0,
            "disabled channel's SendAsync must NOT be invoked");
    }

    [Test]
    public async Task DispatchOnceAsync_UnknownChannelType_RecordsDescriptiveFailure()
    {
        var attemptId = await SeedPendingAttemptAsync();
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var attempt = await db.AlertDeliveryAttempts.FirstAsync(a => a.Id == attemptId);
            var channel = await db.AlertChannels.FirstAsync(c => c.Id == attempt.ChannelId);
            channel.ChannelType = "telegram"; // not registered
            await db.SaveChangesAsync();
        }

        await NewDispatcher().DispatchOnceAsync(default);

        var refreshed = await ReadAttemptAsync();
        refreshed.Status.Should().Be("failed");
        refreshed.Error.Should().Contain("telegram");
    }

    [Test]
    public async Task DispatchOnceAsync_NextAttemptAtInFuture_IsSkipped()
    {
        var attemptId = await SeedPendingAttemptAsync();
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var row = await db.AlertDeliveryAttempts.FirstAsync(a => a.Id == attemptId);
            row.Status = "failed";
            row.NextAttemptAt = _time.GetUtcNow().UtcDateTime.AddMinutes(5);
            row.AttemptNumber = 2;
            await db.SaveChangesAsync();
        }

        var processed = await NewDispatcher().DispatchOnceAsync(default);
        processed.Should().Be(0);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private async Task<Guid> SeedPendingAttemptAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            Severity = AlertSeverity.Warning,
            Title = "test",
            Description = "test",
            Status = AlertStatus.Active,
            CreatedAt = _time.GetUtcNow().UtcDateTime,
        };
        var channel = new AlertChannel
        {
            Id = Guid.NewGuid(),
            Name = "stub",
            ChannelType = "slack",
            IsEnabled = true,
            Config = "{}",
            CredentialsSecretId = Guid.NewGuid(),
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            UpdatedAt = _time.GetUtcNow().UtcDateTime,
        };
        var attempt = new AlertDeliveryAttempt
        {
            Id = Guid.NewGuid(),
            AlertId = alert.Id,
            ChannelId = channel.Id,
            AttemptNumber = 1,
            Status = AlertDeliveryStatus.Pending,
            CreatedAt = _time.GetUtcNow().UtcDateTime,
        };
        db.Alerts.Add(alert);
        db.AlertChannels.Add(channel);
        db.AlertDeliveryAttempts.Add(attempt);
        await db.SaveChangesAsync();
        return attempt.Id;
    }

    private sealed class StubChannel : IAlertChannel
    {
        public StubChannel(string type) { ChannelType = type; }
        public string ChannelType { get; }
        public DeliveryResult NextResult { get; set; } =
            new(Success: true, Error: null);
        public int Invocations { get; private set; }

        public Task<DeliveryResult> SendAsync(
            Alert alert, AlertChannel channel, CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(NextResult);
        }
    }
}
