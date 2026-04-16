using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Dtos.Settings;
using Tamma.Api.Services.Diagnostics;
using Tamma.Api.Services.Diagnostics.Models;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

public static class ProviderEndpoints
{
    public static async Task<IResult> GetHealthSummary(IProviderHealthRepository repo, ITenantContext tc)
    {
        var all = await repo.GetAllAsync(tc.TenantId);
        return Results.Ok(new { providers = all.Select(h => new { h.ProviderKey, h.Status, h.FailureCount, h.LastSuccess, h.LastFailure }) });
    }

    public static async Task<IResult> ListProviderHealth(IProviderHealthRepository repo, ITenantContext tc)
    {
        var all = await repo.GetAllAsync(tc.TenantId);
        return Results.Ok(all.Select(h => new { h.ProviderKey, h.Status, h.FailureCount, h.LastSuccess, h.LastFailure }));
    }

    public static async Task<IResult> GetProviderHealth(string key, IProviderHealthRepository repo, ITenantContext tc)
    {
        var health = await repo.GetStatusAsync(key, tc.TenantId);
        return health is not null
            ? Results.Ok(new { health.ProviderKey, health.Status, health.FailureCount, health.LastSuccess, health.LastFailure })
            : Results.NotFound(new { error = "Provider not found" });
    }

    public static async Task<IResult> RecordFailure(string key, IProviderHealthRepository repo, ITenantContext tc)
    {
        await repo.RecordFailureAsync(key, tc.TenantId);
        return Results.Ok(new { message = "Failure recorded" });
    }

    public static async Task<IResult> RecordSuccess(string key, IProviderHealthRepository repo, ITenantContext tc)
    {
        await repo.RecordSuccessAsync(key, tc.TenantId);
        return Results.Ok(new { message = "Success recorded" });
    }

    public static async Task<IResult> ResetProvider(string key, IProviderHealthRepository repo, ITenantContext tc)
    {
        await repo.ResetAsync(key, tc.TenantId);
        return Results.Ok(new { message = "Provider health reset" });
    }

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
