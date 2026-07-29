using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Services.PromptStore;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Endpoints.Admin;

/// <summary>
/// Story 41-30 (D8) — the closed allowlist of workflow definition ids the
/// scheduled-trigger admin API accepts. An admin-writable <c>definition_id</c>
/// is otherwise an arbitrary-workflow-dispatch primitive — a privilege-
/// escalation surface (a tenant admin could schedule <c>delete-tenant</c>).
/// Enforced server-side in BOTH operating modes; do NOT relax this to "any
/// registered definition".
///
/// <para>The five Wave-2 consumers (41-11 / 41-16 / 41-17 sweep / 41-20 /
/// 41-23) are REGISTERABLE here, not registered — this story ships none of
/// their bindings; each consumer story adds its own schedule rows.</para>
/// </summary>
public static class SchedulableDefinitions
{
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        // 41-11 — tech-debt sweep
        "tech-debt-triage",
        // 41-16 — regression / flaky-test management
        "regression-management",
        // 41-17 — the PR-triage scheduled sweep half
        "pr-triage-sweep",
        // 41-20 — scheduled security audit
        "security-audit",
        // 41-23 — capacity & health review
        "capacity-review",
    };
}

/// <summary>
/// Story 41-30 (D8) — admin CRUD + run-now over <c>scheduled_triggers</c>.
/// Static minimal-API handlers registered in Program.cs under
/// <c>/api/admin/scheduled-triggers</c> (the <c>AdminTenantDatabasesEndpoints</c>
/// shape).
///
/// <para><b>Per-mode RBAC, answered separately</b> (CLAUDE.md's universal
/// rule):</para>
/// <list type="bullet">
///   <item><b>single-user:</b> the sole user owns their triggers — any
///     authenticated caller reads and writes everything, including
///     <c>tenant_id IS NULL</c> template rows. No further RBAC.</item>
///   <item><b>SaaS:</b> a TEMPLATE row (<c>tenant_id IS NULL</c>) is
///     platform-owner only (<c>platformRole=platform_admin</c>) — a tenant
///     must not write a row that materialises into every other tenant. A
///     CONCRETE row is writable by <c>tenant_owner</c>/<c>tenant_admin</c>
///     for THEIR OWN tenant (the <c>ScheduleManage</c> policy /
///     <c>schedules:manage</c> permission — member gets 403 at the policy);
///     another tenant's row reads/writes as 404 (no existence leak). Reads
///     are any-member, scoped to own tenant + templates; a platform admin
///     sees all rows.</item>
/// </list>
///
/// <para><b>Write-time validation</b> (AC5): malformed cron ⇒ typed 400
/// (<c>invalid_cron_expression</c>) and NO row written — a fire-time throw
/// is structurally impossible for API-written rows. <c>definition_id</c>
/// outside <see cref="SchedulableDefinitions.Allowed"/> ⇒ 400.</para>
/// </summary>
public static class ScheduledTriggerEndpoints
{
    // ── DTOs ──

    public sealed record ScheduledTriggerUpsertRequest(
        Guid? TenantId,
        string? DefinitionId,
        string? Name,
        string? CronExpression,
        bool? Enabled,
        JsonElement? Input);

    public sealed record ScheduledTriggerResponse(
        Guid Id,
        Guid? TenantId,
        string DefinitionId,
        string Name,
        string CronExpression,
        bool Enabled,
        JsonElement Input,
        DateTime? NextDueAt,
        string? LastWindowKey,
        DateTime? LastFiredAt,
        DateTime CreatedAt,
        DateTime UpdatedAt);

    // ── GET /api/admin/scheduled-triggers ──

    public static async Task<IResult> List(
        ControlPlaneDbContext db,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider,
        CancellationToken ct = default)
    {
        var query = db.ScheduledTriggers.AsNoTracking();

        if (modeProvider.Mode == TammaMode.SaaS && !IsPlatformAdmin(principal))
        {
            var tenantId = CallerTenantId(principal, tenantContext);
            query = query.Where(t => t.TenantId == null || t.TenantId == tenantId);
        }

        var rows = await query
            .OrderBy(t => t.TenantId).ThenBy(t => t.Name)
            .ToListAsync(ct);
        return Results.Ok(rows.Select(ToResponse).ToList());
    }

    // ── GET /api/admin/scheduled-triggers/{id} ──

