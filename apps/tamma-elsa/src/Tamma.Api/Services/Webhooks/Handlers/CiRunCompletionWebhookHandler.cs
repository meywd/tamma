using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Tamma.Core.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Services.Webhooks.Handlers;

/// <summary>
/// Epic 31 P4 M1 — CI-completion webhook wake-up: the DG-5 ACCELERATOR on top
/// of P3's <c>CiCompletionPollerService</c>. When the platform pushes a
/// terminal CI-run event (GitHub <c>workflow_run.completed</c>, Gitea/Forgejo
/// <c>workflow_run</c>, GitLab <c>pipeline</c>), this handler resumes the
/// matching suspended CI wait IMMEDIATELY instead of waiting for the next poll
/// tick. The poller stays registered as the fallback (no ingress dependency);
/// this handler is purely additive.
///
/// <para><b>Resume path — the poller's, reused verbatim.</b> The handler
/// enumerates suspended waits from the engine
/// (<c>GET /elsa/api/ci/waits</c>) and resumes by EXACT bookmark id
/// (<c>POST /elsa/api/ci/waits/resume</c>). Elsa burns the wait's bookmarks on
/// completion, so a late webhook (poller or timeout won the race) answers 404
/// — a benign no-op, never a double-advance. The same idempotency argument as
/// the poller, plus the receiver's <c>platform_webhook_deliveries</c> dedupe
/// in front.</para>
///
/// <para><b>Tenant + repo scoping (the plan's named risk).</b> A wait is only
/// resumed when BOTH match the delivery: the wait's <c>Repository</c> equals
/// the event's repo (ordinal-ignore-case) AND the wait's <c>TenantId</c>
/// equals the event's resolved tenant (both-null = the single-user case). A
/// webhook resolved to tenant A can therefore never resume tenant B's wait,
/// even when both tenants build the same fork with colliding run ids.</para>
/// </summary>
public sealed class CiRunCompletionWebhookHandler : IWebhookHandler
{
    /// <summary>Same engine client + seam the poller uses.</summary>
    internal const string ElsaClientName = Ci.CiCompletionPollerService.ElsaClientName;
    internal const string ListWaitsPath = Ci.CiCompletionPollerService.ListWaitsPath;
    internal const string ResumePath = Ci.CiCompletionPollerService.ResumePath;

    /// <summary>GitLab pipeline statuses that are terminal (the run will
    /// not change state again).</summary>
    private static readonly string[] GitLabTerminalStatuses =
        ["success", "failed", "canceled", "cancelled", "skipped"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CiRunCompletionWebhookHandler> _logger;

    public CiRunCompletionWebhookHandler(
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        PlatformKind kind,
        string eventTypePattern,
        ILogger<CiRunCompletionWebhookHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventTypePattern);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        Kind = kind;
        EventTypePattern = eventTypePattern;
        _logger = logger;
    }

    public PlatformKind Kind { get; }

    public string EventTypePattern { get; }

