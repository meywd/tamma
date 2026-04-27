using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 28-5 AC6 — public tenant-status polling endpoint
/// (<c>GET /api/v1/tenants/{id}/status</c>). The frontend onboarding
/// flow polls this every ~2s while the user's tenant is provisioning;
/// the response carries the step ladder (which steps started, which
/// completed, which failed) and an estimated completion time.
///
/// <para><b>Authorization</b>: caller must have a membership on the
/// requested tenant id, OR be a platform owner. The endpoint is
/// accessible while the tenant is in <c>provisioning</c> /
/// <c>pending_verification</c> states (Doc 03 §6.3 — the user just
/// signed up + needs to see progress before <c>active</c>).</para>
///
/// <para><b>Response shape</b>: matches Doc 03 §6.3 — tenantId, status,
/// startedAt, completedAt, estimatedCompletion, currentStep,
/// correlationId, steps[]. The step ladder is folded from
/// <c>platform_events</c> rows tagged with this tenant id, looking for
/// <c>TENANT.PROVISION.STEP_*</c> + <c>TENANT.DELETE.STEP_*</c>
/// markers and stitching them by the <c>step</c> tag.</para>
///
/// <para><b>Estimated completion</b>: derived from a rolling p50 stored
/// in CP (<c>provisioning_p50_ms</c> gauge — populated by Story 28-10's
/// analytics rollup on <c>TENANT.PROVISIONED.SUCCESS</c>). When the
/// gauge isn't populated yet (fresh install), falls back to
/// <c>startedAt + 45s</c> (the median observed during dev).</para>
/// </summary>
public static class TenantStatusEndpoint
{
    /// <summary>Default fallback for estimated completion when the
    /// rolling p50 gauge isn't populated.</summary>
    private static readonly TimeSpan FallbackProvisioningEstimate =
        TimeSpan.FromSeconds(45);

    public static async Task<IResult> GetStatus(
        Guid tenantId,
        ControlPlaneDbContext db,
        HttpContext http,
        CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            return Results.BadRequest(new { error = "tenant_id_required" });

        // Authorize: caller must have membership OR be platform owner.
        // Accessible during provisioning so the onboarding flow can poll
        // while the user's tenant is being built.
        var userId = TryGetUserId(http);
        if (userId is null)
            return Results.Unauthorized();

        var isPlatformOwner = http.User.HasClaim("isPlatformOwner", "true")
            || http.User.IsInRole("platform_owner");

        if (!isPlatformOwner)
        {
            var hasMembership = await db.TenantMemberships
                .AsNoTracking()
                .AnyAsync(m => m.TenantId == tenantId && m.UserId == userId.Value, ct);
            if (!hasMembership)
                return Results.NotFound(new { error = "tenant_not_found" });
        }

        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.DeletedAt == null, ct);
        if (tenant is null)
            return Results.NotFound(new { error = "tenant_not_found" });

        var status = (string?)db.Entry(tenant).Property("Status").CurrentValue
            ?? "active";
        var startedAt = (DateTime?)db.Entry(tenant)
            .Property("ProvisioningStartedAt").CurrentValue
            ?? tenant.CreatedAt;
        var completedAt = (DateTime?)db.Entry(tenant)
            .Property("ProvisioningCompletedAt").CurrentValue;

