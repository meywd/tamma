using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Dtos.Providers;
using Tamma.Api.Dtos.Settings;
using Tamma.Api.Services.Diagnostics;
using Tamma.Api.Services.Diagnostics.Models;
using Tamma.Api.Services.Providers;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using BudgetConfigModel = Tamma.Api.Services.Diagnostics.Models.BudgetConfig;

namespace Tamma.Api.Endpoints;

public static class ProviderEndpoints
{
    // ── Health / circuit-breaker endpoints ───────────────────────────────────

    public static async Task<IResult> GetHealthSummary(
        [FromServices] ICircuitBreakerService breaker,
        [FromServices] ITenantContext tc)
    {
        var all = await breaker.ListAsync(tc.TenantId);
        // Dual-shape response per finding 012 — TS dashboards consumed a keyed
        // map (Object.entries(response)); the C# API returns the array under
        // `providers` for forward-compat AND mirrors the map under `byKey`.
        var entries = all.Select(s => new
        {
            providerKey = s.ProviderKey,
            state = s.State.ToString(),
            status = MapLegacyStatus(s.State),
            failureCount = s.FailureCount,
            lastSuccess = s.LastSuccess,
            lastFailure = s.LastFailure,
            circuitOpenUntil = s.CircuitOpenUntil,
            halfOpenInProgress = s.HalfOpenInProgress,
            healthy = s.State == CircuitBreakerState.Closed,
            circuitOpen = s.State == CircuitBreakerState.Open,
            halfOpen = s.State == CircuitBreakerState.HalfOpen,
            failures = s.FailureCount,
        }).ToList();
        return Results.Ok(new
        {
            providers = entries,
            byKey = entries.ToDictionary(e => e.providerKey, e => (object)e),
        });
    }

    public static async Task<IResult> ListProviderHealth(
        [FromServices] ICircuitBreakerService breaker,
        [FromServices] ITenantContext tc)
    {
        var all = await breaker.ListAsync(tc.TenantId);
        return Results.Ok(all.Select(s => new
        {
            providerKey = s.ProviderKey,
            state = s.State.ToString(),
            status = MapLegacyStatus(s.State),
            failureCount = s.FailureCount,
            lastSuccess = s.LastSuccess,
            lastFailure = s.LastFailure,
            circuitOpenUntil = s.CircuitOpenUntil,
            halfOpenInProgress = s.HalfOpenInProgress,
        }));
    }

    public static async Task<IResult> GetProviderHealth(
        string key,
        [FromServices] ICircuitBreakerService breaker,
        [FromServices] ITenantContext tc)
    {
        // Validate the key shape (finding 013) before doing any I/O.
        if (!IsValidProviderKey(key, out var validationError))
        {
            return Results.BadRequest(new { error = validationError });
        }

        // Unknown keys synthesise a healthy response (200) — matches TS
        // GET /health/providers/:key behaviour. Finding 012 reverses the
        // earlier 404 regression so dashboards can poll without branch logic.
        var s = await breaker.GetStateAsync(key, tc.TenantId);
        return Results.Ok(new
        {
            providerKey = s.ProviderKey,
            state = s.State.ToString(),
            status = MapLegacyStatus(s.State),
            failureCount = s.FailureCount,
            lastSuccess = s.LastSuccess,
            lastFailure = s.LastFailure,
            circuitOpenUntil = s.CircuitOpenUntil,
            halfOpenInProgress = s.HalfOpenInProgress,
            // TS-compat scalar — boolean shorthand for "circuit-closed".
            healthy = s.State == CircuitBreakerState.Closed,
            circuitOpen = s.State == CircuitBreakerState.Open,
            halfOpen = s.State == CircuitBreakerState.HalfOpen,
            failures = s.FailureCount,
        });
    }

