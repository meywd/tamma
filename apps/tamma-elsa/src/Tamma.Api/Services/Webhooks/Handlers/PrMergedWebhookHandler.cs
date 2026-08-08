using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tamma.Core.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Services.Webhooks.Handlers;

/// <summary>
/// Epic 31 P4 M2 (DG-6) — merged-PR → <c>WaitForPRMerged</c> resume: the
/// 12h-timeout hole closes. Maps GitHub
/// <c>pull_request.closed(merged=true)</c>, Gitea/Forgejo
/// <c>pull_request.closed(merged=true)</c>, and GitLab
/// <c>merge_request action=merge</c> onto the engine-side
/// <c>PrMergedResumeEndpoint</c>, carrying the merge commit SHA. Webhook
/// resume is now the PRIMARY merge-confirmation source; the 12h TimedOut SLA
/// stays as the exception path.
///
/// <para><b>Tenant + repo scoping.</b> The handler forwards the
/// RECEIVER-resolved tenant (never anything from the payload body) plus the
/// repo slug; the engine folds both into the qualified bookmark name
/// (<c>pr-merged-{tenant}-{repo}-{n}</c>), so a delivery resolved to tenant A
/// can never resume tenant B's wait. The engine's legacy-name fallback covers
/// only pre-P4 suspensions and refuses on ambiguity.</para>
///
/// <para><b>Idempotency.</b> The receiver's platform delivery table dedupes
/// re-deliveries; a duplicate that slips through (or a race with the SLA
/// edge) meets a burned bookmark and the engine answers 404 — a benign no-op,
/// never a double-advance.</para>
///
/// <para><b>Chosen over a <c>github.*</c> task handler</b> (plan §6): the
/// dead deferred-task write in <c>InstallationRouterService</c> is deleted in
/// the same milestone so the two paths can never double-resume.</para>
/// </summary>
public sealed class PrMergedWebhookHandler : IWebhookHandler
{
    internal const string ElsaClientName = Ci.CiCompletionPollerService.ElsaClientName;
    internal const string ResumePath = "/elsa/api/adl/pr-merged/resume";

    /// <summary>DCB audit event emitted once per successful webhook resume.</summary>
    internal const string WaitResumedEventType = "CYCLE.PR_MERGE_WAIT.RESUMED";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PrMergedWebhookHandler> _logger;

    public PrMergedWebhookHandler(
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        PlatformKind kind,
        string eventTypePattern,
        ILogger<PrMergedWebhookHandler> logger)
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

        if (!TryExtractMergedPr(evt, out var prNumber, out var mergeSha))
        {
            // closed-without-merge, or an unparseable payload — not a merge.
            return;
        }

        var repo = evt.RepoFullName;
        if (string.IsNullOrWhiteSpace(repo))
        {
            _logger.LogDebug(
                "{Kind} merged-PR event carried no repository — skipping resume", Kind);
            return;
        }

        var engine = _httpClientFactory.CreateClient(ElsaClientName);
        using var response = await engine.PostAsJsonAsync(
            ResumePath,
            new
            {
                prNumber,
                mergeSha,
                tenantId = evt.TenantId?.ToString(),
                repository = repo,
            },
            Json, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Benign: no wait is suspended for this PR (already resumed, the
            // 12h SLA fired first, a duplicate delivery, or a PR outside a
            // Tamma cycle — most merges are).
            _logger.LogDebug(
                "No suspended pr-merged wait for {Repo}#{PrNumber} — nothing to resume",
                LogSanitizer.Clean(repo), prNumber);
            return;
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            _logger.LogError(
                "Ambiguous pr-merged wait for {Repo}#{PrNumber} — engine refused to resume an arbitrary instance",
                LogSanitizer.Clean(repo), prNumber);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "pr-merged resume for {Repo}#{PrNumber} returned {Status}; the 12h SLA remains the backstop",
                LogSanitizer.Clean(repo), prNumber, (int)response.StatusCode);
            return;
        }

        _logger.LogInformation(
            "Resumed WaitForPRMerged for {Repo}#{PrNumber} on the Merged edge (sha {MergeSha})",
            LogSanitizer.Clean(repo), prNumber, LogSanitizer.Clean(mergeSha ?? "<none>"));

        await EmitResumedEventAsync(evt, repo, prNumber, mergeSha, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extract (prNumber, mergeSha) from a merged-PR payload. Returns false
    /// for a close-without-merge or an unrecognizable body.
    /// </summary>
    internal static bool TryExtractMergedPr(
        PlatformWebhookEvent evt, out int prNumber, out string? mergeSha)
    {
        prNumber = 0;
        mergeSha = null;
        var root = evt.ParsedJson;
        if (root.ValueKind != JsonValueKind.Object) return false;

        switch (evt.Kind)
        {
            case PlatformKind.GitHub:
            case PlatformKind.Gitea:
            case PlatformKind.Forgejo:
            {
                // { action: "closed", number, pull_request: { number, merged,
                //   merge_commit_sha | merged_commit_id } }
                if (!root.TryGetProperty("pull_request", out var pr)
                    || pr.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var merged = pr.TryGetProperty("merged", out var m)
                    && m.ValueKind == JsonValueKind.True;
                if (!merged) return false; // closed without merging

                prNumber = ReadInt(root, "number") ?? ReadInt(pr, "number") ?? 0;
                if (prNumber <= 0) return false;

                // GitHub: merge_commit_sha. Gitea/Forgejo: merged_commit_id
                // (their API field name); tolerate either.
                mergeSha = ReadString(pr, "merge_commit_sha")
                    ?? ReadString(pr, "merged_commit_id");
                return true;
            }

            case PlatformKind.GitLab:
            {
                // Merge Request Hook: { object_attributes: { iid, action:
                //   "merge", merge_commit_sha } } — the receiver already
                //   pattern-matched action=merge; re-check defensively.
                if (!root.TryGetProperty("object_attributes", out var attrs)
                    || attrs.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var action = ReadString(attrs, "action");
                if (!string.Equals(action, "merge", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                prNumber = ReadInt(attrs, "iid") ?? 0;
                if (prNumber <= 0) return false;

                mergeSha = ReadString(attrs, "merge_commit_sha");
                return true;
            }

            default:
                return false;
        }
    }

    private static int? ReadInt(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(prop.GetString(), out var s) => s,
            _ => null,
        };
    }

    private static string? ReadString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private async Task EmitResumedEventAsync(
        PlatformWebhookEvent evt,
        string repo,
        int prNumber,
        string? mergeSha,
        CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var events = scope.ServiceProvider.GetRequiredService<IEventRepository>();
            await events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = WaitResumedEventType,
                TenantId = evt.TenantId,
                Tags = JsonSerializer.Serialize(new
                {
                    tenantId = evt.TenantId?.ToString(),
                    repo,
                    prNumber = prNumber.ToString(),
                }),
                Metadata = JsonSerializer.Serialize(new { workflowVersion = "1.0.0", eventSource = "system" }),
                Data = JsonSerializer.Serialize(new
                {
                    prNumber,
                    mergeSha,
                    source = "webhook",
                    platform = evt.Kind.ToString(),
                    deliveryId = evt.DeliveryId,
                }),
                CreatedAt = DateTime.UtcNow,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "CYCLE.PR_MERGE_WAIT.RESUMED event append failed; the resume itself already happened");
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
