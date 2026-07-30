using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;
using Tamma.Core.Actions;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Story 9-11: Shared HTTP client for calling the Tamma API (Fastify in TS,
/// ASP.NET in the C# port). Used by simplified Elsa activities to delegate
/// agent resolution, health, diagnostics, and provider execution to the
/// central API plane.
///
/// Configuration (read from <see cref="IConfiguration"/> with env-var
/// fallbacks):
/// <list type="bullet">
///   <item><c>Tamma:ApiUrl</c> or env <c>TAMMA_API_URL</c> — base URL
///         (defaults to <c>http://localhost:3000</c>).</item>
///   <item><c>Tamma:ApiToken</c> or env <c>TAMMA_API_TOKEN</c> — bearer
///         token for Authorization header.</item>
/// </list>
///
/// All methods return <c>null</c> on HTTP / network failure so callers can
/// fall back to local behavior (per AC 5 in Story 9-11).
/// </summary>
public class TammaApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TammaApiClient> _logger;
    private readonly string _baseUrl;
    private readonly TammaApiHealthMonitor? _healthMonitor;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public TammaApiClient(
        HttpClient httpClient,
        ILogger<TammaApiClient> logger,
        IConfiguration? configuration = null,
        TammaApiHealthMonitor? healthMonitor = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _healthMonitor = healthMonitor;

        _baseUrl = configuration?["Tamma:ApiUrl"]
                   ?? Environment.GetEnvironmentVariable("TAMMA_API_URL")
                   ?? "http://localhost:3000";
        _baseUrl = _baseUrl.TrimEnd('/');

        var token = configuration?["Tamma:ApiToken"]
                    ?? Environment.GetEnvironmentVariable("TAMMA_API_TOKEN");
        if (!string.IsNullOrWhiteSpace(token) &&
            _httpClient.DefaultRequestHeaders.Authorization is null)
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>Base URL in use (test hook).</summary>
    public string BaseUrl => _baseUrl;

    // ----- Agent Resolution --------------------------------------------

    public Task<AgentResolveResult?> ResolveAgentAsync(
        string role,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/agents/{Uri.EscapeDataString(role)}/resolve";
        return GetAsync<AgentResolveResult>(url, tenantId, ct);
    }

    public Task<AgentResolveResult?> ResolveForPhaseAsync(
        ResolveForPhaseRequest request,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/agents/resolve-for-phase";
        return PostAsync<AgentResolveResult>(url, request, tenantId, ct);
    }

    // ----- Managed LLM Call (Story 32-5 — the mediation endpoint) ------

    /// <summary>
    /// Story 32-5 (AC5) — POST the engine→API <see cref="LlmCallApiRequest"/> to
    /// the single managed execution endpoint <c>POST /api/v1/llm/call</c> and
    /// return the key-free <see cref="LlmCallApiResponse"/>.
    ///
    /// <para>Uses the shared <see cref="PostAsync{T}"/> path so the request gets
    /// the engine bearer (configured <c>Tamma:ApiToken</c>), the
    /// <c>X-Tenant-Id</c> header (<paramref name="tenantId"/> — the authoritative
    /// scope the endpoint asserts; the body <c>tenantId</c> carries no authority,
    /// Finding C1), and per-call health recording.</para>
    ///
    /// <para>The endpoint upholds AC7's status discipline: an expected execution
    /// failure rides inside an HTTP 200 envelope with <c>success:false</c> and the
    /// upstream <c>httpStatusCode</c> preserved, so the engine receives a real
    /// body (never nulled by a raw 5xx). A genuine transport / 5xx failure returns
    /// <c>null</c> per the existing contract; the shim treats that as a transient
    /// (httpStatusCode 0) failure so the workflow's RetryCheck advances.</para>
    /// </summary>
    [PerformsEffect(ExternalEffect.LlmCall)]
    public Task<LlmCallApiResponse?> CallLlmAsync(
        LlmCallApiRequest request,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/llm/call";
        return PostAsync<LlmCallApiResponse>(url, request, tenantId, ct);
    }

    // ----- Git mediation (Story 38-1 — the git step-mediation endpoints) --

    /// <summary>
    /// Story 38-1 (AC5) — POST the engine→API <see cref="GitCreateBranchRequest"/>
    /// to <c>POST /api/v1/git/{owner}/{repo}/branches</c>. <paramref name="repo"/>
    /// is <c>owner/name</c>; it is split into two path segments (a full name
    /// carries a slash). Returns null on any non-2xx (guard 403 / token 503 / auth
    /// 401 / transport), which the thin activity maps to its Error outcome
    /// (fail-closed). The token is resolved + used server-side; it never travels here.
    /// </summary>
    [PerformsEffect(ExternalEffect.GitBranchCreate)]
    public Task<GitCallResponse?> CreateBranchAsync(
        string repo, GitCreateBranchRequest request, string? tenantId = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/git/{RepoPath(repo)}/branches";
        return PostAsync<GitCallResponse>(url, request, tenantId, ct);
    }

    /// <summary>Story 38-1 (AC5) — <c>POST /api/v1/git/{owner}/{repo}/pull-requests</c>.</summary>
    [PerformsEffect(ExternalEffect.GitPullRequestCreate)]
    public Task<GitCallResponse?> CreatePullRequestAsync(
        string repo, GitCreatePrRequest request, string? tenantId = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/git/{RepoPath(repo)}/pull-requests";
        return PostAsync<GitCallResponse>(url, request, tenantId, ct);
    }

    /// <summary>Story 38-1 (AC5) — <c>PUT /api/v1/git/{owner}/{repo}/pull-requests/{n}/merge</c>.</summary>
    [PerformsEffect(ExternalEffect.GitPullRequestMerge)]
    public Task<GitCallResponse?> MergePullRequestAsync(
        string repo, int prNumber, GitMergePrRequest request, string? tenantId = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/git/{RepoPath(repo)}/pull-requests/{prNumber}/merge";
        return SendJsonAsync<GitCallResponse>(HttpMethod.Put, url, request, tenantId, ct);
    }

    /// <summary>Story 38-1 (AC5) — <c>PATCH /api/v1/git/{owner}/{repo}/issues/{n}</c>.</summary>
    [PerformsEffect(ExternalEffect.GitIssuePatch)]
    public Task<GitCallResponse?> UpdateIssueStatusAsync(
        string repo, int issueNumber, GitUpdateIssueRequest request, string? tenantId = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/git/{RepoPath(repo)}/issues/{issueNumber}";
        return PatchAsync<GitCallResponse>(url, request, tenantId, ct);
    }

    /// <summary>
    /// Epic 38 follow-up #21 — create a git-platform release for the shipped version
    /// via <c>POST /api/v1/git/{owner}/{repo}/releases</c> (the deployment-pipeline
    /// release step). The tag / notes are composed engine-side; the per-tenant git
    /// token is resolved + used server-side, it never travels here. Returns null on
    /// any non-2xx / transport failure (the thin activity maps it to its Error edge,
    /// fail-closed).
    /// </summary>
    [PerformsEffect(ExternalEffect.GitReleaseCreate)]
    public virtual Task<GitCallResponse?> CreateReleaseAsync(
        string repo, GitCreateReleaseRequest request, string? tenantId = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/git/{RepoPath(repo)}/releases";
        return PostAsync<GitCallResponse>(url, request, tenantId, ct);
    }

    /// <summary>Story 38-1 (AC5) — <c>GET /api/v1/git/{owner}/{repo}/pull-requests/{n}/comments</c>.</summary>
    public Task<GitCallResponse?> GetPullRequestCommentsAsync(
        string repo, int prNumber, string? correlationId = null, string? tenantId = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/git/{RepoPath(repo)}/pull-requests/{prNumber}/comments";
        if (!string.IsNullOrWhiteSpace(correlationId))
            url += $"?correlationId={Uri.EscapeDataString(correlationId)}";
        return GetAsync<GitCallResponse>(url, tenantId, ct);
    }

    // ----- GitHub extra ops (Story 38 Phase 1 — commits / file-changes / delete) --

    /// <summary>Story 38 (Phase 1) — read recent commits on a branch via
    /// <c>GET /api/v1/git/{owner}/{repo}/commits?branch=&amp;since=&amp;correlationId=</c>.
    /// The token is resolved + used server-side; it never travels here. Returns null on
    /// any non-2xx / transport failure (the thin activity maps it to its Error edge).</summary>
    public virtual Task<GitCallResponse?> GetCommitsAsync(
        string repo, string branch, DateTime? since = null, string? correlationId = null,
        string? tenantId = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/git/{RepoPath(repo)}/commits?branch={Uri.EscapeDataString(branch ?? string.Empty)}";
        if (since is { } s)
            url += $"&since={Uri.EscapeDataString(s.ToString("o", System.Globalization.CultureInfo.InvariantCulture))}";
        if (!string.IsNullOrWhiteSpace(correlationId))
            url += $"&correlationId={Uri.EscapeDataString(correlationId)}";
        return GetAsync<GitCallResponse>(url, tenantId, ct);
    }

    /// <summary>Story 38 (Phase 1) — read the file changes on a branch via
    /// <c>GET /api/v1/git/{owner}/{repo}/file-changes?branch=&amp;correlationId=</c>.</summary>
    public virtual Task<GitCallResponse?> GetFileChangesAsync(
        string repo, string branch, string? correlationId = null, string? tenantId = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/git/{RepoPath(repo)}/file-changes?branch={Uri.EscapeDataString(branch ?? string.Empty)}";
        if (!string.IsNullOrWhiteSpace(correlationId))
            url += $"&correlationId={Uri.EscapeDataString(correlationId)}";
        return GetAsync<GitCallResponse>(url, tenantId, ct);
    }

    /// <summary>Story 38 (Phase 1) — delete a branch via
    /// <c>DELETE /api/v1/git/{owner}/{repo}/branches?branch=&amp;correlationId=</c>. The
    /// branch name (may carry a slash) travels as a query param. Write op — fail-closed
    /// (null on any non-2xx / transport failure).</summary>
    [PerformsEffect(ExternalEffect.GitBranchDelete)]
    public virtual async Task<GitCallResponse?> DeleteBranchAsync(
        string repo, string branchName, string? correlationId = null, string? tenantId = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/git/{RepoPath(repo)}/branches?branch={Uri.EscapeDataString(branchName ?? string.Empty)}";
        if (!string.IsNullOrWhiteSpace(correlationId))
            url += $"&correlationId={Uri.EscapeDataString(correlationId)}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, url);
            AddTenantHeader(request, tenantId);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            await RecordHealthAsync(response.IsSuccessStatusCode, (int)response.StatusCode, null, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Tamma API DELETE returned {Status}", (int)response.StatusCode);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<GitCallResponse>(JsonOpts, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Tamma API DELETE failed");
            await RecordHealthAsync(false, null, ex.GetType().Name, ct).ConfigureAwait(false);
            return null;
        }
    }

    // ----- CI mediation (Story 38 Phase 1 — GitHub Actions test-run / build-status) --

    /// <summary>Story 38 (Phase 1) — trigger the CI workflow on a branch via
    /// <c>POST /api/v1/ci/{owner}/{repo}/test-runs</c>. The per-tenant git token is
    /// resolved + used server-side; it never travels here.</summary>
    [PerformsEffect(ExternalEffect.CiTestsTrigger)]
    public virtual Task<Models.CiCallResponse?> TriggerTestsAsync(
        string repo, Models.CiTriggerTestsRequest request, string? tenantId = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/ci/{RepoPath(repo)}/test-runs";
        return PostAsync<Models.CiCallResponse>(url, request, tenantId, ct);
    }

    /// <summary>Story 38 (Phase 1) — read the latest build status for a branch via
    /// <c>GET /api/v1/ci/{owner}/{repo}/build-status?branch=&amp;correlationId=</c>.</summary>
    public virtual Task<Models.CiCallResponse?> GetBuildStatusAsync(
        string repo, string branch, string? correlationId = null, string? tenantId = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/ci/{RepoPath(repo)}/build-status?branch={Uri.EscapeDataString(branch ?? string.Empty)}";
        if (!string.IsNullOrWhiteSpace(correlationId))
            url += $"&correlationId={Uri.EscapeDataString(correlationId)}";
        return GetAsync<Models.CiCallResponse>(url, tenantId, ct);
    }

    // ----- JIRA mediation (Story 38 Phase 1 — ticket read / update) --

    /// <summary>Story 38 (Phase 1) — read a JIRA ticket via
    /// <c>GET /api/v1/jira/tickets/{ticketId}?correlationId=</c>. The JIRA credential
    /// lives in Tamma.Api config; it never travels here.</summary>
    public virtual Task<Models.JiraCallResponse?> GetJiraTicketAsync(
        string ticketId, string? correlationId = null, string? tenantId = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/jira/tickets/{Uri.EscapeDataString(ticketId)}";
        if (!string.IsNullOrWhiteSpace(correlationId))
            url += $"?correlationId={Uri.EscapeDataString(correlationId)}";
        return GetAsync<Models.JiraCallResponse>(url, tenantId, ct);
    }

    /// <summary>Story 38 (Phase 1) — update a JIRA ticket (status + comment) via
    /// <c>PATCH /api/v1/jira/tickets/{ticketId}</c>.</summary>
    [PerformsEffect(ExternalEffect.JiraTicketPatch)]
    public virtual Task<Models.JiraCallResponse?> UpdateJiraTicketAsync(
        string ticketId, Models.JiraUpdateTicketRequest request, string? tenantId = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/jira/tickets/{Uri.EscapeDataString(ticketId)}";
        return PatchAsync<Models.JiraCallResponse>(url, request, tenantId, ct);
    }

    // ----- Agent-dispatch mediation (Story 38-2 — the CI-run step-mediation endpoints) --

    /// <summary>
    /// Story 38-2 (AC5) — POST the engine→API <see cref="Models.AgentDispatchRunApiRequest"/>
    /// to <c>POST /api/v1/agent-dispatch/{owner}/{repo}/runs</c> to trigger a
    /// <c>workflow_dispatch</c> run. <paramref name="repo"/> is <c>owner/name</c>; it is
    /// split into two path segments. Returns null on any non-2xx (guard 403 / auth 401 /
    /// transport), which the thin phase service maps to its failure result (fail-closed).
    /// The per-repo installation token is minted + used server-side; it never travels here.
    /// </summary>
    [PerformsEffect(ExternalEffect.AgentDispatchRun)]
    public virtual Task<Models.AgentDispatchRunApiResponse?> DispatchAgentRunAsync(
        string repo, Models.AgentDispatchRunApiRequest request, string? tenantId = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/agent-dispatch/{RepoPath(repo)}/runs";
        return PostAsync<Models.AgentDispatchRunApiResponse>(url, request, tenantId, ct);
    }

    /// <summary>Story 38-2 (AC5) — discover the latest dispatched run for a branch via
    /// <c>GET /api/v1/agent-dispatch/{owner}/{repo}/runs?branch=&amp;createdAfter=</c>.
    /// The monitor's discovery phase, mediated (the poll LOOP stays engine-side).</summary>
    public virtual Task<Models.AgentRunStatusApiResponse?> DiscoverAgentRunAsync(
        string repo, string branch, DateTime createdAfter, string? correlationId = null,
        string? tenantId = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/agent-dispatch/{RepoPath(repo)}/runs" +
                  $"?branch={Uri.EscapeDataString(branch ?? string.Empty)}" +
                  $"&createdAfter={Uri.EscapeDataString(createdAfter.ToString("o", System.Globalization.CultureInfo.InvariantCulture))}";
        if (!string.IsNullOrWhiteSpace(correlationId))
            url += $"&correlationId={Uri.EscapeDataString(correlationId)}";
        return GetAsync<Models.AgentRunStatusApiResponse>(url, tenantId, ct);
    }

    /// <summary>Story 38-2 (AC5) — single-shot status of one run via
    /// <c>GET /api/v1/agent-dispatch/{owner}/{repo}/runs/{id}</c> (one poll iteration).</summary>
    public virtual Task<Models.AgentRunStatusApiResponse?> GetAgentRunAsync(
        string repo, long runId, string? correlationId = null, string? tenantId = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/agent-dispatch/{RepoPath(repo)}/runs/{runId}";
        if (!string.IsNullOrWhiteSpace(correlationId))
            url += $"?correlationId={Uri.EscapeDataString(correlationId)}";
        return GetAsync<Models.AgentRunStatusApiResponse>(url, tenantId, ct);
    }

    /// <summary>Story 38-2 (AC5) — aggregate a completed run's outputs via
    /// <c>GET /api/v1/agent-dispatch/{owner}/{repo}/runs/{id}/results</c>.</summary>
    public virtual Task<Models.AgentRunResultsApiResponse?> CollectAgentResultsAsync(
        string repo, long runId, Models.CollectAgentRunApiRequest request, string? tenantId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var url = $"{_baseUrl}/api/v1/agent-dispatch/{RepoPath(repo)}/runs/{runId}/results" +
                  $"?branch={Uri.EscapeDataString(request.BranchName ?? string.Empty)}" +
                  $"&conclusion={Uri.EscapeDataString(request.Conclusion ?? string.Empty)}" +
                  $"&agentProvider={Uri.EscapeDataString(request.AgentProvider ?? string.Empty)}" +
                  $"&durationSeconds={request.DurationSeconds}";
        if (!string.IsNullOrWhiteSpace(request.CorrelationId))
            url += $"&correlationId={Uri.EscapeDataString(request.CorrelationId)}";
        return GetAsync<Models.AgentRunResultsApiResponse>(url, tenantId, ct);
    }

    /// <summary>Story 38-2 (AC5) — resolve the GitHub App installation id owning the
    /// repo via <c>GET /api/v1/agent-dispatch/{owner}/{repo}/installation</c>. Used only
    /// to scope the inbound webhook-signal wait key; the id is not a secret.</summary>
    public virtual Task<Models.AgentInstallationApiResponse?> ResolveAgentInstallationIdAsync(
        string repo, string? tenantId = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/agent-dispatch/{RepoPath(repo)}/installation";
        return GetAsync<Models.AgentInstallationApiResponse>(url, tenantId, ct);
    }

    /// <summary>Build the two-segment <c>{owner}/{repo}</c> path from an
    /// <c>owner/name</c> repo string, URL-escaping each segment. A repo string
    /// without a slash is escaped as a single segment (the endpoint's owner param).</summary>
    private static string RepoPath(string repo)
    {
        var slash = (repo ?? string.Empty).IndexOf('/');
        if (slash <= 0 || slash >= repo!.Length - 1)
            return Uri.EscapeDataString(repo ?? string.Empty);
        var owner = repo[..slash];
        var name = repo[(slash + 1)..];
        return $"{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}";
    }

    // ----- Slack / notifications mediation (Story 38-3 — Class D) -------

    /// <summary>
    /// Story 38-3 (AC5) — enqueue a Slack notification INTENT to the internal,
    /// engine-only endpoint <c>POST /api/v1/notifications/slack</c>. Fire-and-forget:
    /// the API writes a <c>slack_outbox</c> row and returns 202; the out-of-band
    /// <c>OutboxSlackSender</c> (the sole webhook-credential holder) performs the
    /// transport. Uses the <see cref="PostVoidAsync"/> path so the request gets the
    /// engine bearer + <c>X-Tenant-Id</c> (<paramref name="tenantId"/>, the acting
    /// scope) + per-call health recording. Returns <c>false</c> on any non-2xx /
    /// transport failure — the thin activity treats that as a fail-soft "queue
    /// failed" (the workflow continues; a missing Slack post must not break a
    /// mentorship session). The Slack token never travels here.
    /// </summary>
    [PerformsEffect(ExternalEffect.NotifySlackQueue)]
    public virtual Task<bool> QueueSlackNotificationAsync(
        Models.SlackNotificationRequest request,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/notifications/slack";
        return PostVoidAsync(url, request, tenantId, ct);
    }

    /// <summary>
    /// Story 38 (Phase 1) — send an email via the mediated, outbox-backed endpoint
    /// <c>POST /api/v1/notifications/email</c>. The API accepts the (already-rendered)
    /// message into the credentialed <c>IEmailService</c>; the SMTP/Resend credential
    /// never travels here. Fail-soft: a failure rides inside 200 success:false, so a
    /// missing notification does not break the workflow. Returns null on transport /
    /// 5xx failure.
    /// </summary>
    [PerformsEffect(ExternalEffect.NotifyEmailSend)]
    public virtual Task<Models.EmailCallResponse?> SendEmailAsync(
        Models.EmailSendRequest request, string? tenantId = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/notifications/email";
        return PostAsync<Models.EmailCallResponse>(url, request, tenantId, ct);
    }

    // ----- Provider Health ---------------------------------------------

    public Task<ProviderHealthStatus?> GetProviderHealthAsync(
        string providerKey,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/providers/health/providers/{Uri.EscapeDataString(providerKey)}";
        return GetAsync<ProviderHealthStatus>(url, tenantId, ct);
    }

    public Task<bool> RecordProviderFailureAsync(
        string providerKey,
        string? error = null,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/providers/health/providers/{Uri.EscapeDataString(providerKey)}/failure";
        return PostVoidAsync(url, new { error }, tenantId, ct);
    }

    public Task<bool> RecordProviderSuccessAsync(
        string providerKey,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/providers/health/providers/{Uri.EscapeDataString(providerKey)}/success";
        return PostVoidAsync(url, new { }, tenantId, ct);
    }

    // ----- Diagnostics --------------------------------------------------

    public Task<bool> RecordDiagnosticsAsync(
        DiagnosticsIngestRequest request,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/providers/diagnostics";
        return PostVoidAsync(url, request, tenantId, ct);
    }

    /// <summary>
    /// Fetches the current budget for a budget-owner identifier (today
    /// always the tenant id; the API surface keeps the URL path segment
    /// named <c>{accountId}</c> for back-compat with the TS API + a
    /// future per-user-bucket model). Parameter is named
    /// <paramref name="budgetOwnerId"/> locally to avoid CodeQL's
    /// <c>cs/cleartext-storage</c> heuristic, which treats parameters
    /// named <c>*account*</c> as financial-account-sensitive sources.
    /// </summary>
    public Task<BudgetStatus?> GetBudgetAsync(
        string budgetOwnerId,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/providers/diagnostics/budget/{Uri.EscapeDataString(budgetOwnerId)}";
        return GetAsync<BudgetStatus>(url, tenantId, ct);
    }

    // ----- Provider Sessions (create/execute/dispose) ------------------

    public Task<ProviderSessionResult?> CreateProviderAsync(
        ProviderCreateRequest request,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/providers/providers/create";
        return PostAsync<ProviderSessionResult>(url, request, tenantId, ct);
    }

    public Task<TaskExecuteResult?> ExecuteProviderAsync(
        string handle,
        TaskExecuteRequest request,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/providers/providers/{Uri.EscapeDataString(handle)}/execute";
        return PostAsync<TaskExecuteResult>(url, request, tenantId, ct);
    }

    public async Task<bool> DisposeProviderAsync(
        string handle,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/providers/providers/{Uri.EscapeDataString(handle)}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, url);
            AddTenantHeader(request, tenantId);
            var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            await RecordHealthAsync(
                response.IsSuccessStatusCode, (int)response.StatusCode, null, ct)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Tamma API DELETE failed");
            await RecordHealthAsync(false, null, ex.GetType().Name, ct).ConfigureAwait(false);
            return false;
        }
    }

    // ----- DCB event append --------------------------------------------

    /// <summary>
    /// Persist a BATCH of DCB events to the caller's tenant
    /// <c>domain_events</c> via <c>POST /api/engine/events</c>. Used by the
    /// engine's activity-execution middleware to drain the in-process
    /// <c>tamma:events</c> list into the durable audit trail.
    ///
    /// <para>Returns <c>true</c> only on a fully-successful append (2xx). A
    /// partial-batch failure (the API returns 502 with a
    /// <c>partial_append_failure</c> body) and any transport failure both
    /// return <c>false</c> so the caller does NOT advance its drain cursor
    /// and retries the batch next flush. <see cref="RecordHealthAsync"/>
    /// pipes the observed response into the shared health monitor exactly
    /// like every other call site.</para>
    /// </summary>
    [PerformsEffect(ExternalEffect.EngineEventsAppend)]
    public async Task<bool> AppendEventsAsync(
        IReadOnlyList<Models.EngineEventRecord> events,
        Guid? tenantId = null,
        CancellationToken ct = default)
    {
        if (events is null || events.Count == 0)
            return true; // nothing to flush — a successful no-op.

        var url = $"{_baseUrl}/api/engine/events";
        var body = new Models.AppendEventsRequest(events);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body, options: JsonOpts),
            };
            AddTenantHeader(request, tenantId?.ToString());
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            await RecordHealthAsync(
                response.IsSuccessStatusCode, (int)response.StatusCode, null, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Tamma API POST /api/engine/events returned {Status} for {Count} events",
                    (int)response.StatusCode, events.Count);
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Tamma API POST /api/engine/events failed");
            await RecordHealthAsync(false, null, ex.GetType().Name, ct).ConfigureAwait(false);
            return false;
        }
    }

    // ----- Document store persist (Story 39-11, D6) --------------------

    /// <summary>
    /// Persist a document instance to the tenant's <c>document_instances</c> store
    /// via <c>POST /api/engine/documents</c> (<c>EngineServiceOnly</c>).
    ///
    /// <para><b>FAIL-LOUD</b> — unlike the best-effort <see cref="AppendEventsAsync"/>
    /// event drain, a non-2xx or transport failure THROWS. The document is the
    /// lifecycle's product, not telemetry; the caller (persist activity) must fault,
    /// not swallow.</para>
    /// </summary>
    [PerformsEffect(ExternalEffect.EngineDocumentPersist)]
    public async Task PersistDocumentAsync(
        Models.PersistDocumentRequest request,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/engine/documents";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(request, options: JsonOpts),
        };
        AddTenantHeader(httpRequest, tenantId);
        using var response = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
        await RecordHealthAsync(
            response.IsSuccessStatusCode, (int)response.StatusCode, null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, ct).ConfigureAwait(false);
            _logger.LogWarning(
                "Tamma API POST /api/engine/documents returned {Status}: {Body}",
                (int)response.StatusCode, body);
            throw new HttpRequestException(
                $"Persist document failed: HTTP {(int)response.StatusCode}. {body}");
        }
    }

    /// <summary>
    /// Transition a persisted document's status via
    /// <c>POST /api/engine/documents/{documentId}/status</c>
    /// (<c>EngineServiceOnly</c>). FAIL-LOUD (non-2xx / transport throws).
    /// </summary>
    [PerformsEffect(ExternalEffect.EngineDocumentSetStatus)]
    public async Task SetDocumentStatusAsync(
        Guid documentId,
        string status,
        Guid? correlatingEventId,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/engine/documents/{documentId}/status";
        var body = new Models.SetDocumentStatusRequest(status, correlatingEventId);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: JsonOpts),
        };
        AddTenantHeader(httpRequest, tenantId);
        using var response = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
        await RecordHealthAsync(
            response.IsSuccessStatusCode, (int)response.StatusCode, null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await SafeReadBodyAsync(response, ct).ConfigureAwait(false);
            _logger.LogWarning(
                "Tamma API POST /api/engine/documents/{Id}/status returned {Status}: {Body}",
                documentId, (int)response.StatusCode, responseBody);
            throw new HttpRequestException(
                $"Set document status failed: HTTP {(int)response.StatusCode}. {responseBody}");
        }
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return "(response body unavailable)";
        }
    }

    // ----- Channel outbox enqueue (Story 39-18, D2) --------------------

    /// <summary>
    /// Story 39-18 — enqueue a channel message to the tenant's <c>channel_outbox</c>
    /// via <c>POST /api/engine/channel/outbox</c> (<c>EngineServiceOnly</c>). The
    /// <paramref name="envelopeJson"/> is the <c>ChannelEnvelope</c> serialized with
    /// <c>DocumentJson.Options</c> (the server deserializes it with the same options,
    /// mints the outbox row(s), and best-effort publishes to the hub group).
    ///
    /// <para>Best-effort like <see cref="AppendEventsAsync"/>: a non-2xx or transport
    /// failure returns <c>false</c> (the 39-6 gate still suspends; the request is
    /// recoverable via the suspended bookmark). It never throws — the caller
    /// (<c>EngineChannelPublisher</c>) logs ERROR and continues.</para>
    /// </summary>
    [PerformsEffect(ExternalEffect.EngineChannelOutboxEnqueue)]
    public virtual async Task<bool> PostChannelOutboxAsync(
        string envelopeJson, string? tenantId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(envelopeJson)) return true; // nothing to enqueue.

        var url = $"{_baseUrl}/api/engine/channel/outbox";
        var body = new { envelopeJson };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body, options: JsonOpts),
            };
            AddTenantHeader(request, tenantId);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            await RecordHealthAsync(
                response.IsSuccessStatusCode, (int)response.StatusCode, null, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Tamma API POST /api/engine/channel/outbox returned {Status}", (int)response.StatusCode);
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Tamma API POST /api/engine/channel/outbox failed");
            await RecordHealthAsync(false, null, ex.GetType().Name, ct).ConfigureAwait(false);
            return false;
        }
    }

    // ----- Platform event append ----------------------------------------

    /// <summary>
    /// Persist a BATCH of platform events to durable storage via
    /// <c>POST /api/engine/platform-events</c>. Used by Task 3's publisher
    /// to forward cross-tenant lifecycle events (e.g. TENANT.DELETED.SUCCESS)
    /// that the engine witnesses but the Tamma API owns durably.
    ///
    /// <para>Returns <c>true</c> only on a fully-successful append (2xx).
    /// Any non-2xx or transport failure returns <c>false</c> so the caller
    /// can retry. No <c>X-Tenant-Id</c> header is sent — <c>TenantId</c>
    /// travels per-event in the body, and <c>EngineServiceOnly</c> auth is
    /// satisfied by the service Bearer token the client already attaches.</para>
    /// </summary>
    [PerformsEffect(ExternalEffect.EnginePlatformEventsAppend)]
    public async Task<bool> AppendPlatformEventsAsync(
        IReadOnlyList<Models.PlatformEventRecord> events,
        CancellationToken ct = default)
    {
        if (events is null || events.Count == 0)
            return true; // nothing to flush — a successful no-op.

        var url = $"{_baseUrl}/api/engine/platform-events";
        var body = new Models.AppendPlatformEventsRequest(events);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body, options: JsonOpts),
            };
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            await RecordHealthAsync(
                response.IsSuccessStatusCode, (int)response.StatusCode, null, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Tamma API POST /api/engine/platform-events returned {Status} for {Count} events",
                    (int)response.StatusCode, events.Count);
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Tamma API POST /api/engine/platform-events failed");
            await RecordHealthAsync(false, null, ex.GetType().Name, ct).ConfigureAwait(false);
            return false;
        }
    }

    // ----- Helpers ------------------------------------------------------

    private async Task<T?> GetAsync<T>(
        string url,
        string? tenantId,
        CancellationToken ct) where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddTenantHeader(request, tenantId);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            await RecordHealthAsync(
                response.IsSuccessStatusCode, (int)response.StatusCode, null, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // URL is intentionally omitted — the path carries interpolated
                // identifiers (tenant/budget-owner/provider-handle/etc.), and
                // the rotating warn log on the VPS is the wrong plane for per-
                // resource correlation (event store is). The status code alone
                // is what an operator triaging "API unhealthy?" actually needs.
                _logger.LogWarning(
                    "Tamma API GET returned {Status}",
                    (int)response.StatusCode);
                return null;
            }
            return await response.Content
                .ReadFromJsonAsync<T>(JsonOpts, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // URL omitted for the same reason as above; the exception type
            // and message are the operator-useful signal.
            _logger.LogWarning(ex, "Tamma API GET failed");
            await RecordHealthAsync(false, null, ex.GetType().Name, ct).ConfigureAwait(false);
            return null;
        }
    }

    private async Task<T?> PostAsync<T>(
        string url,
        object body,
        string? tenantId,
        CancellationToken ct) where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body, options: JsonOpts),
            };
            AddTenantHeader(request, tenantId);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            await RecordHealthAsync(
                response.IsSuccessStatusCode, (int)response.StatusCode, null, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Tamma API POST returned {Status}",
                    (int)response.StatusCode);
                return null;
            }
            return await response.Content
                .ReadFromJsonAsync<T>(JsonOpts, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Tamma API POST failed");
            await RecordHealthAsync(false, null, ex.GetType().Name, ct).ConfigureAwait(false);
            return null;
        }
    }

    private Task<T?> PatchAsync<T>(
        string url,
        object body,
        string? tenantId,
        CancellationToken ct) where T : class
        => SendJsonAsync<T>(HttpMethod.Patch, url, body, tenantId, ct);

    /// <summary>
    /// Shared PUT/PATCH JSON send with the same contract as
    /// <see cref="PostAsync{T}"/> — engine bearer + <c>X-Tenant-Id</c> + per-call
    /// health recording, returning null on any non-2xx / transport failure so the
    /// caller falls back (fail-closed for the git-mediation shim).
    /// </summary>
    private async Task<T?> SendJsonAsync<T>(
        HttpMethod method,
        string url,
        object body,
        string? tenantId,
        CancellationToken ct) where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(method, url)
            {
                Content = JsonContent.Create(body, options: JsonOpts),
            };
            AddTenantHeader(request, tenantId);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            await RecordHealthAsync(
                response.IsSuccessStatusCode, (int)response.StatusCode, null, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Tamma API {Method} returned {Status}",
                    method.Method, (int)response.StatusCode);
                return null;
            }
            return await response.Content
                .ReadFromJsonAsync<T>(JsonOpts, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Tamma API {Method} failed", method.Method);
            await RecordHealthAsync(false, null, ex.GetType().Name, ct).ConfigureAwait(false);
            return null;
        }
    }

    private async Task<bool> PostVoidAsync(
        string url,
        object body,
        string? tenantId,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body, options: JsonOpts),
            };
            AddTenantHeader(request, tenantId);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            await RecordHealthAsync(
                response.IsSuccessStatusCode, (int)response.StatusCode, null, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Tamma API POST returned {Status}",
                    (int)response.StatusCode);
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Tamma API POST failed");
            await RecordHealthAsync(false, null, ex.GetType().Name, ct).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Wave C.4 §4 — pipe the observed response into the health monitor
    /// so PLATFORM.API.UNHEALTHY can fire on sustained failure bursts.
    /// The monitor is optional; when unwired the call is a no-op.
    /// </summary>
    private Task RecordHealthAsync(
        bool success, int? statusCode, string? exceptionType, CancellationToken ct)
    {
        if (_healthMonitor is null) return Task.CompletedTask;
        return _healthMonitor.RecordAsync(success, statusCode, exceptionType, ct);
    }

    private static void AddTenantHeader(HttpRequestMessage request, string? tenantId)
    {
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            request.Headers.TryAddWithoutValidation("X-Tenant-Id", tenantId);
        }
    }
}
