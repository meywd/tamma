using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
/// Story 32-5 (AC4) — the agentic tool loop, extracted VERBATIM from
/// <c>CallLlmInlineActivity.AgenticToolLoop(...)</c> and its private helpers
/// (multi-turn callers, body builders, response parsers, single-turn callers
/// used by the compaction summarizer, and the BYOK→platform provider-config
/// resolution). No logic change: the only mechanical edits are the namespace,
/// the constructor-injected collaborators (replacing the activity's
/// <c>this.</c> fields), and replacing <c>context.WorkflowExecutionContext.Id</c>
/// with the <c>correlationId</c> argument and <c>context.CancellationToken</c>
/// with the <c>ct</c> argument.
///
/// <para>This is the single home of the loop, shared by the engine activity
/// (locally, today) and <c>Tamma.Api</c> (T3+). KEY SAFETY: the resolved
/// <see cref="LlmProviderConfig.ApiKey"/> is used for the outbound header only;
/// it is never logged, returned, or persisted.</para>
/// </summary>
public sealed class InlineToolLoopRunner : IInlineToolLoopRunner
{
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;
    private readonly ILogger? _logger;
    private readonly IContentSanitizer? _sanitizer;
    private readonly IToolExecutorRegistry? _toolRegistry;
    private readonly IToolCallValidator? _toolCallValidator;
    private readonly ContextCompactor? _contextCompactor;
    private readonly ToolLoopEventEmitter? _eventEmitter;
    private readonly ParallelToolExecutor? _parallelExecutor;
    private readonly IProviderCredentialResolver? _credentialResolver;

