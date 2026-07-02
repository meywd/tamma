using System.Buffers.Binary;
using Tamma.Core.Audit;

namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-3 — the parsed, validated, immutable filter for an audit query over
/// the curated <c>audit_records</c> read-model (Story 37-1). Parsing is total:
/// <see cref="TryParse"/> returns a descriptive error string for any bad input
/// (→ 400 in the handler) and NEVER throws or silently matches nothing (AC3).
///
/// <para>Closed enums (<c>category</c> / <c>severity</c> / <c>outcome</c>) are
/// validated against the Story 37-1 projector's own vocabulary
/// (<see cref="AuditCategory"/> / <see cref="AuditSeverity"/> + the
/// <c>ck_audit_records_outcome</c> CHECK) so an unknown value 400s rather than
/// returning an empty page. <c>action</c> (<c>action_code</c>) is a free-form
/// exact match — the catalog carries dozens of codes and an unknown one simply
/// matches nothing, which is the correct exact-match semantics.</para>
///
/// <para><b>Keyset, not offset (AC5):</b> <see cref="Cursor"/> is the last-seen
/// <c>source_sequence_number</c>, decoded from the opaque base64url
/// <c>cursor</c> query value. The next page is
/// <c>WHERE source_sequence_number &lt; cursor ORDER BY … DESC</c> — stable
/// under concurrent inserts.</para>
/// </summary>
public sealed record AuditQueryFilter(
    string? Category,
    string? Action,
    Guid? ActorUserId,
    string? TargetType,
    string? TargetId,
    string? Severity,
    string? Outcome,
    string? IpAddress,
    DateTime? From,
    DateTime? To,
    string? Search,
    int Limit,
    long? Cursor)
{
    public const int DefaultLimit = 50;
    public const int MinLimit = 1;
    public const int MaxLimit = 200;

    /// <summary>Closed vocab derived from the Story 37-1 enums (lowercased member
    /// names, exactly as the projector writes them).</summary>
    private static readonly HashSet<string> ValidCategories =
        new(Enum.GetNames<AuditCategory>().Select(n => n.ToLowerInvariant()), StringComparer.Ordinal);

    private static readonly HashSet<string> ValidSeverities =
        new(Enum.GetNames<AuditSeverity>().Select(n => n.ToLowerInvariant()), StringComparer.Ordinal);

    /// <summary>Matches the <c>ck_audit_records_outcome</c> CHECK constraint.</summary>
    private static readonly HashSet<string> ValidOutcomes =
        new(new[] { "success", "failure", "denied" }, StringComparer.Ordinal);

    /// <summary>
    /// Parse + validate the raw query values. Returns <c>(filter, null)</c> on
    /// success or <c>(null, error)</c> on the first validation failure. Never
    /// throws.
    /// </summary>
    public static (AuditQueryFilter? Filter, string? Error) TryParse(
        string? category,
        string? action,
        string? actorUserId,
        string? targetType,
        string? targetId,
        string? severity,
        string? outcome,
        string? ipAddress,
        DateTime? from,
        DateTime? to,
        string? q,
        int? limit,
        string? cursor)
    {
        var normCategory = Trimmed(category);
        if (normCategory is not null)
        {
            normCategory = normCategory.ToLowerInvariant();
            if (!ValidCategories.Contains(normCategory))
                return (null, $"category must be one of: {string.Join(", ", ValidCategories.Order())}");
        }

        var normSeverity = Trimmed(severity);
        if (normSeverity is not null)
        {
            normSeverity = normSeverity.ToLowerInvariant();
            if (!ValidSeverities.Contains(normSeverity))
                return (null, $"severity must be one of: {string.Join(", ", ValidSeverities.Order())}");
        }

        var normOutcome = Trimmed(outcome);
        if (normOutcome is not null)
        {
            normOutcome = normOutcome.ToLowerInvariant();
            if (!ValidOutcomes.Contains(normOutcome))
                return (null, $"outcome must be one of: {string.Join(", ", ValidOutcomes.Order())}");
        }

        Guid? actorGuid = null;
        var rawActor = Trimmed(actorUserId);
        if (rawActor is not null)
        {
            if (!Guid.TryParse(rawActor, out var g))
                return (null, "actorUserId must be a valid GUID");
            actorGuid = g;
        }

        var fromUtc = ToUtc(from);
        var toUtc = ToUtc(to);
        if (fromUtc is not null && toUtc is not null && fromUtc > toUtc)
            return (null, "from must not be after to");

        long? cursorSeq = null;
        var rawCursor = Trimmed(cursor);
        if (rawCursor is not null)
        {
            if (!TryDecodeCursor(rawCursor, out var seq))
                return (null, "cursor is malformed");
            cursorSeq = seq;
        }

        // limit is clamped, never rejected (AC6).
        var clampedLimit = Math.Clamp(limit ?? DefaultLimit, MinLimit, MaxLimit);

        var filter = new AuditQueryFilter(
            Category: normCategory,
            Action: Trimmed(action),
            ActorUserId: actorGuid,
            TargetType: Trimmed(targetType),
            TargetId: Trimmed(targetId),
            Severity: normSeverity,
            Outcome: normOutcome,
            IpAddress: Trimmed(ipAddress),
            From: fromUtc,
            To: toUtc,
            Search: Trimmed(q),
            Limit: clampedLimit,
            Cursor: cursorSeq);

        return (filter, null);
    }

    /// <summary>The applied-filter shape recorded in the <c>AUDIT.QUERIED</c>
    /// meta-audit event's <c>Data</c> (AC10) — the filter set, NEVER the result
    /// rows. Only keys that were actually applied are present.</summary>
    public IReadOnlyDictionary<string, object?> ToAuditableShape()
    {
        var shape = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (Category is not null) shape["category"] = Category;
        if (Action is not null) shape["action"] = Action;
        if (ActorUserId is not null) shape["actorUserId"] = ActorUserId.ToString();
        if (TargetType is not null) shape["targetType"] = TargetType;
        if (TargetId is not null) shape["targetId"] = TargetId;
        if (Severity is not null) shape["severity"] = Severity;
        if (Outcome is not null) shape["outcome"] = Outcome;
        if (IpAddress is not null) shape["ipAddress"] = IpAddress;
        if (From is not null) shape["from"] = From.Value.ToString("o");
        if (To is not null) shape["to"] = To.Value.ToString("o");
        if (Search is not null) shape["hasSearch"] = true;
        shape["limit"] = Limit;
        if (Cursor is not null) shape["cursor"] = Cursor;
        return shape;
    }

    /// <summary>The set of applied filter KEYS (no values) — safe for INFO logs
    /// (never leaks a search term, IP, or actor id).</summary>
    public string AppliedFilterKeys()
    {
        var keys = new List<string>();
        if (Category is not null) keys.Add("category");
        if (Action is not null) keys.Add("action");
        if (ActorUserId is not null) keys.Add("actorUserId");
        if (TargetType is not null) keys.Add("targetType");
        if (TargetId is not null) keys.Add("targetId");
        if (Severity is not null) keys.Add("severity");
        if (Outcome is not null) keys.Add("outcome");
        if (IpAddress is not null) keys.Add("ipAddress");
        if (From is not null) keys.Add("from");
        if (To is not null) keys.Add("to");
        if (Search is not null) keys.Add("q");
        if (Cursor is not null) keys.Add("cursor");
        return keys.Count == 0 ? "(none)" : string.Join(",", keys);
    }

    // ── Opaque cursor codec — base64url of a single big-endian long ──

    /// <summary>Encode a <c>source_sequence_number</c> as an opaque base64url
    /// cursor. Opaque so clients don't hand-roll their own; trivially decodable
    /// server-side.</summary>
    public static string EncodeCursor(long sequenceNumber)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buf, sequenceNumber);
        return Base64UrlEncode(buf);
    }

    /// <summary>Decode an opaque cursor back to its <c>source_sequence_number</c>.
    /// Returns <c>false</c> for any malformed input (→ 400).</summary>
    public static bool TryDecodeCursor(string cursor, out long sequenceNumber)
    {
        sequenceNumber = 0;
        if (string.IsNullOrWhiteSpace(cursor)) return false;
        if (!TryBase64UrlDecode(cursor.Trim(), out var bytes) || bytes.Length != 8) return false;
        sequenceNumber = BinaryPrimitives.ReadInt64BigEndian(bytes);
        return true;
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: return false; // never a valid base64 length
        }

        try
        {
            bytes = Convert.FromBase64String(s);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? Trimmed(string? v) =>
        string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static DateTime? ToUtc(DateTime? v) =>
        v is null ? null : DateTime.SpecifyKind(v.Value.ToUniversalTime(), DateTimeKind.Utc);
}
