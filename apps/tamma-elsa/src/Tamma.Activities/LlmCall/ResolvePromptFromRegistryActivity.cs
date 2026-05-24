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
///     Thrown when the prompt registry cannot be reached or returns an error for
///     an otherwise taxonomy-valid pair (operational / transient failure). The
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

        HttpResponseMessage response;
        try
        {
            var httpClient = _httpClientFactory?.CreateClient() ?? new HttpClient();

            // Add tenant context header for tenant-scoped resolution
            if (!string.IsNullOrEmpty(tenantId))
            {
                httpClient.DefaultRequestHeaders.Remove("X-Tenant-Id");
                httpClient.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
            }

            // Parse variables
            Dictionary<string, object>? variables = null;
            if (!string.IsNullOrEmpty(variablesJson) && variablesJson != "{}")
            {
                variables = JsonSerializer.Deserialize<Dictionary<string, object>>(variablesJson);
            }

            // Call render endpoint
            response = await httpClient.PostAsJsonAsync(
                $"{callbackUrl.TrimEnd('/')}/api/prompts/{Uri.EscapeDataString(role)}/{Uri.EscapeDataString(action)}/render",
                new { variables = variables ?? new Dictionary<string, object>() });
        }
        catch (Exception ex)
        {
            // Story 27-18: NO plain fallback. A registry call that can't complete
            // (network/transient) fails loud — retryable so the workflow engine
            // can retry rather than running an LLM on the wrong/empty prompt.
            Logger?.LogError(ex, "Failed to reach prompt registry for {Role}/{Action}", role, action);
            throw new TammaError(
                "LLM.PROMPT.RESOLVE.REGISTRY_UNAVAILABLE",
                $"Could not reach the prompt registry for (role='{role}', action='{action}'): {ex.Message}",
                new Dictionary<string, object?> { ["role"] = role, ["action"] = action, ["tenantId"] = tenantId },
                retryable: true,
                severity: TammaErrorSeverity.High);
        }

        if (!response.IsSuccessStatusCode)
        {
            // Story 27-18: a 404/non-success is a real fault for a taxonomy-valid
            // pair (every valid (role, action) ships a system default). Fail loud
            // instead of degrading to the plain prompt.
            Logger?.LogError("Prompt registry returned {Status} for {Role}/{Action}", response.StatusCode, role, action);
            throw new TammaError(
                "LLM.PROMPT.RESOLVE.REGISTRY_MISS",
                $"Prompt registry returned {(int)response.StatusCode} for (role='{role}', action='{action}').",
                new Dictionary<string, object?>
                {
                    ["role"] = role,
                    ["action"] = action,
                    ["tenantId"] = tenantId,
                    ["status"] = (int)response.StatusCode,
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