    /// <summary>
    /// Validate a provider key shape — non-empty, ≤ 256 chars, matching the
    /// TS regex <c>^[a-zA-Z0-9._\-:/]+$</c>. Finding 013.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex KeyPattern =
        new("^[a-zA-Z0-9._\\-:/]+$",
            System.Text.RegularExpressions.RegexOptions.Compiled |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsValidProviderKey(string key, out string error)
    {
        if (string.IsNullOrEmpty(key))
        {
            error = "key must not be empty";
            return false;
        }
        if (key.Length > 256)
        {
            error = "key too long (max 256)";
            return false;
        }
        if (!KeyPattern.IsMatch(key))
        {
            error = "key contains invalid characters";
            return false;
        }
        error = string.Empty;
        return true;
    }

    public static async Task<IResult> RecordFailure(
        string key,
        [FromServices] ICircuitBreakerService breaker,
        [FromServices] ITenantContext tc)
    {
        if (!IsValidProviderKey(key, out var err)) return Results.BadRequest(new { error = err });
        var s = await breaker.RecordFailureAsync(key, tc.TenantId);
        return Results.Ok(new
        {
            message = "Failure recorded",
            state = s.State.ToString(),
            failureCount = s.FailureCount,
            circuitOpenUntil = s.CircuitOpenUntil,
        });
    }

    public static async Task<IResult> RecordSuccess(
        string key,
        [FromServices] ICircuitBreakerService breaker,
        [FromServices] ITenantContext tc)
    {
        if (!IsValidProviderKey(key, out var err)) return Results.BadRequest(new { error = err });
        var s = await breaker.RecordSuccessAsync(key, tc.TenantId);
        return Results.Ok(new
        {
            message = "Success recorded",
            state = s.State.ToString(),
            failureCount = s.FailureCount,
        });
    }

    public static async Task<IResult> ResetProvider(
        string key,
        [FromServices] ICircuitBreakerService breaker,
        [FromServices] ITenantContext tc)
    {
        if (!IsValidProviderKey(key, out var err)) return Results.BadRequest(new { error = err });
        var s = await breaker.ResetAsync(key, tc.TenantId);
        return Results.Ok(new
        {
            message = "Provider health reset",
            state = s.State.ToString(),
        });
    }

    public static async Task<IResult> ResolveChain(
        ResolveChainRequest req,
        [FromServices] IProviderChainResolver resolver,
        [FromServices] ITenantContext tc)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Role) || string.IsNullOrWhiteSpace(req.Action))
        {
            return Results.BadRequest(new { error = "role and action are required" });
        }

        var result = await resolver.ResolveAsync(tc.TenantId, req.Role, req.Action);
        if (!result.HasCandidates)
        {
            return Results.Ok(new
            {
                ordered = Array.Empty<object>(),
                skipped = result.Skipped.Select(e => new
                {
                    provider = e.Provider.Provider,
                    model = e.Provider.Model,
                    key = e.Provider.Key,
                    reason = e.Reason.ToString(),
                }),
                error = result.ErrorCode,
                message = result.ErrorMessage,
            });
        }

        return Results.Ok(new
        {
            ordered = result.Ordered.Select(e => new
            {
                provider = e.Provider.Provider,
                model = e.Provider.Model,
                key = e.Provider.Key,
                reason = e.Reason.ToString(),
            }),
            skipped = result.Skipped.Select(e => new
            {
                provider = e.Provider.Provider,
                model = e.Provider.Model,
                key = e.Provider.Key,
                reason = e.Reason.ToString(),
            }),
        });
    }

    private static string MapLegacyStatus(CircuitBreakerState state) => state switch
    {
        CircuitBreakerState.Closed => "healthy",
        CircuitBreakerState.HalfOpen => "degraded",
        CircuitBreakerState.Open => "down",
        _ => "unknown",
    };

    // ── Diagnostics endpoints (owned by Agent 3 — do not modify) ─────────────

    public static async Task<IResult> GetDiagnostics(IDiagnosticsRepository repo, int? limit, int? offset)
    {
        var (items, total) = await repo.QueryAsync(null, null, null, limit ?? 50, offset ?? 0);
        return Results.Ok(new { items = items.Select(d => new { d.Id, d.ProviderKey, d.RequestDurationMs, d.TokensUsed, d.Cost, d.Success, d.CreatedAt }), total });
    }

    /// <summary>
    /// Query diagnostics with support for provider, date-range, success, and
    /// paging filters. Tenant scoping is applied via the ambient
    /// <see cref="ITenantContext"/> (EF global query filter).
    /// </summary>
    public static async Task<IResult> QueryDiagnostics(
        [FromServices] IDiagnosticsService service,
        [FromServices] ITenantContext tc,
        string? providerKey,
        DateTime? from,
        DateTime? to,
        int? limit,
        int? offset,
        bool? success,
        string? model)
    {
        var filter = new DiagnosticsFilter
        {
            ProviderKey = providerKey,
            From = from,
            To = to,
            Success = success,
            Model = model,
            Limit = Math.Clamp(limit ?? 50, 1, 500),
            Offset = Math.Max(0, offset ?? 0),
            TenantId = tc.TenantId
        };
        var (items, total) = await service.QueryAsync(filter);
        return Results.Ok(new
        {
            items = items.Select(d => new
            {
                d.Id,
                d.ProviderKey,
                d.RequestDurationMs,
                d.TokensUsed,
                d.Cost,
                d.Model,
                d.Success,
                d.ErrorMessage,
                d.TenantId,
                d.CreatedAt
            }),
            total
        });
    }

    /// <summary>
    /// Return a diagnostics report. Two aggregation modes:
    /// <list type="bullet">
    ///   <item><c>?groupBy=provider|model|agentType</c> — per-dimension report
    ///         (TS shape, restored by finding 009).</item>
    ///   <item><c>?bucketSize=5m|hour|day</c> — time-bucketed report
    ///         (current C# behaviour).</item>
    /// </list>
    /// If <c>groupBy</c> is supplied it takes precedence; otherwise the
    /// time-bucketed report runs.
    /// </summary>
    public static async Task<IResult> GetReport(
        [FromServices] IDiagnosticsService service,
        [FromServices] ITenantContext tc,
        DateTime? from,
        DateTime? to,
        string? bucketSize,
        string? groupBy)
    {
        var fromDt = from ?? DateTime.UtcNow.AddDays(-1);
        var toDt = to ?? DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(groupBy))
        {
            if (!TryParseGroupBy(groupBy, out var dim))
            {
                return Results.BadRequest(new
                {
                    error = $"Invalid groupBy value: {groupBy}. " +
                            "Must be one of: provider, model, agentType",
                });
            }
            var dimReport = await service.GetDimensionReportAsync(
                tc.TenantId, fromDt, toDt, dim);
            return Results.Ok(dimReport);
        }

        var parsedBucket = ParseBucketSize(bucketSize, BucketSize.Hour);
        var report = await service.GetReportAsync(tc.TenantId, fromDt, toDt, parsedBucket);
        return Results.Ok(report);
    }

    private static bool TryParseGroupBy(string raw, out DimensionGroup result)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "provider":
                result = DimensionGroup.Provider;
                return true;
            case "model":
                result = DimensionGroup.Model;
                return true;
            case "agenttype":
            case "agent_type":
            case "agent-type":
                result = DimensionGroup.AgentType;
                return true;
            default:
                result = default;
                return false;
        }
    }

    /// <summary>
    /// Return current-period budget status for the given account id. The
    /// <paramref name="accountId"/> route parameter must parse as a
    /// <see cref="Guid"/>; bad input yields <c>400 Bad Request</c>.
    /// </summary>
    public static async Task<IResult> GetBudget(string accountId, [FromServices] IDiagnosticsService service)
    {
        if (!Guid.TryParse(accountId, out var id))
            return Results.BadRequest(new { error = "accountId must be a GUID." });

        var status = await service.GetBudgetAsync(id);
        return Results.Ok(status);
    }

    /// <summary>
    /// Replace the budget configuration for an account. Implements the
    /// missing PUT side of finding 005 — without this endpoint there was no
    /// way for an admin to set <c>LimitUsd</c> on a per-tenant basis, so
    /// budget enforcement could never fire.
    /// </summary>
    /// <remarks>
    /// Persistence is in-memory in the current build; a multi-replica
    /// deployment requires the Postgres-backed provider tracked in the
    /// budget-persistence story. <c>SettingsManage</c> gated at the route.
    /// </remarks>
    public static IResult UpdateBudget(
        string accountId,
        UpdateBudgetRequest req,
        [FromServices] IBudgetConfigProvider provider)
    {
        if (!Guid.TryParse(accountId, out var id))
            return Results.BadRequest(new { error = "accountId must be a GUID." });
        if (req is null)
            return Results.BadRequest(new { error = "Request body required." });
        if (req.LimitUsd < 0)
            return Results.BadRequest(new { error = "limitUsd must be >= 0." });
        if (req.AlertThreshold is < 0 or > 1)
            return Results.BadRequest(new { error = "alertThreshold must be in [0,1]." });
        if (req.PeriodDays is < 1 or > 366)
            return Results.BadRequest(new { error = "periodDays must be in [1,366]." });

        var now = DateTime.UtcNow;
        var period = TimeSpan.FromDays(req.PeriodDays);
        var cfg = new BudgetConfigModel(
            LimitUsd: req.LimitUsd,
            AlertThreshold: req.AlertThreshold,
            PeriodStart: now - period,
            PeriodEnd: now + period);

        provider.SetConfig(id, cfg);
        return Results.Ok(new
        {
            accountId = id,
            limitUsd = cfg.LimitUsd,
            alertThreshold = cfg.AlertThreshold,
            periodDays = req.PeriodDays,
        });
    }

    /// <summary>
    /// Accept a new diagnostic event. Writes through
    /// <see cref="IDiagnosticsService.RecordEventAsync"/> so the recent-events
    /// cache is kept warm for the settings UI.
    /// </summary>
    public static async Task<IResult> IngestDiagnostic(
        IngestDiagnosticRequest req,
        [FromServices] IDiagnosticsService service,
        [FromServices] ITenantContext tc)
    {
        var id = await service.RecordEventAsync(MapDiagnostic(req, tc.TenantId));
        return Results.Created($"/api/providers/diagnostics/{id}", new { id });
    }

    /// <summary>
    /// Batch diagnostic ingest — accepts up to 100 records per call.
    /// Mirrors the TS <c>POST /diagnostics</c> array shape (finding 010).
    /// </summary>
    public static async Task<IResult> IngestDiagnosticBatch(
        IngestDiagnosticRequest[] reqs,
        [FromServices] IDiagnosticsService service,
        [FromServices] ITenantContext tc)
    {
        if (reqs is null || reqs.Length == 0)
            return Results.BadRequest(new { error = "At least one diagnostics record is required" });
        if (reqs.Length > 100)
            return Results.BadRequest(new
            {
                error = $"Batch size {reqs.Length} exceeds max 100"
            });

        var ids = new List<Guid>(reqs.Length);
        foreach (var req in reqs)
        {
            var id = await service.RecordEventAsync(MapDiagnostic(req, tc.TenantId));
            ids.Add(id);
        }
        return Results.Created($"/api/providers/diagnostics/batch", new
        {
            recorded = ids.Count,
            ids,
        });
    }

    private static ProviderDiagnostic MapDiagnostic(IngestDiagnosticRequest req, Guid? tenantId)
    {
        // Default the input/output split: if caller only sent TokensUsed,
        // attribute it all to input so per-token cost recomputation works.
        var inputTok = req.InputTokens ?? (req.OutputTokens is null ? req.TokensUsed : 0);
        var outputTok = req.OutputTokens ?? 0;

        return new ProviderDiagnostic
        {
            ProviderKey = req.ProviderKey,
            RequestDurationMs = req.DurationMs,
            TokensUsed = req.TokensUsed,
            InputTokens = inputTok,
            OutputTokens = outputTok,
            Cost = req.Cost,
            Model = req.Model,
            Success = req.Success,
            ErrorMessage = req.Error,
            ErrorCode = req.ErrorCode,
            CorrelationId = req.CorrelationId,
            AgentType = req.AgentType,
            ProjectId = req.ProjectId,
            EngineId = req.EngineId,
            TaskId = req.TaskId,
            TaskType = req.TaskType,
            // RequestType is the legacy field that mirrors EventType when
            // the caller doesn't set it explicitly.
            RequestType = req.EventType ?? req.TaskType,
            TenantId = tenantId,
        };
    }

    private static BucketSize ParseBucketSize(string? raw, BucketSize fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return raw.Trim().ToLowerInvariant() switch
        {
            "5m" or "5min" or "fiveminutes" or "5minutes" or "5-min" => BucketSize.FiveMinutes,
            "1h" or "hour" or "hourly" or "1hour" => BucketSize.Hour,
            "1d" or "day" or "daily" or "1day" => BucketSize.Day,
            _ => Enum.TryParse<BucketSize>(raw, ignoreCase: true, out var parsed) ? parsed : fallback
        };
    }

    // ── Provider session endpoints (Story 9-4 — ported from TS) ─────────────

    private static readonly System.Text.RegularExpressions.Regex HandleUuidRegex =
        new("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Create a provider session and return its handle. Mirrors the TS
    /// <c>POST /providers/create</c> contract.
    /// </summary>
    public static async Task<IResult> CreateProvider(
        CreateProviderSessionRequest req,
        [FromServices] IProviderSessionService sessions,
        [FromServices] ITenantContext tc)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Provider))
        {
            return Results.BadRequest(new { error = "provider is required" });
        }

        var model = string.IsNullOrWhiteSpace(req.Model) ? "default" : req.Model;
        var session = await sessions.CreateAsync(req.Provider, model, tc.TenantId);
        return Results.Created(
            $"/api/providers/providers/{session.Handle}",
            new CreateProviderSessionResponse(session.Handle, session.Provider, session.Model));
    }

    /// <summary>
    /// Execute an invocation against the session identified by
    /// <paramref name="handle"/>. Tenant-scoped — a handle created under
    /// tenant A is not executable by tenant B (returns 404).
    /// </summary>
    public static async Task<IResult> ExecuteProvider(
        string handle,
        ExecuteProviderSessionRequest req,
        [FromServices] IProviderSessionService sessions,
        [FromServices] ITenantContext tc)
    {
        if (!HandleUuidRegex.IsMatch(handle))
        {
            return Results.BadRequest(new { error = "Invalid session handle format" });
        }

        var prompt = req?.Input ?? req?.Prompt;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Results.BadRequest(new { error = "input (or legacy 'prompt') is required" });
        }

        try
        {
            var result = await sessions.ExecuteTenantScopedAsync(
                callerTenantId: tc.TenantId,
                handle: handle,
                req: new ExecuteRequest(
                    Handle: handle,
                    Input: prompt!,
                    MaxTokens: req?.MaxTokens,
                    Temperature: req?.Temperature));

            return Results.Ok(new ExecuteProviderSessionResponse(
                Content: result.Content,
                TokenUsage: result.TokenUsage,
                CostUsd: result.CostUsd,
                DurationMs: result.DurationMs));
        }
        catch (ProviderSessionNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (ProviderNotSupportedException ex)
        {
            // 501 (Not Implemented) is the most accurate code for "provider
            // is registered but the transport adapter isn't ported yet".
            return Results.Problem(
                title: "PROVIDER_NOT_SUPPORTED",
                detail: ex.Message,
                statusCode: 501);
        }
    }

    /// <summary>
    /// Dispose a session. Returns 404 if the handle does not belong to the
    /// caller's tenant.
    /// </summary>
    public static async Task<IResult> DeleteProvider(
        string handle,
        [FromServices] IProviderSessionService sessions,
        [FromServices] ITenantContext tc)
    {
        if (!HandleUuidRegex.IsMatch(handle))
        {
            return Results.BadRequest(new { error = "Invalid session handle format" });
        }

        var disposed = await sessions.DeleteTenantScopedAsync(tc.TenantId, handle);
        if (!disposed)
        {
            return Results.NotFound(new { error = $"Session not found: {handle}" });
        }
        return Results.Ok(new { disposed = true });
    }

    /// <summary>
    /// List active sessions scoped to the caller's tenant.
    /// </summary>
    public static async Task<IResult> ListSessions(
        [FromServices] IProviderSessionService sessions,
        [FromServices] ITenantContext tc)
    {
        var list = await sessions.ListAsync(tc.TenantId);
        var dto = list.Select(s => new ProviderSessionDto(
            s.Handle, s.Provider, s.Model, s.CreatedAt, s.LastUsed, s.TenantId)).ToList();
        return Results.Ok(new { sessions = dto, count = dto.Count });
    }
}

/// <summary>Request body for <c>POST /api/providers/chain/resolve</c>.</summary>
public sealed record ResolveChainRequest(string Role, string Action);

/// <summary>
/// Request body for <c>PUT /api/providers/diagnostics/budget/{accountId}</c>.
/// Limits cap the rolling-window USD spend for the tenant; alerts fire at
/// <c>AlertThreshold</c> (e.g. 0.8 = 80%).
/// </summary>
public sealed record UpdateBudgetRequest(
    decimal LimitUsd,
    double AlertThreshold = 0.8,
    int PeriodDays = 30);
