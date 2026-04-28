using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Services.Alerts;
using Tamma.Api.Services.Alerts.Rules;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 5.6 (Wave C.2) — admin CRUD + synthetic-fire endpoints for
/// alert rules. Mounted under <c>/api/v1/admin/alert-rules/*</c> with
/// <c>OwnerAccess</c>.
///
/// <para><b>Endpoints</b>:</para>
/// <list type="bullet">
///   <item><description><c>GET    /api/v1/admin/alert-rules</c> —
///     paged list (filter by eventType / isEnabled).</description></item>
///   <item><description><c>GET    /api/v1/admin/alert-rules/{id}</c> —
///     single rule.</description></item>
///   <item><description><c>POST   /api/v1/admin/alert-rules</c> —
///     create custom (non-built-in) rule; predicate validated.</description></item>
///   <item><description><c>PATCH  /api/v1/admin/alert-rules/{id}</c> —
///     update. Built-ins reject changes to locked fields (409).</description></item>
///   <item><description><c>DELETE /api/v1/admin/alert-rules/{id}</c> —
///     hard-delete custom rules; 409 on built-ins (use <c>PATCH</c>
///     with <c>is_enabled=false</c> to silence a built-in).</description></item>
///   <item><description><c>POST   /api/v1/admin/alert-rules/{id}/_test</c>
///     — synthetic-fire; returns the would-be <c>AlertPayload</c>
///     without invoking <c>IAlertSink</c>.</description></item>
/// </list>
/// </summary>
public static class AlertRuleEndpoints
{
    /// <summary>
    /// Built-in rule fields that cannot be changed via PATCH. Admins
    /// can toggle <c>is_enabled</c>, edit <c>severity</c>, adjust
    /// <c>throttle_seconds</c>, link <c>channel_ids</c>, or edit
    /// <c>description</c>. Attempting to change one of these returns
    /// a 409 with a structured body.
    /// </summary>
    private static readonly HashSet<string> LockedBuiltInFields = new(
        StringComparer.Ordinal)
    {
        "event_type", "predicate", "built_in_key", "is_built_in", "name",
    };

    public static async Task<IResult> ListRules(
        HttpContext http,
        ControlPlaneDbContext db,
        string? eventType = null,
        bool? isEnabled = null,
        int? limit = null)
    {
        var take = Math.Clamp(limit ?? 100, 1, 500);
        var q = db.AlertRules.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(eventType))
            q = q.Where(r => r.EventType == eventType);
        if (isEnabled is bool e)
            q = q.Where(r => r.IsEnabled == e);

        var rows = await q
            .OrderBy(r => r.Name)
            .Take(take)
            .ToListAsync(http.RequestAborted)
            .ConfigureAwait(false);

