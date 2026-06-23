using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.Security;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Story 32-5 (AC5/AC6) — THIN CLIENT over the managed execution endpoint.
///
/// <para>This activity used to hold a provider key, build the Anthropic/OpenAI
/// request, perform the external provider HTTP call, and run a full agentic tool
/// loop IN THE ENGINE PROCESS. After the Epic 32
/// pivot it owns NONE of that: a workflow step never calls an external provider.
/// It maps its <see cref="Input{T}"/> props into an <see cref="LlmCallApiRequest"/>,
/// sends it via <see cref="TammaApiClient.CallLlmAsync"/> to
/// <c>POST /api/v1/llm/call</c> (which holds the credential, gates, runs the loop
/// server-side, and meters), and writes the result back into the SAME workflow
/// variables it wrote before — <c>LastDiagnostic</c> (a
/// <see cref="ProviderAttemptDiagnostic"/>), <c>LastResponse</c> (a
/// <see cref="NormalizedLlmResponse"/>), and
/// <c>ToolLoopTokens</c>/<c>ToolLoopTurns</c>/<c>ToolLoopExhausted</c> — so
/// <c>LlmCallWorkflow.cs</c>'s <c>BuildRetryLoop</c>/<c>RetryCheck</c>/
/// <c>SkipIfSucceeded</c>/circuit-breaker keep working byte-for-byte (AC6).</para>
///
/// <para><b>No key, no HTTP-to-provider, no tool loop here.</b>
/// <c>enableToolLoop</c> + <c>toolLoopConfig</c> are passed THROUGH to the
/// endpoint (executed server-side), never locally. The engine resolver/runner DI
/// removal + the eight other in-engine callers are T6.</para>
/// </summary>
[Activity(
    "Tamma.LlmCall",
    "Call LLM Inline",
    "Send a managed LLM call to POST /api/v1/llm/call (inline for Sequence-based workflows)",
    Kind = ActivityKind.Task
)]
public class CallLlmInlineActivity : CodeActivity
{
    [Input(Description = "Serialized workflow input JSON")]
    public Input<string> InputJsonProp { get; set; } = default!;

    [Input(Description = "Provider key")]
    public Input<string> ProviderNameProp { get; set; } = default!;

    [Input(Description = "Resolved system prompt")]
    public Input<string> SystemPromptProp { get; set; } = default!;

    [Input(Description = "Resolved tools JSON")]
    public Input<string?> ToolsJsonProp { get; set; } = default!;

    [Input(Description = "Attempt number")]
    public Input<int> AttemptNumberProp { get; set; } = default!;

    [Input(Description = "Whether to enable the agentic tool loop")]
    public Input<bool> EnableToolLoopProp { get; set; } = new(false);

    [Input(Description = "Tool loop configuration JSON (serialized ToolLoopConfig)")]
    public Input<string?> ToolLoopConfigJsonProp { get; set; } = default!;

    /// <summary>
    /// Story 32-3 (AC3) — tenant id (GUID string) threaded from
    /// <c>LlmCallWorkflow</c>'s existing <c>TenantId</c> variable. It is sent as
    /// the <c>X-Tenant-Id</c> header — the authoritative scope the endpoint
    /// asserts (Finding C1). Empty / whitespace ⇒ single-user / platform scope.
    /// </summary>
    [Input(Description = "Tenant id (GUID string) for BYOK credential resolution; empty = single-user/platform")]
    public Input<string?> TenantIdProp { get; set; } = new((string?)null);

    private readonly ILogger<CallLlmInlineActivity>? _logger;
    private readonly TammaApiClient? _apiClient;

    [JsonConstructor]
    public CallLlmInlineActivity() : this(null, null)
    {
    }

