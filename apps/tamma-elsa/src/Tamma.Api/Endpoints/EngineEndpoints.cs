using Tamma.Api.Dtos.Engine;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

public static class EngineEndpoints
{
    public static Task<IResult> SendCommand(SendCommandRequest req) =>
        Task.FromResult(Results.Ok(new { message = "Command accepted", command = req.Command }));

    public static async Task<IResult> GetState(IEventRepository eventRepo, ITenantContext tc)
    {
        var events = await eventRepo.QueryAsync(tc.TenantId, null, null, 10);
        return Results.Ok(new { state = "idle", events = events.Count });
    }

    public static async Task<IResult> GetStats(IEventRepository eventRepo, ITenantContext tc)
    {
        var events = await eventRepo.QueryAsync(tc.TenantId, null, null, 1000);
        return Results.Ok(new { totalEvents = events.Count, timestamp = DateTime.UtcNow });
    }

    public static Task<IResult> GetPlan() =>
        Task.FromResult(Results.Ok(new { plan = (object?)null, message = "No active plan" }));

    public static async Task<IResult> GetHistory(IEventRepository eventRepo, ITenantContext tc, int? limit)
    {
        var events = await eventRepo.QueryAsync(tc.TenantId, null, null, limit ?? 50);
        return Results.Ok(events.Select(e => new { e.Id, e.Type, e.Data, e.CreatedAt }));
    }

    public static async Task<IResult> GetEventsState(IEventRepository eventRepo, ITenantContext tc, int? limit)
    {
        var events = await eventRepo.QueryAsync(tc.TenantId, null, null, limit ?? 20);
        return Results.Ok(events.Select(e => new { e.Id, e.Type, e.CreatedAt }));
    }

    public static async Task<IResult> GetEventsLogs(IEventRepository eventRepo, ITenantContext tc, int? limit)
    {
        var events = await eventRepo.QueryAsync(tc.TenantId, null, null, limit ?? 50);
        return Results.Ok(events.Select(e => new { e.Id, e.Type, e.Data, e.CreatedAt }));
    }

    public static async Task<IResult> StoreContext(StoreContextRequest req, IEventRepository eventRepo, ITenantContext tc)
    {
        await eventRepo.AppendAsync(new DomainEvent
        {
            Type = "CONTEXT.STORED",
            TenantId = tc.TenantId,
            IssueNumber = req.IssueNumber,
            Data = System.Text.Json.JsonSerializer.Serialize(req.Context)
        });
        return Results.Ok(new { message = "Context stored" });
    }

    public static async Task<IResult> GetContext(int issueNumber, IEventRepository eventRepo, ITenantContext tc)
    {
        var events = await eventRepo.QueryAsync(tc.TenantId, "CONTEXT.STORED", issueNumber, 1);
        return events.Count > 0
            ? Results.Ok(new { issueNumber, context = events[0].Data })
            : Results.NotFound(new { error = "No context found" });
    }

    public static Task<IResult> QueryContext(QueryContextRequest req) =>
        Task.FromResult(Results.Ok(new { query = req.Query, results = Array.Empty<object>() }));

    public static Task<IResult> GetRepoConfig() =>
        Task.FromResult(Results.Ok(new { configured = false }));

    public static Task<IResult> GetIssues() =>
        Task.FromResult(Results.Ok(Array.Empty<object>()));

    public static Task<IResult> GetSecurityAlerts() =>
        Task.FromResult(Results.Ok(Array.Empty<object>()));

    public static Task<IResult> PostIssueComment(IssueCommentRequest req) =>
        Task.FromResult(Results.Ok(new { message = "Comment posted (stub)", repo = req.Repo, issueNumber = req.IssueNumber }));

    public static Task<IResult> PostIssueLabels(IssueLabelRequest req) =>
        Task.FromResult(Results.Ok(new { message = "Labels added (stub)", labels = req.Labels }));

    public static Task<IResult> DeleteIssueLabel(string repo, int issueNumber, string label) =>
        Task.FromResult(Results.Ok(new { message = $"Label '{label}' removed (stub)" }));

    public static Task<IResult> CreateIssue(CreateIssueRequest req) =>
        Task.FromResult(Results.Ok(new { message = "Issue created (stub)", title = req.Title }));

    public static Task<IResult> TriggerCi(TriggerCiRequest req) =>
        Task.FromResult(Results.Ok(new { message = "CI triggered (stub)", workflow = req.Workflow }));

    public static Task<IResult> ExecuteTask(ExecuteTaskRequest req) =>
        Task.FromResult(Results.Ok(new { message = "Task execution started (stub)", taskType = req.TaskType }));

    public static async Task<IResult> PostCycleResult(CycleResultRequest req, IEventRepository eventRepo, ITenantContext tc)
    {
        await eventRepo.AppendAsync(new DomainEvent
        {
            Type = "CYCLE.RESULT",
            TenantId = tc.TenantId,
            IssueNumber = req.IssueNumber,
            Data = System.Text.Json.JsonSerializer.Serialize(req.Result)
        });
        return Results.Ok(new { message = "Cycle result stored" });
    }

    public static async Task<IResult> GetCycleResults(IEventRepository eventRepo, ITenantContext tc, int? limit)
    {
        var events = await eventRepo.QueryAsync(tc.TenantId, "CYCLE.RESULT", null, limit ?? 20);
        return Results.Ok(events.Select(e => new { e.Id, e.IssueNumber, e.Data, e.CreatedAt }));
    }

    public static Task<IResult> AgentAvailable(AgentAvailableRequest req) =>
        Task.FromResult(Results.Ok(new { message = "Agent registered", engineId = req.EngineId }));
}