        return Results.Ok(new
        {
            items = rows.Select(ToDto).ToList(),
            count = rows.Count,
            limit = take,
        });
    }

    public static async Task<IResult> GetRule(
        HttpContext http, ControlPlaneDbContext db, Guid id)
    {
        var row = await db.AlertRules.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, http.RequestAborted)
            .ConfigureAwait(false);
        if (row is null)
            return Results.NotFound(new { error = "alert rule not found" });
        return Results.Ok(ToDto(row));
    }

    public static async Task<IResult> CreateRule(
        HttpContext http,
        ControlPlaneDbContext db,
        TimeProvider timeProvider,
        [FromBody] CreateAlertRuleRequest body)
    {
        if (body is null)
            return Results.BadRequest(new { error = "body is required" });

        // Name — 1..255, required, unique.
        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 255)
            return Results.BadRequest(new
            {
                error = "name is required and must be 1..255 characters.",
            });
        if (string.IsNullOrWhiteSpace(body.Description))
            return Results.BadRequest(new { error = "description is required." });
        if (string.IsNullOrWhiteSpace(body.Severity) ||
            !AlertSeverity.IsValid(body.Severity))
        {
            return Results.BadRequest(new
            {
                error =
                    $"severity must be one of: " +
                    $"{string.Join(", ", AlertSeverity.All)}.",
            });
        }
        if (string.IsNullOrWhiteSpace(body.EventType))
            return Results.BadRequest(new { error = "eventType is required." });
        if (string.IsNullOrWhiteSpace(body.Predicate))
            return Results.BadRequest(new { error = "predicate is required." });

        // Validate predicate against the DSL grammar. Rejected on
        // 400 with field-path + reason. Admins writing custom rules
        // need this; built-ins are seeded with vetted predicates.
        try
        {
            AlertRulePredicateParser.Parse(body.Predicate);
        }
        catch (InvalidAlertRulePredicateException ex)
        {
            return Results.BadRequest(new
            {
                error = "invalid predicate.",
                fieldPath = ex.FieldPath,
                reason = ex.Message,
            });
        }

        if (body.ThrottleSeconds < 0)
            return Results.BadRequest(new
            {
                error = "throttleSeconds must be >= 0.",
            });

        // Name uniqueness enforced by unique index, but we want a
        // clean 409 instead of a raw Postgres exception.
        var dupe = await db.AlertRules
            .AsNoTracking()
            .AnyAsync(r => r.Name == body.Name, http.RequestAborted)
            .ConfigureAwait(false);
        if (dupe)
            return Results.Conflict(new { error = "name already exists." });

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var row = new AlertRule
        {
            Name = body.Name,
            Description = body.Description,
            IsEnabled = body.IsEnabled ?? true,
            Severity = body.Severity,
            EventType = body.EventType,
            Predicate = body.Predicate,
            ThrottleSeconds = body.ThrottleSeconds,
            ChannelIds = body.ChannelIds ?? Array.Empty<Guid>(),
            IsBuiltIn = false,  // admin-created rules are never built-in
            BuiltInKey = null,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AlertRules.Add(row);
        await db.SaveChangesAsync(http.RequestAborted).ConfigureAwait(false);
        return Results.Created(
            $"/api/v1/admin/alert-rules/{row.Id}", ToDto(row));
    }

    public static async Task<IResult> UpdateRule(
        HttpContext http,
        ControlPlaneDbContext db,
        TimeProvider timeProvider,
        Guid id,
        [FromBody] UpdateAlertRuleRequest body)
    {
        if (body is null)
            return Results.BadRequest(new { error = "body is required" });

        var row = await db.AlertRules
            .FirstOrDefaultAsync(r => r.Id == id, http.RequestAborted)
            .ConfigureAwait(false);
        if (row is null)
            return Results.NotFound(new { error = "alert rule not found" });

        // Built-in rules reject locked-field edits. Collect violations
        // and report all at once for a better DX.
        if (row.IsBuiltIn)
        {
            var violations = new List<string>();
            if (body.EventType is not null && body.EventType != row.EventType)
                violations.Add("event_type");
            if (body.Predicate is not null && body.Predicate != row.Predicate)
                violations.Add("predicate");
            if (body.Name is not null && body.Name != row.Name)
                violations.Add("name");
            // built_in_key / is_built_in aren't on the UpdateAlertRuleRequest
            // surface so can't be edited at all — no violation check needed.

            if (violations.Count > 0)
            {
                return Results.Conflict(new
                {
                    error =
                        "cannot edit locked fields on a built-in rule; " +
                        "disable the rule or link a channel instead.",
                    lockedFields = violations,
                });
            }
        }

        // Validate new predicate (applies to non-built-in rules only).
        if (body.Predicate is not null && body.Predicate != row.Predicate)
        {
            try
            {
                AlertRulePredicateParser.Parse(body.Predicate);
            }
            catch (InvalidAlertRulePredicateException ex)
            {
                return Results.BadRequest(new
                {
                    error = "invalid predicate.",
                    fieldPath = ex.FieldPath,
                    reason = ex.Message,
                });
            }
            row.Predicate = body.Predicate;
        }

        if (body.Description is not null)
            row.Description = body.Description;
        if (body.IsEnabled is bool ie)
            row.IsEnabled = ie;
        if (body.Severity is not null)
        {
            if (!AlertSeverity.IsValid(body.Severity))
                return Results.BadRequest(new
                {
                    error =
                        $"severity must be one of: " +
                        $"{string.Join(", ", AlertSeverity.All)}.",
                });
            row.Severity = body.Severity;
        }
        if (body.EventType is not null && !row.IsBuiltIn)
        {
            if (string.IsNullOrWhiteSpace(body.EventType))
                return Results.BadRequest(new
                {
                    error = "eventType cannot be empty.",
                });
            row.EventType = body.EventType;
        }
        if (body.Name is not null && !row.IsBuiltIn)
        {
            if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 255)
                return Results.BadRequest(new
                {
                    error = "name must be 1..255 characters.",
                });
            var dupe = await db.AlertRules
                .AsNoTracking()
                .AnyAsync(
                    r => r.Id != row.Id && r.Name == body.Name,
                    http.RequestAborted)
                .ConfigureAwait(false);
            if (dupe)
                return Results.Conflict(new { error = "name already exists." });
            row.Name = body.Name;
        }
        if (body.ThrottleSeconds is int ts)
        {
            if (ts < 0)
                return Results.BadRequest(new
                {
                    error = "throttleSeconds must be >= 0.",
                });
            row.ThrottleSeconds = ts;
        }
        if (body.ChannelIds is not null)
            row.ChannelIds = body.ChannelIds;

        row.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(http.RequestAborted).ConfigureAwait(false);
        return Results.Ok(ToDto(row));
    }

    public static async Task<IResult> DeleteRule(
        HttpContext http, ControlPlaneDbContext db, Guid id)
    {
        var row = await db.AlertRules
            .FirstOrDefaultAsync(r => r.Id == id, http.RequestAborted)
            .ConfigureAwait(false);
        if (row is null)
            return Results.NotFound(new { error = "alert rule not found" });
        if (row.IsBuiltIn)
        {
            return Results.Conflict(new
            {
                error =
                    "cannot delete a built-in rule. " +
                    "PATCH with is_enabled=false to silence it instead.",
            });
        }

        db.AlertRules.Remove(row);
        await db.SaveChangesAsync(http.RequestAborted).ConfigureAwait(false);
        return Results.NoContent();
    }

    /// <summary>
    /// Synthetic-fire: evaluate the rule against a fake
    /// <c>DomainEvent</c> matching the rule's event type + caller-
    /// supplied tags/data. Returns the would-be payload without
    /// calling <see cref="IAlertSink.RaiseAsync"/>. Useful for
    /// predicate authoring.
    /// </summary>
    public static async Task<IResult> TestFireRule(
        HttpContext http,
        ControlPlaneDbContext db,
        TimeProvider timeProvider,
        Guid id,
        [FromBody] TestFireAlertRuleRequest? body)
    {
        var row = await db.AlertRules.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, http.RequestAborted)
            .ConfigureAwait(false);
        if (row is null)
            return Results.NotFound(new { error = "alert rule not found" });

        DatabaseBackedAlertRule rule;
        try
        {
            rule = new DatabaseBackedAlertRule(row);
        }
        catch (InvalidAlertRulePredicateException ex)
        {
            return Results.BadRequest(new
            {
                error = "stored predicate is invalid.",
                fieldPath = ex.FieldPath,
                reason = ex.Message,
            });
        }

        var syntheticTags = body?.Tags is not null
            ? JsonSerializer.Serialize(body.Tags)
            : "{}";
        var syntheticData = body?.Data is not null
            ? JsonSerializer.Serialize(body.Data)
            : "{}";

        var synthetic = new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = row.EventType == "*" ? "TEST.SYNTHETIC" : row.EventType,
            TenantId = body?.TenantId,
            Tags = syntheticTags,
            Metadata = """{"eventSource":"synthetic","eventKind":"test_fire"}""",
            Data = syntheticData,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
        };

        // Fresh in-memory window store for the test so we don't leak
        // test state into production rules. For count_gte rules the
        // caller usually sets threshold=1 or supplies enough context
        // for the single-event fire.
        var windowStore = new InMemoryRuleWindowStore();
        var ctx = new AlertRuleContext(rule.Id, synthetic, windowStore);
        var payload = rule.Evaluate(ctx);
        if (payload is null)
        {
            return Results.Ok(new
            {
                fired = false,
                reason =
                    "predicate did not match the synthetic event " +
                    "(e.g. count_gte needs more occurrences, or tag/data " +
                    "fields missing).",
            });
        }

        return Results.Ok(new
        {
            fired = true,
            payload = new
            {
                severity = payload.Severity,
                title = payload.Title,
                description = payload.Description,
                correlationId = payload.CorrelationId,
                tenantId = payload.TenantId,
                ruleId = payload.RuleId,
                metadata = payload.Metadata,
            },
        });
    }

    private static object ToDto(AlertRule r) => new
    {
        id = r.Id,
        name = r.Name,
        description = r.Description,
        isEnabled = r.IsEnabled,
        severity = r.Severity,
        eventType = r.EventType,
        predicate = r.Predicate,
        throttleSeconds = r.ThrottleSeconds,
        channelIds = r.ChannelIds,
        isBuiltIn = r.IsBuiltIn,
        builtInKey = r.BuiltInKey,
        createdAt = r.CreatedAt,
        updatedAt = r.UpdatedAt,
    };
}

public sealed record CreateAlertRuleRequest(
    string Name,
    string Description,
    string Severity,
    string EventType,
    string Predicate,
    int ThrottleSeconds,
    Guid[]? ChannelIds,
    bool? IsEnabled);

public sealed record UpdateAlertRuleRequest(
    string? Name,
    string? Description,
    bool? IsEnabled,
    string? Severity,
    string? EventType,
    string? Predicate,
    int? ThrottleSeconds,
    Guid[]? ChannelIds);

public sealed record TestFireAlertRuleRequest(
    Guid? TenantId,
    Dictionary<string, object?>? Tags,
    Dictionary<string, object?>? Data);
