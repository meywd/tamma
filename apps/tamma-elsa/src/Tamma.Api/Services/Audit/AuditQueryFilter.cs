using System.Buffers.Binary;
using Tamma.Core.Audit;

namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-3 — the keyset position: the last-seen row's TOTAL-order key. The
/// audit read orders by <c>(source_sequence_number DESC, id DESC)</c>.
///
/// <para><b>Why the compound key:</b> in the control-plane <c>audit_records</c>
/// table <c>source_sequence_number</c> is NOT unique — the table is fed by two
/// independent identity sequences (<c>domain_events.SequenceNumber</c> AND
/// <c>platform_events.SequenceNumber</c>, both starting at 1) whose values
/// collide; the table is unique only on <c>source_event_id</c>. Seeking on the
/// sequence alone would silently drop the OTHER row that shares the boundary
/// sequence — a compliance completeness failure. The globally-unique surrogate
/// <see cref="Id"/> (the PK Guid) is the deterministic tiebreak that makes the
/// seek TOTAL: no row skipped or repeated at a page boundary.</para>
/// </summary>
public readonly record struct AuditCursor(long Seq, Guid Id);

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
/// row's <see cref="AuditCursor"/> (<c>source_sequence_number</c> + row
/// <c>id</c>), decoded from the opaque base64url <c>cursor</c> query value. The
/// next page is
/// <c>WHERE (source_sequence_number, id) &lt; (cursor.seq, cursor.id)
/// ORDER BY source_sequence_number DESC, id DESC</c> — a TOTAL order (the
/// unique <c>id</c> tiebreak) that stays stable under concurrent inserts AND
/// never drops rows that share a non-unique <c>source_sequence_number</c>.</para>
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
    AuditCursor? Cursor)
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

        AuditCursor? cursorKey = null;
        var rawCursor = Trimmed(cursor);
        if (rawCursor is not null)
        {
            if (!TryDecodeCursor(rawCursor, out var key))
                return (null, "cursor is malformed");
            cursorKey = key;
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
            Cursor: cursorKey);

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
        if (Cursor is { } cur)
            shape["cursor"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["seq"] = cur.Seq,
                ["id"] = cur.Id.ToString(),
            };
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

    // ── Opaque cursor codec — base64url of (big-endian seq | 16-byte id) ──

    /// <summary>Encode a compound keyset position (<c>source_sequence_number</c>
    /// + row <c>id</c>) as an opaque base64url cursor: 8 big-endian bytes of
    /// sequence followed by the 16-byte row id. Opaque so clients don't hand-roll
    /// their own; trivially decodable server-side. The row id makes the position
    /// TOTAL — the sequence alone is not unique in the CP <c>audit_records</c>
    /// table (two identity sequences feed it).</summary>
    public static string EncodeCursor(long sequenceNumber, Guid id)
    {
        Span<byte> buf = stackalloc byte[24];
        BinaryPrimitives.WriteInt64BigEndian(buf, sequenceNumber);
        var wrote = id.TryWriteBytes(buf[8..]);
        System.Diagnostics.Debug.Assert(wrote, "Guid is exactly 16 bytes");
        return Base64UrlEncode(buf);
    }

    /// <summary>Decode an opaque cursor back to its compound
    /// <see cref="AuditCursor"/> (sequence + row id). Returns <c>false</c> for any
    /// malformed input — wrong length included (→ 400).</summary>
    public static bool TryDecodeCursor(string cursor, out AuditCursor value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(cursor)) return false;
        if (!TryBase64UrlDecode(cursor.Trim(), out var bytes) || bytes.Length != 24) return false;
        var seq = BinaryPrimitives.ReadInt64BigEndian(bytes);
        var id = new Guid(bytes.AsSpan(8, 16));
        value = new AuditCursor(seq, id);
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

    /// <summary>
    /// Normalize a from/to boundary to UTC WITHOUT shifting the intended instant:
    /// <list type="bullet">
    ///   <item><description><see cref="DateTimeKind.Utc"/> — kept as-is.</description></item>
    ///   <item><description><see cref="DateTimeKind.Local"/> — converted, so a
    ///     <c>+02:00</c>-offset input and its UTC equivalent select the SAME
    ///     boundary.</description></item>
    ///   <item><description><see cref="DateTimeKind.Unspecified"/> — treated as
    ///     already-UTC. It is NOT passed through <c>ToUniversalTime()</c>, which
    ///     would silently subtract the HOST's local offset and shift the window —
    ///     the class of TZ drift that UTC-only CI hides.</description></item>
    /// </list>
    /// </summary>
    private static DateTime? ToUtc(DateTime? v)
    {
        if (v is null) return null;
        var d = v.Value;
        return d.Kind switch
        {
            DateTimeKind.Utc => d,
            DateTimeKind.Local => d.ToUniversalTime(),
            _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
        };
    }
}
