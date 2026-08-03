using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.Security;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Story 32-5 (AC9) — THIN CLIENT over the managed execution endpoint.
///
/// <para>This flowchart-style activity used to read <c>Anthropic:ApiKey</c>
/// directly, build the Anthropic/OpenAI request, and perform the external
/// <c>POST /v1/messages</c> / <c>/v1/chat/completions</c> IN THE ENGINE PROCESS —
/// it was the most severe rule-1 violator. After the Epic 32 pivot it owns NONE
/// of that: a workflow step never calls an external provider. It maps its
/// <see cref="Input{T}"/> props into an <see cref="LlmCallApiRequest"/>, sends it
/// via <see cref="TammaApiClient.CallLlmAsync"/> to <c>POST /api/v1/llm/call</c>
/// (which holds the credential, gates, runs the loop server-side, and meters),
/// and maps the key-free result onto the SAME <c>LastDiagnostic</c>/
/// <c>LastResponse</c> workflow variables + the SAME
/// "Success"/"Retryable"/"Fatal" outcomes it produced before.</para>
///
/// <para>No live workflow wires this activity today (the <c>LlmCallWorkflow</c>
/// chain uses <see cref="CallLlmInlineActivity"/>); it is retained — gutted — as
/// the flowchart-shaped sibling and for the existing constructor/UIHint regression
/// nets. <b>No key, no HTTP-to-provider, no tool loop here.</b></para>
///
/// Outcomes:
///   "Success" — call succeeded, response is available.
///   "Retryable" — transient failure (429/5xx/0), caller should retry or failover.
///   "Fatal" — non-retryable failure (401/403/400), skip this provider.
/// </summary>
[Activity(
    "Tamma.LlmCall",
    "Call LLM",
    "Send a managed LLM call to POST /api/v1/llm/call with Success/Retryable/Fatal outcomes",
    Kind = ActivityKind.Task
)]
[FlowNode("Success", "Retryable", "Fatal")]
public class CallLlmActivity : Activity
{
    private readonly ILogger<CallLlmActivity>? _logger;
    private readonly TammaApiClient? _apiClient;

    /// <summary>Provider key (e.g. "anthropic", "openai") — sent as the explicit
    /// provider override the call-LLM endpoint honours.</summary>
    [Input(Description = "Provider key")]
    public Input<string> ProviderName { get; set; } = default!;

    /// <summary>Resolved system prompt (carried for back-compat; the API renders
    /// the prompt authoritatively via Epic 27, so this is not forwarded).</summary>
    [Input(Description = "System prompt")]
    public Input<string> SystemPrompt { get; set; } = default!;

    /// <summary>User prompt.</summary>
    [Input(Description = "User prompt")]
    public Input<string> UserPrompt { get; set; } = default!;

    /// <summary>Model override (optional, falls back to provider default).</summary>
    [Input(Description = "Model override")]
    public Input<string?> ModelOverride { get; set; } = default!;

    /// <summary>Max tokens.</summary>
    [Input(Description = "Max tokens", DefaultValue = 4096)]
    public Input<int> MaxTokens { get; set; } = new(4096);

    /// <summary>Temperature.</summary>
    [Input(Description = "Temperature", DefaultValue = 0.7)]
    public Input<double> Temperature { get; set; } = new(0.7);

    /// <summary>Serialized tools JSON (list of ResolvedTool).</summary>
    [Input(Description = "Serialized tools (JSON array of ResolvedTool)", UIHint = "json-editor")]
    public Input<string?> ToolsJson { get; set; } = default!;

    /// <summary>Current attempt number (1-based, managed by the workflow's retry loop).</summary>
    [Input(Description = "Current attempt number (1-based)", DefaultValue = 1)]
    public Input<int> AttemptNumber { get; set; } = new(1);

