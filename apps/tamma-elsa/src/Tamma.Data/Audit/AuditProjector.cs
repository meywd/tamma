using System.Text.Json;
using Tamma.Core.Audit;
using Tamma.Core.Redaction;
using Tamma.Data.Entities;

namespace Tamma.Data.Audit;

/// <summary>
/// Story 37-1 — pure projection of a raw DCB event into a curated, redacted,
/// per-mode-owned <see cref="AuditRecord"/>. No I/O: classification keys off
/// <see cref="SensitiveActionCatalog"/>; the payload is redacted via
/// <see cref="CredentialRedactor"/> BEFORE the row leaves this class (AC10);
/// ownership is assigned per <see cref="AuditOwnershipMode"/> (AC11). The
/// background host does the reading + writing.
/// </summary>
public sealed class AuditProjector : IAuditProjector
{
    // Tag/data keys actor identity may travel under across the various
    // emitters (verified by grep): impersonation uses actorUserId/actorEmail;
    // tenant membership uses userId; some carry callerId. Probed in order.
    private static readonly string[] ActorIdKeys =
        { "actorUserId", "userId", "callerId", "callerSub", "actorId" };
    private static readonly string[] ActorEmailKeys =
        { "actorEmail", "email", "userEmail" };
    private static readonly string[] TargetIdKeys =
        { "targetUserId", "targetId", "secretId", "agentId", "planId", "tenantId" };
    private static readonly string[] IpKeys = { "ipAddress", "ip", "clientIp" };
    private static readonly string[] UserAgentKeys = { "userAgent", "ua" };

    /// <summary>Safe placeholder payload written on the quarantine row (C2) —
    /// NEVER the raw / un-redacted <c>Data</c>/<c>Tags</c>.</summary>
    public const string QuarantinePayload = "{\"_projection_error\":\"redaction_failed\"}";

    /// <summary>Generic marker for the <c>target_type</c> when an event's
    /// classification could not be resolved at all (no catalog descriptor).</summary>
    public const string UnclassifiedTargetType = "unclassified";

    /// <summary>Dedicated marker for the <c>category</c> of a quarantine row whose
    /// event has no catalog descriptor (so the real <see cref="AuditCategory"/>
    /// cannot be derived). Distinct from <see cref="UnclassifiedTargetType"/> —
    /// the category and target-type columns are different dimensions and must not
    /// share one sentinel constant, even though their fallback spelling is the
    /// same. Not an <see cref="AuditCategory"/> member: it is a sentinel reserved
    /// for the descriptor-missing quarantine path only.</summary>
    public const string UnclassifiedCategory = "unclassified";

    // Physical varchar column lengths of audit_records (see
    // TammaModelConfiguration.ConfigureAuditEntities + migration AddAuditRecords).
    // Every resolved string field is clamped to its column length BEFORE the row
    // is built so no over-length — possibly attacker-controlled — value can throw
    // Npgsql 22001 (string_data_right_truncation) on INSERT and break the curated
    // trail for that event. Belt-and-suspenders: this protects EVERY current and
    // future emitter, not just any one call site.
    private const int MaxActionCodeLength = 128;
    private const int MaxCategoryLength = 32;
    private const int MaxSeverityLength = 16;
    private const int MaxActorEmailLength = 320;
    private const int MaxTargetTypeLength = 64;
    private const int MaxTargetIdLength = 255;
    private const int MaxOutcomeLength = 16;
    private const int MaxIpAddressLength = 64;
    private const int MaxUserAgentLength = 512;

