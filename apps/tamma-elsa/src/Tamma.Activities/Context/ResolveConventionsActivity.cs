using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;
using Tamma.Api.Services.Agents;
using Tamma.Core;

namespace Tamma.Activities.Context;

/// <summary>
/// Story 27-13 — resolves the project's coding conventions from the
/// convention store (Story 27-9/27-10) for a given <c>(role, action)</c>
/// pair, then feeds the result into the <c>{{conventions}}</c> template
/// variable consumed by <see cref="LlmCall.ResolvePromptFromRegistryActivity"/>.
///
/// <para>
/// Calls: <c>POST {Engine:CallbackUrl}/api/conventions/resolve</c> with body
/// <c>{ "role": &lt;wire&gt;, "action": &lt;wire&gt; }</c> and an
/// <c>X-Tenant-Id</c> header (Story 27-6 tenant-routing convention; mirrors
/// <see cref="LlmCall.ResolvePromptFromRegistryActivity"/>).
/// </para>
///
/// <para>
/// <b>Fail-loud resolution.</b> A taxonomy-valid <c>(role, action)</c> always
/// ships a system-default convention (Story 27-9 seeder), so a registry miss
/// (404 — <c>CONVENTION_NOT_FOUND</c>) is a real fault: it propagates a
/// <see cref="TammaError"/> rather than silently degrading to an empty body.
/// This mirrors the Story 27-18 hardening on the prompt activity and the
/// locked user mandate that a miss is NEVER an empty/plain body.
/// </para>
///
/// <para>
/// <b>Empty-action legacy path.</b> Dispatch sites that still emit a raw
/// prompt with no registry action (e.g. BlockerDiagnosis-style dict inputs
/// — SPEC §3.5 / §6 transitional) skip the convention store entirely and
/// pass <see cref="LegacyConventions"/> straight through. That path is the
/// ONLY case where the legacy <see cref="ReadRepoConventionsActivity"/>
/// <c>.tamma/config.json</c> string is honoured; once Story 27-19 fully
/// specialises every dispatch site, this branch can be deleted.
/// TODO(27-19): remove the empty-action opt-out when no raw-prompt dispatch
/// sites remain.
/// </para>
///
/// <para>
/// <b>Intentional two-exception-type boundary</b> (mirrors the prompt activity):
/// <list type="bullet">
///   <item><term><see cref="ArgumentException"/></term><description>
///     Thrown by <see cref="AgentRoleExtensions.Parse"/> /
///     <see cref="AgentActionExtensions.Parse"/> when the supplied role or
///     action is not a recognised taxonomy token. This is a caller/config
///     error (the workflow was wired with a dead or misspelled token) and
///     should NOT be retried.</description></item>
///   <item><term><see cref="TammaError"/></term><description>
///     Thrown for operational / transient failures:
///     <c>REGISTRY_UNAVAILABLE</c> (retryable) — network exception OR
///     5xx server error; <c>NO_ROW</c> (non-retryable) — 404 (row
///     genuinely absent) or other 4xx (auth/policy — retrying won't
///     help).</description></item>
/// </list>
/// </para>
/// </summary>
[Activity(
    "Tamma.Conventions",
    "Resolve Conventions",
    "Resolve project conventions from the convention store by (role, action)",
    Kind = ActivityKind.Task
)]
public class ResolveConventionsActivity : TammaAsyncActivity
{
    public override string? EventType => "LLM.CONVENTIONS.RESOLVE";

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    [Input(Description = "LLM role (developer, tester, architect, etc.)")]
    public Input<string> Role { get; set; } = default!;

    [Input(Description = "Action (context-scan, plan-implementation, implement-feature, etc.) — empty triggers the legacy passthrough path")]
    public Input<string> Action { get; set; } = new("");

    [Input(Description = "Tenant ID for tenant-scoped convention resolution (empty = system defaults)")]
    public Input<string> TenantId { get; set; } = new("");

    [Input(Description = "Legacy conventions string (from .tamma/config.json via ReadRepoConventionsActivity) — used ONLY for the empty-action passthrough path")]
    public Input<string> LegacyConventions { get; set; } = new("");

