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

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Resolves a prompt from the prompt registry using role + action.
/// Calls: POST /api/prompts/{role}/{action}/render with variables.
///
/// <para>
/// <b>Story 27-18 — fail-loud resolution.</b> When an <see cref="Action"/> is
/// specified, the <c>(role, action)</c> pair is validated against the taxonomy
/// via <see cref="AgentRoleExtensions.Parse"/> / <see cref="AgentActionExtensions.Parse"/>
/// at the boundary (an unknown role/action throws). A registry miss (404 /
/// error / empty rendered body) now PROPAGATES a <see cref="TammaError"/> — it
/// does NOT silently fall back to the plain <see cref="FallbackPrompt"/>. A
/// taxonomy-valid pair always ships a system default, so a miss is a real
/// fault worth failing loud on.
/// </para>
/// <para>
/// The <see cref="FallbackPrompt"/> is retained ONLY for the empty-action legacy
/// path: dispatch sites that send a raw prompt with no registry action (e.g.
/// BlockerDiagnosis-style dict inputs) still use it. That legacy path is a
/// separate concern from registry-miss fallback and is left intact pending the
/// dispatch-site specialisation work (SPEC §6 initiative 2 / Story 27-19).
/// </para>
///
/// <para>
/// <b>Intentional two-exception-type boundary.</b> This activity surfaces exactly
/// two exception types from the resolve path:
/// <list type="bullet">
///   <item><term><see cref="ArgumentException"/></term><description>
///     Thrown by <see cref="AgentRoleExtensions.Parse"/> or
///     <see cref="AgentActionExtensions.Parse"/> when the supplied role or action
///     is not a recognised taxonomy token. This is a caller/config error (the
///     workflow was wired with a dead or misspelled token) and should NOT be
///     retried.</description></item>
///   <item><term><see cref="TammaError"/></term><description>
///     Thrown for operational / transient failures:
///     <c>REGISTRY_UNAVAILABLE</c> (retryable) — network exception OR 5xx
///     server error; <c>NO_ROW</c> (non-retryable) — 404 (row genuinely
///     absent) or other 4xx (auth/policy — retrying won't help). The
///     <c>PromptEndpoints</c> HTTP boundary translates <see cref="TammaError"/>
///     into HTTP 404; <see cref="ArgumentException"/> is NOT caught there and
///     surfaces as a 500 (it represents a configuration bug, not a lookup miss).
///     Do NOT change <see cref="AgentRoleExtensions.Parse"/> /
///     <see cref="AgentActionExtensions.Parse"/> to throw
///     <see cref="TammaError"/> — that is Story 27-15 scope.</description></item>
/// </list>
/// </para>
///
/// When TenantId is provided, sends the X-Tenant-Id header for
/// tenant-scoped prompt resolution (Story 27-6).
/// </summary>
[Activity(
    "Tamma.LlmCall",
    "Resolve Prompt",
    "Resolve prompt from registry by role + action, interpolate variables",
    Kind = ActivityKind.Task
)]
public class ResolvePromptFromRegistryActivity : TammaAsyncActivity
{
    public override string? EventType => "LLM.PROMPT.RESOLVE";

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    [Input(Description = "LLM role (developer, tester, architect, etc.)")]
    public Input<string> Role { get; set; } = default!;

    [Input(Description = "Action (context-scan, plan-implementation, implement-feature, etc.) — empty to skip registry")]
    public Input<string> Action { get; set; } = new("");

    [Input(Description = "Variables JSON for template interpolation")]
    public Input<string> VariablesJson { get; set; } = new("{}");

    [Input(Description = "Fallback prompt if registry unavailable or no action specified")]
    public Input<string> FallbackPrompt { get; set; } = new("");

    [Input(Description = "Tenant ID for tenant-scoped prompt resolution (empty = system defaults)")]
    public Input<string> TenantId { get; set; } = new("");

    [Output(Description = "Resolved prompt text")]
    public Output<string> ResolvedPrompt { get; set; } = default!;

    [Output(Description = "Resolved system prompt")]
    public Output<string> ResolvedSystemPrompt { get; set; } = default!;

    [Output(Description = "Whether tools should be enabled")]
    public Output<bool> EnableTools { get; set; } = default!;

    [Output(Description = "Max tokens for the LLM call")]
    public Output<int> MaxTokens { get; set; } = default!;

    [JsonConstructor]
    public ResolvePromptFromRegistryActivity() { }

