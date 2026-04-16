using System.Text.Json;
using Tamma.Api.Dtos.Settings;
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

    public static async Task<IResult> QueryDiagnostics(
        IDiagnosticsRepository repo,
        string? providerKey,
        DateTime? from,
        DateTime? to,
        int? limit,
        int? offset)
    {
        var (items, total) = await repo.QueryAsync(providerKey, from, to, limit ?? 50, offset ?? 0);
        return Results.Ok(new { items, total });
    }

    public static async Task<IResult> GetReport(IDiagnosticsRepository repo, DateTime? from, DateTime? to)
    {
        var report = await repo.GetReportAsync(from ?? DateTime.UtcNow.AddDays(-30), to ?? DateTime.UtcNow);
        return Results.Ok(report);
    }

    public static async Task<IResult> GetBudget(string accountId, IDiagnosticsRepository repo)
    {
        var budget = await repo.GetBudgetAsync(accountId);
        return Results.Ok(budget);
    }

    public static async Task<IResult> IngestDiagnostic(IngestDiagnosticRequest req, IDiagnosticsRepository repo, ITenantContext tc)
    {
        var id = await repo.InsertAsync(new ProviderDiagnostic
        {
            ProviderKey = req.ProviderKey,
            RequestDurationMs = req.DurationMs,
            TokensUsed = req.TokensUsed,
            Cost = req.Cost,
            Model = req.Model,
            Success = req.Success,
            ErrorMessage = req.Error,
            TenantId = tc.TenantId
        });
        return Results.Created($"/api/providers/diagnostics/{id}", new { id });
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