    public InlineToolLoopRunner(
        ILogger<InlineToolLoopRunner>? logger,
        IHttpClientFactory? httpClientFactory,
        IConfiguration? configuration,
        IContentSanitizer? sanitizer,
        IToolExecutorRegistry? toolRegistry = null,
        IToolCallValidator? toolCallValidator = null,
        ContextCompactor? contextCompactor = null,
        ToolLoopEventEmitter? eventEmitter = null,
        ParallelToolExecutor? parallelExecutor = null,
        IProviderCredentialResolver? credentialResolver = null)
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
    }

    /// <inheritdoc />
    public async Task<InlineToolLoopResult> RunAsync(
        string provider,
        LlmProviderConfig providerConfig,
        string model,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        double temperature,
        IReadOnlyList<ResolvedTool>? tools,
        bool enableToolLoop,
        ToolLoopConfig loopConfig,
        string correlationId,
        CancellationToken ct)
    {
        // The loop already folds the cumulative totals onto the response's
        // PromptTokens/CompletionTokens (their sum == the old TotalTokens), so
        // we discard the redundant scalar and surface the split counts below.
        var (response, _, turns, exhausted) = await AgenticToolLoop(
            provider, providerConfig, model, systemPrompt, userPrompt,
            maxTokens, temperature, tools, loopConfig, correlationId, ct);

        return new InlineToolLoopResult
        {
            Response = response,
            // The loop already writes cumulative totals onto the response;
            // surface them split (their sum equals the old TotalTokens).
            InputTokens = response.PromptTokens,
            OutputTokens = response.CompletionTokens,
            Turns = turns,
            Exhausted = exhausted
        };
    }

    // =======================================================================
    // Agentic Tool Loop  (moved VERBATIM from CallLlmInlineActivity)
    // =======================================================================

    /// <summary>
    /// Multi-turn agentic tool loop. Calls LLM, executes tools, feeds results back, repeats.
    /// </summary>
    private async Task<(NormalizedLlmResponse Response, int TotalTokens, int Turns, bool Exhausted)>
        AgenticToolLoop(
            string providerName,
            LlmProviderConfig providerConfig,
            string model,
            string systemPrompt,
            string userPrompt,
            int maxTokens,
            double temperature,
            IReadOnlyList<ResolvedTool>? tools,
            ToolLoopConfig loopConfig,
            string workflowInstanceId,
            CancellationToken cancellationToken)
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = systemPrompt },
            new() { Role = "user", Content = userPrompt }
        };

        var httpClient = _httpClientFactory?.CreateClient($"llm-{providerName}")
                       ?? new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(providerConfig.TimeoutSeconds);

        var totalPromptTokens = 0;
        var totalCompletionTokens = 0;
        var totalToolCalls = 0;
        var exhausted = false;
        NormalizedLlmResponse lastResponse = new() { Success = false, ErrorMessage = "No LLM call made" };
        var loopSw = System.Diagnostics.Stopwatch.StartNew();
        var completedTurns = 0;

        for (var step = 0; step < loopConfig.MaxSteps; step++)
        {
            var turnSw = System.Diagnostics.Stopwatch.StartNew();

            _logger?.LogInformation(
                "Tool loop turn started: WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, MessageCount={MessageCount}",
                workflowInstanceId, step, messages.Count);

            // Emit TURN_STARTED event if streaming is enabled
            if (loopConfig.EnableStreaming && _eventEmitter != null)
            {
                await _eventEmitter.EmitTurnStarted(
                    step, messages.Count, totalPromptTokens + totalCompletionTokens,
                    workflowInstanceId, cancellationToken);
            }

            // ═══ Context compaction check (Story 12.3) ═══
            // CompactIfNeeded handles the "fewer than 6 messages" edge case internally
            if (_contextCompactor != null)
            {
                var (compactedMessages, compactionTokens, wasCompacted) =
                    await _contextCompactor.CompactIfNeeded(
                        messages,
                        loopConfig.ContextWindowTokens,
                        loopConfig.CompactionThreshold,
                        async (prompt, ct) =>
                        {
                            // Make a single-turn summarization LLM call using the same provider
                            var summaryResponse = providerName.Equals("anthropic", StringComparison.OrdinalIgnoreCase)
                                ? await CallAnthropicMessages(httpClient, providerConfig, model,
                                    "You are a precise conversation summarizer.", prompt, 2048, 0.3, null)
                                : await CallOpenAiCompatible(httpClient, providerConfig, model,
                                    "You are a precise conversation summarizer.", prompt, 2048, 0.3, null);

                            return summaryResponse.Success ? summaryResponse.ResponseText : null;
                        },
                        workflowInstanceId,
                        step,
                        cancellationToken: cancellationToken);

                if (wasCompacted)
                {
                    messages = compactedMessages;
                    totalPromptTokens += compactionTokens;
                }
            }

            // Call LLM with full conversation history
            var llmSw = System.Diagnostics.Stopwatch.StartNew();
            NormalizedLlmResponse response;
            if (providerName.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
            {
                response = await CallAnthropicMultiTurn(
                    httpClient, providerConfig, model, messages, maxTokens, temperature, tools);
            }
            else
            {
                response = await CallOpenAiMultiTurn(
                    httpClient, providerConfig, model, messages, maxTokens, temperature, tools);
            }
            llmSw.Stop();

            lastResponse = response;

            _logger?.LogDebug(
                "LLM response received in loop: WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, StopReason={StopReason}, ToolCallCount={ToolCallCount}, InputTokens={InputTokens}, OutputTokens={OutputTokens}, DurationMs={DurationMs}",
                workflowInstanceId, step, response.StopReason,
                response.ToolCalls?.Count ?? 0, response.PromptTokens, response.CompletionTokens,
                llmSw.ElapsedMilliseconds);

            if (!response.Success)
            {
                _logger?.LogWarning("Tool loop LLM call failed on turn {TurnNumber}: {Error}",
                    step, response.ErrorMessage);
                break;
            }

            // Accumulate tokens
            totalPromptTokens += response.PromptTokens;
            totalCompletionTokens += response.CompletionTokens;

            _logger?.LogDebug(
                "Token usage per turn: WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, InputTokens={InputTokens}, OutputTokens={OutputTokens}, CumulativeInputTokens={CumulativeInputTokens}, CumulativeOutputTokens={CumulativeOutputTokens}",
                workflowInstanceId, step, response.PromptTokens, response.CompletionTokens,
                totalPromptTokens, totalCompletionTokens);

            completedTurns++;

            // Check if LLM is done (no tool calls, or explicit end_turn)
            if (response.StopReason != StopReason.ToolUse ||
                response.ToolCalls == null ||
                response.ToolCalls.Count == 0)
            {
                loopSw.Stop();
                _logger?.LogInformation(
                    "Tool loop completed ({Reason}): WorkflowInstanceId={WorkflowInstanceId}, TotalTurns={TotalTurns}, TotalToolCalls={TotalToolCalls}, TotalTokens={TotalTokens}, TotalDurationMs={TotalDurationMs}",
                    response.StopReason == StopReason.EndTurn ? "end_turn" : "text response",
                    workflowInstanceId, completedTurns, totalToolCalls,
                    totalPromptTokens + totalCompletionTokens, loopSw.ElapsedMilliseconds);
                break;
            }

            // ---- Tool call validation (Story 11.3) ----
            // Validate each tool call before execution. Build the allowlist from the
            // resolved tools sent to the LLM. Rejected calls produce error messages
            // that are tracked and fed back to the LLM as tool results (not crashes).
            var rejectedToolCalls = new Dictionary<string, string>(); // toolCallId -> errorMessage
            if (_toolCallValidator != null)
            {
                var allowedToolNames = tools?.Select(t => t.Name).ToList()
                    ?? new List<string>();

                foreach (var tc in response.ToolCalls)
                {
                    var validationResult = _toolCallValidator.Validate(tc, allowedToolNames);
                    if (validationResult.IsValid)
                    {
                        // Use sanitized arguments
                        tc.ArgumentsJson = validationResult.SanitizedArgumentsJson ?? tc.ArgumentsJson;
                    }
                    else
                    {
                        rejectedToolCalls[tc.Id] = validationResult.ErrorMessage
                            ?? "Tool call validation failed.";
                        _logger?.LogWarning(
                            "Tool call rejected by validator: WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, ToolCallId={ToolCallId}, ToolName={ToolName}",
                            workflowInstanceId, step, tc.Id, tc.ToolName);
                    }
                }
            }

            // Append assistant message to conversation history
            messages.Add(new ConversationMessage
            {
                Role = "assistant",
                Content = response.ResponseText,
                ToolCalls = response.ToolCalls.Select(tc =>
                    new ToolCallInfo(tc.Id, tc.ToolName, tc.ArgumentsJson)).ToArray()
            });

            // ---- Execute tool calls ----
            // Separate rejected tool calls (from validator) and executable tool calls.
            // Rejected calls get immediate error results; executable calls may run in parallel.
            var toolsExecuted = 0;
            var toolsSucceeded = 0;
            var toolsFailed = 0;

            // First, handle all rejected tool calls
            foreach (var toolCall in response.ToolCalls)
            {
                if (rejectedToolCalls.TryGetValue(toolCall.Id, out var rejectionMsg))
                {
                    var rejResult = new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                        rejectionMsg, 0);
                    toolsFailed++;
                    totalToolCalls++;
                    toolsExecuted++;

                    var rejOutput = rejResult.Output;
                    if (_sanitizer != null && !string.IsNullOrEmpty(rejOutput))
                    {
                        var sanitized = _sanitizer.SanitizeInput(rejOutput);
                        rejOutput = sanitized.Result;
                    }
                    rejOutput = ToolOutputHelper.RedactSecrets(rejOutput ?? string.Empty);

                    messages.Add(new ConversationMessage
                    {
                        Role = "tool",
                        Content = rejOutput,
                        ToolCallId = toolCall.Id,
                        ToolName = toolCall.ToolName
                    });
                }
            }

            // Collect executable tool calls (not rejected by validator)
            var executableToolCalls = response.ToolCalls
                .Where(tc => !rejectedToolCalls.ContainsKey(tc.Id))
                .ToList();

            // ---- Parallel execution path (Story 12.4) ----
            if (loopConfig.EnableParallelTools && _parallelExecutor != null && _toolRegistry != null
                && executableToolCalls.Count > 0)
            {
                // Pre-filter: check allowlist and registry availability before sending to parallel executor
                var validForExecution = new List<LlmToolCall>();
                foreach (var toolCall in executableToolCalls)
                {
                    if (!_toolRegistry.IsAllowed(toolCall.ToolName, loopConfig.AllowedTools))
                    {
                        _logger?.LogWarning(
                            "Tool call rejected (not allowed): WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, ToolCallId={ToolCallId}, ToolName={ToolName}",
                            workflowInstanceId, step, toolCall.Id, toolCall.ToolName);
                        var notAllowedResult = new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                            $"Tool '{toolCall.ToolName}' is not allowed. Available tools: {string.Join(", ", loopConfig.AllowedTools ?? Array.Empty<string>())}",
                            0);
                        toolsFailed++;
                        totalToolCalls++;
                        toolsExecuted++;

                        var naOutput = notAllowedResult.Output;
                        if (_sanitizer != null && !string.IsNullOrEmpty(naOutput))
                        {
                            var sanitized = _sanitizer.SanitizeInput(naOutput);
                            naOutput = sanitized.Result;
                        }
                        naOutput = ToolOutputHelper.RedactSecrets(naOutput ?? string.Empty);
                        messages.Add(new ConversationMessage
                        {
                            Role = "tool", Content = naOutput,
                            ToolCallId = toolCall.Id, ToolName = toolCall.ToolName
                        });
                    }
                    else
                    {
                        validForExecution.Add(toolCall);
                    }
                }

                if (validForExecution.Count > 0)
                {
                    var parallelResults = await _parallelExecutor.ExecuteToolsInParallelAsync(
                        validForExecution.ToArray(), _toolRegistry, loopConfig.ToolTimeoutMs,
                        workflowInstanceId, step,
                        loopConfig.EnableStreaming ? _eventEmitter : null,
                        cancellationToken);

                    foreach (var result in parallelResults)
                    {
                        totalToolCalls++;
                        toolsExecuted++;
                        if (result.Success) toolsSucceeded++;
                        else toolsFailed++;

                        var toolOutput = result.Output;
                        if (_sanitizer != null && !string.IsNullOrEmpty(toolOutput))
                        {
                            var sanitized = _sanitizer.SanitizeInput(toolOutput);
                            toolOutput = sanitized.Result;
                        }
                        toolOutput = ToolOutputHelper.RedactSecrets(toolOutput ?? string.Empty);

                        messages.Add(new ConversationMessage
                        {
                            Role = "tool",
                            Content = toolOutput,
                            ToolCallId = result.ToolCallId,
                            ToolName = result.ToolName
                        });
                    }
                }
            }
            else
            {
                // ---- Sequential execution path (original behavior) ----
                foreach (var toolCall in executableToolCalls)
                {
                    ToolExecutionResult result;

                    if (_toolRegistry == null)
                    {
                        result = new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                            "Tool execution not available (registry not configured)", 0);
                        toolsFailed++;
                    }
                    else if (!_toolRegistry.IsAllowed(toolCall.ToolName, loopConfig.AllowedTools))
                    {
                        _logger?.LogWarning(
                            "Tool call rejected (not allowed): WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, ToolCallId={ToolCallId}, ToolName={ToolName}",
                            workflowInstanceId, step, toolCall.Id, toolCall.ToolName);
                        result = new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                            $"Tool '{toolCall.ToolName}' is not allowed. Available tools: {string.Join(", ", loopConfig.AllowedTools ?? Array.Empty<string>())}",
                            0);
                        toolsFailed++;
                    }
                    else
                    {
                        var executor = _toolRegistry.GetExecutor(toolCall.ToolName);
                        if (executor == null)
                        {
                            _logger?.LogWarning(
                                "Tool call rejected (unknown tool): WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, ToolCallId={ToolCallId}, ToolName={ToolName}",
                                workflowInstanceId, step, toolCall.Id, toolCall.ToolName);
                            result = new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                                $"Unknown tool: '{toolCall.ToolName}'", 0);
                            toolsFailed++;
                        }
                        else
                        {
                            // Emit TOOL_EXECUTING event (sequential path)
                            if (loopConfig.EnableStreaming && _eventEmitter != null)
                            {
                                await _eventEmitter.EmitToolExecuting(
                                    step, toolCall.ToolName, toolCall.Id,
                                    workflowInstanceId, cancellationToken);
                            }

                            _logger?.LogDebug(
                                "Tool call dispatched: WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, ToolCallId={ToolCallId}, ToolName={ToolName}",
                                workflowInstanceId, step, toolCall.Id, toolCall.ToolName);

                            var toolSw = System.Diagnostics.Stopwatch.StartNew();
                            try
                            {
                                using var toolCts = CancellationTokenSource.CreateLinkedTokenSource(
                                    cancellationToken);
                                toolCts.CancelAfter(loopConfig.ToolTimeoutMs);

                                result = await executor.ExecuteAsync(
                                    toolCall.Id, toolCall.ArgumentsJson, toolCts.Token);
                                toolSw.Stop();

                                _logger?.LogDebug(
                                    "Tool call result received: WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, ToolCallId={ToolCallId}, ToolName={ToolName}, Success={Success}, DurationMs={DurationMs}, OutputSizeBytes={OutputSizeBytes}",
                                    workflowInstanceId, step, toolCall.Id, toolCall.ToolName,
                                    result.Success, toolSw.ElapsedMilliseconds,
                                    Encoding.UTF8.GetByteCount(result.Output ?? ""));

                                if (result.Success)
                                    toolsSucceeded++;
                                else
                                    toolsFailed++;
                            }
                            catch (Exception ex)
                            {
                                toolSw.Stop();
                                _logger?.LogError(
                                    "Tool call exception: WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, ToolCallId={ToolCallId}, ToolName={ToolName}, ExceptionType={ExceptionType}, ExceptionMessage={ExceptionMessage}",
                                    workflowInstanceId, step, toolCall.Id, toolCall.ToolName,
                                    ex.GetType().Name, ex.Message);
                                result = new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                                    $"Tool execution error: {ex.Message}", toolSw.ElapsedMilliseconds);
                                toolsFailed++;
                            }

                            // Emit TOOL_COMPLETED event (sequential path)
                            if (loopConfig.EnableStreaming && _eventEmitter != null)
                            {
                                await _eventEmitter.EmitToolCompleted(
                                    step, toolCall.ToolName, toolCall.Id,
                                    result.Success, result.DurationMs,
                                    workflowInstanceId, cancellationToken);
                            }
                        }
                    }

                    totalToolCalls++;
                    toolsExecuted++;

                    // Sanitize tool output before feeding back to LLM (defense against
                    // indirect prompt injection via file contents, test output, CI logs, etc.)
                    var toolOutput = result.Output;
                    if (_sanitizer != null && !string.IsNullOrEmpty(toolOutput))
                    {
                        var sanitized = _sanitizer.SanitizeInput(toolOutput);
                        toolOutput = sanitized.Result;
                    }
                    toolOutput = ToolOutputHelper.RedactSecrets(toolOutput ?? string.Empty);

                    // Append tool result to conversation history
                    messages.Add(new ConversationMessage
                    {
                        Role = "tool",
                        Content = toolOutput,
                        ToolCallId = toolCall.Id,
                        ToolName = toolCall.ToolName
                    });
                }
            }

            turnSw.Stop();
            _logger?.LogInformation(
                "Tool loop turn completed: WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, ToolsExecuted={ToolsExecuted}, ToolsSucceeded={ToolsSucceeded}, ToolsFailed={ToolsFailed}, TurnDurationMs={TurnDurationMs}, CumulativeTokens={CumulativeTokens}",
                workflowInstanceId, step, toolsExecuted, toolsSucceeded, toolsFailed,
                turnSw.ElapsedMilliseconds, totalPromptTokens + totalCompletionTokens);

            // Emit TURN_COMPLETED event if streaming is enabled
            if (loopConfig.EnableStreaming && _eventEmitter != null)
            {
                await _eventEmitter.EmitTurnCompleted(
                    step, toolsExecuted, turnSw.ElapsedMilliseconds,
                    totalPromptTokens + totalCompletionTokens,
                    workflowInstanceId, cancellationToken);
            }

            // Check if this is the last iteration (we executed tools but won't loop again)
            if (step == loopConfig.MaxSteps - 1)
            {
                exhausted = true;
                loopSw.Stop();
                _logger?.LogWarning(
                    "Tool loop exhausted (maxSteps): WorkflowInstanceId={WorkflowInstanceId}, MaxSteps={MaxSteps}, TotalToolCalls={TotalToolCalls}, TotalTokens={TotalTokens}, TotalDurationMs={TotalDurationMs}",
                    workflowInstanceId, loopConfig.MaxSteps, totalToolCalls,
                    totalPromptTokens + totalCompletionTokens, loopSw.ElapsedMilliseconds);
            }
        }

        // Update token counts on last response to reflect cumulative totals
        lastResponse.PromptTokens = totalPromptTokens;
        lastResponse.CompletionTokens = totalCompletionTokens;

        var totalTokens = totalPromptTokens + totalCompletionTokens;
        var turns = completedTurns;

        return (lastResponse, totalTokens, turns, exhausted);
    }

    // =======================================================================
    // Multi-Turn LLM Call Methods  (moved VERBATIM)
    // =======================================================================

    /// <summary>
    /// Call Anthropic Messages API with a multi-turn conversation history.
    /// </summary>
    private async Task<NormalizedLlmResponse> CallAnthropicMultiTurn(
        HttpClient httpClient, LlmProviderConfig config, string model,
        List<ConversationMessage> messages, int maxTokens, double temperature,
        IReadOnlyList<ResolvedTool>? tools)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(config.BaseUrl)
            ? config.BaseUrl.TrimEnd('/')
            : "https://api.anthropic.com";

        httpClient.DefaultRequestHeaders.Clear();
        httpClient.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);
        httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var requestBody = BuildAnthropicMultiTurnBody(messages, model, maxTokens, temperature, tools);
        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync($"{baseUrl}/v1/messages", content);
        var statusCode = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            return new NormalizedLlmResponse
            {
                Success = false,
                HttpStatusCode = statusCode,
                ErrorMessage = $"Anthropic API error {statusCode}: {Truncate(errorBody)}"
            };
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        return ParseAnthropicResponse(result, statusCode, model);
    }

    /// <summary>
    /// Call OpenAI-compatible API with a multi-turn conversation history.
    /// </summary>
    private async Task<NormalizedLlmResponse> CallOpenAiMultiTurn(
        HttpClient httpClient, LlmProviderConfig config, string model,
        List<ConversationMessage> messages, int maxTokens, double temperature,
        IReadOnlyList<ResolvedTool>? tools)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(config.BaseUrl)
            ? config.BaseUrl.TrimEnd('/')
            : "https://api.openai.com";

        httpClient.DefaultRequestHeaders.Clear();
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");

        var requestBody = BuildOpenAiMultiTurnBody(messages, model, maxTokens, temperature, tools);
        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync($"{baseUrl}/v1/chat/completions", content);
        var statusCode = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            return new NormalizedLlmResponse
            {
                Success = false,
                HttpStatusCode = statusCode,
                ErrorMessage = $"OpenAI-compatible API error {statusCode}: {Truncate(errorBody)}"
            };
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        return ParseOpenAiResponse(result, statusCode, model);
    }

    // =======================================================================
    // Multi-Turn Body Builders  (moved VERBATIM)
    // =======================================================================

    /// <summary>
    /// Build the Anthropic Messages API request body for a multi-turn conversation.
    /// </summary>
    internal Dictionary<string, object> BuildAnthropicMultiTurnBody(
        List<ConversationMessage> messages,
        string model, int maxTokens, double temperature, IReadOnlyList<ResolvedTool>? tools)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature,
        };

        // System prompt goes to top-level "system" field (NOT a message)
        var systemMsg = messages.FirstOrDefault(m => m.Role == "system");
        if (systemMsg != null)
            body["system"] = systemMsg.Content ?? "";

        // Build messages array (skip system message)
        var apiMessages = new List<object>();

        foreach (var msg in messages.Where(m => m.Role != "system"))
        {
            if (msg.Role == "user" && msg.ToolCallId == null)
            {
                apiMessages.Add(new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = msg.Content ?? ""
                });
            }
            else if (msg.Role == "assistant")
            {
                var contentBlocks = new List<object>();

                if (!string.IsNullOrEmpty(msg.Content))
                {
                    contentBlocks.Add(new Dictionary<string, object>
                    {
                        ["type"] = "text",
                        ["text"] = msg.Content
                    });
                }

                if (msg.ToolCalls != null)
                {
                    foreach (var tc in msg.ToolCalls)
                    {
                        object inputObj;
                        try
                        {
                            inputObj = JsonSerializer.Deserialize<object>(tc.ArgumentsJson) ?? new object();
                        }
                        catch
                        {
                            inputObj = new object();
                        }

                        contentBlocks.Add(new Dictionary<string, object>
                        {
                            ["type"] = "tool_use",
                            ["id"] = tc.Id,
                            ["name"] = tc.Name,
                            ["input"] = inputObj
                        });
                    }
                }

                apiMessages.Add(new Dictionary<string, object>
                {
                    ["role"] = "assistant",
                    ["content"] = contentBlocks
                });
            }
            else if (msg.Role == "tool")
            {
                // Anthropic: tool_result blocks go in a user-role message
                var toolResultBlock = new Dictionary<string, object>
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = msg.ToolCallId ?? "",
                    ["content"] = msg.Content ?? ""
                };

                // Batch multiple tool_result blocks into a single user message
                if (apiMessages.Count > 0 &&
                    apiMessages[^1] is Dictionary<string, object> lastMsg &&
                    lastMsg.TryGetValue("role", out var lastRole) &&
                    lastRole is string roleStr && roleStr == "user" &&
                    lastMsg.TryGetValue("content", out var lastContent) &&
                    lastContent is List<object> existingBlocks &&
                    existingBlocks.Count > 0 &&
                    existingBlocks[0] is Dictionary<string, object> firstBlock &&
                    firstBlock.TryGetValue("type", out var blockType) &&
                    blockType is string blockTypeStr && blockTypeStr == "tool_result")
                {
                    existingBlocks.Add(toolResultBlock);
                }
                else
                {
                    apiMessages.Add(new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = new List<object> { toolResultBlock }
                    });
                }
            }
        }

        body["messages"] = apiMessages;

        if (tools != null && tools.Count > 0)
        {
            body["tools"] = tools.Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.InputSchema
            }).ToList();
        }

        return body;
    }

    /// <summary>
    /// Build the OpenAI Chat Completions API request body for a multi-turn conversation.
    /// </summary>
    internal Dictionary<string, object> BuildOpenAiMultiTurnBody(
        List<ConversationMessage> messages,
        string model, int maxTokens, double temperature, IReadOnlyList<ResolvedTool>? tools)
    {
        var apiMessages = new List<object>();

        foreach (var msg in messages)
        {
            if (msg.Role == "system" || (msg.Role == "user" && msg.ToolCallId == null))
            {
                apiMessages.Add(new Dictionary<string, object>
                {
                    ["role"] = msg.Role,
                    ["content"] = msg.Content ?? ""
                });
            }
            else if (msg.Role == "assistant")
            {
                var assistantMsg = new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = msg.Content
                };

                if (msg.ToolCalls != null && msg.ToolCalls.Length > 0)
                {
                    assistantMsg["tool_calls"] = msg.ToolCalls.Select(tc =>
                        new Dictionary<string, object>
                        {
                            ["id"] = tc.Id,
                            ["type"] = "function",
                            ["function"] = new Dictionary<string, object>
                            {
                                ["name"] = tc.Name,
                                ["arguments"] = tc.ArgumentsJson
                            }
                        }).ToList();
                }

                apiMessages.Add(assistantMsg);
            }
            else if (msg.Role == "tool")
            {
                apiMessages.Add(new Dictionary<string, object>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = msg.ToolCallId ?? "",
                    ["content"] = msg.Content ?? ""
                });
            }
        }

        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature,
            ["messages"] = apiMessages
        };

        if (tools != null && tools.Count > 0)
        {
            body["tools"] = tools.Select(t => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.InputSchema
                }
            }).ToList();
        }

        return body;
    }

    // =======================================================================
    // Response Parsers (shared between single-turn and multi-turn)  (moved VERBATIM)
    // =======================================================================

    /// <summary>
    /// Parse an Anthropic Messages API response into a NormalizedLlmResponse.
    /// </summary>
    internal static NormalizedLlmResponse ParseAnthropicResponse(
        JsonElement result, int statusCode, string fallbackModel)
    {
        var responseText = new StringBuilder();
        var toolCalls = new List<LlmToolCall>();

        if (result.TryGetProperty("content", out var contentArr))
        {
            foreach (var block in contentArr.EnumerateArray())
            {
                var blockType = block.GetProperty("type").GetString();
                if (blockType == "text" && block.TryGetProperty("text", out var t))
                    responseText.Append(t.GetString());
                else if (blockType == "tool_use")
                {
                    toolCalls.Add(new LlmToolCall
                    {
                        Id = block.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        ToolName = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        ArgumentsJson = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}"
                    });
                }
            }
        }

        int promptTokens = 0, completionTokens = 0;
        if (result.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var it)) promptTokens = it.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var ot)) completionTokens = ot.GetInt32();
        }

        var stopReason = StopReason.Unknown;
        if (result.TryGetProperty("stop_reason", out var srProp))
        {
            stopReason = srProp.GetString() switch
            {
                "end_turn" => StopReason.EndTurn,
                "tool_use" => StopReason.ToolUse,
                "max_tokens" => StopReason.MaxTokens,
                "stop_sequence" => StopReason.EndTurn,
                _ => StopReason.Unknown
            };
        }

        return new NormalizedLlmResponse
        {
            Success = true,
            ResponseText = responseText.ToString(),
            Model = result.TryGetProperty("model", out var m) ? m.GetString() : fallbackModel,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            HttpStatusCode = statusCode,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
            StopReason = stopReason
        };
    }

    /// <summary>
    /// Parse an OpenAI Chat Completions API response into a NormalizedLlmResponse.
    /// </summary>
    internal static NormalizedLlmResponse ParseOpenAiResponse(
        JsonElement result, int statusCode, string fallbackModel)
    {
        string? responseText = null;
        var toolCalls = new List<LlmToolCall>();
        var stopReason = StopReason.Unknown;

        if (result.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];

            if (firstChoice.TryGetProperty("finish_reason", out var frProp))
            {
                stopReason = frProp.GetString() switch
                {
                    "stop" => StopReason.EndTurn,
                    "tool_calls" => StopReason.ToolUse,
                    "length" => StopReason.MaxTokens,
                    "content_filter" => StopReason.EndTurn,
                    _ => StopReason.Unknown
                };
            }

            var msg = firstChoice.TryGetProperty("message", out var msgEl) ? msgEl : default;
            if (msg.ValueKind != JsonValueKind.Undefined)
            {
                if (msg.TryGetProperty("content", out var c))
                    responseText = c.GetString();
                if (msg.TryGetProperty("tool_calls", out var tcs))
                {
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        var fn = tc.TryGetProperty("function", out var fnProp) ? fnProp : default;
                        toolCalls.Add(new LlmToolCall
                        {
                            Id = tc.TryGetProperty("id", out var tcId) ? tcId.GetString() ?? "" : "",
                            ToolName = fn.ValueKind != JsonValueKind.Undefined && fn.TryGetProperty("name", out var fnN)
                                ? fnN.GetString() ?? "" : "",
                            ArgumentsJson = fn.ValueKind != JsonValueKind.Undefined && fn.TryGetProperty("arguments", out var fnA)
                                ? fnA.GetString() ?? "{}" : "{}"
                        });
                    }
                }
            }
        }

        int promptTokens = 0, completionTokens = 0;
        if (result.TryGetProperty("usage", out var usg))
        {
            if (usg.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
            if (usg.TryGetProperty("completion_tokens", out var ct)) completionTokens = ct.GetInt32();
        }

        return new NormalizedLlmResponse
        {
            Success = true,
            ResponseText = responseText,
            Model = result.TryGetProperty("model", out var modEl) ? modEl.GetString() : fallbackModel,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            HttpStatusCode = statusCode,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
            StopReason = stopReason
        };
    }

    // =======================================================================
    // Single-Turn LLM Call Methods (used by the compaction summarizer)  (moved VERBATIM)
    // =======================================================================

    internal async Task<NormalizedLlmResponse> CallAnthropicMessages(
        HttpClient httpClient, LlmProviderConfig config, string model,
        string systemPrompt, string userPrompt, int maxTokens, double temperature,
        string? toolsJson)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(config.BaseUrl)
            ? config.BaseUrl.TrimEnd('/')
            : "https://api.anthropic.com";

        httpClient.DefaultRequestHeaders.Clear();
        httpClient.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);
        httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature,
            ["system"] = systemPrompt,
            ["messages"] = new[] { new Dictionary<string, string> { ["role"] = "user", ["content"] = userPrompt } }
        };

        if (!string.IsNullOrWhiteSpace(toolsJson))
        {
            try
            {
                var tools = JsonSerializer.Deserialize<List<ResolvedTool>>(toolsJson);
                if (tools != null && tools.Count > 0)
                {
                    requestBody["tools"] = tools.Select(t => new Dictionary<string, object?>
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["input_schema"] = t.InputSchema
                    }).ToList();
                }
            }
            catch { /* ignore malformed tools */ }
        }

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync($"{baseUrl}/v1/messages", content);
        var statusCode = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            return new NormalizedLlmResponse
            {
                Success = false,
                HttpStatusCode = statusCode,
                ErrorMessage = $"Anthropic API error {statusCode}: {Truncate(errorBody)}"
            };
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        return ParseAnthropicResponse(result, statusCode, model);
    }

    internal async Task<NormalizedLlmResponse> CallOpenAiCompatible(
        HttpClient httpClient, LlmProviderConfig config, string model,
        string systemPrompt, string userPrompt, int maxTokens, double temperature,
        string? toolsJson)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(config.BaseUrl)
            ? config.BaseUrl.TrimEnd('/')
            : "https://api.openai.com";

        httpClient.DefaultRequestHeaders.Clear();
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");

        var messages = new List<Dictionary<string, string>>
        {
            new() { ["role"] = "system", ["content"] = systemPrompt },
            new() { ["role"] = "user", ["content"] = userPrompt }
        };

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature,
            ["messages"] = messages
        };

        if (!string.IsNullOrWhiteSpace(toolsJson))
        {
            try
            {
                var tools = JsonSerializer.Deserialize<List<ResolvedTool>>(toolsJson);
                if (tools != null && tools.Count > 0)
                {
                    requestBody["tools"] = tools.Select(t => new Dictionary<string, object?>
                    {
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = t.Name,
                            ["description"] = t.Description,
                            ["parameters"] = t.InputSchema
                        }
                    }).ToList();
                }
            }
            catch { /* ignore */ }
        }

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync($"{baseUrl}/v1/chat/completions", content);
        var statusCode = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            return new NormalizedLlmResponse
            {
                Success = false,
                HttpStatusCode = statusCode,
                ErrorMessage = $"OpenAI-compatible API error {statusCode}: {Truncate(errorBody)}"
            };
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        return ParseOpenAiResponse(result, statusCode, model);
    }

    // =======================================================================
    // Provider Config Resolution (BYOK→platform)  (moved VERBATIM)
    // =======================================================================

    /// <summary>
    /// Load the provider's NON-secret config (BaseUrl / DefaultModel /
    /// TimeoutSeconds). Story 32-3 (AC3): this NO LONGER reads the API key —
    /// the key is resolved separately via <see cref="IProviderCredentialResolver"/>
    /// in <see cref="LoadProviderConfigWithKeyAsync"/>. <see cref="LlmProviderConfig.ApiKey"/>
    /// is always left empty here so the resolver is the single key source.
    /// </summary>
    internal LlmProviderConfig LoadProviderConfig(string providerName)
    {
        // Validate provider name against allowlist
        if (!ProviderAllowlist.IsAllowedDefault(providerName))
        {
            _logger?.LogWarning("Provider '{Provider}' is not in the allowlist, rejecting", providerName);
            return new LlmProviderConfig { Name = providerName, Enabled = false };
        }

        var section = _configuration?.GetSection($"LlmProviders:{providerName}");
        if (section != null && section.Exists())
        {
            var config = new LlmProviderConfig { Name = providerName };
            config.BaseUrl = section["BaseUrl"] ?? "";
            // ApiKey deliberately NOT read here — resolved via IProviderCredentialResolver.
            config.DefaultModel = section["DefaultModel"] ?? "";
            if (int.TryParse(section["TimeoutSeconds"], out var t)) config.TimeoutSeconds = t;
            return config;
        }

        return providerName.ToLowerInvariant() switch
        {
            "anthropic" => new LlmProviderConfig
            {
                Name = providerName,
                BaseUrl = "https://api.anthropic.com",
                DefaultModel = _configuration?["Anthropic:Model"] ?? "claude-sonnet-4-20250514"
            },
            "openai" => new LlmProviderConfig
            {
                Name = providerName,
                BaseUrl = "https://api.openai.com",
                DefaultModel = "gpt-4o"
            },
            "openrouter" => new LlmProviderConfig
            {
                Name = providerName,
                BaseUrl = "https://openrouter.ai/api",
                DefaultModel = "anthropic/claude-sonnet-4-20250514"
            },
            _ => new LlmProviderConfig { Name = providerName }
        };
    }

    /// <summary>
    /// Story 32-3 (AC3) — load the provider config and populate its API key via
    /// the <see cref="IProviderCredentialResolver"/> (BYOK→platform). Returns
    /// the config plus the resolved <c>credentialSource</c> tag
    /// (<c>"byok"</c> | <c>"platform"</c>) for the diagnostic.
    ///
    /// <para>When the resolver is not wired (e.g. the standalone Elsa engine
    /// with no cabinet) the config's ApiKey is left empty and the source is
    /// null — the legacy platform/config call path (AC12) where the operator's
    /// config supplies the key directly. The fail-closed
    /// <c>PROVIDER_CREDENTIAL_UNAVAILABLE</c> guarantee applies whenever the
    /// resolver IS wired.</para>
    /// </summary>
    internal async Task<(LlmProviderConfig Config, string? CredentialSource)>
        LoadProviderConfigWithKeyAsync(
            string providerName, Guid? tenantId, CancellationToken ct)
    {
        var config = LoadProviderConfig(providerName); // BaseUrl / DefaultModel / Timeout only

        if (_credentialResolver is null)
        {
            // No resolver wired — legacy platform path. The HTTP callers treat an
            // empty ApiKey as "no auth header"; behaviour is identical to today
            // for deployments that never set a per-tenant key.
            return (config, null);
        }

        var cred = await _credentialResolver.ResolveAsync(tenantId, providerName, ct)
            .ConfigureAwait(false);

        // Plaintext used immediately for the outbound header; never stored/logged.
        config.ApiKey = cred.ApiKey;
        return (config, cred.Source.ToString().ToLowerInvariant());
    }

    /// <summary>
    /// Resolve the default model for a provider (BaseUrl / DefaultModel / Timeout
    /// only). Public per <see cref="IInlineToolLoopRunner.GetDefaultModel"/>
    /// (Finding I-1) so <c>ManagedAgent</c> can pick the override provider's own
    /// default model instead of a role-resolved model for a different provider.
    /// </summary>
    public string GetDefaultModel(string providerName)
    {
        return LoadProviderConfig(providerName).DefaultModel;
    }

    // =======================================================================
    // Helpers  (moved VERBATIM)
    // =======================================================================

    private static string Truncate(string? s, int max = 500)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        return s.Length > max ? s[..max] + "..." : s;
    }
}