    public ResolvePromptFromRegistryActivity(
        ILogger<ResolvePromptFromRegistryActivity> logger,
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
        var variablesJson = VariablesJson.Get(context);
        var fallback = FallbackPrompt.Get(context);
        var tenantId = TenantId.Get(context);

        // Empty-action legacy path: a dispatch site that supplies a raw prompt
        // with no registry action (e.g. BlockerDiagnosis dict inputs) keeps
        // using the fallback. This is NOT a registry-miss fallback — it's a
        // distinct "no action requested" mode (SPEC §3.5 / §6 transitional).
        // See activity XML doc + Story 27-18 report (remaining blast radius).
        // TODO(27-19): dispatch specialisation — every site should emit a specific action; remove this opt-out when no raw-prompt dispatch sites remain.
        if (string.IsNullOrEmpty(action))
        {
            ResolvedPrompt.Set(context, fallback);
            ResolvedSystemPrompt.Set(context, "");
            EnableTools.Set(context, false);
            MaxTokens.Set(context, 4096);
            context.TransientProperties["resolvedPromptLength"] = fallback?.Length ?? 0;
            context.TransientProperties["hasSystemPrompt"] = false;
            Logger?.LogInformation("No action specified, using fallback prompt for role {Role}", role);
            return;
        }

        // Boundary validation (Story 27-18): a taxonomy-invalid role/action is a
        // hard fail-fast, never a silent mismatch. Throws on unknown; the base
        // activity emits FAILED and rethrows.
        ValidateTaxonomy(role, action);

        // Try the prompt registry
        var callbackUrl = _configuration?["Engine:CallbackUrl"];
        if (string.IsNullOrEmpty(callbackUrl) || _httpClientFactory == null)
        {
            // No API available — try local prompt registry URL
            callbackUrl = _configuration?["PromptRegistry:BaseUrl"] ?? "http://localhost:3100";
        }

        // Production blocker fix — use the named "tamma-engine" client (wired
        // in Tamma.ElsaServer/Program.cs with TammaEngineAuthHandler) so the
        // outgoing POST to /api/prompts/{role}/{action}/render carries the
        // Authorization: Bearer <token> header when Tamma:ApiToken is
        // configured. Without this, the API returns 401 in production and
        // the activity maps that to a non-retryable NO_ROW — permanently
        // failing the workflow before any LLM runs.
        var httpClient = _httpClientFactory?.CreateClient("tamma-engine") ?? new HttpClient();

        // Parse variables
        Dictionary<string, object>? variables = null;
        if (!string.IsNullOrEmpty(variablesJson) && variablesJson != "{}")
        {
            variables = JsonSerializer.Deserialize<Dictionary<string, object>>(variablesJson);
        }

        var (rendered, systemPrompt, enableTools, maxTokens) = await CallResolveAsync(
            httpClient, callbackUrl!, role, action, tenantId,
            variables ?? new Dictionary<string, object>(), Logger);

        ResolvedPrompt.Set(context, rendered);
        ResolvedSystemPrompt.Set(context, systemPrompt);
        EnableTools.Set(context, enableTools);
        MaxTokens.Set(context, maxTokens);
        context.TransientProperties["resolvedPromptLength"] = rendered.Length;
        context.TransientProperties["hasSystemPrompt"] = !string.IsNullOrEmpty(systemPrompt);

        Logger?.LogInformation("Resolved prompt from registry: {Role}/{Action} ({Length} chars, tenantId={TenantId})",
            role, action, rendered.Length, string.IsNullOrEmpty(tenantId) ? "system" : tenantId);
    }

