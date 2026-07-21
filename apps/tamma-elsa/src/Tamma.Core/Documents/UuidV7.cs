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
/// </summary>
public static class UuidV7
{
    /// <summary>Mint a new UUID v7 stamped with the current UTC time.</summary>
    public static Guid NewGuid() => NewGuid(DateTimeOffset.UtcNow);

    /// <summary>
    /// Mint a UUID v7 stamped with <paramref name="timestamp"/>. Overload exists
    /// so the time-ordering property is deterministically testable.
    /// </summary>
    public static Guid NewGuid(DateTimeOffset timestamp)
    {
        Span<byte> bytes = stackalloc byte[16];

        // Bytes 0..5: 48-bit big-endian Unix-millisecond timestamp.
        long unixMs = timestamp.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(unixMs >> 40);
        bytes[1] = (byte)(unixMs >> 32);
        bytes[2] = (byte)(unixMs >> 24);
        bytes[3] = (byte)(unixMs >> 16);
        bytes[4] = (byte)(unixMs >> 8);
        bytes[5] = (byte)unixMs;

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
}
