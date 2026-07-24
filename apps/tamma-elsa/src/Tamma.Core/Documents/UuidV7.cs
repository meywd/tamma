using System.Security.Cryptography;

namespace Tamma.Core.Documents;

/// <summary>
/// A minimal RFC 9562 UUID version 7 generator (Design Decision D9). net8 has no
/// <c>Guid.CreateVersion7</c>, so document identities are minted here: a 48-bit
/// big-endian Unix-millisecond timestamp prefix (time-ordered), the version
/// nibble 7, the RFC 4122 variant bits, and a cryptographically random tail.
///
/// <para>
/// The <see cref="Guid"/> is constructed big-endian so both its byte
/// representation and its string form sort in creation order. If .NET is ever
/// bumped to 9+, swap the body for <c>Guid.CreateVersion7</c> behind this same
/// static surface.
/// </para>
///
/// <para><b>Intra-millisecond monotonicity.</b> A bare v7 whose sub-timestamp
/// bytes are pure random is NOT monotonic within a single millisecond — two ids
/// minted in the same ms sort by their random tails, not their creation order.
/// Consumers that read back "in id order" and expect enqueue order (the
/// <c>channel_outbox</c> FIFO replay, document lineage) then flake. So the
/// process-time <see cref="NewGuid()"/> path applies RFC 9562 §6.2 "fixed-length
/// dedicated counter": a lock-guarded 12-bit counter occupies the 4 low bits of
/// byte 6 and all of byte 7 — immediately after the timestamp and ahead of the
/// random tail, so <c>(timestamp, counter)</c> dominates the sort. The counter
/// resets each new millisecond and increments within one; on the (4096/ms/process)
/// overflow it rolls the pinned timestamp forward a millisecond. The clock is also
/// pinned monotonic (a backwards system clock cannot lower an already-issued
/// timestamp). Result: successive <see cref="NewGuid()"/> calls are strictly
/// increasing.</para>
///
/// <para>The explicit-<see cref="DateTimeOffset"/> overload stays pure (random
/// tail, no shared counter) so callers that stamp an arbitrary timestamp — e.g.
/// <c>DocumentEnvelope</c> — remain deterministic and order purely by the
/// timestamp they supply.</para>
/// </summary>
public static class UuidV7
{
    // Monotonic-counter state for the process-time NewGuid() path. Guarded by
    // _lock. _lastUnixMs is pinned non-decreasing; _counter is the 12-bit
    // intra-millisecond sequence packed into byte6[low nibble] + byte7.
    private static readonly object _lock = new();
    private static long _lastUnixMs = -1;
    private static int _counter;
    private const int CounterMax = 0x0FFF; // 12 bits → 4096 ids per ms per process

    /// <summary>
    /// Mint a new UUID v7 stamped with the current UTC time. Successive calls are
    /// strictly increasing (monotonic even within a millisecond) — see the type
    /// remarks.
    /// </summary>
    public static Guid NewGuid()
    {
        long unixMs;
        int counter;

        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now > _lastUnixMs)
            {
                // Fresh millisecond: adopt it and restart the counter.
                _lastUnixMs = now;
                _counter = 0;
            }
            else if (_counter >= CounterMax)
            {
                // Counter exhausted for this ms (or the clock went backwards and
                // we're pinned): roll into the next ms to stay strictly ordered.
                _lastUnixMs++;
                _counter = 0;
            }
            else
            {
                // Same (or pinned) millisecond: advance the sequence.
                _counter++;
            }

            unixMs = _lastUnixMs;
            counter = _counter;
        }

        return Build(unixMs, counter);
    }

    /// <summary>
    /// Mint a UUID v7 stamped with <paramref name="timestamp"/>. Overload exists
    /// so the time-ordering property is deterministically testable; it is pure
    /// (random sub-timestamp bytes, no shared counter) and orders solely by the
    /// supplied timestamp.
    /// </summary>
    public static Guid NewGuid(DateTimeOffset timestamp)
    {
        Span<byte> bytes = stackalloc byte[16];

        WriteTimestamp(bytes, timestamp.ToUnixTimeMilliseconds());

        // Bytes 6..15: random.
        RandomNumberGenerator.Fill(bytes[6..]);

        // Version nibble 7 in the high nibble of byte 6.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);

        // RFC 4122 variant (10xx) in the high bits of byte 8.
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        // Construct big-endian so byte- and string-comparison are both
        // time-ordered (net8's Guid(ReadOnlySpan<byte>, bool) ctor).
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>
    /// Build a v7 with a monotonic 12-bit <paramref name="counter"/> in
    /// byte6[low nibble]+byte7 (right after the timestamp, before the random tail)
    /// so <c>(timestamp, counter)</c> dominates the big-endian sort.
    /// </summary>
    private static Guid Build(long unixMs, int counter)
    {
        Span<byte> bytes = stackalloc byte[16];

        WriteTimestamp(bytes, unixMs);

        // Bytes 8..15 random (the counter fully owns bytes 6..7, so only the tail
        // needs entropy; ties on (timestamp, counter) never occur in a process).
        RandomNumberGenerator.Fill(bytes[8..]);

        // Byte 6: version nibble 7 | high 4 bits of the 12-bit counter.
        bytes[6] = (byte)(0x70 | ((counter >> 8) & 0x0F));
        // Byte 7: low 8 bits of the counter.
        bytes[7] = (byte)(counter & 0xFF);

        // RFC 4122 variant (10xx) in the high bits of byte 8.
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes, bigEndian: true);
    }

    private static void WriteTimestamp(Span<byte> bytes, long unixMs)
    {
        // Bytes 0..5: 48-bit big-endian Unix-millisecond timestamp.
        bytes[0] = (byte)(unixMs >> 40);
        bytes[1] = (byte)(unixMs >> 32);
        bytes[2] = (byte)(unixMs >> 24);
        bytes[3] = (byte)(unixMs >> 16);
        bytes[4] = (byte)(unixMs >> 8);
        bytes[5] = (byte)unixMs;
    }
}
