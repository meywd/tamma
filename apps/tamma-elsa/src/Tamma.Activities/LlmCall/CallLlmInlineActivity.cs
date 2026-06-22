using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.LlmCall.Tools;
using Tamma.Activities.Security;
using Tamma.Activities.ToolExecution;
using Tamma.Core;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Inline activity that performs the actual LLM HTTP call.
/// Writes results to workflow variables "LastDiagnostic" and "LastResponse".
/// This is used inside the Sequence-based retry loop of LlmCallWorkflow.
///
/// When EnableToolLoop is true, executes a multi-turn agentic loop:
///   call LLM -> parse tool calls -> execute tools -> feed results back -> repeat
/// until the LLM produces a text-only response or maxSteps is reached.
/// </summary>
[Activity(
    "Tamma.LlmCall",
    "Call LLM Inline",
    "Execute HTTP call to LLM provider (inline for Sequence-based workflows)",
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
    /// Story 32-3 (AC3) — tenant id (GUID string) for BYOK credential
    /// resolution. Threaded from <c>LlmCallWorkflow</c>'s existing
    /// <c>TenantId</c> variable. Empty / whitespace ⇒ single-user / platform
    /// scope (<c>tenantId == null</c>).
    /// </summary>
    [Input(Description = "Tenant id (GUID string) for BYOK credential resolution; empty = single-user/platform")]
    public Input<string?> TenantIdProp { get; set; } = new((string?)null);

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<CallLlmInlineActivity>? _logger;
    private readonly IContentSanitizer? _sanitizer;
    private readonly IToolExecutorRegistry? _toolRegistry;
    private readonly IToolCallValidator? _toolCallValidator;
    private readonly ContextCompactor? _contextCompactor;
    private readonly ToolLoopEventEmitter? _eventEmitter;
    private readonly ParallelToolExecutor? _parallelExecutor;
    private readonly IProviderCredentialResolver? _credentialResolver;

    // Story 32-5 (AC4) — the agentic tool loop + its provider-call/config helpers
    // were extracted VERBATIM into InlineToolLoopRunner. The activity still runs
    // the runner LOCALLY (the cutover to the API endpoint is T5/T6). When a runner
    // is not DI-injected (the [JsonConstructor]/test ctors), one is built lazily
    // from this activity's own collaborators so behaviour is identical.
    private InlineToolLoopRunner? _toolLoop;

    [JsonConstructor]
    public CallLlmInlineActivity() : this(null, null, null, null, null, null, null, null, null, null)
    {
    }

    public CallLlmInlineActivity(
        ILogger<CallLlmInlineActivity>? logger,
        IHttpClientFactory? httpClientFactory,
        IConfiguration? configuration,
        IContentSanitizer? sanitizer,
        IToolExecutorRegistry? toolRegistry = null,
        IToolCallValidator? toolCallValidator = null,
        ContextCompactor? contextCompactor = null,
        ToolLoopEventEmitter? eventEmitter = null,
        ParallelToolExecutor? parallelExecutor = null,
        IProviderCredentialResolver? credentialResolver = null,
        InlineToolLoopRunner? toolLoop = null)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _sanitizer = sanitizer;
        _toolRegistry = toolRegistry;
        _toolCallValidator = toolCallValidator;
        _contextCompactor = contextCompactor;
        _eventEmitter = eventEmitter;
        _parallelExecutor = parallelExecutor;
        _credentialResolver = credentialResolver;
        _toolLoop = toolLoop;
    }

    /// <summary>
    /// The extracted tool-loop runner. Uses the DI-injected instance when present;
    /// otherwise builds one from this activity's own collaborators (single home of
    /// the loop — no fork). Constructed lazily so the [JsonConstructor] path works.
    /// </summary>
    private InlineToolLoopRunner ToolLoop =>
        _toolLoop ??= new InlineToolLoopRunner(
            logger: null,
            httpClientFactory: _httpClientFactory,
            configuration: _configuration,
            sanitizer: _sanitizer,
            toolRegistry: _toolRegistry,
            toolCallValidator: _toolCallValidator,
            contextCompactor: _contextCompactor,
            eventEmitter: _eventEmitter,
            parallelExecutor: _parallelExecutor,
            credentialResolver: _credentialResolver);

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var inputJson = InputJsonProp.Get(context);
        var providerName = ProviderNameProp.Get(context);
        var systemPromptRaw = SystemPromptProp.Get(context);
        var toolsJson = ToolsJsonProp.Get(context);
        var attemptNumber = AttemptNumberProp.Get(context);
        var enableToolLoop = EnableToolLoopProp.Get(context);
        var toolLoopConfigJson = ToolLoopConfigJsonProp.Get(context);

        var input = ParseInput(inputJson);

        // Story 32-3 (AC3) — tenant context for BYOK credential resolution.
        var tenantId = ParseTenantId(TenantIdProp.Get(context));

        // Sanitize prompts before LLM call (defense-in-depth against prompt injection)
        var systemPrompt = SanitizePrompts(context, providerName, systemPromptRaw, input);

        // ======== Backward-compatible guard ========
        // When EnableToolLoop is false, execute the EXACT existing single-turn code path.
        if (!enableToolLoop)
        {
            var model = input.ModelOverrides.TryGetValue(providerName, out var mo)
                ? mo
                : ToolLoop.GetDefaultModel(providerName);
            await SingleTurnCall(context, input, providerName, systemPrompt, toolsJson, attemptNumber, model, tenantId);
            return;
        }

        // ======== Agentic Tool Loop ========
        var loopModel = input.ModelOverrides.TryGetValue(providerName, out var mo2)
            ? mo2
            : ToolLoop.GetDefaultModel(providerName);
        var loopConfig = ParseToolLoopConfig(toolLoopConfigJson);
        var tools = DeserializeResolvedTools(toolsJson);

        // If no tools from the workflow, use registered tool executors' definitions
        if ((tools == null || tools.Count == 0) && _toolRegistry != null)
        {
            var allowedExecutors = _toolRegistry.GetAllowed(loopConfig.AllowedTools);
            tools = allowedExecutors.Select(e => new ResolvedTool
            {
                Name = e.ToolName,
                Description = e.Description,
                InputSchema = e.InputSchema
            }).ToList();
        }

        var startedAt = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _logger?.LogInformation(
            "Tool loop entered: WorkflowInstanceId={WorkflowInstanceId}, Provider={Provider}, Model={Model}, MaxSteps={MaxSteps}, AllowedToolCount={AllowedToolCount}",
            context.WorkflowExecutionContext.Id, providerName, loopModel, loopConfig.MaxSteps,
            loopConfig.AllowedTools?.Length ?? 0);

        // Story 32-3 — resolved BYOK/platform source for the diagnostic tag.
        // Set inside the try (after resolution) so a fail-closed credential
        // error surfaces as a failed diagnostic, never a leaked exception.
        string? credentialSource = null;

        try
        {
            // Resolve provider config + the API key (BYOK→platform) just before
            // the call. A PROVIDER_CREDENTIAL_UNAVAILABLE TammaError thrown here
            // is caught below as a failed attempt so the provider chain advances.
            var (providerConfig, source) =
                await ToolLoop.LoadProviderConfigWithKeyAsync(providerName, tenantId, context.CancellationToken);
            credentialSource = source;

            // Story 32-5 (AC4) — delegate to the extracted runner (run LOCALLY).
            var loop = await ToolLoop.RunAsync(
                providerName, providerConfig, loopModel, systemPrompt,
                input.UserPrompt, input.MaxTokens, input.Temperature, tools,
                enableToolLoop: true, loopConfig,
                correlationId: context.WorkflowExecutionContext.Id, context.CancellationToken);
            var response = loop.Response;
            var totalTokens = loop.InputTokens + loop.OutputTokens;
            var turns = loop.Turns;
            var exhausted = loop.Exhausted;

            sw.Stop();

            // Output sanitization: strip HTML/zero-width from LLM response before storage
            if (_sanitizer != null && response.ResponseText != null)
            {
                var outputResult = _sanitizer.SanitizeOutput(response.ResponseText);
                response.ResponseText = outputResult.Result;
            }

            var diagnostic = new ProviderAttemptDiagnostic
            {
                ProviderName = providerName,
                Model = loopModel,
                AttemptNumber = attemptNumber,
                Succeeded = response.Success,
                HttpStatusCode = response.HttpStatusCode,
                ErrorMessage = response.ErrorMessage,
                DurationMs = sw.ElapsedMilliseconds,
                StartedAtUtc = startedAt,
                PromptTokens = response.PromptTokens,
                CompletionTokens = response.CompletionTokens,
                CredentialSource = credentialSource
            };

            context.SetVariable("LastDiagnostic", JsonSerializer.Serialize(diagnostic));
            context.SetVariable("LastResponse", JsonSerializer.Serialize(response));
            context.SetVariable("ToolLoopTokens", totalTokens);
            context.SetVariable("ToolLoopTurns", turns);
            context.SetVariable("ToolLoopExhausted", exhausted);

            _logger?.LogDebug(
                "Tool loop output written: WorkflowInstanceId={WorkflowInstanceId}, ToolLoopTokens={ToolLoopTokens}, ToolLoopTurns={ToolLoopTurns}",
                context.WorkflowExecutionContext.Id, totalTokens, turns);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // NOTE: never log ex with a key — TammaError messages are key-free.
            _logger?.LogError(ex, "Agentic tool loop failed for {Provider}", providerName);

            var diagnostic = new ProviderAttemptDiagnostic
            {
                ProviderName = providerName,
                Model = loopModel,
                AttemptNumber = attemptNumber,
                Succeeded = false,
                ErrorMessage = ex is TammaError te
                    ? $"{te.Code}: {te.Message}"
                    : $"Tool loop error: {ex.Message}",
                DurationMs = sw.ElapsedMilliseconds,
                StartedAtUtc = startedAt,
                CredentialSource = credentialSource
            };

            context.SetVariable("LastDiagnostic", JsonSerializer.Serialize(diagnostic));
            context.SetVariable("LastResponse", JsonSerializer.Serialize(new NormalizedLlmResponse
            {
                Success = false,
                ErrorMessage = diagnostic.ErrorMessage
            }));
        }
    }

    // =======================================================================
    // Prompt Sanitization
    // =======================================================================

    /// <summary>
    /// Sanitize system and user prompts using the content sanitizer if available.
    /// </summary>
    private string SanitizePrompts(
        ActivityExecutionContext context, string providerName,
        string systemPromptRaw, LlmCallWorkflowInput input)
    {
        if (_sanitizer == null)
            return systemPromptRaw;

        var totalPatterns = 0;

        var systemResult = _sanitizer.SanitizeInput(systemPromptRaw);
        var systemPrompt = systemResult.Result;
        if (systemResult.Warnings.Count > 0)
        {
            totalPatterns += systemResult.Warnings.Count;
            _logger?.LogWarning(
                "Injection pattern detected in SystemPrompt for CallLlmInlineActivity, patterns matched: {Count}, workflow: {WorkflowInstanceId}",
                systemResult.Warnings.Count, context.WorkflowExecutionContext.Id);
        }

        if (!string.IsNullOrEmpty(input.UserPrompt))
        {
            var userResult = _sanitizer.SanitizeInput(input.UserPrompt);
            input.UserPrompt = userResult.Result;
            if (userResult.Warnings.Count > 0)
            {
                totalPatterns += userResult.Warnings.Count;
                _logger?.LogWarning(
                    "Injection pattern detected in UserPrompt for CallLlmInlineActivity, patterns matched: {Count}, workflow: {WorkflowInstanceId}",
                    userResult.Warnings.Count, context.WorkflowExecutionContext.Id);
            }
        }
        else
        {
            _logger?.LogDebug("Sanitization skipped for UserPrompt in CallLlmInlineActivity (empty/null input)");
        }

        if (totalPatterns > 0)
            _logger?.LogInformation(
                "Total injection patterns detected per LLM call: {TotalPatternsMatched}, activity=CallLlmInlineActivity, provider={Provider}, workflow: {WorkflowInstanceId}",
                totalPatterns, providerName, context.WorkflowExecutionContext.Id);

        return systemPrompt;
    }

    // =======================================================================
    // Single-Turn Call (existing behavior, zero changes)
    // =======================================================================

    /// <summary>
    /// Existing single-turn LLM call. Zero changes from the pre-tool-loop implementation.
    /// </summary>
    private async Task SingleTurnCall(
        ActivityExecutionContext context,
        LlmCallWorkflowInput input,
        string providerName,
        string systemPrompt,
        string? toolsJson,
        int attemptNumber,
        string model,
        Guid? tenantId)
    {
        var startedAt = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? credentialSource = null;

        // Apply exponential backoff delay for retry attempts (skip first attempt)
        if (attemptNumber > 1)
        {
            var baseDelay = 1000;
            var maxDelay = 30000;
            var delay = Math.Min(baseDelay * (int)Math.Pow(2, attemptNumber - 1), maxDelay);
            _logger?.LogInformation(
                "Retry backoff: waiting {Delay}ms before attempt {Attempt} for {Provider}",
                delay, attemptNumber, providerName);
            await Task.Delay(delay);
        }

        try
        {
            var httpClient = _httpClientFactory?.CreateClient($"llm-{providerName}")
                             ?? new HttpClient();

            // Resolve provider config + the API key (BYOK→platform) just before
            // the call. A PROVIDER_CREDENTIAL_UNAVAILABLE TammaError thrown here
            // is caught below as a failed attempt so the provider chain advances.
            // The provider-call/config helpers now live on the shared runner (AC4).
            var (providerConfig, source) =
                await ToolLoop.LoadProviderConfigWithKeyAsync(providerName, tenantId, context.CancellationToken);
            credentialSource = source;
            httpClient.Timeout = TimeSpan.FromSeconds(providerConfig.TimeoutSeconds);

            NormalizedLlmResponse response;

            if (providerName.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
            {
                response = await ToolLoop.CallAnthropicMessages(httpClient, providerConfig, model,
                    systemPrompt, input.UserPrompt, input.MaxTokens, input.Temperature, toolsJson);
            }
            else
            {
                response = await ToolLoop.CallOpenAiCompatible(httpClient, providerConfig, model,
                    systemPrompt, input.UserPrompt, input.MaxTokens, input.Temperature, toolsJson);
            }

            sw.Stop();

            // Output sanitization: strip HTML/zero-width from LLM response before storage
            if (_sanitizer != null && response.ResponseText != null)
            {
                var outputResult = _sanitizer.SanitizeOutput(response.ResponseText);
                response.ResponseText = outputResult.Result;
            }

            // Tool call validation (Story 11.3): validate tool calls in single-turn response
            if (_toolCallValidator != null && response.ToolCalls != null && response.ToolCalls.Count > 0)
            {
                var allowedNames = GetAllowedToolNames(toolsJson);
                foreach (var tc in response.ToolCalls)
                {
                    var vr = _toolCallValidator.Validate(tc, allowedNames);
                    if (vr.IsValid)
                    {
                        tc.ArgumentsJson = vr.SanitizedArgumentsJson ?? tc.ArgumentsJson;
                    }
                    else
                    {
                        _logger?.LogWarning(
                            "Tool call '{ToolName}' rejected in single-turn path: {Error}",
                            tc.ToolName, vr.ErrorMessage);
                        response.Success = false;
                        response.ErrorMessage = $"Tool call validation failed: {vr.ErrorMessage}";
                    }
                }
            }

            var diagnostic = new ProviderAttemptDiagnostic
            {
                ProviderName = providerName,
                Model = model,
                AttemptNumber = attemptNumber,
                Succeeded = response.Success,
                HttpStatusCode = response.HttpStatusCode,
                ErrorMessage = response.ErrorMessage,
                DurationMs = sw.ElapsedMilliseconds,
                StartedAtUtc = startedAt,
                PromptTokens = response.PromptTokens,
                CompletionTokens = response.CompletionTokens,
                CredentialSource = credentialSource
            };

            context.SetVariable("LastDiagnostic", JsonSerializer.Serialize(diagnostic));
            context.SetVariable("LastResponse", JsonSerializer.Serialize(response));
        }
        catch (Exception ex)
        {
            sw.Stop();

            var diagnostic = new ProviderAttemptDiagnostic
            {
                ProviderName = providerName,
                Model = model,
                AttemptNumber = attemptNumber,
                Succeeded = false,
                HttpStatusCode = 0,
                // TammaError messages (incl. PROVIDER_CREDENTIAL_UNAVAILABLE) are
                // key-free by construction — safe to surface here.
                ErrorMessage = ex is TaskCanceledException
                    ? "Request timed out"
                    : ex is TammaError te
                        ? $"{te.Code}: {te.Message}"
                        : $"Error: {ex.Message}",
                DurationMs = sw.ElapsedMilliseconds,
                StartedAtUtc = startedAt,
                CredentialSource = credentialSource
            };

            context.SetVariable("LastDiagnostic", JsonSerializer.Serialize(diagnostic));
            context.SetVariable("LastResponse", JsonSerializer.Serialize(new NormalizedLlmResponse
            {
                Success = false,
                ErrorMessage = diagnostic.ErrorMessage
            }));
        }
    }

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

    /// <summary>
    /// Extract the list of allowed tool names from the serialized tools JSON.
    /// Used by tool call validation in the single-turn code path.
    /// </summary>
    private static IReadOnlyList<string> GetAllowedToolNames(string? toolsJson)
    {
        if (string.IsNullOrWhiteSpace(toolsJson))
            return Array.Empty<string>();

        try
        {
            var tools = JsonSerializer.Deserialize<List<ResolvedTool>>(toolsJson);
            return tools?.Select(t => t.Name).ToList() ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