    /// <summary>Tenant id (GUID string) for BYOK credential resolution; empty = single-user/platform.</summary>
    [Input(Description = "Tenant id (GUID string); empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [JsonConstructor]
    public CallLlmActivity() : this(null, (TammaApiClient?)null)
    {
    }

    /// <summary>
    /// Sanitizer-compatible constructor preserved for the existing
    /// <c>CallLlmInlineActivitySanitizationTests</c> regression net. The
    /// provider-side collaborators (http factory, configuration, sanitizer, tool
    /// call validator) are NO LONGER USED — they are accepted and ignored so
    /// callers/tests that still pass them keep compiling. The thin client resolves
    /// its only collaborator, <see cref="TammaApiClient"/>, from the activity
    /// execution context.
    /// </summary>
    public CallLlmActivity(
        ILogger<CallLlmActivity>? logger,
        IHttpClientFactory? httpClientFactory,
        IConfiguration? configuration = null,
        IContentSanitizer? sanitizer = null,
        IToolCallValidator? toolCallValidator = null)
        : this(logger, (TammaApiClient?)null)
    {
    }

    /// <summary>DI constructor used by the engine host.</summary>
    public CallLlmActivity(
        ILogger<CallLlmActivity>? logger,
        TammaApiClient? apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var providerName = ProviderName.Get(context);
        var userPrompt = UserPrompt.Get(context);
        var modelOverride = ModelOverride.Get(context);
        var maxTokens = MaxTokens.Get(context);
        var temperature = Temperature.Get(context);
        var toolsJson = ToolsJson.Get(context);
        var attemptNumber = AttemptNumber.Get(context);
        var tenantIdRaw = TenantId.Get(context);

        var tools = DeserializeTools(toolsJson)?.Select(t => t.Name).ToList();
        // Story 43-14 (AC4) — the RUN correlation (the cycle instance id, threaded
        // as the Elsa correlation), NOT this sub-workflow's own instance id. A
        // grant a human minted at approval is keyed by the run correlation; a
        // sub-workflow that sent its own id would never match it and would 409.
        var correlationId = string.IsNullOrWhiteSpace(context.WorkflowExecutionContext.CorrelationId)
            ? context.WorkflowExecutionContext.Id
            : context.WorkflowExecutionContext.CorrelationId!;

        var request = new LlmCallApiRequest
        {
            TenantId = ParseTenantId(tenantIdRaw),
            Provider = string.IsNullOrWhiteSpace(providerName) ? null : providerName,
            // "developer" is a canonical AgentRole wire — the API's resolver 422s on
            // a non-canonical/unaliased role. (Was "assistant", which is neither.)
            Role = "developer",
            Prompt = userPrompt,
            Model = string.IsNullOrWhiteSpace(modelOverride) ? null : modelOverride,
            Tools = tools is { Count: > 0 } ? tools : null,
            EnableToolLoop = false,
            Params = new LlmCallApiParams { MaxTokens = maxTokens, Temperature = temperature },
            CorrelationId = correlationId,
        };

        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        // Coerce to a canonical Guid string for the authoritative X-Tenant-Id header
        // (a non-Guid value ⇒ platform scope), consistent with both the request body
        // above and MediatedLlmText — rather than forwarding a raw, unvalidated string.
        var tenantHeader = ParseTenantId(tenantIdRaw)?.ToString();

        var startedAt = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var response = await apiClient.CallLlmAsync(request, tenantHeader, context.CancellationToken)
            .ConfigureAwait(false);
        sw.Stop();

        // A null body is a transport / raw-5xx (PostAsync nulled it) — transient.
        var httpStatus = response is null ? 0 : (response.HttpStatusCode ?? (response.Success ? 200 : 0));
        var success = response?.Success ?? false;
        var model = response?.ModelUsed ?? (string.IsNullOrWhiteSpace(modelOverride) ? null : modelOverride);
        var errorMessage = success
            ? null
            : ComposeFailureMessage(response?.FailureCode, response?.FailureReason)
              ?? "call-LLM endpoint unavailable (no response body)";

        var diagnostic = new ProviderAttemptDiagnostic
        {
            ProviderName = providerName,
            Model = model,
            AttemptNumber = attemptNumber,
            Succeeded = success,
            HttpStatusCode = httpStatus,
            ErrorMessage = errorMessage,
            DurationMs = sw.ElapsedMilliseconds,
            StartedAtUtc = startedAt,
            PromptTokens = response?.Usage.PromptTokens ?? 0,
            CompletionTokens = response?.Usage.CompletionTokens ?? 0,
            CredentialSource = response?.CredentialSource,
        };

        var normalized = new NormalizedLlmResponse
        {
            Success = success,
            ResponseText = response?.Text,
            Model = model,
            PromptTokens = response?.Usage.PromptTokens ?? 0,
            CompletionTokens = response?.Usage.CompletionTokens ?? 0,
            HttpStatusCode = httpStatus,
            ErrorMessage = errorMessage,
            ToolCalls = (response?.ToolCalls.Count ?? 0) == 0
                ? null
                : response!.ToolCalls
                    .Select(tc => new LlmToolCall { Id = tc.Id, ToolName = tc.Name, ArgumentsJson = tc.ArgumentsJson })
                    .ToList(),
        };

        context.SetVariable("LastDiagnostic", JsonSerializer.Serialize(diagnostic));
        context.SetVariable("LastResponse", JsonSerializer.Serialize(normalized));

        if (success)
        {
            _logger?.LogInformation(
                "CallLlm succeeded: provider={Provider}, model={Model}, duration={Duration}ms",
                providerName, model, sw.ElapsedMilliseconds);
            await context.CompleteActivityWithOutcomesAsync("Success");
        }
        else if (IsRetryableStatusCode(httpStatus))
        {
            _logger?.LogWarning(
                "CallLlm retryable failure: provider={Provider}, status={Status}, error={Error}",
                providerName, httpStatus, errorMessage);
            await context.CompleteActivityWithOutcomesAsync("Retryable");
        }
        else
        {
            _logger?.LogError(
                "CallLlm fatal failure: provider={Provider}, status={Status}, error={Error}",
                providerName, httpStatus, errorMessage);
            await context.CompleteActivityWithOutcomesAsync("Fatal");
        }
    }

    private static Guid? ParseTenantId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return Guid.TryParse(raw.Trim(), out var g) ? g : null;
    }

    private static string? ComposeFailureMessage(string? failureCode, string? failureReason)
    {
        if (!string.IsNullOrEmpty(failureCode) && !string.IsNullOrEmpty(failureReason))
            return $"{failureCode}: {failureReason}";
        if (!string.IsNullOrEmpty(failureReason)) return failureReason;
        if (!string.IsNullOrEmpty(failureCode)) return failureCode;
        return null;
    }

    private static bool IsRetryableStatusCode(int statusCode) =>
        statusCode is 429 or 502 or 503 or 504 or 0;

    private static List<ResolvedTool>? DeserializeTools(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try { return JsonSerializer.Deserialize<List<ResolvedTool>>(json); }
        catch { return null; }
    }
}
