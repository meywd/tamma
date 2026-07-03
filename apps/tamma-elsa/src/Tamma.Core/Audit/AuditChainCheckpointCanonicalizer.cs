using System.Buffers.Binary;
using System.Text;

namespace Tamma.Core.Audit;

/// <summary>
/// Story 37-2 (AC5) — builds the deterministic preimage a checkpoint signature
/// is computed over: <c>canonical(scope ‖ head_sequence ‖ head_hash ‖ signed_at)</c>.
/// Shared by the signer (write) and the verifier (read) so both sides sign /
/// validate byte-identical input. Same length-prefixed, invariant-culture,
/// version-pinned discipline as <see cref="AuditRecordCanonicalizer"/>.
/// </summary>
public static class AuditChainCheckpointCanonicalizer
{
    /// <summary>Deterministic bytes to sign / verify for one checkpoint anchor.</summary>
    public static byte[] PreimageBytes(
        string scope, Guid? tenantId, long headSequence, string headHashHex, DateTime signedAt)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(headHashHex);

        using var ms = new MemoryStream(128);
        ms.WriteByte(AuditChainGenesis.CanonicalVersion);
        WriteString(ms, scope);
        WriteString(ms, tenantId?.ToString("D") ?? string.Empty);
        WriteInt64(ms, headSequence);
        WriteString(ms, headHashHex);
        WriteString(ms, AuditRecordCanonicalizer.FormatTimestamp(signedAt));
        return ms.ToArray();
    }

    private static void WriteString(Stream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, bytes.Length);
        s.Write(len);
        s.Write(bytes, 0, bytes.Length);
    }

    private static void WriteInt64(Stream s, long value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, value);
        s.Write(b);
    }
}