    public static async Task<IResult> Get(
        Guid id,
        ControlPlaneDbContext db,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider,
        CancellationToken ct = default)
    {
        var row = await db.ScheduledTriggers.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (row is null || !CanRead(row, principal, tenantContext, modeProvider))
            return NotFound();
        return Results.Ok(ToResponse(row));
    }

    // ── POST /api/admin/scheduled-triggers ──

    public static async Task<IResult> Create(
        ScheduledTriggerUpsertRequest req,
        ControlPlaneDbContext db,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider,
        IPlatformEventPublisher events,
        TimeProvider timeProvider,
        CancellationToken ct = default)
    {
        if (Validate(req, requireAll: true) is { } invalid) return invalid;
        if (WriteGate(req.TenantId, principal, tenantContext, modeProvider) is { } forbidden)
            return forbidden;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var row = new ScheduledTrigger
        {
            Id = Guid.NewGuid(),
            TenantId = req.TenantId,
            DefinitionId = req.DefinitionId!.Trim(),
            Name = req.Name!.Trim(),
            CronExpression = req.CronExpression!.Trim(),
            Enabled = req.Enabled ?? true,
            InputJson = InputJsonOf(req),
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = TryGetUserId(principal),
        };
        db.ScheduledTriggers.Add(row);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            return Results.Json(
                new
                {
                    error = "duplicate_schedule",
                    message = "A schedule already exists for this (tenant, definition, name).",
                },
                statusCode: StatusCodes.Status409Conflict);
        }

