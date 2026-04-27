using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using Tamma.Data.Pooling;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.TenantStatus;

/// <summary>
/// Round-2 follow-up — verifies the
/// <see cref="PostgresTenantStatusInvalidationBus"/> publishes a
/// well-formed NOTIFY that is observable by an independent LISTENer
/// on the same Postgres cluster. Uses a real Postgres 17 container
/// because Postgres LISTEN/NOTIFY semantics aren't faithfully
/// emulated by EF InMemory or any in-process double.
/// </summary>
[TestFixture]
public class PostgresTenantStatusInvalidationBusTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tenant_status_bus_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [Test]
    public async Task PublishAsync_Delivers_Payload_To_Independent_Listener()
    {
        // Use TWO distinct NpgsqlDataSources to simulate two pods:
        // one for the bus (publish), one for the listener (subscribe).
        // Same Postgres cluster — that's the point: pods share the
        // CP cluster but not their pool state.
        await using var publisherDataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();
        await using var listenerDataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();

        var bus = new PostgresTenantStatusInvalidationBus(
            publisherDataSource,
            NullLogger<PostgresTenantStatusInvalidationBus>.Instance);

        // Open a long-lived listen connection from a separate data
        // source (different physical connection from the publisher's
        // pool). This is the closest thing to "another pod" inside a
        // single test process.
        await using var listenConn = await listenerDataSource.OpenConnectionAsync();
        var receivedPayloads = new List<string>();
        var notificationReceived = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        listenConn.Notification += (_, e) =>
        {
            receivedPayloads.Add(e.Payload);
            // Surface the first arrival so the test can complete
            // deterministically — additional arrivals (re-runs of
            // the same test fixture, etc.) just append.
            notificationReceived.TrySetResult(e.Payload);
        };

        await using (var listenCmd = listenConn.CreateCommand())
        {
            listenCmd.CommandText =
                $"LISTEN {PostgresTenantStatusInvalidationBus.ChannelName}";
            await listenCmd.ExecuteNonQueryAsync();
        }

        // Drive the listener's wait loop on a background task so the
        // foreground can publish + observe the result. WaitAsync
        // pumps the connection's notification handler when a NOTIFY
        // is delivered.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waitTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await listenConn.WaitAsync(cts.Token);
                }
            }
            catch (OperationCanceledException) { /* expected on test exit */ }
        });

        // Give Postgres a beat to register the LISTEN before we publish.
        await Task.Delay(50);

        var tenantId = Guid.NewGuid();
        await bus.PublishAsync(tenantId);

        var arrived = await Task.WhenAny(
            notificationReceived.Task,
            Task.Delay(TimeSpan.FromSeconds(5)));

        arrived.Should().Be(notificationReceived.Task,
            "the listener must receive the NOTIFY within 5s — Postgres LISTEN/NOTIFY is millisecond-tier");

        cts.Cancel();
        await waitTask;

        receivedPayloads.Should().Contain(
            tenantId.ToString("D"),
            "payload must be the tenant id formatted as a hyphenated Guid");
    }

    [Test]
    public async Task PublishAsync_Sends_Multiple_Distinct_Payloads()
    {
        await using var publisherDataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();
        await using var listenerDataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();

        var bus = new PostgresTenantStatusInvalidationBus(
            publisherDataSource,
            NullLogger<PostgresTenantStatusInvalidationBus>.Instance);

        await using var listenConn = await listenerDataSource.OpenConnectionAsync();
        var received = new List<string>();
        var allReceived = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        const int expectedCount = 5;
        listenConn.Notification += (_, e) =>
        {
            lock (received)
            {
                received.Add(e.Payload);
                if (received.Count >= expectedCount) allReceived.TrySetResult(true);
            }
        };

        await using (var listenCmd = listenConn.CreateCommand())
        {
            listenCmd.CommandText =
                $"LISTEN {PostgresTenantStatusInvalidationBus.ChannelName}";
            await listenCmd.ExecuteNonQueryAsync();
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waitTask = Task.Run(async () =>
        {
            try { while (!cts.IsCancellationRequested) await listenConn.WaitAsync(cts.Token); }
            catch (OperationCanceledException) { }
        });

        await Task.Delay(50);

        var ids = Enumerable.Range(0, expectedCount).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids)
        {
            await bus.PublishAsync(id);
        }

        var done = await Task.WhenAny(
            allReceived.Task,
            Task.Delay(TimeSpan.FromSeconds(5)));

        done.Should().Be(allReceived.Task,
            "all {0} NOTIFY messages should arrive within 5s", expectedCount);

        cts.Cancel();
        await waitTask;

        lock (received)
        {
            received.Should().Contain(ids.Select(i => i.ToString("D")));
        }
    }

    [Test]
    public async Task Publisher_Receives_Its_Own_Notification_Self_Notify_Is_Idempotent()
    {
        // Postgres delivers NOTIFY to every active LISTENer including
        // the publishing session if it happens to be LISTENing on the
        // same channel. Re-invalidating an already-evicted entry is
        // documented as idempotent — verify the wire-level behaviour
        // is what the design assumes.
        await using var dataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();
        var bus = new PostgresTenantStatusInvalidationBus(
            dataSource, NullLogger<PostgresTenantStatusInvalidationBus>.Instance);

        // Open a connection that BOTH listens AND publishes (simulates
        // a pod hosting both bus + listener over the same data source).
        await using var conn = await dataSource.OpenConnectionAsync();
        var seen = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        conn.Notification += (_, e) => seen.TrySetResult(e.Payload);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                $"LISTEN {PostgresTenantStatusInvalidationBus.ChannelName}";
            await cmd.ExecuteNonQueryAsync();
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waitTask = Task.Run(async () =>
        {
            try { while (!cts.IsCancellationRequested) await conn.WaitAsync(cts.Token); }
            catch (OperationCanceledException) { }
        });

        await Task.Delay(50);

        var tenantId = Guid.NewGuid();
        await bus.PublishAsync(tenantId);

        var arrived = await Task.WhenAny(
            seen.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        arrived.Should().Be(seen.Task,
            "publisher's own LISTEN session sees the NOTIFY too — that's expected and idempotent");
        seen.Task.Result.Should().Be(tenantId.ToString("D"));

        cts.Cancel();
        await waitTask;
    }
}
