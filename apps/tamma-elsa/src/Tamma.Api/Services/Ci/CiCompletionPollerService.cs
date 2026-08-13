using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Activities.ADL;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;
using PModels = Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Services.Ci;

/// <summary>
/// Epic 31 P3 (DG-5) — the durable CI completion poller, closing the live hole
/// the execution plan calls out: NOTHING resumed the CI-result bookmark
/// (<c>CIResultBookmarkPayload</c>) — only the 30-minute timeout ended the CI
/// wait, for every platform including GitHub. Polling is the chosen vehicle
/// (no ingress dependency); the P4 webhook handler becomes an accelerator on
/// top, not a replacement.
///
/// <para><b>Shape.</b> A hosted background service (catalogued as
/// <c>automation:ci-completion-poller</c>, the P2
/// <c>PlatformDriverCacheInvalidator</c> convention). Each tick:</para>
/// <list type="number">
///   <item>enumerates suspended CI waits from the engine
///     (<c>GET /elsa/api/ci/waits</c> over the "elsa" named client — bookmarks
///     live in the engine process);</item>
///   <item>for each wait, resolves the tenant's platform driver via
///     <see cref="IPlatformResolver.ResolveForMediationAsync"/> and polls
///     <c>driver.Actions.GetRunStatusAsync</c> — the abstraction, never a
///     platform-specific client;</item>
///   <item>on a terminal conclusion, POSTs the result to the engine's resume
///     seam (<c>POST /elsa/api/ci/waits/resume</c>) and emits one
///     <c>CI.WAIT.RESUMED</c> DCB audit event.</item>
/// </list>
///
/// <para><b>Idempotency / the timeout race.</b> Elsa burns the wait's bookmarks
/// when the activity completes, and the resume seam targets the exact bookmark
/// id: if the timeout edge (or a concurrent tick) advanced the wait first, the
/// resume answers 404 and this poller treats that as a benign no-op. For
/// resumes that are IN FLIGHT (not yet committed), the seam additionally
/// claims the bookmark row atomically before running — a tick that lands
/// mid-continuation (or after the caller-side HTTP timeout of a long burst)
/// loses the claim and gets the same benign 404, so a resume can never
/// double-advance a workflow (see <c>CiWaitEndpoints</c>).</para>
///
/// <para><b>Fail-soft.</b> Every per-wait failure is caught and logged; a bad
/// wait never stops the sweep, and a dead engine or platform never crashes the
/// service — the 30m timeout SLA remains the backstop exactly as before.</para>
/// </summary>
public sealed class CiCompletionPollerService : BackgroundService
{
    internal const string ElsaClientName = "elsa";
    internal const string ListWaitsPath = "/elsa/api/ci/waits";
    internal const string ResumePath = "/elsa/api/ci/waits/resume";

    /// <summary>DCB audit event emitted once per successful resume.</summary>
    internal const string WaitResumedEventType = "CI.WAIT.RESUMED";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CiCompletionPollerService> _logger;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;