    public async Task HandleAsync(PlatformWebhookEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (!TryExtractTerminalRun(evt, out var runId, out var conclusion))
        {
            // In-flight update (queued / in_progress) or unparseable payload —
            // nothing to wake. The poller/timeout SLA still owns the wait.
            return;
        }

        var repo = evt.RepoFullName;
        if (string.IsNullOrWhiteSpace(repo))
        {
            _logger.LogDebug(
                "{Kind} CI-run event {EventType} carried no repository — skipping wake",
                Kind, LogSanitizer.Clean(evt.EventType));
            return;
        }

        var engine = _httpClientFactory.CreateClient(ElsaClientName);

        CiWaitsResponse? waits;
        using (var response = await engine.GetAsync(ListWaitsPath, ct).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "CI wait listing returned {Status}; webhook wake skipped (poller fallback owns it)",
                    (int)response.StatusCode);
                return;
            }
            waits = await response.Content
                .ReadFromJsonAsync<CiWaitsResponse>(Json, ct).ConfigureAwait(false);
        }

        if (waits?.Waits is not { Count: > 0 } list) return;

        var buildPassed = string.Equals(conclusion, "success", StringComparison.OrdinalIgnoreCase);

        foreach (var wait in list)
        {
            ct.ThrowIfCancellationRequested();

            if (!string.Equals(wait.RunId, runId, StringComparison.Ordinal)) continue;
            if (!string.Equals(wait.Repository, repo, StringComparison.OrdinalIgnoreCase)) continue;

            // ── Cross-tenant guard ── the wait's tenant must equal the tenant
            // the RECEIVER resolved for this delivery. Never resume across the
            // boundary; a mismatch is logged and dropped (the poller resolves
            // the right tenant's driver and will complete the wait normally).
            if (!TenantMatches(evt.TenantId, wait.TenantId))
            {
                _logger.LogWarning(
                    "CI-run webhook for {Repo} run {RunId} matched a wait in a DIFFERENT tenant " +
                    "(event tenant={EventTenant}, wait tenant={WaitTenant}) — refusing to resume",
                    LogSanitizer.Clean(repo), LogSanitizer.Clean(runId),
                    evt.TenantId, LogSanitizer.Clean(wait.TenantId));
                continue;
            }

            using var resumeResponse = await engine.PostAsJsonAsync(
                ResumePath,
                new
                {
                    bookmarkId = wait.BookmarkId,
                    runId = wait.RunId,
                    status = conclusion,
                    buildPassed,
                },
                Json, ct).ConfigureAwait(false);

            if (resumeResponse.StatusCode == HttpStatusCode.NotFound)
            {
                // Benign — the timeout edge or a poller tick burned the
                // bookmark first. Never a double-advance.
                _logger.LogDebug(
                    "CI wait {BookmarkId} was already gone when webhook-waking run {RunId}",
                    wait.BookmarkId, LogSanitizer.Clean(runId));
                continue;
            }

            if (!resumeResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "CI webhook wake for run {RunId} on {Repo} returned {Status}",
                    LogSanitizer.Clean(runId), LogSanitizer.Clean(repo),
                    (int)resumeResponse.StatusCode);
                continue;
            }

            _logger.LogInformation(
                "Webhook-woke CI wait for run {RunId} on {Repo} with conclusion {Conclusion} (buildPassed={BuildPassed})",
                LogSanitizer.Clean(runId), LogSanitizer.Clean(repo),
                LogSanitizer.Clean(conclusion), buildPassed);

            await EmitResumedEventAsync(evt, wait, conclusion, buildPassed, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Strict tenant equality between the receiver-resolved tenant and the
    /// wait's bookmark payload tenant. Both-absent (single-user waits carry no
    /// tenant) matches; any one-sided or differing value refuses.
    /// </summary>
    internal static bool TenantMatches(Guid? eventTenant, string? waitTenant)
    {
        var waitParsed = Guid.TryParse(waitTenant, out var g) && g != Guid.Empty
            ? g
            : (Guid?)null;
        var evtNormalized = eventTenant == Guid.Empty ? null : eventTenant;
        return evtNormalized == waitParsed;
    }

    /// <summary>
    /// Extract (runId, terminal conclusion) from the platform payload.
    /// Returns false for non-terminal updates and unparseable bodies.
    /// </summary>
    internal static bool TryExtractTerminalRun(
        PlatformWebhookEvent evt, out string runId, out string conclusion)
    {
        runId = "";
        conclusion = "";
        var root = evt.ParsedJson;
        if (root.ValueKind != JsonValueKind.Object) return false;

        switch (evt.Kind)
        {
            case PlatformKind.GitHub:
            case PlatformKind.Gitea:
            case PlatformKind.Forgejo:
            {
                // GitHub/Gitea/Forgejo share the Actions payload shape:
                // { action, workflow_run: { id, status, conclusion } }.
                if (!root.TryGetProperty("workflow_run", out var run)
                    || run.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }
                if (!run.TryGetProperty("id", out var idEl)) return false;
                runId = idEl.ValueKind switch
                {
                    JsonValueKind.Number when idEl.TryGetInt64(out var n) => n.ToString(),
                    JsonValueKind.String => idEl.GetString() ?? "",
                    _ => "",
                };
                if (string.IsNullOrEmpty(runId)) return false;

                var concl = run.TryGetProperty("conclusion", out var c)
                    && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : null;
                if (string.IsNullOrEmpty(concl)) return false; // still running
                conclusion = concl;
                return true;
            }

            case PlatformKind.GitLab:
            {
                // Pipeline Hook: { object_attributes: { id, status } }.
                if (!root.TryGetProperty("object_attributes", out var attrs)
                    || attrs.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }
                if (!attrs.TryGetProperty("id", out var idEl)) return false;
                runId = idEl.ValueKind switch
                {
                    JsonValueKind.Number when idEl.TryGetInt64(out var n) => n.ToString(),
                    JsonValueKind.String => idEl.GetString() ?? "",
                    _ => "",
                };
                if (string.IsNullOrEmpty(runId)) return false;

                var status = attrs.TryGetProperty("status", out var s)
                    && s.ValueKind == JsonValueKind.String
                    ? s.GetString()
                    : null;
                if (string.IsNullOrEmpty(status)) return false;
                if (!GitLabTerminalStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
                {
                    return false; // running / pending — not a wake
                }
                conclusion = status;
                return true;
            }

            default:
                return false;
        }
    }

    private async Task EmitResumedEventAsync(
        PlatformWebhookEvent evt,
        CiWaitDto wait,
        string conclusion,
        bool buildPassed,
        CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var events = scope.ServiceProvider.GetRequiredService<IEventRepository>();
            await events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = Ci.CiCompletionPollerService.WaitResumedEventType,
                TenantId = evt.TenantId,
                Tags = JsonSerializer.Serialize(new
                {
                    tenantId = evt.TenantId?.ToString(),
                    repo = wait.Repository,
                    runId = wait.RunId,
                    sessionId = wait.SessionId.ToString(),
                }),
                Metadata = JsonSerializer.Serialize(new { workflowVersion = "1.0.0", eventSource = "system" }),
                Data = JsonSerializer.Serialize(new
                {
                    conclusion,
                    buildPassed,
                    bookmarkId = wait.BookmarkId,
                    // Distinguishes the accelerator from the poller in the
                    // audit trail — same event type, different vehicle.
                    source = "webhook",
                    deliveryId = evt.DeliveryId,
                }),
                CreatedAt = DateTime.UtcNow,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "CI.WAIT.RESUMED (webhook) event append failed; the resume itself already happened");
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal sealed record CiWaitsResponse(
        [property: JsonPropertyName("waits")] List<CiWaitDto>? Waits);

    internal sealed record CiWaitDto(
        [property: JsonPropertyName("bookmarkId")] string BookmarkId,
        [property: JsonPropertyName("workflowInstanceId")] string WorkflowInstanceId,
        [property: JsonPropertyName("sessionId")] Guid SessionId,
        [property: JsonPropertyName("runId")] string RunId,
        [property: JsonPropertyName("repository")] string Repository,
        [property: JsonPropertyName("tenantId")] string? TenantId);
}