    [Output(Description = "Resolved conventions string for {{conventions}} template variable")]
    public Output<string> ResolvedConventions { get; set; } = default!;

    [JsonConstructor]
    public ResolveConventionsActivity() { }

    public ResolveConventionsActivity(
        ILogger<ResolveConventionsActivity> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        Logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var role = Role.Get(context);
        var action = Action.Get(context);
        var tenantId = TenantId.Get(context);
        var legacyConventions = LegacyConventions.Get(context) ?? "";

        // Empty-action legacy path: a dispatch site that supplies a raw prompt
        // with no registry action keeps using the legacy conventions string
        // from .tamma/config.json (via ReadRepoConventionsActivity). No HTTP
        // call to the convention store. Distinct from registry-miss fallback
        // — see XML doc.
        // TODO(27-19): dispatch specialisation — every site should emit a specific action; remove this opt-out when no raw-prompt dispatch sites remain.
        if (string.IsNullOrEmpty(action))
        {
            ResolvedConventions.Set(context, legacyConventions);
            context.TransientProperties["resolvedConventionsLength"] = legacyConventions.Length;
            context.TransientProperties["conventionsSource"] = "legacy";
            Logger?.LogInformation(
                "No action specified, using legacy conventions string for role {Role} ({Length} chars)",
                role, legacyConventions.Length);
            return;
        }

        // Boundary validation: a taxonomy-invalid role/action is a hard
        // fail-fast (caller bug — same contract as the prompt activity).
        ValidateTaxonomy(role, action);

        var callbackUrl = _configuration?["Engine:CallbackUrl"];
        if (string.IsNullOrEmpty(callbackUrl) || _httpClientFactory == null)
        {
            // No API base configured AND no DI-injected client — try the
            // PromptRegistry fallback URL (matches prompt-activity behaviour
            // for parity in dev/test bootstrap).
            callbackUrl = _configuration?["PromptRegistry:BaseUrl"] ?? "http://localhost:3100";
        }

        var httpClient = _httpClientFactory?.CreateClient() ?? new HttpClient();

        var (body, source, version) = await CallResolveAsync(httpClient, callbackUrl!, role, action, tenantId, Logger);

        ResolvedConventions.Set(context, body);
        context.TransientProperties["resolvedConventionsLength"] = body.Length;
        context.TransientProperties["conventionsSource"] = source;
        context.TransientProperties["conventionsVersion"] = version;

        Logger?.LogInformation(
            "Resolved conventions from store: {Role}/{Action} ({Length} chars, source={Source}, tenantId={TenantId})",
            role, action, body.Length, source, string.IsNullOrEmpty(tenantId) ? "system" : tenantId);
    }

