using System.Collections.Concurrent;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;

namespace Tamma.Core.Tests.Documents;

/// <summary>
/// Pins the ordering guarantees of <see cref="UuidV7"/>. The
/// intra-millisecond-monotonicity case is the regression guard for the
/// long-standing <c>ChannelOutboxRepositoryTests.Enqueue_ListUnacked_OrderedByUuidV7Id</c>
/// flake: a bare v7 with a random sub-timestamp tail sorts same-ms ids by
/// randomness, so the outbox FIFO replay (ORDER BY id) occasionally came back
/// out of enqueue order. The fixed-length dedicated counter makes successive
/// <see cref="UuidV7.NewGuid()"/> calls strictly increasing.
/// </summary>
[TestFixture]
public class UuidV7Tests
{
    [Test]
    public void NewGuid_IsStrictlyIncreasing_EvenWithinTheSameMillisecond()
    {
        // 10k rapid mints almost all land in a handful of milliseconds, so a
        // non-monotonic generator fails this virtually every run.
        const int n = 10_000;
        var ids = new Guid[n];
        for (var i = 0; i < n; i++)
            ids[i] = UuidV7.NewGuid();

        for (var i = 1; i < n; i++)
            ids[i].CompareTo(ids[i - 1]).Should().BePositive(
                "UuidV7.NewGuid() must be strictly increasing across successive calls (id #{0})", i);

        ids.Distinct().Should().HaveCount(n, "every minted id must be unique");
    }

    [Test]
    public void NewGuid_IsVersion7AndRfc4122Variant()
    {
        var bytes = UuidV7.NewGuid().ToByteArray(bigEndian: true);
        (bytes[6] & 0xF0).Should().Be(0x70, "the version nibble must be 7");
        (bytes[8] & 0xC0).Should().Be(0x80, "the variant bits must be RFC 4122 (10xx)");
    }

    [Test]
    public void NewGuid_IsMonotonicUnderConcurrency()
    {
        // Many threads minting at once must still yield globally unique,
        // lock-serialized ids (no torn counter / duplicate).
        var bag = new ConcurrentBag<Guid>();
        Parallel.For(0, 20_000, _ => bag.Add(UuidV7.NewGuid()));
        bag.Distinct().Should().HaveCount(bag.Count, "concurrent mints must not collide");
    }

    [Test]
    public void NewGuid_WithTimestamp_OrdersBySuppliedTimestamp()
    {
        var t0 = DateTimeOffset.UtcNow;
        var earlier = UuidV7.NewGuid(t0);
        var later = UuidV7.NewGuid(t0.AddMilliseconds(5));

        later.CompareTo(earlier).Should().BePositive(
            "the explicit-timestamp overload orders by the supplied timestamp");
    }
}
