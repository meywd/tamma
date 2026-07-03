using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Tamma.Core.Audit;

/// <summary>
/// Story 37-2 (AC2) — produces the deterministic, culture-invariant, byte-stable
/// serialization of an audit record that the hash-chain is computed over.
///
/// <para><b>Load-bearing determinism.</b> Verification only works if
/// <c>canonical(record)</c> is byte-identical at write time and at every future
/// verify, on every machine. This class therefore:</para>
/// <list type="bullet">
///   <item><description>Uses a FIXED field order (never reflection / dictionary
///     enumeration order).</description></item>
///   <item><description>Length-prefixes every field so no two distinct records
///     can ever produce the same byte stream by concatenation ambiguity
///     (e.g. actor <c>"ab"</c>+target <c>"c"</c> vs actor <c>"a"</c>+target
///     <c>"bc"</c>).</description></item>
///   <item><description>Formats timestamps as UTC ISO-8601 with millisecond
///     precision (<c>yyyy-MM-ddTHH:mm:ss.fffZ</c>) under
///     <see cref="CultureInfo.InvariantCulture"/> — never
///     <c>DateTime.ToString()</c> with an ambient locale.</description></item>
///   <item><description>Prefixes the stream with
///     <see cref="AuditChainGenesis.CanonicalVersion"/> so a future format
///     change is a new, detectable version rather than a silent break.</description></item>
/// </list>
///
/// <para>The output is intentionally NOT JSON — JSON key ordering + whitespace +
/// number formatting are exactly the non-determinism this must avoid.</para>
/// </summary>
public static class AuditRecordCanonicalizer
{
    private const byte FieldNull = 0x00;
    private const byte FieldPresent = 0x01;

    /// <summary>
    /// Serialize the record's stable identity fields (everything EXCEPT the
    /// chain-linkage hashes) into a deterministic byte array.
    /// </summary>
    public static byte[] ToBytes(AuditChainRecordView record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var ms = new MemoryStream(256);

        // Format version first — pins the layout so a v2 canonicalizer never
        // collides with a v1 stream.
        ms.WriteByte(AuditChainGenesis.CanonicalVersion);

        WriteString(ms, record.Discriminator);
        WriteGuid(ms, record.Id);
        WriteNullableGuid(ms, record.TenantId);
        WriteNullableGuid(ms, record.UserId);
        WriteString(ms, record.ActionCode);
        WriteString(ms, record.Category);
        WriteString(ms, record.Severity);
        WriteNullableGuid(ms, record.ActorUserId);
        WriteNullableString(ms, record.ActorEmailSnapshot);
        WriteNullableString(ms, record.TargetType);
        WriteNullableString(ms, record.TargetId);
        WriteString(ms, record.Outcome);
        WriteNullableString(ms, record.IpAddress);
        WriteNullableString(ms, record.UserAgent);
        WriteString(ms, FormatTimestamp(record.OccurredAt));
        WriteGuid(ms, record.SourceEventId);
        WriteInt64(ms, record.SourceSequenceNumber);
        WriteString(ms, record.PayloadJson);
        WriteInt64(ms, record.ChainSequence);

        return ms.ToArray();
    }

    /// <summary>
    /// UTC ISO-8601 with millisecond precision under the invariant culture. The
    /// record's <c>OccurredAt</c> is normalized to UTC first so a row read back
    /// as <c>Unspecified</c>/<c>Local</c> still canonicalizes identically.
    /// </summary>
    internal static string FormatTimestamp(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    private static void WriteNullableString(Stream s, string? value)
    {
        if (value is null)
        {
            s.WriteByte(FieldNull);
            return;
        }
        s.WriteByte(FieldPresent);
        WriteString(s, value);
    }

    private static void WriteString(Stream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteLengthPrefix(s, bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }

    private static void WriteNullableGuid(Stream s, Guid? value)
    {
        if (value is not Guid g)
        {
            s.WriteByte(FieldNull);
            return;
        }
        s.WriteByte(FieldPresent);
        WriteGuid(s, g);
    }

    private static void WriteGuid(Stream s, Guid value)
    {
        Span<byte> b = stackalloc byte[16];
        value.TryWriteBytes(b);
        s.Write(b);
    }

    private static void WriteInt64(Stream s, long value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, value);
        s.Write(b);
    }

    private static void WriteLengthPrefix(Stream s, int length)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, length);
        s.Write(b);
    }
}