        await EmitChangedAsync(events, row, "created", principal, ct);
        return Results.Created($"/api/admin/scheduled-triggers/{row.Id}", ToResponse(row));
    }

    // ── PUT /api/admin/scheduled-triggers/{id} ──

    public static async Task<IResult> Update(
        Guid id,
        ScheduledTriggerUpsertRequest req,
        ControlPlaneDbContext db,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider,
        IPlatformEventPublisher events,
        TimeProvider timeProvider,
        CancellationToken ct = default)
    {
        var row = await db.ScheduledTriggers.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (row is null || !CanRead(row, principal, tenantContext, modeProvider))
            return NotFound();
        if (WriteGate(row.TenantId, principal, tenantContext, modeProvider) is { } forbidden)
            return forbidden;
        if (Validate(req, requireAll: false) is { } invalid) return invalid;

        // The principal (TenantId) of a row is immutable — re-homing a
        // schedule to another tenant is a delete + create, not a PUT.
        if (req.DefinitionId is not null) row.DefinitionId = req.DefinitionId.Trim();
        if (req.Name is not null) row.Name = req.Name.Trim();
        if (req.CronExpression is not null) row.CronExpression = req.CronExpression.Trim();
        if (req.Enabled is not null) row.Enabled = req.Enabled.Value;
        if (req.Input is not null) row.InputJson = InputJsonOf(req);
        row.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync(ct);
        await EmitChangedAsync(events, row, "updated", principal, ct);
        return Results.Ok(ToResponse(row));
    }

    // ── DELETE /api/admin/scheduled-triggers/{id} ──

    public static async Task<IResult> Delete(
        Guid id,
        ControlPlaneDbContext db,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider,
        IPlatformEventPublisher events,
        CancellationToken ct = default)
    {
        var row = await db.ScheduledTriggers.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (row is null || !CanRead(row, principal, tenantContext, modeProvider))
            return NotFound();
        if (WriteGate(row.TenantId, principal, tenantContext, modeProvider) is { } forbidden)
            return forbidden;

        db.ScheduledTriggers.Remove(row);
        await db.SaveChangesAsync(ct);
        await EmitChangedAsync(events, row, "deleted", principal, ct);
        return Results.NoContent();
    }

    // ── POST /api/admin/scheduled-triggers/{id}/run-now ──

    /// <summary>
    /// D8 — claim a synthetic <c>manual:{timestamp}</c> window in the fire
    /// ledger so a manual run can never collide with (or suppress) a cron
    /// window. The engine's tick drains the claim through the same dispatch
    /// + stamp path. Templates cannot be run — they are materialised, never
    /// fired (D6).
    /// </summary>
    public static async Task<IResult> RunNow(
        Guid id,
        ControlPlaneDbContext db,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider,
        IPlatformEventPublisher events,
        TimeProvider timeProvider,
        CancellationToken ct = default)
    {
        var row = await db.ScheduledTriggers.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (row is null || !CanRead(row, principal, tenantContext, modeProvider))
            return NotFound();
        if (WriteGate(row.TenantId, principal, tenantContext, modeProvider) is { } forbidden)
            return forbidden;
        if (row.TenantId is null)
        {
            return Results.BadRequest(new
            {
                error = "template_not_runnable",
                message = "A platform template row is materialised per tenant, never fired. "
                    + "Run the tenant's concrete schedule instead.",
            });
        }

        // 2026-07-29 contract decision: run-now on a DISABLED trigger is a
        // 409, and the engine's manual drain only dispatches enabled
        // triggers' claims — otherwise a claim would sit pending invisibly
        // (or, worse, fire a schedule an admin explicitly switched off).
        if (!row.Enabled)
        {
            return Results.Json(
                new
                {
                    error = "trigger_disabled",
                    message = "This schedule is disabled; the engine only drains run-now claims "
                        + "for enabled schedules. Enable the schedule first, then run it.",
                },
                statusCode: StatusCodes.Status409Conflict);
        }

        var now = timeProvider.GetUtcNow();
        var fire = new ScheduledTriggerFire
        {
            Id = Guid.NewGuid(),
            TriggerId = row.Id,
            TenantId = row.TenantId.Value,
            DefinitionId = row.DefinitionId,
            WindowKey = $"manual:{now.UtcDateTime:yyyyMMdd'T'HHmmss.fff'Z'}",
            ClaimedAt = now.UtcDateTime,
            Outcome = "claimed",
        };
        db.ScheduledTriggerFires.Add(fire);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Two run-now requests in the SAME millisecond produce the same
            // manual:{timestamp} window key — the ledger's unique
            // (TriggerId, WindowKey) index rejects the second. 409, mirroring
            // Create's duplicate handling; the first claim stands.
            return Results.Json(
                new
                {
                    error = "duplicate_run_now",
                    message = "A run-now claim with the same window key already exists "
                        + "(same-millisecond duplicate). The earlier claim will fire; retry "
                        + "if another run is genuinely wanted.",
                },
                statusCode: StatusCodes.Status409Conflict);
        }

        await EmitChangedAsync(events, row, "run-now", principal, ct);
        return Results.Accepted(
            $"/api/admin/scheduled-triggers/{row.Id}",
            new { fireId = fire.Id, windowKey = fire.WindowKey, status = "claimed" });
    }

    // ── helpers ──

    private static IResult NotFound() =>
        Results.NotFound(new { error = "scheduled_trigger_not_found" });

    private static bool IsPlatformAdmin(ClaimsPrincipal principal) =>
        string.Equals(
            principal.FindFirst("platformRole")?.Value, "platform_admin",
            StringComparison.Ordinal);

    private static Guid? TryGetUserId(ClaimsPrincipal principal) =>
        Guid.TryParse(
            principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? principal.FindFirst("sub")?.Value,
            out var id) ? id : null;

    /// <summary>
    /// The caller's tenant. <c>/api/admin/*</c> is a tenant-free path prefix
    /// (TenantContextMiddleware deliberately skips binding there), so
    /// <see cref="ITenantContext"/> is usually empty on these routes — fall
    /// back to the same JWT claims the middleware itself reads
    /// (<c>active_tenant_id</c> / <c>tenantId</c> / <c>tid</c>).
    /// </summary>
    private static Guid? CallerTenantId(ClaimsPrincipal principal, ITenantContext tenantContext)
    {
        if (tenantContext.TenantId is Guid bound) return bound;
        foreach (var claim in new[] { "active_tenant_id", "tenantId", "tid" })
        {
            if (Guid.TryParse(principal.FindFirst(claim)?.Value, out var id))
                return id;
        }
        return null;
    }

    /// <summary>Read visibility: see the class doc's per-mode matrix.</summary>
    private static bool CanRead(
        ScheduledTrigger row,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        if (modeProvider.Mode != TammaMode.SaaS) return true;
        if (IsPlatformAdmin(principal)) return true;
        return row.TenantId is null || row.TenantId == CallerTenantId(principal, tenantContext);
    }

    /// <summary>
    /// Write gate on top of the route-level <c>ScheduleManage</c> policy
    /// (which already 403s SaaS member-role callers). Returns a 403 result,
    /// or null when the write may proceed. Single-user mode: no gate — the
    /// sole user owns everything (D8).
    /// </summary>
    private static IResult? WriteGate(
        Guid? rowTenantId,
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider)
    {
        if (modeProvider.Mode != TammaMode.SaaS) return null;
        if (IsPlatformAdmin(principal)) return null;

        if (rowTenantId is null)
        {
            // A template materialises into EVERY tenant — platform-owner only.
            return Results.Json(
                new
                {
                    error = "platform_template_forbidden",
                    message = "A tenant_id-null template row is platform-owner only.",
                },
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (rowTenantId != CallerTenantId(principal, tenantContext))
        {
            // Another tenant's schedule — no existence leak.
            return NotFound();
        }

        return null;
    }

    /// <summary>
    /// AC5 + D8 validation. <paramref name="requireAll"/> distinguishes POST
    /// (all fields required) from PUT (partial). Any failure ⇒ typed 400 and
    /// NO row written.
    /// </summary>
    private static IResult? Validate(ScheduledTriggerUpsertRequest req, bool requireAll)
    {
        if (requireAll || req.DefinitionId is not null)
        {
            if (string.IsNullOrWhiteSpace(req.DefinitionId))
                return Results.BadRequest(new { error = "definition_id_required" });
            if (!SchedulableDefinitions.Allowed.Contains(req.DefinitionId.Trim()))
                return Results.BadRequest(new
                {
                    error = "definition_not_schedulable",
                    message = "definitionId is not in the closed allowlist of schedulable "
                        + "workflow definitions.",
                    allowed = SchedulableDefinitions.Allowed.OrderBy(d => d, StringComparer.Ordinal),
                });
        }

        if (requireAll || req.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "name_required" });
        }

        if (requireAll || req.CronExpression is not null)
        {
            if (string.IsNullOrWhiteSpace(req.CronExpression))
                return Results.BadRequest(new { error = "cron_expression_required" });
            // Write-time cron validation (AC5): standard 5-field, UTC. The
            // engine re-parses with the same library (Cronos — the parser
            // Elsa.Scheduling itself uses), so accept-here ⇒ parse-there.
            if (!TryParseCron(req.CronExpression.Trim(), out var cronError))
                return Results.BadRequest(new
                {
                    error = "invalid_cron_expression",
                    message = cronError,
                });
        }

        if (req.Input is { } input && input.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined))
        {
            return Results.BadRequest(new
            {
                error = "invalid_input",
                message = "input must be a JSON object (it is merged into the dispatch inputs).",
            });
        }

        return null;
    }

    private static bool TryParseCron(string cron, out string? error)
    {
        try
        {
            _ = Cronos.CronExpression.Parse(cron, Cronos.CronFormat.Standard);
            error = null;
            return true;
        }
        catch (Cronos.CronFormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string InputJsonOf(ScheduledTriggerUpsertRequest req) =>
        req.Input is { ValueKind: JsonValueKind.Object } input
            ? input.GetRawText()
            : "{}";

    private static ScheduledTriggerResponse ToResponse(ScheduledTrigger t) =>
        new(
            t.Id,
            t.TenantId,
            t.DefinitionId,
            t.Name,
            t.CronExpression,
            t.Enabled,
            JsonDocument.Parse(string.IsNullOrWhiteSpace(t.InputJson) ? "{}" : t.InputJson)
                .RootElement.Clone(),
            t.NextDueAt,
            t.LastWindowKey,
            t.LastFiredAt,
            t.CreatedAt,
            t.UpdatedAt);

    /// <summary>SCHEDULE.TRIGGER.CHANGED — the admin mutation audit row.</summary>
    private static async Task EmitChangedAsync(
        IPlatformEventPublisher events,
        ScheduledTrigger row,
        string change,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        try
        {
            await events.AppendAndPublishAsync(new PlatformEvent
            {
                Type = Tamma.Activities.Scheduling.ScheduleEvents.TriggerChanged,
                TenantId = row.TenantId,
                UserId = TryGetUserId(principal),
                Tags = JsonSerializer.Serialize(new
                {
                    tenantId = row.TenantId?.ToString("D"),
                    definitionId = row.DefinitionId,
                    triggerId = row.Id.ToString("D"),
                    status = "success",
                }),
                Metadata = "{\"eventSource\":\"system\",\"emitter\":\"ScheduledTriggerEndpoints\"}",
                Data = JsonSerializer.Serialize(new
                {
                    change,
                    name = row.Name,
                    cronExpression = row.CronExpression,
                    enabled = row.Enabled,
                }),
            }, ct);
        }
        catch
        {
            // Best-effort audit append — the admin mutation itself already
            // committed; the publisher logs its own failures.
        }
    }
}
