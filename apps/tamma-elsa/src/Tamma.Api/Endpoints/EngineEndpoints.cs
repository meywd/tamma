using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Engine;
using Tamma.Api.Services.Engine;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Engine callback HTTP surface — the API the deployed Elsa activities POST
/// to as they orchestrate workflows.
///
/// <para>Each handler corresponds to one or more deleted TS routes from
/// <c>packages/api/src/routes/engine/*</c>. The audit findings 001–013,
/// 016–028 in <c>docs/audit/port-gaps/engine/</c> document the remediation
/// status per endpoint.</para>
/// </summary>
public static class EngineEndpoints
{
    // ─── Engine lifecycle (commands / state / events / stats / plan / history) ─

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

    /// <summary>
    /// Audit finding 012: TS streamed lifecycle events as SSE; the C# port
    /// returns one-shot JSON. Real SSE depends on porting the
    /// <c>TammaEngine</c> abstraction (finding 013). This endpoint advertises
    /// itself as SSE in the <c>Content-Type</c> header but emits a single
    /// snapshot frame followed by an EOF terminator — dashboards that open
    /// EventSource against this URL will at least parse one frame instead of
    /// failing to negotiate.
    ///
    /// TODO(epic-10/story-10-1): full live SSE once TammaEngine ports.
    /// </summary>
    public static async Task<IResult> GetEventsState(
        HttpResponse response,
        IEventRepository eventRepo,
        ITenantContext tc,
        int? limit)
    {
        var events = await eventRepo.QueryAsync(tc.TenantId, null, null, limit ?? 20);
        return await WriteSseSnapshotAsync(response, "state",
            events.Select(e => new { e.Id, e.Type, e.CreatedAt }));
    }

    /// <summary>Audit finding 012 — same SSE-snapshot treatment as state.</summary>
    public static async Task<IResult> GetEventsLogs(
        HttpResponse response,
        IEventRepository eventRepo,
        ITenantContext tc,
        int? limit)
    {
        var events = await eventRepo.QueryAsync(tc.TenantId, null, null, limit ?? 50);
        return await WriteSseSnapshotAsync(response, "log",
            events.Select(e => new { e.Id, e.Type, e.Data, e.CreatedAt }));
    }