    /// <summary>
    /// Static helper: performs the HTTP POST to <c>/api/conventions/resolve</c>
    /// and maps the response to (body, source, version). Extracted from
    /// <c>RunAsync</c> so it is unit-testable without an Elsa
    /// <see cref="ActivityExecutionContext"/>, matching the test convention
    /// used elsewhere in this assembly.
    /// </summary>
    /// <remarks>
    /// The <paramref name="tenantId"/> is forwarded as an <c>X-Tenant-Id</c>
    /// request header (set on the outgoing <see cref="HttpRequestMessage"/>
    /// rather than on <see cref="HttpClient.DefaultRequestHeaders"/> so
    /// that the behaviour is fully visible in per-request tests).
    /// </remarks>
    /// <exception cref="TammaError">
    /// <list type="bullet">
    ///   <item><c>LLM.CONVENTIONS.RESOLVE.REGISTRY_UNAVAILABLE</c> (retryable):
    ///     network / transport exception, OR any 5xx status (transient server fault).</item>
    ///   <item><c>LLM.CONVENTIONS.RESOLVE.NO_ROW</c> (non-retryable):
    ///     HTTP 404 (the row genuinely doesn't exist) OR any other 4xx (auth
    ///     failure, 403 policy denial — retrying won't help).</item>
    /// </list>
    /// </exception>
    public static async Task<(string Body, string Source, int Version)> CallResolveAsync(
        HttpClient httpClient,
        string callbackUrl,
        string role,
        string action,
        string tenantId,
        ILogger? logger = null)
    {
        HttpResponseMessage response;
        try
        {
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{callbackUrl.TrimEnd('/')}/api/conventions/resolve");

            if (!string.IsNullOrEmpty(tenantId))
                request.Headers.Add("X-Tenant-Id", tenantId);

            request.Content = JsonContent.Create(new { role, action });

            response = await httpClient.SendAsync(request);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to reach convention store for {Role}/{Action}", role, action);
            throw new TammaError(
                "LLM.CONVENTIONS.RESOLVE.REGISTRY_UNAVAILABLE",
                $"Could not reach the convention store for (role='{role}', action='{action}'): {ex.Message}",
                new Dictionary<string, object?> { ["role"] = role, ["action"] = action, ["tenantId"] = tenantId },
                retryable: true,
                severity: TammaErrorSeverity.High);
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;

            // 5xx: transient server fault (DB outage, unhandled exception on the
            // server, etc.). Retryable — the row may well exist once the server
            // recovers. Must NOT be labelled NO_ROW.
            if (statusCode >= 500)
            {
                logger?.LogError(
                    "Convention store returned transient {Status} for {Role}/{Action}",
                    response.StatusCode, role, action);
                throw new TammaError(
                    "LLM.CONVENTIONS.RESOLVE.REGISTRY_UNAVAILABLE",
                    $"Convention store returned server error {statusCode} for (role='{role}', action='{action}').",
                    new Dictionary<string, object?>
                    {
                        ["role"] = role,
                        ["action"] = action,
                        ["tenantId"] = tenantId,
                        ["status"] = statusCode,
                    },
                    retryable: true,
                    severity: TammaErrorSeverity.High);
            }

            // 404 or other 4xx: the row doesn't exist (404) or a permanent
            // client-side fault (401/403 — retrying won't help). Both are
            // non-retryable. Taxonomy-valid (role,action) always has a system
            // default per Story 27-9, so a miss is a real fault.
            logger?.LogError(
                "Convention store returned {Status} for {Role}/{Action}",
                response.StatusCode, role, action);
            throw new TammaError(
                "LLM.CONVENTIONS.RESOLVE.NO_ROW",
                $"Convention store returned {statusCode} for (role='{role}', action='{action}').",
                new Dictionary<string, object?>
                {
                    ["role"] = role,
                    ["action"] = action,
                    ["tenantId"] = tenantId,
                    ["status"] = statusCode,
                },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var body = result.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var source = result.TryGetProperty("source", out var s) ? s.GetString() ?? "system" : "system";
        var version = result.TryGetProperty("version", out var v) ? v.GetInt32() : 0;

        return (body, source, version);
    }

    /// <summary>
    /// Boundary taxonomy validation for a non-empty <c>(role, action)</c> pair.
    /// A taxonomy-invalid role or action throws (fail-fast); the activity then
    /// surfaces the failure rather than silently resolving the wrong cell.
    /// Mirrors <see cref="LlmCall.ResolvePromptFromRegistryActivity.ValidateTaxonomy"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown role or action.</exception>
    public static void ValidateTaxonomy(string role, string action)
    {
        AgentRoleExtensions.Parse(role);
        AgentActionExtensions.Parse(action);
    }

    public override Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["role"] = Role.Get(context),
        ["action"] = Action.Get(context),
        ["tenantId"] = TenantId.Get(context),
    };

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context)
    {
        var chars = context.TransientProperties.TryGetValue("resolvedConventionsLength", out var len) ? len : 0;
        var src = context.TransientProperties.TryGetValue("conventionsSource", out var s) ? s : "system";
        var ver = context.TransientProperties.TryGetValue("conventionsVersion", out var v) ? v : 0;
        return new()
        {
            ["role"] = Role.Get(context),
            ["action"] = Action.Get(context),
            ["tenantId"] = TenantId.Get(context),
            ["chars"] = chars,
            ["source"] = src,
            ["version"] = ver,
        };
    }
}
