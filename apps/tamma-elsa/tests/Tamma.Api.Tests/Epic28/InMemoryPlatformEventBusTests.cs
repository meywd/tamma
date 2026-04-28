using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.PlatformEvents;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-6 — unit tests for <see cref="InMemoryPlatformEventBus"/>.
/// Exercises the publish/subscribe contract: filter by type prefix,
/// sequential dispatch, exception isolation, subscriber dispose, and the
/// AppendAndPublishAsync convenience seam (including the dedup no-op
/// path where publication is skipped).
/// </summary>
[TestFixture]
public class InMemoryPlatformEventBusTests
{
    private InMemoryPlatformEventBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = new InMemoryPlatformEventBus(NullLogger<InMemoryPlatformEventBus>.Instance);
    }

    private static PlatformEvent NewEvent(string type) => new()
    {
        Type = type,
        Tags = "{}",
        Metadata = """{"eventSource":"system"}""",
        Data = "{}",
    };

    // ── Subscribe / Publish ───────────────────────────────────────────────────

    [Test]
    public async Task Publish_DeliversTo_AllUnfilteredSubscribers()
    {
        var seenA = new List<string>();
        var seenB = new List<string>();
        using var subA = _bus.Subscribe((e, _) => { seenA.Add(e.Type); return Task.CompletedTask; });
        using var subB = _bus.Subscribe((e, _) => { seenB.Add(e.Type); return Task.CompletedTask; });

        await _bus.PublishAsync(NewEvent("TENANT.CREATED"));

        seenA.Should().ContainSingle().Which.Should().Be("TENANT.CREATED");
        seenB.Should().ContainSingle().Which.Should().Be("TENANT.CREATED");
    }

    [Test]
    public async Task Publish_FiltersByTypePrefix()
    {
        var tenantOnly = new List<string>();
        var userOnly = new List<string>();
        using var s1 = _bus.Subscribe("TENANT.",
            (e, _) => { tenantOnly.Add(e.Type); return Task.CompletedTask; });
        using var s2 = _bus.Subscribe("USER.",
            (e, _) => { userOnly.Add(e.Type); return Task.CompletedTask; });

        await _bus.PublishAsync(NewEvent("TENANT.CREATED"));
        await _bus.PublishAsync(NewEvent("USER.REGISTERED"));
        await _bus.PublishAsync(NewEvent("ORCHESTRATOR.TICK"));

        tenantOnly.Should().BeEquivalentTo(new[] { "TENANT.CREATED" });
        userOnly.Should().BeEquivalentTo(new[] { "USER.REGISTERED" });
    }

    [Test]
    public async Task Publish_SubscriberException_IsLogged_DoesNotPropagate_DoesNotStopOthers()
    {
        var goodSeen = 0;
        using var bad = _bus.Subscribe((_, _) => throw new InvalidOperationException("boom"));
        using var good = _bus.Subscribe((_, _) => { goodSeen++; return Task.CompletedTask; });

        var act = async () => await _bus.PublishAsync(NewEvent("TENANT.CREATED"));

        await act.Should().NotThrowAsync(
            "subscriber exceptions never surface to the publisher");
        goodSeen.Should().Be(1,
            "the well-behaved subscriber must still receive the event");
    }

    [Test]
    public async Task Subscribe_ReturnedTokenDispose_RemovesHandler()
    {
        var seen = 0;
        var token = _bus.Subscribe((_, _) => { seen++; return Task.CompletedTask; });

        await _bus.PublishAsync(NewEvent("E1"));
        seen.Should().Be(1);

        token.Dispose();
        await _bus.PublishAsync(NewEvent("E2"));

        seen.Should().Be(1, "disposed subscription must not receive further events");
        _bus.SubscriberCount.Should().Be(0);
    }

    [Test]
    public void SubscriberCount_TracksRegistrationAndDispose()
    {
        _bus.SubscriberCount.Should().Be(0);

        var a = _bus.Subscribe((_, _) => Task.CompletedTask);
        var b = _bus.Subscribe("X.", (_, _) => Task.CompletedTask);

        _bus.SubscriberCount.Should().Be(2);

        a.Dispose();
        _bus.SubscriberCount.Should().Be(1);

        b.Dispose();
        _bus.SubscriberCount.Should().Be(0);
    }

    [Test]
    public void Subscribe_NullHandler_Throws()
    {
        var act1 = () => _bus.Subscribe(null!);
        var act2 = () => _bus.Subscribe("PREFIX.", null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Publish_NullEvent_Throws()
    {
        var act = async () => await _bus.PublishAsync(null!);
        act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task Publish_DeliversSequentially_NotConcurrently()
    {
        // The bus contract guarantees sequential dispatch (subscriber N+1
        // does not start before subscriber N completes). It does NOT
        // guarantee a specific ordering across subscriptions — the
        // backing collection is concurrent so insertion order is not
        // preserved on enumeration.
        var inFlight = 0;
        var maxObservedConcurrency = 0;
        var seen = 0;

        async Task Handler(PlatformEvent _, CancellationToken __)
        {
            var current = Interlocked.Increment(ref inFlight);
            // Track the highest concurrency observed across all subscribers.
            int snapshot;
            do
            {
                snapshot = Volatile.Read(ref maxObservedConcurrency);
                if (current <= snapshot) break;
            } while (Interlocked.CompareExchange(
                ref maxObservedConcurrency, current, snapshot) != snapshot);

            await Task.Delay(15);
            Interlocked.Increment(ref seen);
            Interlocked.Decrement(ref inFlight);
        }

        using var s1 = _bus.Subscribe(Handler);
        using var s2 = _bus.Subscribe(Handler);
        using var s3 = _bus.Subscribe(Handler);

        await _bus.PublishAsync(NewEvent("E1"));

        seen.Should().Be(3, "all subscribers must receive the event");
        maxObservedConcurrency.Should().Be(1,
            "the bus contract requires sequential dispatch — " +
            "two handlers must never run at the same time for one event");
    }

    // ── AppendAndPublishAsync ────────────────────────────────────────────────

    [Test]
    public async Task AppendAndPublish_PersistsThenPublishes_OnSuccess()
    {
        using var db = NewCpContext();
        var repo = new PlatformEventRepository(db);
        var seen = new List<Guid>();
        using var sub = _bus.Subscribe((e, _) => { seen.Add(e.Id); return Task.CompletedTask; });

        var persisted = await _bus.AppendAndPublishAsync(repo, NewEvent("TENANT.CREATED"));

        persisted.Should().NotBeNull();
        persisted!.Id.Should().NotBe(Guid.Empty);
        seen.Should().ContainSingle().Which.Should().Be(persisted.Id);
    }

    [Test]
    public async Task AppendAndPublish_NullRepository_Throws()
    {
        var act = async () => await _bus.AppendAndPublishAsync(null!, NewEvent("E1"));
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task AppendAndPublish_NullEvent_Throws()
    {
        using var db = NewCpContext();
        var repo = new PlatformEventRepository(db);

        var act = async () => await _bus.AppendAndPublishAsync(repo, null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task AppendAndPublish_DedupNoOp_SkipsRepublish()
    {
        // Build a stub repository that returns null (dedup hit) on append.
        var stub = new StubDedupRepository();
        var seen = 0;
        using var sub = _bus.Subscribe((_, _) => { seen++; return Task.CompletedTask; });

        var result = await _bus.AppendAndPublishAsync(stub, NewEvent("TENANT.PROVISION.STEP_1"));

        result.Should().BeNull("a null return from AppendAsync is the dedup signal");
        seen.Should().Be(0,
            "subscribers must NOT see the event again — they saw it on the first append");
    }

    private static ControlPlaneDbContext NewCpContext()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new ControlPlaneDbContext(options);
    }

    /// <summary>
    /// Stand-in for the partial-unique step-dedup index hit on Postgres —
    /// PlatformEventRepository.AppendAsync returns null in that case.
    /// </summary>
    private sealed class StubDedupRepository : IPlatformEventRepository
    {
        public Task<PlatformEvent?> AppendAsync(PlatformEvent evt, CancellationToken ct = default)
            => Task.FromResult<PlatformEvent?>(null);

        public Task<PlatformEvent?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<PlatformEvent?>(null);

        public Task<IReadOnlyList<PlatformEvent>> QueryAsync(
            Guid? tenantId = null,
            Guid? userId = null,
            string? typePrefix = null,
            DateTime? since = null,
            bool includePlatformWide = false,
            int limit = 100,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlatformEvent>>(Array.Empty<PlatformEvent>());
    }
}
