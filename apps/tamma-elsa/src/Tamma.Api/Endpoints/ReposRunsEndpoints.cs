using System.Text.Json;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 21-4 — the tenant-facing user-dashboard read surface behind the
/// SPA's <c>/repos</c> and <c>/runs</c> destinations. Three thin, read-only,
/// tenant-scoped projections over data that already exists:
/// <list type="bullet">
///   <item><c>GET /api/v1/repos</c> — the tenant's connected platform
///     installations (<see cref="ITenantPlatformInstallationRepository"/>).</item>
///   <item><c>GET /api/v1/runs</c> — the tenant's workflow runs
///     (<see cref="IWorkflowRepository.ListInstancesAsync"/>).</item>
///   <item><c>GET /api/v1/runs/{runId}</c> — a single run's DCB event/log
///     timeline + the tenant's OWN recorded per-run cost
///     (<see cref="IEventRepository.ListByCorrelationIdAsync"/>).</item>
/// </list>
///
/// <para><b>Tenant is resolved strictly from
/// <see cref="ITenantContext"/></b> (populated per-request by
/// <c>TenantContextMiddleware</c> from the caller's principal) — never from a
/// body/route value, so there is no IDOR surface. A null / empty ambient
/// tenant <b>FAILS CLOSED</b> with <c>404 no_active_tenant</c> BEFORE any
/// repository call, mirroring the Story 23-6 (#283) diagnostics fix: a
/// tenant-scoped read must never fan out across every tenant's data.</para>
///
/// <para><b>No economics leak.</b> The per-run cost is summed from the run's
/// OWN <c>Data.costUsd</c> event fields (the tenant's recorded spend) — it
/// never reads a <c>MarginPolicy</c> or any platform price/markup. A member
/// sees only their tenant's repos/runs and their tenant's own cost.</para>
/// </summary>
public static class ReposRunsEndpoints
{
    private const int DefaultRunLimit = 25;
    private const int MaxRunLimit = 100;

    // ─── GET /api/v1/repos ────────────────────────────────────────────────

    /// <summary>
    /// Connected repositories / platform installations for the caller's
    /// tenant, newest-first. Backs the <c>/repos</c> page.
    /// </summary>
    public static async Task<IResult> ListRepos(
        ITenantPlatformInstallationRepository installations,
        ITenantContext tc,
        HttpContext http)
    {
        // Fail closed (Story 23-6 / #283): a null-or-empty ambient tenant on
        // this tenant-scoped route must NOT enumerate every tenant's
        // installations. Reject before touching the repository.
        if (tc.TenantId is not Guid tenantId || tenantId == Guid.Empty)
        {
            return Results.NotFound(new { error = "no_active_tenant" });
        }

        var rows = await installations
            .ListByTenantAsync(tenantId, http.RequestAborted)
            .ConfigureAwait(false);

        var repos = rows.Select(r => new
        {
            id = r.Id,
            name = DisplayName(r),
            platform = r.PlatformKind,
            baseUrl = r.BaseUrl,
            externalId = r.InstallationExternalId,
            status = r.Status,
            isPrimary = r.IsPrimary,
            connectedAt = r.CreatedAt,
            updatedAt = r.UpdatedAt,
        }).ToList();

        return Results.Ok(new { tenantId, repos, count = repos.Count });
    }

    // ─── GET /api/v1/runs ─────────────────────────────────────────────────