    public CiCompletionPollerService(
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<CiCompletionPollerService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _enabled = configuration.GetValue("Ci:CompletionPoll:Enabled", true);
        var seconds = configuration.GetValue("Ci:CompletionPoll:IntervalSeconds", 30);
        _interval = TimeSpan.FromSeconds(Math.Max(5, seconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("CI completion poller disabled (Ci:CompletionPoll:Enabled=false)");
            return;
        }

        _logger.LogInformation(
            "CI completion poller started (interval {IntervalSeconds}s) — CI waits now resume on run completion instead of only the timeout SLA",
            _interval.TotalSeconds);

        using var timer = new PeriodicTimer(_interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Fail-soft: the timeout SLA is the backstop; the next tick retries.
                _logger.LogWarning(ex, "CI completion poll tick failed; will retry next interval");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One sweep: list waits → poll each run through the resolved driver →
    /// resume terminal ones. Returns the number of waits resumed (test seam).
    /// </summary>
    internal async Task<int> PollOnceAsync(CancellationToken ct)
    {
        var engine = _httpClientFactory.CreateClient(ElsaClientName);

        CiWaitsResponse? waits;
        using (var response = await engine.GetAsync(ListWaitsPath, ct).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "CI wait listing returned {Status}; skipping this tick", (int)response.StatusCode);
                return 0;
            }
            waits = await response.Content.ReadFromJsonAsync<CiWaitsResponse>(Json, ct).ConfigureAwait(false);
        }

        if (waits?.Waits is not { Count: > 0 } list) return 0;

        var resumed = 0;
        using var scope = _scopeFactory.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IPlatformResolver>();
        var events = scope.ServiceProvider.GetRequiredService<IEventRepository>();

        foreach (var wait in list)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await TryResumeWaitAsync(engine, resolver, events, wait, ct).ConfigureAwait(false))
                {
                    resumed++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "CI completion poll failed for run {RunId} on {Repository}; the wait keeps its timeout SLA",
                    wait.RunId, wait.Repository);
            }
        }

        return resumed;
    }

    private async Task<bool> TryResumeWaitAsync(
        HttpClient engine, IPlatformResolver resolver, IEventRepository events, CiWaitDto wait, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(wait.Repository) || string.IsNullOrWhiteSpace(wait.RunId))
            return false;

        Guid? tenantId = Guid.TryParse(wait.TenantId, out var parsed) && parsed != Guid.Empty
            ? parsed
            : null;

        var resolution = await resolver.ResolveForMediationAsync(tenantId, ct).ConfigureAwait(false);
        var actions = resolution?.Driver.Actions;
        if (actions is null)
        {
            // No driver / no Actions surface — the wait keeps its timeout SLA.
            return false;
        }

        var (owner, repoName) = GitRepoName.Split(wait.Repository);
        var statusRes = await actions.GetRunStatusAsync(owner, repoName, wait.RunId, ct).ConfigureAwait(false);
        if (statusRes is not PlatformResult<PModels.WorkflowRun>.Ok ok) return false;

        var run = ok.Value;
        if (run.Conclusion is null) return false; // still running — next tick.

        var buildPassed = string.Equals(run.Conclusion, "success", StringComparison.OrdinalIgnoreCase);
        using var resumeResponse = await engine.PostAsJsonAsync(
            ResumePath,
            new
            {
                bookmarkId = wait.BookmarkId,
                runId = wait.RunId,
                status = run.Conclusion,
                buildPassed,
            },
            Json, ct).ConfigureAwait(false);

        if (resumeResponse.StatusCode == HttpStatusCode.NotFound)
        {
            // Benign: the timeout edge (or a sibling tick) burned the bookmark
            // first. Never a double-advance.
            _logger.LogDebug(
                "CI wait {BookmarkId} was already gone when resuming run {RunId} — timeout or sibling resume won the race",
                wait.BookmarkId, wait.RunId);
            return false;
        }

        if (!resumeResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "CI wait resume for run {RunId} on {Repository} returned {Status}",
                wait.RunId, wait.Repository, (int)resumeResponse.StatusCode);
            return false;
        }

        _logger.LogInformation(
            "Resumed CI wait for run {RunId} on {Repository} with conclusion {Conclusion} (buildPassed={BuildPassed})",
            wait.RunId, wait.Repository, run.Conclusion, buildPassed);

        await EmitResumedEventAsync(events, tenantId, wait, run.Conclusion, buildPassed, ct).ConfigureAwait(false);
        return true;
    }

    private async Task EmitResumedEventAsync(
        IEventRepository events, Guid? tenantId, CiWaitDto wait, string conclusion, bool buildPassed, CancellationToken ct)
    {
        _ = ct;
        try
        {
            await events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = WaitResumedEventType,
                TenantId = tenantId,
                Tags = JsonSerializer.Serialize(new
                {
                    tenantId = tenantId?.ToString(),
                    repo = wait.Repository,
                    runId = wait.RunId,
                    sessionId = wait.SessionId.ToString(),
                }),
                Metadata = JsonSerializer.Serialize(new { workflowVersion = "1.0.0", eventSource = "system" }),
                Data = JsonSerializer.Serialize(new { conclusion, buildPassed, bookmarkId = wait.BookmarkId }),
                CreatedAt = DateTime.UtcNow,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CI.WAIT.RESUMED event append failed; the resume itself already happened");
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Wire shape of <c>GET /elsa/api/ci/waits</c> (engine-side
    /// <c>CiWaitEndpoints.CiWaitDto</c>).</summary>
    internal sealed record CiWaitsResponse([property: JsonPropertyName("waits")] List<CiWaitDto>? Waits);

    internal sealed record CiWaitDto(
        [property: JsonPropertyName("bookmarkId")] string BookmarkId,
        [property: JsonPropertyName("workflowInstanceId")] string WorkflowInstanceId,
        [property: JsonPropertyName("sessionId")] Guid SessionId,
        [property: JsonPropertyName("runId")] string RunId,
        [property: JsonPropertyName("repository")] string Repository,
        [property: JsonPropertyName("tenantId")] string? TenantId);
}
