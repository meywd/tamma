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
/// Story 43-9 <b>Seam C</b>, surfaced client-side by the 2026-08-01 review finding
/// F5 — the parsed body of an HTTP 409 governance denial.
///
/// <para>A denial is NOT an outage, and the whole point of this type is that the
/// caller can tell. An outage is transient and clears itself; a denial is a
/// deterministic policy decision that repeats identically on every retry until a
/// person decides the pending authorization or an admin lowers the action's
/// threshold. Before this existed, <c>TammaApiClient</c> collapsed both into the
/// same <c>null</c>, so tightening any enforced action handed the engine a
/// permanent block that its <c>RetryCheck</c> / circuit-breaker logic read as a
/// retryable platform failure.</para>
///
/// <para>Shape mirrors <c>AutonomyGateEnforcement.Denial</c> exactly. Fields are
/// nullable because the fail-CLOSED wiring-fault arm
/// (<see cref="MisconfiguredCode"/>) carries only <c>code</c> and <c>error</c>.</para>
/// </summary>
/// <param name="Code">
/// <see cref="RequiresHumanCode"/> (a policy decision) or
/// <see cref="MisconfiguredCode"/> (an enforced route with no binding / no gate —
/// a deployment fault the engine cannot fix by retrying either).
/// </param>
/// <param name="Action">The catalog key wire that was refused.</param>
/// <param name="Group">The action's group wire.</param>
/// <param name="AutonomyLevel">The dial position the decision was taken at.</param>
/// <param name="EffectiveMinAutonomy">The composed threshold that was applied.</param>
/// <param name="AuthorizationId">
/// The pending <c>action_authorizations</c> row a person can decide on, when the
/// server minted one. Null means nobody can clear this block by approving — only
/// a policy change can.
/// </param>
/// <param name="CorrelationId">The run the decision was scoped to.</param>
/// <param name="Reason">Machine-readable decision reason.</param>
/// <param name="AssignmentSource">Provenance wire of the winning tier.</param>
/// <param name="Error">Human-readable remediation text from the server.</param>
public sealed record TammaApiGovernanceDenial(
    string? Code = null,
    string? Action = null,
    string? Group = null,
    int? AutonomyLevel = null,
    int? EffectiveMinAutonomy = null,
    Guid? AuthorizationId = null,
    string? CorrelationId = null,
    string? Reason = null,
    string? AssignmentSource = null,
    string? Error = null)
{
    /// <summary>A policy decision: the system may not do this without a person.</summary>
    public const string RequiresHumanCode = "ACTION.GATE.REQUIRES_HUMAN";

    /// <summary>The fail-CLOSED static-wiring-fault arm.</summary>
    public const string MisconfiguredCode = "ACTION.GATE.MISCONFIGURED";

    /// <summary>
    /// True only for the two codes the gate actually mints. Keeps an unrelated 409
    /// (optimistic concurrency, duplicate resource) from being mistaken for a
    /// governance refusal.
    /// </summary>
    public bool IsGovernanceDenial =>
        string.Equals(Code, RequiresHumanCode, StringComparison.Ordinal)
        || string.Equals(Code, MisconfiguredCode, StringComparison.Ordinal);

    /// <summary>
    /// True when a human CAN clear this block by deciding the pending row. False
    /// means retrying and waiting are both pointless — only a policy or wiring
    /// change moves it.
    /// </summary>
    public bool IsClearableByAHuman =>
        string.Equals(Code, RequiresHumanCode, StringComparison.Ordinal)
        && AuthorizationId is not null;
}

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

    // ----- Governance denials (2026-08-01 review finding F5) -------------

    /// <summary>
    /// The Seam C governance denial observed by the MOST RECENT call on this client
    /// instance, or <c>null</c> if that call was not refused by the autonomy gate.
    ///
    /// <para><b>Why this exists.</b> Every method here collapses a non-2xx into
    /// <c>null</c>, so the HTTP 409 that Story 43-9 introduced was indistinguishable
    /// from a 503 or a socket reset. The two could not be more different: an outage
    /// is transient and clears itself, a governance denial is a PERMANENT,
    /// deterministic refusal that repeats identically on every retry until a person
    /// grants the pending authorization or an admin lowers the action's threshold.
    /// An activity that treats a denial as a retryable outage burns its whole retry
    /// budget and then reports a platform failure for what is a policy decision. The
    /// irony is recorded in <c>AutonomyGateEnforcement</c>: D7 chose 409 over 202
    /// BECAUSE this client "discriminates on nothing but IsSuccessStatusCode" — the
    /// same sentence is why the denial then arrived as an outage.</para>
    ///
    /// <para><b>Contract.</b> Cleared at the START of every request this client
    /// makes, so it can never be read stale: after any call, a non-null value means
    /// THAT call was governance-denied. It is deliberately additive — every existing
    /// method keeps returning <c>null</c> on a 409 and no caller changes shape.</para>
    ///
    /// <para><b>Lifetime.</b> <c>AddHttpClient&lt;TammaApiClient&gt;()</c> registers
    /// the typed client TRANSIENT, so each <c>GetService&lt;TammaApiClient&gt;()</c>
    /// in an activity gets its own instance and this reads unambiguously. It is NOT
    /// safe to interleave concurrent calls on ONE instance and then read this — if a
    /// future caller does that, it must capture the denial per call instead.</para>
    /// </summary>
    public TammaApiGovernanceDenial? LastGovernanceDenial { get; private set; }

    /// <summary>
    /// Clear the denial slot at the start of a request, so a stale denial from an
    /// earlier call can never be attributed to this one.
    /// </summary>
    private void BeginRequest() => LastGovernanceDenial = null;

    /// <summary>
    /// Recognise a Seam C denial on a non-2xx response and record it. Returns true
    /// when the response WAS a governance denial (the caller still returns null —
    /// this only makes the refusal legible).
    ///
    /// <para>Narrow on purpose: ONLY an HTTP 409 whose body carries one of the two
    /// <c>ACTION.GATE.*</c> codes counts. Any other 409 (an optimistic-concurrency
    /// conflict, a duplicate-resource refusal) is left exactly as it was, and an
    /// unparseable body degrades to "not a governance denial" rather than throwing
    /// inside an error path.</para>
    /// </summary>
    private async Task<bool> TryRecordGovernanceDenialAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.Conflict) return false;

        TammaApiGovernanceDenial? denial = null;
        try
        {
            denial = await response.Content
                .ReadFromJsonAsync<TammaApiGovernanceDenial>(JsonOpts, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // A 409 with a body we cannot read is not a governance denial we can
            // describe; fall through and let it stay an ordinary non-2xx.
            _logger.LogDebug(ex, "Tamma API 409 body was not a governance denial envelope");
        }

        if (denial is null || !denial.IsGovernanceDenial) return false;

        LastGovernanceDenial = denial;

        // A DISTINCT log line. "returned 409" in a sea of transport warnings is how
        // an operator spends an hour looking for an outage that is really a policy
        // row somebody changed this morning.
        _logger.LogWarning(
            "Tamma API refused a governed action: {Code} for {Action} (group {Group}); "
            + "autonomyLevel={AutonomyLevel} < effectiveMinAutonomy={EffectiveMin}; "
            + "authorizationId={AuthorizationId}. This is a POLICY DECISION, not an outage — "
            + "retrying will fail identically until a person decides or the threshold changes.",
            denial.Code, denial.Action, denial.Group,
            denial.AutonomyLevel, denial.EffectiveMinAutonomy, denial.AuthorizationId);

        return true;
    }

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
        BeginRequest();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, url);
            AddTenantHeader(request, tenantId);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            await RecordHealthAsync(response.IsSuccessStatusCode, (int)response.StatusCode, null, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // F5 — this route (DELETE /api/v1/git/{owner}/{repo}/branches) is
                // one of the opted-in enforced set, so it can genuinely answer 409.
                if (await TryRecordGovernanceDenialAsync(response, ct).ConfigureAwait(false))
                    return null;

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

    // ----- Governance (Story 43-9 Seam E) --------------------------------

    /// <summary>
    /// Story 43-9 <b>Seam E</b> (AC10, D9) — ask the API whether the system may
    /// perform a catalogued action by itself right now:
    /// <c>POST /api/v1/governance/evaluate</c> (<c>EngineServiceOnly</c>).
    ///
    /// <para><b>It is a READ and mints no <c>ExternalEffect</c> member.</b> The
    /// route is deliberately UNGOVERNED — the gate-evaluation endpoint cannot gate
    /// itself without being circular — and carries that exact justification in
    /// <c>KnownUngovernedEndpoints</c>.</para>
    ///
    /// <para><b>It is also the one method that had to widen a strictly-decreasing
    /// ratchet</b> (Story 43-9 Decision D17). <c>KnownNonEffectClientMethods</c>
    /// is count-pinned at 19 with a shrink-only history, so a genuinely read-only
    /// new method could not be baselined without either mis-classifying it as an
    /// effect or splitting the client so the sweep stops seeing it. It is instead
    /// listed in a NAMED, DATED, per-method exception set that is itself
    /// count-pinned and shrink-only — see
    /// <c>MediationClientEffectSweepTests.ReviewedNonEffectExceptions</c>.</para>
    ///
    /// <para>Returns <c>null</c> on any non-2xx or transport failure, like every
    /// other method here. The CALLER decides what null means; for
    /// <c>CheckActionGateActivity</c> it means FAIL OPEN, because Seam E's one
    /// adoption is an additive OR term and a control-plane blip must not stall a
    /// deployment pipeline that would have proceeded anyway.</para>
    /// </summary>
    public Task<Policy.GovernanceEvaluateResponse?> EvaluateGovernanceAsync(
        Policy.GovernanceEvaluateRequest request,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/governance/evaluate";
        return PostAsync<Policy.GovernanceEvaluateResponse>(url, request, tenantId, ct);
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
        BeginRequest();
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
                // F5 — this is an enforced route; a governance refusal is permanent
                // and must not be retried as though the drain hit an outage.
                if (await TryRecordGovernanceDenialAsync(response, ct).ConfigureAwait(false))
                    return false;

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
        BeginRequest();
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
            // F5 — record the denial before the fail-loud throw, so a caller that
            // catches the HttpRequestException can still tell policy from outage.
            await TryRecordGovernanceDenialAsync(response, ct).ConfigureAwait(false);
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
        BeginRequest();
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
            // F5 — see PersistDocumentAsync.
            await TryRecordGovernanceDenialAsync(response, ct).ConfigureAwait(false);
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
        BeginRequest();
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
                // F5 — enforced route; see AppendEventsAsync.
                if (await TryRecordGovernanceDenialAsync(response, ct).ConfigureAwait(false))
                    return false;

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
        BeginRequest();
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
                // F5 — enforced route; see AppendEventsAsync.
                if (await TryRecordGovernanceDenialAsync(response, ct).ConfigureAwait(false))
                    return false;

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
        BeginRequest();
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
                // F5 — a governance 409 gets its own, unmistakable log line and is
                // recorded on LastGovernanceDenial. The return value stays null so
                // no existing caller changes shape.
                if (await TryRecordGovernanceDenialAsync(response, ct).ConfigureAwait(false))
                    return null;

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
        BeginRequest();
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
                // F5 — see GetAsync. A governance denial is a policy decision, not
                // an outage, and must not read to the caller as one.
                if (await TryRecordGovernanceDenialAsync(response, ct).ConfigureAwait(false))
                    return null;

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
        BeginRequest();
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
                // F5 — see GetAsync. PUT/PATCH carry two enforced routes
                // (git pull-request merge, git issue patch, jira ticket patch).
                if (await TryRecordGovernanceDenialAsync(response, ct).ConfigureAwait(false))
                    return null;

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
        BeginRequest();
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
                // F5 — carries the enforced POST /api/v1/notifications/slack route,
                // whose `false` return is otherwise identical for an outage and a
                // governance refusal.
                if (await TryRecordGovernanceDenialAsync(response, ct).ConfigureAwait(false))
                    return false;

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
