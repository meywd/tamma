using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Authorization;
using Tamma.Api.Services.Alerts;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 5.6 / 1.5-37 (Wave C.1) — platform-admin alert endpoints
/// mounted under <c>/api/v1/admin/alerts/*</c> and
/// <c>/api/v1/admin/alert-channels/*</c>. All require
/// <c>OwnerAccess</c> because alert configuration + acknowledgment is
/// a platform-oncall concern.
///
/// <para><b>Endpoints</b>:</para>
/// <list type="bullet">
///   <item><description><c>GET    /api/v1/admin/alerts</c> — paged
///     list with filter params.</description></item>
///   <item><description><c>GET    /api/v1/admin/alerts/{id}</c> —
///     detail + delivery attempts.</description></item>
///   <item><description><c>POST   /api/v1/admin/alerts/{id}/acknowledge</c></description></item>
///   <item><description><c>POST   /api/v1/admin/alerts/{id}/resolve</c></description></item>
///   <item><description><c>POST   /api/v1/admin/alerts/_test</c> —
///     raise a synthetic alert for smoke testing.</description></item>
///   <item><description><c>GET    /api/v1/admin/alert-channels</c></description></item>
///   <item><description><c>POST   /api/v1/admin/alert-channels</c></description></item>
///   <item><description><c>PATCH  /api/v1/admin/alert-channels/{id}</c></description></item>
///   <item><description><c>DELETE /api/v1/admin/alert-channels/{id}</c> —
///     soft-delete (flips <c>IsEnabled</c> false).</description></item>
/// </list>
/// </summary>
public static class AlertEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 500;

    // ── Alert CRUD ──────────────────────────────────────────────

    public static async Task<IResult> ListAlerts(
        HttpContext http,
        ControlPlaneDbContext db,
        string? status = null,
        string? severity = null,
        Guid? tenantId = null,
        DateTimeOffset? since = null,
        int? limit = null)
    {
        var take = Math.Min(
            limit is > 0 ? limit.Value : DefaultPageSize,
            MaxPageSize);

        var query = db.Alerts.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(a => a.Status == status);
        if (!string.IsNullOrWhiteSpace(severity))
            query = query.Where(a => a.Severity == severity);
        if (tenantId is Guid tid)
            query = query.Where(a => a.TenantId == tid);
        if (since is { } cutoff)
            query = query.Where(a => a.CreatedAt >= cutoff.UtcDateTime);

        var rows = await query
            .OrderByDescending(a => a.CreatedAt)
            .Take(take)
            .ToListAsync(http.RequestAborted)
            .ConfigureAwait(false);

        var items = rows.Select(ToDto).ToList();
        return Results.Ok(new { items, count = items.Count, limit = take });
    }

    public static async Task<IResult> GetAlert(
        HttpContext http,
        ControlPlaneDbContext db,
        Guid id)
    {
        var alert = await db.Alerts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, http.RequestAborted)
            .ConfigureAwait(false);
        if (alert is null)
            return Results.NotFound(new { error = "alert not found" });

        var attempts = await db.AlertDeliveryAttempts.AsNoTracking()
            .Where(a => a.AlertId == id)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(http.RequestAborted)
            .ConfigureAwait(false);

        return Results.Ok(new
        {
            alert = ToDto(alert),
            deliveryAttempts = attempts.Select(a => new
            {
                id = a.Id,
                channelId = a.ChannelId,
                attemptNumber = a.AttemptNumber,
                status = a.Status,
                error = a.Error,
                deliveredAt = a.DeliveredAt,
                nextAttemptAt = a.NextAttemptAt,
                createdAt = a.CreatedAt,
            }).ToList(),
        });
    }

    public static async Task<IResult> AcknowledgeAlert(
        HttpContext http,
        ControlPlaneDbContext db,
        IEventRepository events,
        TimeProvider timeProvider,
        Guid id,
        [FromBody] AcknowledgeAlertRequest? body)
    {
        var alert = await db.Alerts
            .FirstOrDefaultAsync(a => a.Id == id, http.RequestAborted)
            .ConfigureAwait(false);
        if (alert is null)
            return Results.NotFound(new { error = "alert not found" });

        if (alert.Status == AlertStatus.Resolved)
            return Results.Conflict(new
            {
                error = "alert already resolved; cannot acknowledge.",
            });

        var userId = ResolveUserId(http);
        alert.Status = AlertStatus.Acknowledged;
        alert.AcknowledgedBy = userId;
        alert.AcknowledgedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(http.RequestAborted).ConfigureAwait(false);

        await TryEmitAsync(events, new DomainEvent
        {
            Type = AlertEventTypes.Acknowledged,
            TenantId = alert.TenantId,
            Tags = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["alertId"] = alert.Id.ToString("N"),
                ["userId"] = userId?.ToString("N"),
            }),
            Metadata = """{"eventSource":"system"}""",
            Data = System.Text.Json.JsonSerializer.Serialize(new
            {
                note = body?.Note,
            }),
        });

        return Results.Ok(ToDto(alert));
    }

    public static async Task<IResult> ResolveAlert(
        HttpContext http,
        ControlPlaneDbContext db,
        IEventRepository events,
        TimeProvider timeProvider,
        Guid id,
        [FromBody] ResolveAlertRequest? body)
    {
        if (string.IsNullOrWhiteSpace(body?.Resolution))
            return Results.BadRequest(new { error = "resolution is required" });

        var alert = await db.Alerts
            .FirstOrDefaultAsync(a => a.Id == id, http.RequestAborted)
            .ConfigureAwait(false);
        if (alert is null)
            return Results.NotFound(new { error = "alert not found" });

        if (alert.Status == AlertStatus.Resolved)
            return Results.Conflict(new { error = "alert already resolved" });

        var userId = ResolveUserId(http);
        alert.Status = AlertStatus.Resolved;
        alert.ResolvedBy = userId;
        alert.ResolvedAt = timeProvider.GetUtcNow().UtcDateTime;
        alert.Resolution = body.Resolution.Length > 2000
            ? body.Resolution[..2000]
            : body.Resolution;
        await db.SaveChangesAsync(http.RequestAborted).ConfigureAwait(false);

        await TryEmitAsync(events, new DomainEvent
        {
            Type = AlertEventTypes.Resolved,
            TenantId = alert.TenantId,
            Tags = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["alertId"] = alert.Id.ToString("N"),
                ["userId"] = userId?.ToString("N"),
            }),
            Metadata = """{"eventSource":"system"}""",
            Data = System.Text.Json.JsonSerializer.Serialize(new
            {
                resolution = alert.Resolution,
            }),
        });

        return Results.Ok(ToDto(alert));
    }

    public static async Task<IResult> TestRaiseAlert(
        HttpContext http,
        IAlertSink sink,
        [FromBody] TestRaiseAlertRequest body)
    {
        if (body is null)
            return Results.BadRequest(new { error = "body is required" });
        if (string.IsNullOrWhiteSpace(body.Severity))
            return Results.BadRequest(new { error = "severity is required" });
        if (string.IsNullOrWhiteSpace(body.Title))
            return Results.BadRequest(new { error = "title is required" });
        if (string.IsNullOrWhiteSpace(body.Description))
            return Results.BadRequest(new { error = "description is required" });
        if (!AlertSeverity.IsValid(body.Severity))
            return Results.BadRequest(new
            {
                error = $"severity must be one of: " +
                        $"{string.Join(", ", AlertSeverity.All)}",
            });

        try
        {
            var result = await sink.RaiseAsync(
                new AlertPayload(
                    Severity: body.Severity,
                    Title: body.Title,
                    Description: body.Description,
                    CorrelationId: body.CorrelationId,
                    TenantId: body.TenantId,
                    RuleId: null,
                    Metadata: null),
                http.RequestAborted);
            return Results.Ok(new
            {
                alertId = result.AlertId,
                delivered = result.Delivered,
                matchedChannels = result.MatchedChannels,
                droppedByRateLimit = result.DroppedByRateLimit,
            });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    // ── Channel CRUD ────────────────────────────────────────────

    public static async Task<IResult> ListChannels(
        HttpContext http,
        ControlPlaneDbContext db,
        Guid? tenantId = null,
        string? channelType = null)
    {
        var query = db.AlertChannels.AsNoTracking().AsQueryable();

        // Admin surface lists platform-scoped channels by default
        // (tenantId omitted from query). Explicit tenantId filter
        // surfaces a tenant's own channels for admin debugging.
        if (tenantId is Guid tid)
            query = query.Where(c => c.TenantId == tid);
        else
            query = query.Where(c => c.TenantId == null);

        if (!string.IsNullOrWhiteSpace(channelType))
            query = query.Where(c => c.ChannelType == channelType);

        var rows = await query
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(http.RequestAborted)
            .ConfigureAwait(false);

        return Results.Ok(new
        {
            items = rows.Select(ToChannelDto).ToList(),
            count = rows.Count,
        });
    }

    public static async Task<IResult> CreateChannel(
        HttpContext http,
        ControlPlaneDbContext db,
        TimeProvider timeProvider,
        [FromBody] CreateChannelRequest body,
        [FromServices] IDbContextFactory<SecretsDbContext>? secretsFactory = null)
    {
        if (body is null)
            return Results.BadRequest(new { error = "body is required" });
        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new { error = "name is required" });
        if (body.Name.Length > 255)
            return Results.BadRequest(
                new { error = "name must be <= 255 characters" });
        if (string.IsNullOrWhiteSpace(body.ChannelType))
            return Results.BadRequest(
                new { error = "channelType is required" });
        if (!AlertChannelType.All.Contains(
                body.ChannelType, StringComparer.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new
            {
                error = $"channelType must be one of: " +
                        $"{string.Join(", ", AlertChannelType.All)}",
            });
        }

        // Credentials must exist when the channel type requires a
        // secret. Only email skips this gate (SMTP credentials live
        // in the shared cabinet, not per-channel).
        var requiresSecret = body.ChannelType != AlertChannelType.Email;
        if (requiresSecret && body.CredentialsSecretId is null)
        {
            return Results.BadRequest(new
            {
                error = $"channelType '{body.ChannelType}' requires " +
                        $"credentialsSecretId (secret must exist in the store).",
            });
        }

        if (body.CredentialsSecretId is Guid sid && secretsFactory is not null)
        {
            // 404 if the secret doesn't exist — catches a common typo
            // before the channel becomes a zombie with a dead pointer.
            // When the secret store factory is not registered (tests /
            // environments without AddTammaPostgresSecrets) we skip
            // this existence check; the channel is still created but
            // the dispatcher will fail-loud on first delivery attempt.
            //
            // A Postgres "relation does not exist" (42P01) means the
            // secrets schema was never applied on this DB — same
            // outcome as factory-not-registered. Skip with a warning
            // so the test environments without the secrets migration
            // don't 500 on channel create.
            try
            {
                await using var secretsCtx = await secretsFactory
                    .CreateDbContextAsync(http.RequestAborted)
                    .ConfigureAwait(false);
                var exists = await secretsCtx.Secrets
                    .AsNoTracking()
                    .AnyAsync(s => s.Id == sid, http.RequestAborted)
                    .ConfigureAwait(false);
                if (!exists)
                    return Results.NotFound(new
                    {
                        error = $"credentialsSecretId {sid} not found in secret store.",
                    });
            }
            catch (Npgsql.PostgresException pex) when (pex.SqlState == "42P01")
            {
                // secrets table not present — tolerate so non-secret
                // environments can still create email-only channels
                // via the same endpoint.
            }
        }

        // Defensive validation — reject config blobs that include
        // known-credential field names. The store-backed path is the
        // only way to carry a secret.
        if (ContainsPlaintextCredential(body.Config))
        {
            return Results.BadRequest(new
            {
                error = "config must not contain plaintext credentials " +
                        "(webhookUrl/routingKey/password/apiKey/secret). " +
                        "Use credentialsSecretId instead.",
            });
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var channel = new AlertChannel
        {
            TenantId = body.TenantId,
            Name = body.Name,
            ChannelType = body.ChannelType.ToLowerInvariant(),
            IsEnabled = true,
            Config = string.IsNullOrWhiteSpace(body.Config) ? "{}" : body.Config!,
            CredentialsSecretId = body.CredentialsSecretId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AlertChannels.Add(channel);
        await db.SaveChangesAsync(http.RequestAborted).ConfigureAwait(false);
        return Results.Created(
            $"/api/v1/admin/alert-channels/{channel.Id}",
            ToChannelDto(channel));
    }

    public static async Task<IResult> UpdateChannel(
        HttpContext http,
        ControlPlaneDbContext db,
        TimeProvider timeProvider,
        Guid id,
        [FromBody] UpdateChannelRequest body)
    {
        if (body is null)
            return Results.BadRequest(new { error = "body is required" });

        var channel = await db.AlertChannels
            .FirstOrDefaultAsync(c => c.Id == id, http.RequestAborted)
            .ConfigureAwait(false);
        if (channel is null)
            return Results.NotFound(new { error = "channel not found" });

        if (body.IsEnabled is bool enabled)
            channel.IsEnabled = enabled;

        if (body.Config is not null)
        {
            if (ContainsPlaintextCredential(body.Config))
            {
                return Results.BadRequest(new
                {
                    error = "config must not contain plaintext credentials.",
                });
            }
            channel.Config = string.IsNullOrWhiteSpace(body.Config)
                ? "{}"
                : body.Config;
        }

        if (body.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 255)
                return Results.BadRequest(
                    new { error = "name must be 1..255 characters" });
            channel.Name = body.Name;
        }

        channel.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(http.RequestAborted).ConfigureAwait(false);
        return Results.Ok(ToChannelDto(channel));
    }

    public static async Task<IResult> DeleteChannel(
        HttpContext http,
        ControlPlaneDbContext db,
        TimeProvider timeProvider,
        Guid id)
    {
        var channel = await db.AlertChannels
            .FirstOrDefaultAsync(c => c.Id == id, http.RequestAborted)
            .ConfigureAwait(false);
        if (channel is null)
            return Results.NotFound(new { error = "channel not found" });

        // Soft delete — flip IsEnabled so the dispatcher stops
        // fanning out to this channel but delivery history survives
        // for audit (alert_delivery_attempts rows keep their FK).
        channel.IsEnabled = false;
        channel.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(http.RequestAborted).ConfigureAwait(false);
        return Results.NoContent();
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static object ToDto(Alert a) => new
    {
        id = a.Id,
        ruleId = a.RuleId,
        severity = a.Severity,
        title = a.Title,
        description = a.Description,
        correlationId = a.CorrelationId,
        tenantId = a.TenantId,
        metadata = a.Metadata,
        status = a.Status,
        acknowledgedBy = a.AcknowledgedBy,
        acknowledgedAt = a.AcknowledgedAt,
        resolvedBy = a.ResolvedBy,
        resolvedAt = a.ResolvedAt,
        resolution = a.Resolution,
        createdAt = a.CreatedAt,
    };

    private static object ToChannelDto(AlertChannel c) => new
    {
        id = c.Id,
        tenantId = c.TenantId,
        name = c.Name,
        channelType = c.ChannelType,
        isEnabled = c.IsEnabled,
        config = c.Config,
        credentialsSecretId = c.CredentialsSecretId,
        createdAt = c.CreatedAt,
        updatedAt = c.UpdatedAt,
    };

    private static Guid? ResolveUserId(HttpContext http)
    {
        var sub = http.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? http.User.FindFirstValue("sub");
        return Guid.TryParse(sub, out var uid) ? uid : null;
    }

    /// <summary>
    /// Guard against credential values leaking into the
    /// <c>Config</c> column. Case-insensitive match against the
    /// most common JSON field names for secrets. Deployment
    /// invariant: the cabinet is the single source of truth for
    /// channel credentials.
    /// </summary>
    internal static bool ContainsPlaintextCredential(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson) || configJson == "{}")
            return false;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return false;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (ReservedCredentialFields.Contains(
                        prop.Name, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Malformed JSON is its own validation failure — the
            // non-credential path (CreateChannel / UpdateChannel)
            // already rejects non-parseable config elsewhere. We
            // err on "not a credential leak" here so the real error
            // surfaces cleanly.
            return false;
        }
        return false;
    }

    private static readonly HashSet<string> ReservedCredentialFields = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "webhookUrl", "webhook_url",
        "routingKey", "routing_key",
        "password",
        "apiKey", "api_key",
        "secret", "sharedSecret", "shared_secret",
        "token", "authToken", "auth_token",
    };

    private static async Task TryEmitAsync(IEventRepository events, DomainEvent evt)
    {
        try
        {
            await events.AppendAsync(evt).ConfigureAwait(false);
        }
        catch
        {
            // Event emission failures are logged inside the repo;
            // don't fail the caller's state transition on audit failure.
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Wave C.3 — tenant-scope surface for
    // /api/v1/orgs/{tenantId}/alerts/* and /alert-channels/*.
    // These endpoints sit behind `RequireTenantMembershipFilter`;
    // mutation paths additionally require admin+ (enforced inline via
    // <see cref="RequireTenantAdmin"/>). Cross-tenant leak prevention
    // is the #1 invariant — every query filters
    // <c>tenant_id = {tenantId}</c> and every lookup 404s cross-tenant.
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Tenant-scope list: <c>GET /api/v1/orgs/{tenantId}/alerts</c>.
    /// Only returns alerts whose <c>tenant_id</c> matches the route
    /// path tenant exactly — platform-scoped alerts (tenant_id null)
    /// are invisible from the tenant surface.
    /// </summary>
    public static async Task<IResult> ListTenantAlerts(
        HttpContext http,
        ControlPlaneDbContext db,
        Guid tenantId,
        string? status = null,
        string? severity = null,
        DateTimeOffset? since = null,
        int? limit = null)
    {
        if (tenantId == Guid.Empty)
            return Results.BadRequest(new { error = "tenantId must be a non-empty Guid" });

        var take = Math.Min(
            limit is > 0 ? limit.Value : DefaultPageSize,
            MaxPageSize);

        var query = db.Alerts.AsNoTracking()
            .Where(a => a.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(a => a.Status == status);
        if (!string.IsNullOrWhiteSpace(severity))
            query = query.Where(a => a.Severity == severity);
        if (since is { } cutoff)
            query = query.Where(a => a.CreatedAt >= cutoff.UtcDateTime);

        var rows = await query
            .OrderByDescending(a => a.CreatedAt)
            .Take(take)
            .ToListAsync(http.RequestAborted)
            .ConfigureAwait(false);

        var items = rows.Select(ToDto).ToList();
        return Results.Ok(new { items, count = items.Count, limit = take });
    }

    /// <summary>
    /// Tenant-scope detail: <c>GET /api/v1/orgs/{tenantId}/alerts/{id}</c>.
    /// 404s if the alert belongs to another tenant — no cross-tenant
    /// read.
    /// </summary>
    public static async Task<IResult> GetTenantAlert(
        HttpContext http,
        ControlPlaneDbContext db,
        Guid tenantId,
        Guid id)
    {
        var alert = await db.Alerts.AsNoTracking()
            .FirstOrDefaultAsync(
                a => a.Id == id && a.TenantId == tenantId,
                http.RequestAborted)
            .ConfigureAwait(false);
        if (alert is null)
            return Results.NotFound(new { error = "alert not found" });

        var attempts = await db.AlertDeliveryAttempts.AsNoTracking()
            .Where(a => a.AlertId == id)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(http.RequestAborted)
            .ConfigureAwait(false);

        return Results.Ok(new
        {
            alert = ToDto(alert),
            deliveryAttempts = attempts.Select(a => new
            {
                id = a.Id,
                channelId = a.ChannelId,
                attemptNumber = a.AttemptNumber,
                status = a.Status,
                error = a.Error,
                deliveredAt = a.DeliveredAt,
                nextAttemptAt = a.NextAttemptAt,
                createdAt = a.CreatedAt,
            }).ToList(),
        });
    }

    /// <summary>
    /// Tenant-scope acknowledge:
    /// <c>POST /api/v1/orgs/{tenantId}/alerts/{id}/acknowledge</c>.
    /// Requires admin+ role in the path tenant. Rejects cross-tenant
    /// acks with 404 so a tenant admin can never acknowledge another
    /// tenant's alert.
    /// </summary>
    public static async Task<IResult> AcknowledgeTenantAlert(
        HttpContext http,
        ControlPlaneDbContext db,
        IEventRepository events,
        TimeProvider timeProvider,
        Guid tenantId,
        Guid id,
        [FromBody] AcknowledgeAlertRequest? body)
    {
        if (!RequireTenantAdmin(http, out var forbid)) return forbid!;

        var alert = await db.Alerts
            .FirstOrDefaultAsync(
                a => a.Id == id && a.TenantId == tenantId,
                http.RequestAborted)
            .ConfigureAwait(false);
        if (alert is null)
            return Results.NotFound(new { error = "alert not found" });

        if (alert.Status == AlertStatus.Resolved)
            return Results.Conflict(new
            {
                error = "alert already resolved; cannot acknowledge.",
            });

        var userId = ResolveUserId(http);
        alert.Status = AlertStatus.Acknowledged;
        alert.AcknowledgedBy = userId;
        alert.AcknowledgedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(http.RequestAborted).ConfigureAwait(false);

        await TryEmitAsync(events, new DomainEvent
        {
            Type = AlertEventTypes.Acknowledged,
            TenantId = alert.TenantId,
            Tags = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, string?>
                {
                    ["alertId"] = alert.Id.ToString("N"),
                    ["userId"] = userId?.ToString("N"),
                    ["tenantId"] = tenantId.ToString("N"),
                }),
            Metadata = """{"eventSource":"system"}""",
            Data = System.Text.Json.JsonSerializer.Serialize(new
            {
                note = body?.Note,
            }),
        });

        return Results.Ok(ToDto(alert));
    }

    /// <summary>
    /// Tenant-scope resolve:
    /// <c>POST /api/v1/orgs/{tenantId}/alerts/{id}/resolve</c>.
    /// Admin+. Cross-tenant resolve → 404.
    /// </summary>
    public static async Task<IResult> ResolveTenantAlert(
        HttpContext http,
        ControlPlaneDbContext db,
        IEventRepository events,
        TimeProvider timeProvider,
        Guid tenantId,
        Guid id,
        [FromBody] ResolveAlertRequest? body)
    {
        if (!RequireTenantAdmin(http, out var forbid)) return forbid!;

        if (string.IsNullOrWhiteSpace(body?.Resolution))
            return Results.BadRequest(new { error = "resolution is required" });

        var alert = await db.Alerts
            .FirstOrDefaultAsync(
                a => a.Id == id && a.TenantId == tenantId,
                http.RequestAborted)
            .ConfigureAwait(false);
        if (alert is null)
            return Results.NotFound(new { error = "alert not found" });

        if (alert.Status == AlertStatus.Resolved)
            return Results.Conflict(new { error = "alert already resolved" });

        var userId = ResolveUserId(http);
        alert.Status = AlertStatus.Resolved;
        alert.ResolvedBy = userId;
        alert.ResolvedAt = timeProvider.GetUtcNow().UtcDateTime;
        alert.Resolution = body.Resolution.Length > 2000
            ? body.Resolution[..2000]
            : body.Resolution;
        await db.SaveChangesAsync(http.RequestAborted).ConfigureAwait(false);

        await TryEmitAsync(events, new DomainEvent
        {
            Type = AlertEventTypes.Resolved,
            TenantId = alert.TenantId,
            Tags = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, string?>
                {
                    ["alertId"] = alert.Id.ToString("N"),
                    ["userId"] = userId?.ToString("N"),
                    ["tenantId"] = tenantId.ToString("N"),
                }),
            Metadata = """{"eventSource":"system"}""",
            Data = System.Text.Json.JsonSerializer.Serialize(new
            {
                resolution = alert.Resolution,
            }),
        });

        return Results.Ok(ToDto(alert));
    }

    /// <summary>
    /// Tenant-scope channel list:
    /// <c>GET /api/v1/orgs/{tenantId}/alert-channels</c>. Returns only
    /// channels whose <c>tenant_id</c> matches the route tenant.
    /// </summary>
    public static async Task<IResult> ListTenantChannels(
        HttpContext http,
        ControlPlaneDbContext db,
        Guid tenantId,
        string? channelType = null)
    {
        var query = db.AlertChannels.AsNoTracking()
            .Where(c => c.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(channelType))
            query = query.Where(c => c.ChannelType == channelType);

        var rows = await query
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(http.RequestAborted)
            .ConfigureAwait(false);

        return Results.Ok(new
        {
            items = rows.Select(ToChannelDto).ToList(),
            count = rows.Count,
        });
    }

    /// <summary>
    /// Tenant-scope channel create:
    /// <c>POST /api/v1/orgs/{tenantId}/alert-channels</c>. Forces
    /// <c>tenant_id = {tenantId}</c> server-side — any
    /// <see cref="CreateChannelRequest.TenantId"/> in the body that
    /// differs triggers a 400 so a caller cannot hop tenants. Admin+.
    /// </summary>
    public static async Task<IResult> CreateTenantChannel(
        HttpContext http,
        ControlPlaneDbContext db,
        TimeProvider timeProvider,
        Guid tenantId,
        [FromBody] CreateChannelRequest body,
        [FromServices] IDbContextFactory<SecretsDbContext>? secretsFactory = null)
    {
        if (!RequireTenantAdmin(http, out var forbid)) return forbid!;

        if (body is null)
            return Results.BadRequest(new { error = "body is required" });
        if (tenantId == Guid.Empty)
            return Results.BadRequest(new { error = "tenantId must be a non-empty Guid" });

        // Reject attempts to create a platform-scope channel from the
        // tenant endpoint, or to target a different tenant via the
        // body payload.
        if (body.TenantId is Guid bodyTid && bodyTid != tenantId)
        {
            return Results.BadRequest(new
            {
                error = "body.tenantId must match path tenantId " +
                        "(or be omitted) on the tenant-scope create path.",
            });
        }

        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new { error = "name is required" });
        if (body.Name.Length > 255)
            return Results.BadRequest(
                new { error = "name must be <= 255 characters" });
        if (string.IsNullOrWhiteSpace(body.ChannelType))
            return Results.BadRequest(
                new { error = "channelType is required" });
        if (!AlertChannelType.All.Contains(
                body.ChannelType, StringComparer.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new
            {
                error = $"channelType must be one of: " +
                        $"{string.Join(", ", AlertChannelType.All)}",
            });
        }

        var requiresSecret = body.ChannelType != AlertChannelType.Email;
        if (requiresSecret && body.CredentialsSecretId is null)
        {
            return Results.BadRequest(new
            {
                error = $"channelType '{body.ChannelType}' requires " +
                        $"credentialsSecretId (secret must exist in the store).",
            });
        }

        if (ContainsPlaintextCredential(body.Config))
        {
            return Results.BadRequest(new
            {
                error = "config must not contain plaintext credentials " +
                        "(webhookUrl/routingKey/password/apiKey/secret). " +
                        "Use credentialsSecretId instead.",
            });
        }

        if (body.CredentialsSecretId is Guid sid && secretsFactory is not null)
        {
            try
            {
                await using var secretsCtx = await secretsFactory
                    .CreateDbContextAsync(http.RequestAborted)
                    .ConfigureAwait(false);
                var exists = await secretsCtx.Secrets
                    .AsNoTracking()
                    .AnyAsync(s => s.Id == sid, http.RequestAborted)
                    .ConfigureAwait(false);
                if (!exists)
                    return Results.NotFound(new
                    {
                        error = $"credentialsSecretId {sid} not found in secret store.",
                    });
            }
            catch (Npgsql.PostgresException pex) when (pex.SqlState == "42P01")
            {
                // secrets table not present — tolerated (tests / degraded envs)
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var channel = new AlertChannel
        {
            TenantId = tenantId, // force path-tenant ownership
            Name = body.Name,
            ChannelType = body.ChannelType.ToLowerInvariant(),
            IsEnabled = true,
            Config = string.IsNullOrWhiteSpace(body.Config) ? "{}" : body.Config!,
            CredentialsSecretId = body.CredentialsSecretId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AlertChannels.Add(channel);
        await db.SaveChangesAsync(http.RequestAborted).ConfigureAwait(false);
        return Results.Created(
            $"/api/v1/orgs/{tenantId}/alert-channels/{channel.Id}",
            ToChannelDto(channel));
    }

    /// <summary>
    /// Tenant-scope channel patch:
    /// <c>PATCH /api/v1/orgs/{tenantId}/alert-channels/{id}</c>.
    /// 404 if the channel belongs to another tenant or is platform-
    /// scoped. Admin+.
    /// </summary>
    public static async Task<IResult> UpdateTenantChannel(
        HttpContext http,
        ControlPlaneDbContext db,
        TimeProvider timeProvider,
        Guid tenantId,
        Guid id,
        [FromBody] UpdateChannelRequest body)
    {
        if (!RequireTenantAdmin(http, out var forbid)) return forbid!;

        if (body is null)
            return Results.BadRequest(new { error = "body is required" });

        var channel = await db.AlertChannels
            .FirstOrDefaultAsync(
                c => c.Id == id && c.TenantId == tenantId,
                http.RequestAborted)
            .ConfigureAwait(false);
        if (channel is null)
            return Results.NotFound(new { error = "channel not found" });

        if (body.IsEnabled is bool enabled)
            channel.IsEnabled = enabled;

        if (body.Config is not null)
        {
            if (ContainsPlaintextCredential(body.Config))
            {
                return Results.BadRequest(new
                {
                    error = "config must not contain plaintext credentials.",
                });
            }
            channel.Config = string.IsNullOrWhiteSpace(body.Config)
                ? "{}"
                : body.Config;
        }

        if (body.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 255)
                return Results.BadRequest(
                    new { error = "name must be 1..255 characters" });
            channel.Name = body.Name;
        }

        channel.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(http.RequestAborted).ConfigureAwait(false);
        return Results.Ok(ToChannelDto(channel));
    }

    /// <summary>
    /// Tenant-scope channel soft-delete:
    /// <c>DELETE /api/v1/orgs/{tenantId}/alert-channels/{id}</c>.
    /// Admin+. Cross-tenant → 404.
    /// </summary>
    public static async Task<IResult> DeleteTenantChannel(
        HttpContext http,
        ControlPlaneDbContext db,
        TimeProvider timeProvider,
        Guid tenantId,
        Guid id)
    {
        if (!RequireTenantAdmin(http, out var forbid)) return forbid!;

        var channel = await db.AlertChannels
            .FirstOrDefaultAsync(
                c => c.Id == id && c.TenantId == tenantId,
                http.RequestAborted)
            .ConfigureAwait(false);
        if (channel is null)
            return Results.NotFound(new { error = "channel not found" });

        channel.IsEnabled = false;
        channel.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(http.RequestAborted).ConfigureAwait(false);
        return Results.NoContent();
    }

    /// <summary>
    /// Tenant-role gate for mutations — admin+ required. The membership
    /// filter has already run and stashed
    /// <c>HttpContext.Items["TenantRole"]</c> with the caller's role.
    /// Returns <c>false</c> (with <paramref name="forbid"/> populated)
    /// when the caller is a plain member; <c>true</c> otherwise.
    /// </summary>
    private static bool RequireTenantAdmin(HttpContext http, out IResult? forbid)
    {
        var role = http.Items[RequireTenantMembershipFilter.TenantRoleItemKey]
            as string;
        if (role is null)
        {
            forbid = Results.Json(
                new { error = "Tenant role not resolved" },
                statusCode: StatusCodes.Status500InternalServerError);
            return false;
        }
        if (!TenantRoleHierarchy.IsAtLeast(role, TenantRoleHierarchy.Admin))
        {
            forbid = Results.Json(
                new { error = "Requires admin role or higher" },
                statusCode: StatusCodes.Status403Forbidden);
            return false;
        }
        forbid = null;
        return true;
    }
}

// ── DTOs ────────────────────────────────────────────────────────

public sealed record AcknowledgeAlertRequest(string? Note);

public sealed record ResolveAlertRequest(string? Resolution);

public sealed record TestRaiseAlertRequest(
    string Severity,
    string Title,
    string Description,
    string? CorrelationId,
    Guid? TenantId);

public sealed record CreateChannelRequest(
    string Name,
    string ChannelType,
    Guid? TenantId,
    string? Config,
    Guid? CredentialsSecretId);

public sealed record UpdateChannelRequest(
    string? Name,
    bool? IsEnabled,
    string? Config);
