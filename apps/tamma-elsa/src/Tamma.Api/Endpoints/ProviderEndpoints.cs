using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Dtos.Settings;
using Tamma.Api.Services.Diagnostics;
using Tamma.Api.Services.Diagnostics.Models;
using Tamma.Api.Services.Providers;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

public static class ProviderEndpoints
{
    // ── Health / circuit-breaker endpoints ───────────────────────────────────

    public static async Task<IResult> GetHealthSummary(
        [FromServices] ICircuitBreakerService breaker,
        [FromServices] ITenantContext tc)
    {
        var all = await breaker.ListAsync(tc.TenantId);
        return Results.Ok(new
        {
            providers = all.Select(s => new
            {
                providerKey = s.ProviderKey,
                state = s.State.ToString(),
                status = MapLegacyStatus(s.State),
                failureCount = s.FailureCount,
                lastSuccess = s.LastSuccess,
                lastFailure = s.LastFailure,
                circuitOpenUntil = s.CircuitOpenUntil,
                halfOpenInProgress = s.HalfOpenInProgress,
            }),
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
        [FromServices] IProviderHealthRepository repo,
        [FromServices] ITenantContext tc)
    {
        // Require an existing row; unseen keys return 404 for parity with the prior API.
        var row = await repo.GetStatusAsync(key, tc.TenantId);
        if (row is null) return Results.NotFound(new { error = "Provider not found" });

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
        });
    }

    public static async Task<IResult> RecordFailure(
        string key,
        [FromServices] ICircuitBreakerService breaker,
        [FromServices] ITenantContext tc)
    {
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
    /// Return a time-bucketed diagnostics report (<see cref="BucketSize.FiveMinutes"/>,
    /// <see cref="BucketSize.Hour"/>, or <see cref="BucketSize.Day"/>) across the
    /// half-open range <c>[from, to)</c>.
    /// </summary>
    public static async Task<IResult> GetReport(
        [FromServices] IDiagnosticsService service,
        [FromServices] ITenantContext tc,
        DateTime? from,
        DateTime? to,
        string? bucketSize)
    {
        var fromDt = from ?? DateTime.UtcNow.AddDays(-1);
        var toDt = to ?? DateTime.UtcNow;
        var parsedBucket = ParseBucketSize(bucketSize, BucketSize.Hour);

        var report = await service.GetReportAsync(tc.TenantId, fromDt, toDt, parsedBucket);
        return Results.Ok(report);
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
    /// Accept a new diagnostic event. Writes through
    /// <see cref="IDiagnosticsService.RecordEventAsync"/> so the recent-events
    /// cache is kept warm for the settings UI.
    /// </summary>
    public static async Task<IResult> IngestDiagnostic(
        IngestDiagnosticRequest req,
        [FromServices] IDiagnosticsService service,
        [FromServices] ITenantContext tc)
    {
        var diag = new ProviderDiagnostic
        {
            ProviderKey = req.ProviderKey,
            RequestDurationMs = req.DurationMs,
            TokensUsed = req.TokensUsed,
            Cost = req.Cost,
            Model = req.Model,
            Success = req.Success,
            ErrorMessage = req.Error,
            TenantId = tc.TenantId
        };
        var id = await service.RecordEventAsync(diag);
        return Results.Created($"/api/providers/diagnostics/{id}", new { id });
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

    // Provider session stubs
    public static Task<IResult> CreateProvider(CreateProviderRequest req) =>
        Task.FromResult(Results.Ok(new { handle = Guid.NewGuid().ToString(), type = req.Type }));

    public static Task<IResult> ExecuteProvider(string handle, ExecuteProviderRequest req) =>
        Task.FromResult(Results.Ok(new { handle, response = "Provider execution stub" }));

    public static Task<IResult> DeleteProvider(string handle) =>
        Task.FromResult(Results.Ok(new { message = $"Provider session {handle} deleted" }));

    public static Task<IResult> ListSessions() =>
        Task.FromResult(Results.Ok(Array.Empty<object>()));
}

/// <summary>Request body for <c>POST /api/providers/chain/resolve</c>.</summary>
public sealed record ResolveChainRequest(string Role, string Action);