    /// <summary>
    /// Sanitizer-compatible constructor preserved for the existing
    /// <c>CallLlmInlineActivitySanitizationTests</c> regression net. The
    /// provider-side collaborators (http factory, configuration, sanitizer, tool
    /// registry, validator, compactor, event emitter, parallel executor,
    /// credential resolver, loop runner) are NO LONGER USED by the shim — they are
    /// accepted and ignored so callers/tests that still pass them keep compiling
    /// (the engine-side DI removal is T6). The shim resolves its only collaborator,
    /// <see cref="TammaApiClient"/>, from the activity execution context.
    /// </summary>
    public CallLlmInlineActivity(
        ILogger<CallLlmInlineActivity>? logger,
        IHttpClientFactory? httpClientFactory,
        IConfiguration? configuration = null,
        IContentSanitizer? sanitizer = null,
        object? toolRegistry = null,
        object? toolCallValidator = null,
        object? contextCompactor = null,
        object? eventEmitter = null,
        object? parallelExecutor = null,
        object? credentialResolver = null,
        object? toolLoop = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// DI constructor used by the engine host. Takes the
    /// <see cref="TammaApiClient"/> the shim delegates to. (Resolution from the
    /// activity execution context is the fallback when this ctor is not used —
    /// e.g. the <see cref="JsonConstructorAttribute"/> path.)
    /// </summary>
    public CallLlmInlineActivity(
        ILogger<CallLlmInlineActivity>? logger,
        TammaApiClient? apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var inputJson = InputJsonProp.Get(context);
        var providerName = ProviderNameProp.Get(context);
        var systemPrompt = SystemPromptProp.Get(context);
        var toolsJson = ToolsJsonProp.Get(context);
        var attemptNumber = AttemptNumberProp.Get(context);
        var enableToolLoop = EnableToolLoopProp.Get(context);
        var toolLoopConfigJson = ToolLoopConfigJsonProp.Get(context);
        var tenantIdRaw = TenantIdProp.Get(context);

        var input = ParseInput(inputJson);
        var toolLoopConfig = ParseToolLoopConfig(toolLoopConfigJson);

        var model = input.ModelOverrides.TryGetValue(providerName, out var mo) ? mo : null;
        var correlationId = context.WorkflowExecutionContext.Id;

        // Map the activity's Input<> props → the wire request. The per-iteration
        // provider name (the ForEach<provider> chain in LlmCallWorkflow) maps to
        // the explicit Provider OVERRIDE the API honours for THIS call (Finding
        // I-1) — it is the provider KEY (anthropic/openai/openrouter), NOT a
        // persona. The API renders the prompt authoritatively (Epic 27), so the
        // engine forwards NO system prompt.
        var request = BuildLlmCallRequest(
            input, providerName, systemPrompt, toolsJson, model,
            enableToolLoop, toolLoopConfig, tenantIdRaw, correlationId);

        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var tenantHeader = string.IsNullOrWhiteSpace(tenantIdRaw) ? null : tenantIdRaw!.Trim();

        var startedAt = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var response = await apiClient.CallLlmAsync(request, tenantHeader, context.CancellationToken)
            .ConfigureAwait(false);
        sw.Stop();

        // A null response is a transport / raw-5xx failure (PostAsync nulled it).
        // Treat it as a transient failure (httpStatusCode 0) so RetryCheck retries
        // — never a successful empty result.
        if (response is null)
        {
            _logger?.LogWarning(
                "call-LLM returned no body (transport/5xx) for provider {Provider}, workflow {WorkflowInstanceId}",
                providerName, correlationId);

            WriteVariables(
                context,
                BuildTransportFailure(providerName, model, attemptNumber, sw.ElapsedMilliseconds, startedAt));
            return;
        }

        WriteVariables(
            context,
            MapResponseToVariables(response, providerName, model, attemptNumber, sw.ElapsedMilliseconds, startedAt));

        _logger?.LogDebug(
            "call-LLM result written: workflow {WorkflowInstanceId}, provider {Provider}, success {Success}, "
            + "httpStatus {HttpStatus}, toolLoopTokens {ToolLoopTokens}",
            correlationId, providerName, response.Success,
            response.HttpStatusCode, response.Usage.ToolLoopTokens);
    }

    // =======================================================================
    // Pure, testable mapping helpers (no Elsa context required)
    // =======================================================================

    /// <summary>
    /// Map the activity's input props into the wire <see cref="LlmCallApiRequest"/>.
    /// Static + context-free so it is unit-testable (the convention used by
    /// <see cref="ResolvePromptFromRegistryActivity.CallResolveAsync"/>).
    /// </summary>
    public static LlmCallApiRequest BuildLlmCallRequest(
        LlmCallWorkflowInput input,
        string providerName,
        string? systemPrompt,
        string? toolsJson,
        string? model,
        bool enableToolLoop,
        ToolLoopConfig toolLoopConfig,
        string? tenantIdRaw,
        string correlationId)
    {
        // Finding I-1 — the API renders the prompt AUTHORITATIVELY (Epic 27,
        // (principal, role, action) resolution). The engine forwards NO system
        // prompt: the dead `variables["systemPrompt"]` mapping is gone, and
        // `systemPrompt` is now ignored here (the param is retained only because
        // the workflow still wires SystemPromptProp; it carries no authority). The
        // per-iteration provider maps to the explicit Provider OVERRIDE the API
        // honours, NOT to Persona.
        _ = systemPrompt;
        var tools = DeserializeResolvedTools(toolsJson)?.Select(t => t.Name).ToList();

        return new LlmCallApiRequest
        {
            TenantId = ParseTenantId(tenantIdRaw),
            Provider = string.IsNullOrWhiteSpace(providerName) ? null : providerName,
            // Canonical AgentRole wire default — the endpoint 422s on an unknown role.
            Role = string.IsNullOrWhiteSpace(input.Role) ? "developer" : input.Role,
            Action = string.IsNullOrWhiteSpace(input.OperationName) ? null : input.OperationName,
            Prompt = input.UserPrompt,
            Variables = new Dictionary<string, object?>(),
            Model = model,
            Tools = tools is { Count: > 0 } ? tools : null,
            EnableToolLoop = enableToolLoop,
            ToolLoopConfig = enableToolLoop ? toolLoopConfig : null,
            Params = new LlmCallApiParams
            {
                MaxTokens = input.MaxTokens,
                Temperature = input.Temperature,
                BudgetCapUsd = input.BudgetCapUsd,
            },
            CorrelationId = correlationId,
        };
    }

    /// <summary>
    /// Project the wire <see cref="LlmCallApiResponse"/> into the SAME workflow
    /// variable shapes the legacy local path produced: a
    /// <see cref="ProviderAttemptDiagnostic"/> (for <c>LastDiagnostic</c>), a
    /// <see cref="NormalizedLlmResponse"/> (for <c>LastResponse</c>), and the three
    /// tool-loop counters. Static + context-free so it is unit-testable.
    ///
    /// <para><b>HttpStatusCode (load-bearing for RetryCheck).</b> When the API body
    /// omits an upstream status it is derived: success ⇒ 200 (not in the transient
    /// set), failure ⇒ 0 (transient → RetryCheck advances). A preserved upstream
    /// status (429/502/503/504/0) flows through unchanged.</para>
    /// </summary>
    public static MappedLlmVariables MapResponseToVariables(
        LlmCallApiResponse response,
        string providerName,
        string? model,
        int attemptNumber,
        long durationMs,
        DateTime startedAtUtc)
    {
        var httpStatus = response.HttpStatusCode ?? (response.Success ? 200 : 0);

        var diagnostic = new ProviderAttemptDiagnostic
        {
            ProviderName = providerName,
            Model = response.ModelUsed ?? model,
            AttemptNumber = attemptNumber,
            Succeeded = response.Success,
            HttpStatusCode = httpStatus,
            ErrorMessage = response.Success
                ? null
                : ComposeFailureMessage(response.FailureCode, response.FailureReason),
            DurationMs = durationMs,
            StartedAtUtc = startedAtUtc,
            PromptTokens = response.Usage.PromptTokens,
            CompletionTokens = response.Usage.CompletionTokens,
            CredentialSource = response.CredentialSource,
        };

        var normalized = new NormalizedLlmResponse
        {
            Success = response.Success,
            ResponseText = response.Text,
            Model = response.ModelUsed ?? model,
            PromptTokens = response.Usage.PromptTokens,
            CompletionTokens = response.Usage.CompletionTokens,
            HttpStatusCode = httpStatus,
            ErrorMessage = response.Success
                ? null
                : ComposeFailureMessage(response.FailureCode, response.FailureReason),
            ToolCalls = response.ToolCalls.Count == 0
                ? null
                : response.ToolCalls
                    .Select(tc => new LlmToolCall { Id = tc.Id, ToolName = tc.Name, ArgumentsJson = tc.ArgumentsJson })
                    .ToList(),
        };

        return new MappedLlmVariables(
            diagnostic,
            normalized,
            response.Usage.ToolLoopTokens,
            response.Usage.ToolLoopTurns,
            response.Usage.ToolLoopExhausted);
    }

    /// <summary>
    /// The fail-closed variables for a transport / raw-5xx (null body) result:
    /// a failed diagnostic with the transient <c>httpStatusCode 0</c> so RetryCheck
    /// advances, plus an unsuccessful empty response.
    /// </summary>
    public static MappedLlmVariables BuildTransportFailure(
        string providerName,
        string? model,
        int attemptNumber,
        long durationMs,
        DateTime startedAtUtc)
    {
        const string message = "call-LLM endpoint unavailable (no response body)";
        var diagnostic = new ProviderAttemptDiagnostic
        {
            ProviderName = providerName,
            Model = model,
            AttemptNumber = attemptNumber,
            Succeeded = false,
            HttpStatusCode = 0,
            ErrorMessage = message,
            DurationMs = durationMs,
            StartedAtUtc = startedAtUtc,
        };
        var normalized = new NormalizedLlmResponse
        {
            Success = false,
            HttpStatusCode = 0,
            ErrorMessage = message,
        };
        return new MappedLlmVariables(diagnostic, normalized, 0, 0, false);
    }

    /// <summary>
    /// Write the mapped variables to the workflow context using the SAME
    /// serialization the legacy path used (default <see cref="JsonSerializer"/> ⇒
    /// PascalCase), so <c>RetryCheck</c>/<c>SuccessCheck</c>/the success-output
    /// builder deserialize them byte-compatibly.
    /// </summary>
    private static void WriteVariables(ActivityExecutionContext context, MappedLlmVariables v)
    {
        context.SetVariable("LastDiagnostic", JsonSerializer.Serialize(v.Diagnostic));
        context.SetVariable("LastResponse", JsonSerializer.Serialize(v.Response));
        context.SetVariable("ToolLoopTokens", v.ToolLoopTokens);
        context.SetVariable("ToolLoopTurns", v.ToolLoopTurns);
        context.SetVariable("ToolLoopExhausted", v.ToolLoopExhausted);
    }

    private static string ComposeFailureMessage(string? failureCode, string? failureReason)
    {
        if (!string.IsNullOrEmpty(failureCode) && !string.IsNullOrEmpty(failureReason))
            return $"{failureCode}: {failureReason}";
        if (!string.IsNullOrEmpty(failureReason)) return failureReason!;
        if (!string.IsNullOrEmpty(failureCode)) return failureCode!;
        return "LLM call failed";
    }

    // =======================================================================
    // Parsing helpers (carried over verbatim from the legacy activity)
    // =======================================================================

    /// <summary>
    /// Parse the <c>TenantIdProp</c> string into a <see cref="Guid"/>. Empty /
    /// whitespace / unparseable ⇒ null (single-user / platform scope).
    /// </summary>
    private static Guid? ParseTenantId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return Guid.TryParse(raw.Trim(), out var g) ? g : null;
    }