    private static async Task<IResult> WriteSseSnapshotAsync(
        HttpResponse response, string eventName, object payload)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.Append("Cache-Control", "no-cache");
        response.Headers.Append("X-Accel-Buffering", "no");
        var json = JsonSerializer.Serialize(payload);
        await response.WriteAsync($"event: {eventName}\n");
        await response.WriteAsync($"data: {json}\n\n");
        await response.Body.FlushAsync();
        return Results.Empty;
    }

    // ─── Context endpoints (store / get / query) — finding 004 ────────────────

    public static async Task<IResult> StoreContext(
        StoreContextRequest req,
        IEventRepository eventRepo,
        IContextStore contextStore,
        ITenantContext tc)
    {
        if (string.IsNullOrWhiteSpace(req.Repository))
            return Results.BadRequest(new { error = "repository is required" });

        // Two payload shapes the deployed Elsa activities send:
        //   StoreFindingsActivity     → {repository, issueNumber, findings: {...}}
        //   StoreRoleFindingActivity  → {repository, issueNumber, role, finding}
        // Normalise to a single {role: content} object.
        JsonElement findingsToStore;
        if (req.Findings is JsonElement f && f.ValueKind != JsonValueKind.Undefined)
        {
            findingsToStore = f;
        }
        else if (!string.IsNullOrEmpty(req.Role) &&
                 req.Finding is JsonElement single && single.ValueKind != JsonValueKind.Undefined)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WritePropertyName(req.Role);
                single.WriteTo(writer);
                writer.WriteEndObject();
            }
            using var doc = JsonDocument.Parse(ms.ToArray());
            findingsToStore = doc.RootElement.Clone();
        }
        else
        {
            return Results.BadRequest(new { error = "findings or {role, finding} required" });
        }

        await contextStore.StoreAsync(req.Repository, req.IssueNumber, findingsToStore);

        await eventRepo.AppendAsync(new DomainEvent
        {
            Type = "CONTEXT.STORED",
            TenantId = tc.TenantId,
            IssueNumber = req.IssueNumber,
            Data = JsonSerializer.Serialize(new
            {
                repository = req.Repository,
                issueNumber = req.IssueNumber,
                role = req.Role,
                hasFindings = true
            })
        });

        return Results.Ok(new
        {
            ok = true,
            repository = req.Repository,
            issueNumber = req.IssueNumber,
            storedAt = DateTime.UtcNow
        });
    }

    public static async Task<IResult> GetContext(
        int issueNumber,
        IContextStore contextStore,
        [FromQuery] string? repository = null)
    {
        var entry = await contextStore.GetAsync(repository, issueNumber);
        if (entry is null)
            return Results.NotFound(new { error = "No context found" });

        return Results.Ok(new
        {
            repository = entry.Repository,
            issueNumber = entry.IssueNumber,
            findings = entry.Findings,
            storedAt = entry.StoredAt
        });
    }

    public static async Task<IResult> QueryContext(
        QueryContextRequest req,
        IContextStore contextStore)
    {
        if (string.IsNullOrWhiteSpace(req.Query))
            return Results.BadRequest(new { error = "query is required" });

        var (chunks, totalTokens) = await contextStore.QueryAsync(
            req.Repository, req.IssueNumber, req.Query, req.Role, req.MaxTokens);

        return Results.Ok(new
        {
            query = req.Query,
            chunks = chunks.Select(c => new { content = c.Content, role = c.Role, score = c.Score }),
            totalTokens
        });
    }

    // ─── GitHub-proxy endpoints (findings 005-011) ────────────────────────────

    public static async Task<IResult> GetRepoConfig(
        IGitHubEngineCallbackService github,
        [FromQuery] string? repo,
        [FromQuery] string? branch)
    {
        if (string.IsNullOrEmpty(repo))
            return Results.BadRequest(new { error = "Missing required query parameter: repo" });

        var (owner, name) = ParseOwnerRepo(repo);
        if (owner is null || name is null)
            return Results.BadRequest(new { error = $"Invalid repo format: \"{repo}\". Expected \"owner/repo\"." });

        var result = await github.ReadRepoConfigAsync(owner, name, branch ?? "main");
        if (result.ServiceUnavailable)
        {
            // TS contract: graceful degradation — return {} instead of 5xx so
            // the deployed Elsa activity falls through to its empty-conventions
            // path. Keeps workflows running on installations without a wired
            // GitHub App client.
            return Results.Ok(JsonDocument.Parse("{}").RootElement);
        }
        return Results.Ok(result.Result);
    }

    public static async Task<IResult> GetIssues(
        IGitHubEngineCallbackService github,
        [FromQuery] string? repo,
        [FromQuery] string? state,
        [FromQuery] string? labels,
        [FromQuery] int? per_page,
        [FromQuery] int? page)
    {
        if (string.IsNullOrEmpty(repo))
            return Results.BadRequest(new { error = "Missing required query parameter: repo" });

        var (owner, name) = ParseOwnerRepo(repo);
        if (owner is null || name is null)
            return Results.BadRequest(new { error = $"Invalid repo format: \"{repo}\"." });

        var result = await github.ListIssuesAsync(
            owner, name, state ?? "open", labels, per_page ?? 30, page ?? 1);
        return ToHttpResult(result, r => Results.Ok(new { issues = r.Issues, total = r.Total }));
    }

    public static async Task<IResult> GetSecurityAlerts(
        IGitHubEngineCallbackService github,
        [FromQuery] string? repo,
        [FromQuery] string? type)
    {
        if (string.IsNullOrEmpty(repo))
            return Results.BadRequest(new { error = "Missing required query parameter: repo" });

        var (owner, name) = ParseOwnerRepo(repo);
        if (owner is null || name is null)
            return Results.BadRequest(new { error = $"Invalid repo format: \"{repo}\"." });

        var result = await github.ListSecurityAlertsAsync(owner, name, type ?? "all");
        return ToHttpResult(result, r => Results.Ok(new
        {
            dependabot = r.Dependabot,
            codeScanning = r.CodeScanning
        }));
    }

    public static async Task<IResult> PostIssueComment(
        IssueCommentRequest req,
        IGitHubEngineCallbackService github)
    {
        if (string.IsNullOrEmpty(req.Repository))
            return Results.BadRequest(new { error = "repository is required" });
        if (string.IsNullOrEmpty(req.Body))
            return Results.BadRequest(new { error = "body is required" });

        var (owner, name) = ParseOwnerRepo(req.Repository);
        if (owner is null || name is null)
            return Results.BadRequest(new { error = "Invalid repo format" });

        var result = await github.PostIssueCommentAsync(owner, name, req.IssueNumber, req.Body);
        return ToHttpResult(result, r => Results.Ok(new { id = r.Id, htmlUrl = r.HtmlUrl }));
    }

    public static async Task<IResult> PostIssueLabels(
        IssueLabelRequest req,
        IGitHubEngineCallbackService github)
    {
        if (string.IsNullOrEmpty(req.Repository))
            return Results.BadRequest(new { error = "repository is required" });
        if (req.Labels is null || req.Labels.Length == 0)
            return Results.BadRequest(new { error = "labels[] must not be empty" });

        var (owner, name) = ParseOwnerRepo(req.Repository);
        if (owner is null || name is null)
            return Results.BadRequest(new { error = "Invalid repo format" });

        var result = await github.AddIssueLabelsAsync(owner, name, req.IssueNumber, req.Labels);
        return ToHttpResult(result, r => Results.Ok(new { labels = r }));
    }

    public static async Task<IResult> DeleteIssueLabel(
        string repo,
        int issueNumber,
        string label,
        IGitHubEngineCallbackService github)
    {
        var (owner, name) = ParseOwnerRepo(repo);
        if (owner is null || name is null)
            return Results.BadRequest(new { error = "Invalid repo format" });

        var result = await github.RemoveIssueLabelAsync(owner, name, issueNumber, label);
        return ToHttpResult(result, _ => Results.Ok(new { removed = true, label }));
    }

    public static async Task<IResult> CreateIssue(
        CreateIssueRequest req,
        IGitHubEngineCallbackService github)
    {
        if (string.IsNullOrEmpty(req.Repository))
            return Results.BadRequest(new { error = "repository is required" });
        if (string.IsNullOrEmpty(req.Title))
            return Results.BadRequest(new { error = "title is required" });

        var (owner, name) = ParseOwnerRepo(req.Repository);
        if (owner is null || name is null)
            return Results.BadRequest(new { error = "Invalid repo format" });

        var result = await github.CreateIssueAsync(
            owner, name, req.Title, req.Body, req.Labels, req.Assignees);
        return ToHttpResult(result, r => Results.Created(
            $"https://github.com/{owner}/{name}/issues/{r.Number}",
            new { number = r.Number, htmlUrl = r.HtmlUrl, title = r.Title }));
    }

    public static async Task<IResult> TriggerCi(
        TriggerCiRequest req,
        IGitHubEngineCallbackService github)
    {
        if (string.IsNullOrEmpty(req.Repository))
            return Results.BadRequest(new { error = "repository is required" });
        if (string.IsNullOrEmpty(req.BranchName))
            return Results.BadRequest(new { error = "branchName is required" });
        if (string.IsNullOrEmpty(req.WorkflowFile))
            return Results.BadRequest(new { error = "workflowFile is required" });

        var (owner, name) = ParseOwnerRepo(req.Repository);
        if (owner is null || name is null)
            return Results.BadRequest(new { error = "Invalid repo format" });

        var result = await github.TriggerCiAsync(
            owner, name, req.BranchName, req.WorkflowFile, req.Inputs);
        return ToHttpResult(result, r => Results.Ok(new
        {
            dispatched = r.Dispatched,
            workflowFile = r.WorkflowFile,
            branch = r.Branch
        }));
    }

    // ─── Execute task — finding 001 ───────────────────────────────────────────

    /// <summary>
    /// Run an LLM-driven task on behalf of an Elsa activity.
    ///
    /// <para>Audit finding 001 (P0): the previous one-line stub returned
    /// <c>{message, taskType}</c> — none of the deployed activities can
    /// parse that. Restored to TS shape via <see cref="IExecuteTaskService"/>
    /// which delegates to <c>ILlmProxyService</c>. Real role-based agent
    /// resolution + tool loop ports later.</para>
    /// </summary>
    public static async Task<IResult> ExecuteTask(
        ExecuteTaskRequest req,
        IExecuteTaskService taskService,
        ITenantContext tc)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
            return Results.BadRequest(new { error = "prompt is required" });

        var input = new ExecuteTaskInput(
            Prompt: req.Prompt,
            Role: req.Role,
            AnalysisType: req.AnalysisType,
            Repository: req.Repository,
            EnableTools: req.EnableTools,
            Model: req.Model,
            MaxBudgetUsd: req.MaxBudgetUsd,
            Cwd: req.Cwd);

        var result = await taskService.ExecuteAsync(input, tc.TenantId);

        if (!result.Success)
        {
            // 500 with the documented response shape so the activity can
            // surface the error rather than throw on missing-property access.
            return Results.Json(new
            {
                success = false,
                output = string.Empty,
                tokensUsed = 0,
                costUsd = 0,
                durationMs = result.DurationMs,
                toolCalls = 0,
                error = result.Error
            }, statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Ok(new
        {
            success = true,
            output = result.Output,
            tokensUsed = result.TokensUsed,
            costUsd = result.CostUsd,
            durationMs = result.DurationMs,
            toolCalls = result.ToolCalls
        });
    }

    // ─── Cycle results — finding 003 ──────────────────────────────────────────

    public static async Task<IResult> PostCycleResult(
        CycleResultRequest req, IEventRepository eventRepo, ITenantContext tc)
    {
        if (string.IsNullOrWhiteSpace(req.ExitReason))
            return Results.BadRequest(new { error = "exitReason is required" });

        // Persist all structured fields so the dashboard's failure-classification
        // queries see exitReason / error / durationMs first-class.
        await eventRepo.AppendAsync(new DomainEvent
        {
            Type = "CYCLE.RESULT",
            TenantId = tc.TenantId,
            IssueNumber = req.IssueNumber,
            Data = JsonSerializer.Serialize(new
            {
                exitReason = req.ExitReason,
                issueNumber = req.IssueNumber,
                repository = req.Repository,
                error = req.Error,
                durationMs = req.DurationMs,
                metadata = req.Metadata
            })
        });
        return Results.Created(
            $"/api/engine/cycle-results/{Guid.NewGuid()}",
            new { ok = true, storedAt = DateTime.UtcNow });
    }

    public static async Task<IResult> GetCycleResults(IEventRepository eventRepo, ITenantContext tc, int? limit)
    {
        var events = await eventRepo.QueryAsync(tc.TenantId, "CYCLE.RESULT", null, limit ?? 20);
        return Results.Ok(events.Select(e => new
        {
            e.Id,
            e.IssueNumber,
            data = SafeParseJson(e.Data),
            createdAt = e.CreatedAt
        }));
    }

    // ─── Agent availability — finding 002 ─────────────────────────────────────

    /// <summary>
    /// Audit finding 002 — converted from POST-with-body (the old
    /// engine-registration mis-port) to the TS contract: a parameter-free
    /// GET that returns <c>{available: bool}</c>.
    /// </summary>
    public static IResult AgentAvailable(IConfiguration config)
    {
        var available = !string.IsNullOrWhiteSpace(config["Anthropic:ApiKey"]);
        return Results.Ok(new { available });
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static (string? Owner, string? Repo) ParseOwnerRepo(string repo)
    {
        if (string.IsNullOrEmpty(repo)) return (null, null);
        var parts = repo.Split('/');
        if (parts.Length != 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
            return (null, null);
        return (parts[0], parts[1]);
    }

    private static IResult ToHttpResult<T>(GitHubCallbackResult<T> result, Func<T, IResult> ok)
    {
        if (result.ServiceUnavailable)
        {
            return Results.Json(new
            {
                error = "github_client_not_configured",
                detail = "GitHub App client is not wired in this deployment"
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (result.Result is null)
        {
            return Results.Json(new { error = result.ErrorReason ?? "github_error" },
                statusCode: StatusCodes.Status502BadGateway);
        }
        return ok(result.Result);
    }

    private static JsonElement SafeParseJson(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return JsonDocument.Parse("null").RootElement.Clone();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("null").RootElement.Clone();
        }
    }
}