        // Fold platform_events into a step ladder. We pull the most
        // recent ~50 STEP_* events for this tenant and reduce to the
        // latest state per step (started → completed → failed). 50 is
        // generous for the 11-step provisioning + 5-step delete flows
        // even with a few attempts.
        var stepEvents = await db.PlatformEvents
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId
                        && (e.Type == "TENANT.PROVISION.STEP_STARTED"
                            || e.Type == "TENANT.PROVISION.STEP_COMPLETED"
                            || e.Type == "TENANT.PROVISION.STEP_FAILED"
                            || e.Type == "TENANT.DELETE.STEP_STARTED"
                            || e.Type == "TENANT.DELETE.STEP_COMPLETED"
                            || e.Type == "TENANT.DELETE.STEP_FAILED"))
            .OrderByDescending(e => e.CreatedAt)
            .Take(100)
            .Select(e => new StepEvent(e.Type, e.Tags, e.CreatedAt))
            .ToListAsync(ct);

        var ladder = BuildStepLadder(stepEvents);
        var currentStep = ladder
            .Where(s => s.State == "running")
            .OrderByDescending(s => s.UpdatedAt)
            .FirstOrDefault()?.Step;

        // Estimated completion: rolling p50 gauge or fallback.
        var p50Ms = await db.PlatformEvents
            .AsNoTracking()
            .Where(e => e.Type == "PROVISIONING.P50_GAUGE")
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => e.Data)
            .FirstOrDefaultAsync(ct);
        var estimatedCompletion = ComputeEstimatedCompletion(
            startedAt,
            completedAt,
            status,
            p50Ms);

        // Correlation id: use the most recent PROVISIONING_REQUESTED
        // event's id. Stable across the whole provisioning attempt; the
        // frontend uses it to thread support tickets back to a workflow.
        var correlationId = await db.PlatformEvents
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId
                        && e.Type == "TENANT.PROVISIONING_REQUESTED")
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(ct);

        return Results.Ok(new TenantStatusResponse(
            TenantId: tenantId,
            Status: status,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            EstimatedCompletion: estimatedCompletion,
            CurrentStep: currentStep,
            CorrelationId: correlationId,
            Steps: ladder));
    }

    /// <summary>
    /// Strongly-typed projection of the platform_events row shape consumed
    /// by <see cref="BuildStepLadder"/>. Keeping this typed (instead of
    /// projecting into an anonymous type and feeding the reducer
    /// <c>IEnumerable&lt;dynamic&gt;</c>) lets nullable analysis run on
    /// every member access in the reducer and makes the reducer
    /// directly unit-testable from the test project.
    /// </summary>
    /// <param name="Type">Event type — one of the
    /// <c>TENANT.PROVISION.STEP_*</c> / <c>TENANT.DELETE.STEP_*</c>
    /// markers.</param>
    /// <param name="Tags">JSONB <c>tags</c> column. May be null or
    /// <c>{}</c>; the <c>step</c> tag is the only one read.</param>
    /// <param name="CreatedAt">Event wall-clock timestamp.</param>
    internal sealed record StepEvent(
        string Type,
        string? Tags,
        DateTime CreatedAt);

    internal static List<TenantStepStatus> BuildStepLadder(
        IEnumerable<StepEvent> events)
    {
        // Reduce: per (step), the LATEST event wins. Started → running.
        // Completed → done. Failed → failed.
        var byStep = new Dictionary<string, TenantStepStatus>(StringComparer.Ordinal);
        // Iterate oldest-to-newest so the latest event overwrites.
        foreach (var e in events.OrderBy(x => x.CreatedAt))
        {
            string? step = TryGetTag(e.Tags, "step");
            if (step is null) continue;

            string state = e.Type switch
            {
                "TENANT.PROVISION.STEP_STARTED" => "running",
                "TENANT.DELETE.STEP_STARTED" => "running",
                "TENANT.PROVISION.STEP_COMPLETED" => "done",
                "TENANT.DELETE.STEP_COMPLETED" => "done",
                "TENANT.PROVISION.STEP_FAILED" => "failed",
                "TENANT.DELETE.STEP_FAILED" => "failed",
                _ => "unknown",
            };
            byStep[step] = new TenantStepStatus(
                Step: step,
                State: state,
                UpdatedAt: e.CreatedAt);
        }
        return byStep.Values.OrderBy(s => s.UpdatedAt).ToList();
    }

    private static string? TryGetTag(string? tagsJson, string key)
    {
        if (string.IsNullOrWhiteSpace(tagsJson) || tagsJson == "{}")
            return null;
        try
        {
            using var doc = JsonDocument.Parse(tagsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty(key, out var v)
                && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTime? ComputeEstimatedCompletion(
        DateTime startedAt,
        DateTime? completedAt,
        string status,
        string? p50DataJson)
    {
        if (completedAt.HasValue) return completedAt;
        if (status == "active" || status == "deleted" || status == "failed")
            return null;

        // Try to read the rolling p50 gauge from the event's data
        // payload. Expected shape: {"p50_ms": 32500}.
        if (!string.IsNullOrWhiteSpace(p50DataJson) && p50DataJson != "{}")
        {
            try
            {
                using var doc = JsonDocument.Parse(p50DataJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("p50_ms", out var v)
                    && v.ValueKind == JsonValueKind.Number
                    && v.TryGetInt64(out var p50Ms)
                    && p50Ms > 0)
                {
                    return startedAt + TimeSpan.FromMilliseconds(p50Ms);
                }
            }
            catch (JsonException) { /* fall through */ }
        }
        return startedAt + FallbackProvisioningEstimate;
    }

    private static Guid? TryGetUserId(HttpContext http)
    {
        var sub = http.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? http.User.FindFirstValue("sub");
        return Guid.TryParse(sub, out var userId) ? userId : null;
    }
}

/// <summary>
/// Story 28-5 AC6 — response payload for the public tenant-status
/// polling endpoint. Public DTO; kept stable across releases.
/// </summary>
public sealed record TenantStatusResponse(
    Guid TenantId,
    string Status,
    DateTime StartedAt,
    DateTime? CompletedAt,
    DateTime? EstimatedCompletion,
    string? CurrentStep,
    Guid? CorrelationId,
    IReadOnlyList<TenantStepStatus> Steps);

/// <summary>
/// Story 28-5 AC6 — one entry in the step ladder.
/// </summary>
/// <param name="Step">Short kebab-case step identifier (e.g.
/// <c>create-role</c>, <c>migrate-tenant-db</c>).</param>
/// <param name="State"><c>running</c> | <c>done</c> | <c>failed</c>.</param>
/// <param name="UpdatedAt">Timestamp of the most recent
/// state-changing event for this step.</param>
public sealed record TenantStepStatus(
    string Step,
    string State,
    DateTime UpdatedAt);
