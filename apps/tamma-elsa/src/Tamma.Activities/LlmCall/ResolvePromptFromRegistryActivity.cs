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

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Resolves a prompt from the prompt registry using role + action.
/// Calls: POST /api/prompts/{role}/{action}/render with variables.
/// Falls back to the taskPrompt input if no action is specified
/// or if the registry is unavailable.
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

    [Input(Description = "Action (context-scan, plan, implement, etc.) — empty to skip registry")]
    public Input<string> Action { get; set; } = new("");

    [Input(Description = "Variables JSON for template interpolation")]
    public Input<string> VariablesJson { get; set; } = new("{}");

    [Input(Description = "Fallback prompt if registry unavailable or no action specified")]
    public Input<string> FallbackPrompt { get; set; } = new("");

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

        // If no action specified, use fallback prompt (legacy path)
        if (string.IsNullOrEmpty(action))
        {
            ResolvedPrompt.Set(context, fallback);
            ResolvedSystemPrompt.Set(context, "");
            EnableTools.Set(context, false);
            MaxTokens.Set(context, 4096);
            Logger?.LogInformation("No action specified, using fallback prompt for role {Role}", role);
            return;
        }

        // Try the prompt registry
        var callbackUrl = _configuration?["Engine:CallbackUrl"];
        if (string.IsNullOrEmpty(callbackUrl) || _httpClientFactory == null)
        {
            // No API available — try local prompt registry URL
            callbackUrl = _configuration?["PromptRegistry:BaseUrl"] ?? "http://localhost:3100";
        }

        try
        {
            var httpClient = _httpClientFactory?.CreateClient() ?? new HttpClient();

            // Parse variables
            Dictionary<string, object>? variables = null;
            if (!string.IsNullOrEmpty(variablesJson) && variablesJson != "{}")
            {
                variables = JsonSerializer.Deserialize<Dictionary<string, object>>(variablesJson);
            }

            // Call render endpoint
            var response = await httpClient.PostAsJsonAsync(
                $"{callbackUrl.TrimEnd('/')}/api/prompts/{Uri.EscapeDataString(role)}/{Uri.EscapeDataString(action)}/render",
                new { variables = variables ?? new Dictionary<string, object>() });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<JsonElement>();

                var rendered = result.TryGetProperty("rendered", out var r) ? r.GetString() ?? "" : "";
                var systemPrompt = result.TryGetProperty("systemPrompt", out var sp) ? sp.GetString() ?? "" : "";
                var enableTools = result.TryGetProperty("enableTools", out var et) && et.GetBoolean();
                var maxTokens = result.TryGetProperty("maxTokens", out var mt) ? mt.GetInt32() : 4096;

                ResolvedPrompt.Set(context, rendered);
                ResolvedSystemPrompt.Set(context, systemPrompt);
                EnableTools.Set(context, enableTools);
                MaxTokens.Set(context, maxTokens);

                Logger?.LogInformation("Resolved prompt from registry: {Role}/{Action} ({Length} chars)",
                    role, action, rendered.Length);
                return;
            }

            Logger?.LogWarning("Prompt registry returned {Status} for {Role}/{Action}, using fallback",
                response.StatusCode, role, action);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Failed to resolve prompt from registry for {Role}/{Action}, using fallback",
                role, action);
        }

        // Fallback
        ResolvedPrompt.Set(context, fallback);
        ResolvedSystemPrompt.Set(context, "");
        EnableTools.Set(context, false);
        MaxTokens.Set(context, 4096);
    }

    public override Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["role"] = Role.Get(context),
        ["action"] = Action.Get(context),
    };

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["role"] = Role.Get(context),
        ["action"] = Action.Get(context),
        ["promptLength"] = ResolvedPrompt.Get(context)?.Length ?? 0,
        ["hasSystemPrompt"] = !string.IsNullOrEmpty(ResolvedSystemPrompt.Get(context)),
    };
}
