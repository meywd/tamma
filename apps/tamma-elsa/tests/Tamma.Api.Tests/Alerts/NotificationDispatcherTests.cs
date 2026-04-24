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
        MaxAttempts = 6,
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

    // ── Backoff schedule coverage ───────────────────────────────
    //
    // Regression guard for the "dead delay entries" bug: before the
    // fix the default MaxAttempts=5 short-circuited the schedule
    // lookup before idx=3 (15m) and idx=4 (30m) were ever reached,
    // so those two entries were effectively dead code. Raising the
    // default to 6 unlocks idx=3; we also assert idx=4 here (with a
    // larger MaxAttempts so the terminal gate doesn't swallow it) to
    // pin the schedule contract as "5 delays, explicitly
    // [30s, 2m, 5m, 15m, 30m]".
    //
    // The parameter grid: pre-seed AttemptNumber = scheduleIndex + 1
    // (so the fail-and-increment produces post = scheduleIndex + 2,
    // which in ComputeNextAttempt maps to idx = scheduleIndex).
    [TestCase(0, 30.0)]   // 30 seconds
    [TestCase(1, 120.0)]  // 2 minutes
    [TestCase(2, 300.0)]  // 5 minutes
    [TestCase(3, 900.0)]  // 15 minutes
    [TestCase(4, 1800.0)] // 30 minutes
    public async Task DispatchOnceAsync_OnFailure_UsesExpectedBackoffDelay(
        int scheduleIndex, double expectedDelaySeconds)
    {
        var attemptId = await SeedPendingAttemptAsync();
        var preSeedAttemptNumber = scheduleIndex + 1;
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var row = await db.AlertDeliveryAttempts.FirstAsync(a => a.Id == attemptId);
            row.AttemptNumber = preSeedAttemptNumber;
            row.Status = "failed";
            row.NextAttemptAt = _time.GetUtcNow().UtcDateTime.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        // Use a generous MaxAttempts so the terminal gate doesn't
        // override the schedule lookup for the final (idx=4) entry.
        // This test's purpose is to prove each of the 5 schedule
        // entries maps to the correct delay when reached.
        var options = DefaultOptions();
        options.MaxAttempts = 100;

        _stubChannel.NextResult = new DeliveryResult(false, "transient");
        await NewDispatcher(options).DispatchOnceAsync(default);

        var refreshed = await ReadAttemptAsync();
        refreshed.AttemptNumber.Should().Be(preSeedAttemptNumber + 1);
        refreshed.Status.Should().Be("failed");
        refreshed.NextAttemptAt.Should().NotBeNull();
        refreshed.NextAttemptAt!.Value
            .Should().BeCloseTo(
                _time.GetUtcNow().UtcDateTime.AddSeconds(expectedDelaySeconds),
                TimeSpan.FromSeconds(1),
                $"schedule entry at idx={scheduleIndex} must map to " +
                $"{expectedDelaySeconds}s delay");
    }

    [Test]
    public async Task DispatchOnceAsync_AfterMaxAttempts_NextAttemptAt_IsMaxValue()
    {
        var attemptId = await SeedPendingAttemptAsync();
        // Arrange: with the new default of MaxAttempts=6, put the row at
        // attempt 5 so the next failure is the 6th (terminal) and the
        // eligibility gate still lets it through (AttemptNumber <
        // MaxAttempts, i.e. 5 < 6).
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var row = await db.AlertDeliveryAttempts.FirstAsync(a => a.Id == attemptId);
            row.AttemptNumber = 5;
            row.Status = "failed";
            row.NextAttemptAt = _time.GetUtcNow().UtcDateTime.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        _stubChannel.NextResult = new DeliveryResult(false, "still broken");
        await NewDispatcher().DispatchOnceAsync(default);

        var refreshed = await ReadAttemptAsync();
        refreshed.AttemptNumber.Should().Be(6);
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
            // With MaxAttempts=6, a terminal row sits at AttemptNumber=6.
            row.AttemptNumber = 6;
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