    /// <inheritdoc />
    public AuditRecord? TryBuildRecord(
        RawAuditEvent rawEvent, AuditOwnershipMode mode, Guid? singleUserOwnerId)
    {
        ArgumentNullException.ThrowIfNull(rawEvent);

        var descriptor = SensitiveActionCatalog.Resolve(rawEvent.Type);
        if (descriptor is null) return null; // AC7 — non-catalog events are skipped.

        var tags = ParseObject(rawEvent.Tags);
        var data = ParseObject(rawEvent.Data);

        var record = new AuditRecord
        {
            Id = Guid.NewGuid(),
            ActionCode = descriptor.ActionCode,
            Category = descriptor.Category.ToString().ToLowerInvariant(),
            Severity = descriptor.Severity.ToString().ToLowerInvariant(),
            ActorUserId = ResolveGuid(tags, data, ActorIdKeys),
            ActorEmailSnapshot = ResolveString(tags, data, ActorEmailKeys),
            TargetType = ResolveTargetType(tags, data, descriptor.TargetTypeHint),
            TargetId = ResolveString(tags, data, TargetIdKeys),
            Outcome = ResolveOutcome(rawEvent.Type, tags, data),
            IpAddress = ResolveString(tags, data, IpKeys),
            UserAgent = ResolveString(tags, data, UserAgentKeys),
            OccurredAt = DateTime.SpecifyKind(rawEvent.CreatedAt, DateTimeKind.Utc),
            SourceEventId = rawEvent.Id,
            SourceSequenceNumber = rawEvent.SequenceNumber,
            // AC10 — redact the projected payload BEFORE it ever becomes a row.
            // If the redactor throws, the exception propagates; the host then
            // builds a QUARANTINE row (BuildQuarantineRecord) keyed by the same
            // source_event_id with a safe placeholder payload, so the action is
            // still recorded (never dropped) and the cursor can advance (C2).
            PayloadJson = CredentialRedactor.Clean(ProjectPayload(rawEvent)),
            // Reserved for Story 37-2 — left null here (AC12).
            RecordHash = null,
            PrevRecordHash = null,
        };

        ClampToColumnLimits(record);
        AssignOwnership(record, rawEvent, mode, singleUserOwnerId);
        return record;
    }

    /// <inheritdoc />
    public AuditRecord BuildQuarantineRecord(
        RawAuditEvent rawEvent, AuditOwnershipMode mode, Guid? singleUserOwnerId)
    {
        ArgumentNullException.ThrowIfNull(rawEvent);

        // C2 — quarantine, do NOT drop and do NOT halt. Classification keys off
        // the (already-parsed) event type + tags/data, which is the part that did
        // NOT throw — the redaction of the PAYLOAD is what failed. So we reuse the
        // known-safe classifiable fields and substitute a SAFE placeholder payload;
        // the raw Data/Tags NEVER reach the row. If even the descriptor is missing
        // (a build failure with no classification), fall back to a generic marker.
        var descriptor = SensitiveActionCatalog.Resolve(rawEvent.Type);

        // Field extraction parses JSON only; it does not run the redactor, so it
        // is safe to retry here. Guard it anyway so a pathological-JSON throw can't
        // turn the quarantine itself into a poison pill — degrade to empty maps.
        Dictionary<string, JsonElement> tags;
        Dictionary<string, JsonElement> data;
        try
        {
            tags = ParseObject(rawEvent.Tags);
            data = ParseObject(rawEvent.Data);
        }
        catch
        {
            tags = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }

        var record = new AuditRecord
        {
            Id = Guid.NewGuid(),
            ActionCode = descriptor?.ActionCode ?? rawEvent.Type,
            Category = descriptor is not null
                ? descriptor.Category.ToString().ToLowerInvariant()
                : UnclassifiedCategory,
            Severity = descriptor is not null
                ? descriptor.Severity.ToString().ToLowerInvariant()
                : "warning",
            ActorUserId = SafeResolveGuid(tags, data, ActorIdKeys),
            ActorEmailSnapshot = SafeResolveString(tags, data, ActorEmailKeys),
            TargetType = descriptor is not null
                ? SafeResolveTargetType(tags, data, descriptor.TargetTypeHint)
                : UnclassifiedTargetType,
            TargetId = SafeResolveString(tags, data, TargetIdKeys),
            // The action could not be fully materialized — record it as a failure.
            Outcome = "failure",
            IpAddress = SafeResolveString(tags, data, IpKeys),
            UserAgent = SafeResolveString(tags, data, UserAgentKeys),
            OccurredAt = DateTime.SpecifyKind(rawEvent.CreatedAt, DateTimeKind.Utc),
            SourceEventId = rawEvent.Id,
            SourceSequenceNumber = rawEvent.SequenceNumber,
            // SAFE placeholder — never the raw/un-redacted payload (C2).
            PayloadJson = QuarantinePayload,
            RecordHash = null,
            PrevRecordHash = null,
        };

        ClampToColumnLimits(record);
        AssignOwnership(record, rawEvent, mode, singleUserOwnerId);
        return record;
    }

