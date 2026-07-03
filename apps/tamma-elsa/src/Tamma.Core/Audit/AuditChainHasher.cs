using System.Security.Cryptography;

namespace Tamma.Core.Audit;

/// <summary>
/// Story 37-2 — composes a record's hash from the prior record's hash and the
/// canonical bytes: <c>record_hash = SHA-256( prev_hash ‖ canonical(record) )</c>.
///
/// <para>The previous hash is folded in as its RAW 32 bytes (decoded from the
/// stored lowercase-hex), so the chain binds to the actual prior digest rather
/// than to its text encoding. The result is returned as lowercase-hex to match
/// the reserved <c>record_hash</c>/<c>prev_hash</c> varchar columns.</para>
/// </summary>
public static class AuditChainHasher
{
    /// <summary>Length in hex chars of a SHA-256 digest.</summary>
    public const int HexLength = 64;

    /// <summary>
    /// Compose the next hash from the previous hash (lowercase-hex) and the
    /// canonical bytes. Returns lowercase-hex (64 chars).
    /// </summary>
    public static string ComposeHex(string prevHashHex, byte[] canonical)
    {
        ArgumentNullException.ThrowIfNull(prevHashHex);
        ArgumentNullException.ThrowIfNull(canonical);

        var prev = DecodeHex(prevHashHex);
        using var sha = SHA256.Create();
        sha.TransformBlock(prev, 0, prev.Length, null, 0);
        sha.TransformFinalBlock(canonical, 0, canonical.Length);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>
    /// Decode a lowercase/uppercase-hex 32-byte hash. Throws on malformed input
    /// so a corrupt <c>prev_hash</c> surfaces loudly rather than silently
    /// producing a wrong (but plausible) chain link.
    /// </summary>
    internal static byte[] DecodeHex(string hex)
    {
        if (hex.Length != HexLength)
        {
            throw new FormatException(
                $"audit chain hash must be {HexLength} hex chars, got {hex.Length}.");
        }
        return Convert.FromHexString(hex);
    }
}