    /// <summary>
    /// Static helper: performs the HTTP POST to
    /// <c>/api/prompts/{role}/{action}/render</c> and maps the response to the
    /// resolved prompt tuple. Extracted from <c>RunAsync</c> so it is
    /// unit-testable without an Elsa <see cref="ActivityExecutionContext"/>,
    /// matching the convention used by <see cref="Context.ResolveConventionsActivity"/>.
    /// </summary>
    /// <remarks>
    /// The <paramref name="tenantId"/> is forwarded as an <c>X-Tenant-Id</c>
    /// request header set on the outgoing <see cref="HttpRequestMessage"/>
    /// (not on <see cref="HttpClient.DefaultRequestHeaders"/>) so the
    /// behaviour is fully visible in per-request unit tests.
    /// </remarks>
    /// <exception cref="TammaError">
    /// <list type="bullet">
    ///   <item><c>LLM.PROMPT.RESOLVE.REGISTRY_UNAVAILABLE</c> (retryable):
    ///     network / transport exception, OR any 5xx status (transient server
    ///     fault).</item>
    ///   <item><c>LLM.PROMPT.RESOLVE.NO_ROW</c> (non-retryable):
    ///     HTTP 404 (the row genuinely doesn't exist) OR any other 4xx
    ///     (auth/policy — retrying won't help).</item>
    /// </list>
    /// </exception>
    public static async Task<(string Rendered, string SystemPrompt, bool EnableTools, int MaxTokens)> CallResolveAsync(
        HttpClient httpClient,
        string callbackUrl,
        string role,
        string action,
        string tenantId,
        Dictionary<string, object> variables,
        ILogger? logger = null)
    {
        HttpResponseMessage response;
        try
        {
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{callbackUrl.TrimEnd('/')}/api/prompts/{Uri.EscapeDataString(role)}/{Uri.EscapeDataString(action)}/render");

            if (!string.IsNullOrEmpty(tenantId))
                request.Headers.Add("X-Tenant-Id", tenantId);

            request.Content = JsonContent.Create(new { variables });

            response = await httpClient.SendAsync(request);
        }
        catch (Exception ex)
        {
            // Story 27-18: NO plain fallback. A registry call that can't complete
            // (network/transient) fails loud — retryable so the workflow engine
            // can retry rather than running an LLM on the wrong/empty prompt.
            logger?.LogError(ex, "Failed to reach prompt registry for {Role}/{Action}", role, action);
            throw new TammaError(
                "LLM.PROMPT.RESOLVE.REGISTRY_UNAVAILABLE",
                $"Could not reach the prompt registry for (role='{role}', action='{action}'): {ex.Message}",
                new Dictionary<string, object?> { ["role"] = role, ["action"] = action, ["tenantId"] = tenantId },
                retryable: true,
                severity: TammaErrorSeverity.High);
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;

            // 5xx: transient server fault. Retryable — the prompt may well
            // exist once the server recovers. Must NOT be labelled NO_ROW.
            if (statusCode >= 500)
            {
                logger?.LogError(
                    "Prompt registry returned transient {Status} for {Role}/{Action}",
                    response.StatusCode, role, action);
                throw new TammaError(
                    "LLM.PROMPT.RESOLVE.REGISTRY_UNAVAILABLE",
                    $"Prompt registry returned server error {statusCode} for (role='{role}', action='{action}').",
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

            // Story 27-18: a 404 / other 4xx is a real fault for a taxonomy-valid
            // pair (every valid (role, action) ships a system default). Fail loud
            // instead of degrading to the plain prompt. 4xx is non-retryable —
            // 404 means the row doesn't exist, other 4xx (401/403) are permanent
            // client-side faults.
            logger?.LogError("Prompt registry returned {Status} for {Role}/{Action}", response.StatusCode, role, action);
            throw new TammaError(
                "LLM.PROMPT.RESOLVE.NO_ROW",
                $"Prompt registry returned {statusCode} for (role='{role}', action='{action}').",
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

        // Render endpoint returns the user-prompt half under "renderedTemplate"
        // (audit prompts/003). Keep the legacy "rendered" key as a tolerant
        // fallback for any older serialisation, then "renderedSystemPrompt".
        var rendered = result.TryGetProperty("renderedTemplate", out var rt) ? rt.GetString() ?? ""
            : result.TryGetProperty("rendered", out var r) ? r.GetString() ?? ""
            : "";
        var systemPrompt = result.TryGetProperty("renderedSystemPrompt", out var rsp) ? rsp.GetString() ?? ""
            : result.TryGetProperty("systemPrompt", out var sp) ? sp.GetString() ?? ""
            : "";
        var enableTools = result.TryGetProperty("enableTools", out var et) && et.GetBoolean();
        var maxTokens = result.TryGetProperty("maxTokens", out var mt) ? mt.GetInt32() : 4096;

        return (rendered, systemPrompt, enableTools, maxTokens);
    }

    /// <summary>
    /// Boundary taxonomy validation for a non-empty <c>(role, action)</c> pair
    /// (Story 27-18). A taxonomy-invalid role or action throws (fail-fast); the
    /// activity then surfaces the failure rather than silently rendering the
    /// wrong prompt. Extracted as a static so it is unit-testable without an
    /// Elsa <c>ActivityExecutionContext</c> (per the convention in
    /// <c>CheckBudgetActivityEmissionTests</c>).
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
        var promptLength = context.TransientProperties.TryGetValue("resolvedPromptLength", out var len) ? len : 0;
        var hasSystem = context.TransientProperties.TryGetValue("hasSystemPrompt", out var hs) && hs is true;
        return new()
        {
            ["role"] = Role.Get(context),
            ["action"] = Action.Get(context),
            ["tenantId"] = TenantId.Get(context),
            ["promptLength"] = promptLength,
            ["hasSystemPrompt"] = hasSystem,
        };
    }
}
