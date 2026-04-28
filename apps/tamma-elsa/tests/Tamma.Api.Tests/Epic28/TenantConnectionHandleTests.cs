using FluentAssertions;
using Npgsql;
using NUnit.Framework;
using Tamma.Data.Pooling;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-4 AC4 — unit tests for <see cref="TenantConnectionHandle"/>
/// in isolation (no LRU resolver involvement). Exercises the ref-count
/// state machine: acquire / dispose / pending-dispose interactions,
/// callback-on-final-release semantics, double-dispose tolerance, and
/// post-disposal access errors.
///
/// <para>The handle wraps an <see cref="NpgsqlDataSource"/> built from a
/// stub connection string — we never open a connection so no Postgres
/// is required. The data source itself is disposed in
/// <see cref="TearDown"/> to avoid socket leaks across tests.</para>
/// </summary>
[TestFixture]
public class TenantConnectionHandleTests
{
    private NpgsqlDataSource _ds = null!;

    [SetUp]
    public void SetUp()
    {
        _ds = NpgsqlDataSource.Create(
            "Host=stub.invalid;Port=5432;Database=t;Username=u;Password=p");
    }

    [TearDown]
    public void TearDown() => _ds.Dispose();

    private static TenantConnectionHandle NewHandle(
        NpgsqlDataSource ds,
        Action<TenantConnectionHandle>? onDisposed = null) =>
        new(Guid.NewGuid(), ds, onDisposed);

    [Test]
    public void Initial_Handle_Has_RefCount_One_And_Not_Pending()
    {
        var h = NewHandle(_ds);
        h.RefCount.Should().Be(1);
        h.IsPendingDispose.Should().BeFalse();
        h.DataSource.Should().BeSameAs(_ds);
    }

    [Test]
    public async Task DisposeAsync_Without_Pending_Does_Not_Invoke_Callback()
    {
        var fired = 0;
        var h = NewHandle(_ds, _ => Interlocked.Increment(ref fired));

        await h.DisposeAsync();

        fired.Should().Be(0,
            "callback only fires when MarkPendingDispose was set");
        h.RefCount.Should().Be(0);
    }

    [Test]
    public async Task MarkPendingDispose_Then_Final_Release_Fires_Callback()
    {
        var fired = 0;
        var h = NewHandle(_ds, _ => Interlocked.Increment(ref fired));
        h.MarkPendingDispose();

        await h.DisposeAsync();

        fired.Should().Be(1, "callback fires on the last lease release");
    }

    [Test]
    public async Task Sibling_Handle_Shares_RefCount_And_Defers_Callback()
    {
        var fired = 0;
        var h = NewHandle(_ds, _ => Interlocked.Increment(ref fired));
        var sibling = h.Acquire();
        h.MarkPendingDispose();

        await h.DisposeAsync();
        fired.Should().Be(0,
            "callback must NOT fire while sibling lease is open");

        await sibling.DisposeAsync();
        fired.Should().Be(1, "final lease release fires callback exactly once");
    }

    [Test]
    public async Task Multiple_Siblings_Defer_Until_All_Released()
    {
        var fired = 0;
        var h = NewHandle(_ds, _ => Interlocked.Increment(ref fired));
        var siblings = Enumerable.Range(0, 5)
            .Select(_ => h.Acquire())
            .ToArray();
        h.MarkPendingDispose();

        await h.DisposeAsync();
        for (var i = 0; i < 4; i++)
            await siblings[i].DisposeAsync();
        fired.Should().Be(0,
            "callback waits for the LAST outstanding sibling");

        await siblings[4].DisposeAsync();
        fired.Should().Be(1);
    }

    [Test]
    public async Task Double_Dispose_Is_NoOp()
    {
        var fired = 0;
        var h = NewHandle(_ds, _ => Interlocked.Increment(ref fired));
        h.MarkPendingDispose();

        await h.DisposeAsync();
        await h.DisposeAsync();   // second dispose
        await h.DisposeAsync();   // third dispose

        fired.Should().Be(1,
            "callback fires exactly once even under repeat-disposal");
    }

    [Test]
    public void DataSource_Access_After_Dispose_Throws()
    {
        var h = NewHandle(_ds);
        h.DisposeAsync().AsTask().Wait();

        Action act = () => _ = h.DataSource;
        act.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public async Task Acquire_After_Final_Release_Throws()
    {
        var h = NewHandle(_ds);
        await h.DisposeAsync();

        Action act = () => h.Acquire();
        act.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public void MarkPendingDispose_Returns_PostMark_RefCount()
    {
        var h = NewHandle(_ds);
        var sib1 = h.Acquire();
        var sib2 = h.Acquire();

        var count = h.MarkPendingDispose();
        count.Should().Be(3,
            "1 implicit lease + 2 siblings");

        // Cleanup so the test doesn't leak the data source
        sib1.DisposeAsync().AsTask().Wait();
        sib2.DisposeAsync().AsTask().Wait();
        h.DisposeAsync().AsTask().Wait();
    }

    [Test]
    public async Task Concurrent_Acquire_Are_All_Counted()
    {
        var fired = 0;
        var h = NewHandle(_ds, _ => Interlocked.Increment(ref fired));

        const int parallel = 50;
        var siblings = await Task.WhenAll(
            Enumerable.Range(0, parallel).Select(_ => Task.Run(() => h.Acquire())));

        h.MarkPendingDispose();
        await h.DisposeAsync();

        // Drop them all concurrently too.
        await Task.WhenAll(siblings.Select(s => s.DisposeAsync().AsTask()));

        fired.Should().Be(1,
            "concurrent races must not double-fire the callback");
    }
}