    /// <summary>
    /// Workflow runs for the caller's tenant, newest-first, offset-paginated.
    /// Backs the <c>/runs</c> list. Reuses the same tenant-scoped
    /// <see cref="IWorkflowRepository.ListInstancesAsync"/> read as the
    /// existing dashboard "recent runs" widget.
    /// </summary>
    public static async Task<IResult> ListRuns(
        IWorkflowRepository workflows,
        ITenantContext tc,
        int? limit,
        int? page)
    {
        if (tc.TenantId is not Guid tenantId || tenantId == Guid.Empty)
        {
            return Results.NotFound(new { error = "no_active_tenant" });
        }

        var pageSize = Math.Clamp(limit ?? DefaultRunLimit, 1, MaxRunLimit);
        var pageNumber = Math.Max(page ?? 1, 1);

        var (instances, total) = await workflows
            .ListInstancesAsync(definitionId: null, tenantId: tenantId, page: pageNumber, pageSize: pageSize)
            .ConfigureAwait(false);

        var runs = instances.Select(ToRunSummary).ToList();

        return Results.Ok(new
        {
            tenantId,
            total,
            page = pageNumber,
            pageSize,
            runs,
        });
    }

    // ─── GET /api/v1/runs/{runId} ─────────────────────────────────────────

    /// <summary>
    /// One run's detail: the workflow-instance metadata plus its full DCB
    /// event timeline (correlationId = the run id), a derived log stream, and
    /// the tenant's OWN recorded total cost. Returns <c>404 run_not_found</c>
    /// when the run does not belong to the caller's tenant (defence-in-depth
    /// on top of the structurally tenant-scoped reads).
    /// </summary>
    public static async Task<IResult> GetRunDetail(
        Guid runId,
        IWorkflowRepository workflows,
        IEventRepository events,
        ITenantContext tc)
    {
        if (tc.TenantId is not Guid tenantId || tenantId == Guid.Empty)
        {
            return Results.NotFound(new { error = "no_active_tenant" });
        }
        if (runId == Guid.Empty)
        {
            return Results.NotFound(new { error = "run_not_found" });
        }

        // GetInstanceAsync is physically scoped to the ambient tenant's schema;
        // the explicit TenantId equality below is defence-in-depth so a bare-id
        // read can never surface another tenant's run.
        var instance = await workflows.GetInstanceAsync(runId).ConfigureAwait(false);
        if (instance is null || instance.TenantId != tenantId)
        {
            return Results.NotFound(new { error = "run_not_found" });
        }

        // Full run timeline from the DCB store. ListByCorrelationIdAsync is
        // structurally tenant-scoped (it throws on an empty tenant) and matches
        // Tags->>'correlationId' == runId, oldest-first.
        var timeline = await events
            .ListByCorrelationIdAsync(tenantId, runId.ToString())
            .ConfigureAwait(false);

        decimal totalCostUsd = 0m;
        string? provider = null;
        string? repository = null;
        int? issueNumber = null;
        string? prUrl = null;
        var filesChanged = new List<string>();
        var eventDtos = new List<object>(timeline.Count);
        var logs = new List<string>(timeline.Count);

        foreach (var e in timeline)
        {
            var tags = ParseObject(e.Tags);
            var data = ParseObject(e.Data);

            // Per-run cost = the tenant's OWN recorded spend, summed from the
            // run's own events. NEVER a platform margin (no MarginPolicy read).
            if (DataDecimal(data, "costUsd") is decimal cost)
            {
                totalCostUsd += cost;
            }

            provider ??= TagString(tags, "provider");
            repository ??= TagString(tags, "repository") ?? DataString(data, "repository");
            prUrl ??= DataString(data, "prUrl")
                ?? DataString(data, "pullRequestUrl")
                ?? DataString(data, "htmlUrl");
            issueNumber ??= e.IssueNumber;

            CollectFilesChanged(data, filesChanged);

            eventDtos.Add(new
            {
                id = e.Id,
                type = e.Type,
                tags = SafeElement(e.Tags),
                data = SafeElement(e.Data),
                createdAt = e.CreatedAt,
                sequenceNumber = e.SequenceNumber,
            });

            logs.Add(FormatLogLine(e, data));
        }

        return Results.Ok(new
        {
            id = instance.Id,
            definitionId = instance.DefinitionId,
            status = instance.Status,
            currentActivity = instance.CurrentActivity,
            createdAt = instance.CreatedAt,
            startedAt = instance.StartedAt,
            completedAt = instance.CompletedAt,
            durationMs = DurationMs(instance),
            provider,
            issueNumber,
            repository,
            prUrl,
            filesChanged,
            totalCostUsd,
            eventCount = timeline.Count,
            events = eventDtos,
            logs,
        });
    }