    private static LlmCallWorkflowInput ParseInput(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new LlmCallWorkflowInput();
        try { return JsonSerializer.Deserialize<LlmCallWorkflowInput>(json) ?? new LlmCallWorkflowInput(); }
        catch { return new LlmCallWorkflowInput(); }
    }

    private static ToolLoopConfig ParseToolLoopConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ToolLoopConfig();
        try { return JsonSerializer.Deserialize<ToolLoopConfig>(json) ?? new ToolLoopConfig(); }
        catch { return new ToolLoopConfig(); }
    }

    private static List<ResolvedTool>? DeserializeResolvedTools(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<ResolvedTool>>(json); }
        catch { return null; }
    }
}

/// <summary>
/// Story 32-5 (AC5) — the mapped workflow-variable bundle the shim writes from a
/// wire <see cref="LlmCallApiResponse"/>. Returned by
/// <see cref="CallLlmInlineActivity.MapResponseToVariables"/> /
/// <see cref="CallLlmInlineActivity.BuildTransportFailure"/> so the mapping is
/// unit-testable without an Elsa context.
/// </summary>
public sealed record MappedLlmVariables(
    ProviderAttemptDiagnostic Diagnostic,
    NormalizedLlmResponse Response,
    int ToolLoopTokens,
    int ToolLoopTurns,
    bool ToolLoopExhausted);