    /// <summary>
    /// Defensively cap every varchar-bounded field to its physical column length
    /// so an over-length resolved value (e.g. an attacker-padded login email in
    /// <c>ActorEmailSnapshot</c>) can NEVER overflow the column and throw Npgsql
    /// 22001 (string_data_right_truncation) on INSERT. Left unbounded, that
    /// overflow breaks the audit trail for the event — the failed insert leaves a
    /// tracked poison entity that re-throws on the next SaveChanges (the cursor
    /// write), stalling the projector tick — so an attacker could suppress the
    /// audit of their own failed logins by padding the email. Capping here makes
    /// the insert always fit. The redacted <c>PayloadJson</c> is already
    /// length-bounded by <see cref="CredentialRedactor.MaxLength"/>; the outcome
    /// is a closed enum but is capped too for uniformity. Guid / long / timestamp
    /// columns cannot overflow and are left untouched.
    /// </summary>
    private static void ClampToColumnLimits(AuditRecord record)
    {
        record.ActionCode = Truncate(record.ActionCode, MaxActionCodeLength)!;
        record.Category = Truncate(record.Category, MaxCategoryLength)!;
        record.Severity = Truncate(record.Severity, MaxSeverityLength)!;
        record.ActorEmailSnapshot = Truncate(record.ActorEmailSnapshot, MaxActorEmailLength);
        record.TargetType = Truncate(record.TargetType, MaxTargetTypeLength);
        record.TargetId = Truncate(record.TargetId, MaxTargetIdLength);
        record.Outcome = Truncate(record.Outcome, MaxOutcomeLength)!;
        record.IpAddress = Truncate(record.IpAddress, MaxIpAddressLength);
        record.UserAgent = Truncate(record.UserAgent, MaxUserAgentLength);
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is not null && value.Length > maxLength ? value[..maxLength] : value;

    // Quarantine field extraction must never itself throw — wrap the resolvers.
    private static string? SafeResolveString(
        IReadOnlyDictionary<string, JsonElement> tags,
        IReadOnlyDictionary<string, JsonElement> data, string[] keys)
    {
        try { return ResolveString(tags, data, keys); }
        catch { return null; }
    }

    private static Guid? SafeResolveGuid(
        IReadOnlyDictionary<string, JsonElement> tags,
        IReadOnlyDictionary<string, JsonElement> data, string[] keys)
    {
        try { return ResolveGuid(tags, data, keys); }
        catch { return null; }
    }

    private static string SafeResolveTargetType(
        IReadOnlyDictionary<string, JsonElement> tags,
        IReadOnlyDictionary<string, JsonElement> data, string hint)
    {
        try { return ResolveTargetType(tags, data, hint); }
        catch { return hint; }
    }

    /// <summary>
    /// AC11 — per-mode ownership routing. single-user → key by <c>user_id</c>;
    /// SaaS → key by <c>tenant_id</c> (null for platform-only actions, which the
    /// host routes to the control plane). Exactly one of the two is set so the
    /// XOR CHECK holds.
    /// </summary>
    private static void AssignOwnership(
        AuditRecord record, RawAuditEvent rawEvent,
        AuditOwnershipMode mode, Guid? singleUserOwnerId)
    {
        if (mode == AuditOwnershipMode.SingleUser)
        {
            // In single-user mode there is no tenant dimension; every row is
            // keyed by the sole user. Prefer the explicit owner id; fall back to
            // the event's actor so a self-hosted instance still keys the row.
            record.TenantId = null;
            record.UserId = singleUserOwnerId
                ?? record.ActorUserId
                ?? throw new InvalidOperationException(
                    "single-user audit projection requires a user id to own the row " +
                    $"(event {rawEvent.Id} '{rawEvent.Type}' carried no resolvable actor).");
        }
        else
        {
            // SaaS — key by tenant. A platform-only event (TenantId null) is a
            // platform row: tenant_id stays null and the host writes it to the
            // control-plane store (never a tenant's view). The XOR CHECK permits
            // tenant_id null only with user_id null — exactly the platform case.
            record.UserId = null;
            record.TenantId = rawEvent.TenantId;
        }
    }

    /// <summary>
    /// AC10 — build the JSON payload to persist. Combines the raw event's tags +
    /// data into one object; the caller redacts it. Kept small + deterministic.
    /// </summary>
    private static string ProjectPayload(RawAuditEvent rawEvent)
    {
        var payload = new Dictionary<string, object?>
        {
            ["tags"] = RawJson(rawEvent.Tags),
            ["data"] = RawJson(rawEvent.Data),
        };
        return JsonSerializer.Serialize(payload);
    }

    // Embed the raw tag/data JSON as nested objects when parseable, else as a
    // string — so the redactor sees the secret-shaped substrings either way.
    private static object? RawJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static Dictionary<string, JsonElement> ParseObject(string json)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.Clone();
            }
        }
        catch (JsonException)
        {
            // Malformed JSONB — projector treats it as "no fields"; the raw
            // payload still lands (redacted) so nothing is lost.
        }
        return result;
    }

    private static string? ResolveString(
        IReadOnlyDictionary<string, JsonElement> tags,
        IReadOnlyDictionary<string, JsonElement> data,
        string[] keys)
    {
        foreach (var key in keys)
        {
            if (TryGetString(tags, key, out var v) || TryGetString(data, key, out v))
            {
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        return null;
    }

    private static Guid? ResolveGuid(
        IReadOnlyDictionary<string, JsonElement> tags,
        IReadOnlyDictionary<string, JsonElement> data,
        string[] keys)
    {
        var s = ResolveString(tags, data, keys);
        return Guid.TryParse(s, out var g) ? g : null;
    }

    private static string ResolveTargetType(
        IReadOnlyDictionary<string, JsonElement> tags,
        IReadOnlyDictionary<string, JsonElement> data,
        string hint)
    {
        var explicitType = ResolveString(tags, data, new[] { "targetType" });
        return string.IsNullOrWhiteSpace(explicitType) ? hint : explicitType;
    }

    /// <summary>
    /// Derive outcome from the event-type suffix + any explicit outcome field.
    /// <c>*.FAILED</c> → failure; <c>*.DENIED</c> → denied; an explicit
    /// <c>outcome</c>/<c>success=false</c> field wins; default success.
    /// </summary>
    private static string ResolveOutcome(
        string type,
        IReadOnlyDictionary<string, JsonElement> tags,
        IReadOnlyDictionary<string, JsonElement> data)
    {
        var explicitOutcome = ResolveString(tags, data, new[] { "outcome" });
        if (!string.IsNullOrWhiteSpace(explicitOutcome))
        {
            var normalized = explicitOutcome.Trim().ToLowerInvariant();
            if (normalized is "success" or "failure" or "denied") return normalized;
        }

        if (TryGetBool(data, "success", out var success) ||
            TryGetBool(tags, "success", out success))
        {
            if (!success) return "failure";
        }

        if (type.EndsWith(".FAILED", StringComparison.Ordinal) ||
            type.EndsWith(".FAILURE", StringComparison.Ordinal))
        {
            return "failure";
        }
        if (type.EndsWith(".DENIED", StringComparison.Ordinal) ||
            type.Contains("REUSE_DETECTED", StringComparison.Ordinal))
        {
            return "denied";
        }
        return "success";
    }

    private static bool TryGetString(
        IReadOnlyDictionary<string, JsonElement> map, string key, out string? value)
    {
        value = null;
        if (!map.TryGetValue(key, out var el)) return false;
        value = el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => el.ToString(),
        };
        return value is not null;
    }

    private static bool TryGetBool(
        IReadOnlyDictionary<string, JsonElement> map, string key, out bool value)
    {
        value = false;
        if (!map.TryGetValue(key, out var el)) return false;
        switch (el.ValueKind)
        {
            case JsonValueKind.True: value = true; return true;
            case JsonValueKind.False: value = false; return true;
            case JsonValueKind.String when bool.TryParse(el.GetString(), out var b):
                value = b; return true;
            default: return false;
        }
    }
}