    // ─── Projections / helpers ────────────────────────────────────────────

    private static object ToRunSummary(WorkflowInstance i) => new
    {
        id = i.Id,
        definitionId = i.DefinitionId,
        status = i.Status,
        currentActivity = i.CurrentActivity,
        createdAt = i.CreatedAt,
        startedAt = i.StartedAt,
        completedAt = i.CompletedAt,
        durationMs = DurationMs(i),
    };

    private static double? DurationMs(WorkflowInstance i)
        => i.StartedAt is null || i.CompletedAt is null
            ? null
            : (i.CompletedAt.Value - i.StartedAt.Value).TotalMilliseconds;

    /// <summary>
    /// Best-effort human label for a connected installation. Prefers a
    /// metadata "name"/"fullName"/"account" field; falls back to the external
    /// id or the base URL host.
    /// </summary>
    private static string DisplayName(TenantPlatformInstallation r)
    {
        var meta = ParseObject(r.MetadataJson);
        var fromMeta = DataString(meta, "name")
            ?? DataString(meta, "fullName")
            ?? DataString(meta, "account")
            ?? DataString(meta, "login");
        if (!string.IsNullOrWhiteSpace(fromMeta)) return fromMeta;
        if (!string.IsNullOrWhiteSpace(r.InstallationExternalId))
            return $"{r.PlatformKind}:{r.InstallationExternalId}";
        return string.IsNullOrWhiteSpace(r.BaseUrl) ? r.PlatformKind : r.BaseUrl;
    }

    private static string FormatLogLine(DomainEvent e, JsonElement data)
    {
        var message = DataString(data, "message")
            ?? DataString(data, "error")
            ?? DataString(data, "reason")
            ?? DataString(data, "activityName")
            ?? string.Empty;
        var stamp = e.CreatedAt.ToUniversalTime().ToString("O");
        return string.IsNullOrEmpty(message)
            ? $"{stamp}  {e.Type}"
            : $"{stamp}  {e.Type}  {message}";
    }

    private static void CollectFilesChanged(JsonElement data, List<string> sink)
    {
        if (data.ValueKind != JsonValueKind.Object) return;
        if (!data.TryGetProperty("filesChanged", out var files)) return;
        if (files.ValueKind != JsonValueKind.Array) return;
        foreach (var f in files.EnumerateArray())
        {
            if (f.ValueKind == JsonValueKind.String)
            {
                var value = f.GetString();
                if (!string.IsNullOrWhiteSpace(value) && !sink.Contains(value))
                    sink.Add(value);
            }
        }
    }

    private static JsonElement ParseObject(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }
    }

    /// <summary>Parse a JSONB string for wire emission; invalid → null literal.</summary>
    private static JsonElement SafeElement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return JsonDocument.Parse("null").RootElement.Clone();
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("null").RootElement.Clone();
        }
    }

    private static string? TagString(JsonElement tags, string key)
        => tags.ValueKind == JsonValueKind.Object
           && tags.TryGetProperty(key, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString()
                : v.ValueKind == JsonValueKind.Null ? null
                : v.ToString())
            : null;

    private static string? DataString(JsonElement data, string key)
        => data.ValueKind == JsonValueKind.Object
           && data.TryGetProperty(key, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static decimal? DataDecimal(JsonElement data, string key)
        => data.ValueKind == JsonValueKind.Object
           && data.TryGetProperty(key, out var v)
           && v.ValueKind == JsonValueKind.Number
           && v.TryGetDecimal(out var d)
            ? d
            : null;
}
