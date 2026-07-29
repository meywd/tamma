using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.LlmCall.Tools;
using Tamma.Activities.Security;
using Tamma.Activities.ToolExecution;
using Tamma.Api.Services.Providers;
using Tamma.Core;

namespace Tamma.Api.Services.Agents;

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
    /// <summary>
    /// The runner's <see cref="HttpClient"/> is deliberately a PLAIN client:
    /// every call clears <c>DefaultRequestHeaders</c> and targets an absolute
    /// URL, with base URL / auth header / version header applied per call from
    /// the <see cref="ProviderCatalog"/> descriptor + resolved (BYOK-aware)
    /// provider config. This replaces the phantom
    /// <c>CreateClient($"llm-{provider}")</c> lookup — no <c>llm-*</c> named
    /// client was ever registered, so those names always resolved to
    /// unconfigured default clients; one shared name keeps the exact same
    /// wire behaviour while pooling handlers sanely.
    /// </summary>
    public const string RunnerHttpClientName = "llm-provider";

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;
    private readonly ILogger? _logger;
    private readonly IContentSanitizer? _sanitizer;
    private readonly IToolLoopAutonomyGate _autonomyGate;
    private readonly IToolExecutorRegistry? _toolRegistry;
    private readonly IToolCallValidator? _toolCallValidator;
    private readonly ContextCompactor? _contextCompactor;
    private readonly ToolLoopEventEmitter? _eventEmitter;
    private readonly ParallelToolExecutor? _parallelExecutor;
    private readonly IProviderCredentialResolver? _credentialResolver;
    private readonly IProviderSettingsStore? _settingsStore;

    public InlineToolLoopRunner(
        ILogger<InlineToolLoopRunner>? logger,
        IHttpClientFactory? httpClientFactory,
        IConfiguration? configuration,
        IContentSanitizer? sanitizer,
        // Epic 43 Seam B — the tool-dispatch autonomy gate. REQUIRED, not
        // optional-nullable like every other collaborator on this path: an
        // optional gate would be absent exactly when the optional validator
        // is absent, which is the failure the epic's siting decision forbids
        // (epic README, Seam B). Sits post-sanitization, pre-fork below.
        IToolLoopAutonomyGate autonomyGate,
        IToolExecutorRegistry? toolRegistry = null,
        IToolCallValidator? toolCallValidator = null,
        ContextCompactor? contextCompactor = null,
        ToolLoopEventEmitter? eventEmitter = null,
        ParallelToolExecutor? parallelExecutor = null,
        IProviderCredentialResolver? credentialResolver = null,
        IProviderSettingsStore? settingsStore = null)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _sanitizer = sanitizer;
        _autonomyGate = autonomyGate ?? throw new ArgumentNullException(
            nameof(autonomyGate),
            "The Seam B autonomy gate is a required collaborator (Epic 43): "
            + "pass CatalogDefaultToolLoopAutonomyGate for the behaviour-preserving v1 default.");
        _toolRegistry = toolRegistry;
        _toolCallValidator = toolCallValidator;
        _contextCompactor = contextCompactor;
        _eventEmitter = eventEmitter;
        _parallelExecutor = parallelExecutor;
        _credentialResolver = credentialResolver;
        // Story 46-1 — the persisted-model-selection layer. OPTIONAL (null in
        // the standalone engine and in pre-46 unit-test compositions): with no
        // store, default-model resolution is byte-identical to pre-46-1.
        _settingsStore = settingsStore;
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
        RepairRingPlan? repair,
        CancellationToken ct)
    {
        // The loop already folds the cumulative totals onto the response's
        // PromptTokens/CompletionTokens (their sum == the old TotalTokens), so
        // we discard the redundant scalar and surface the split counts below.
        // Story 39-9 — the loop also runs the deterministic repair ring (when
        // `repair` is supplied) inside the SAME conversation before returning.
        var (response, _, turns, exhausted, contentValid, repairTurns, repairHistory) =
            await AgenticToolLoop(
                provider, providerConfig, model, systemPrompt, userPrompt,
                maxTokens, temperature, tools, loopConfig, correlationId, repair, ct);

        return new InlineToolLoopResult
        {
            Response = response,
            // The loop already writes cumulative totals onto the response;
            // surface them split (their sum equals the old TotalTokens).
            InputTokens = response.PromptTokens,
            OutputTokens = response.CompletionTokens,
            Turns = turns,
            Exhausted = exhausted,
            // Story 39-9 (D1) — repair-ring outcome. All default (ContentValid == null)
            // when no validator was supplied ⇒ behaviour byte-identical to before.
            ContentValid = contentValid,
            RepairTurns = repairTurns,
            RepairHistory = repairHistory,
        };
    }

    // =======================================================================
    // Agentic Tool Loop  (moved VERBATIM from CallLlmInlineActivity)
    // =======================================================================

    /// <summary>
    /// Multi-turn agentic tool loop. Calls LLM, executes tools, feeds results back, repeats.
    /// </summary>
    private async Task<(NormalizedLlmResponse Response, int TotalTokens, int Turns, bool Exhausted,
            bool? ContentValid, int RepairTurns, IReadOnlyList<RepairTurnRecord> RepairHistory)>
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
            RepairRingPlan? repair,
            CancellationToken cancellationToken)
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = systemPrompt },
            new() { Role = "user", Content = userPrompt }
        };

        var httpClient = _httpClientFactory?.CreateClient(RunnerHttpClientName)
                       ?? new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(providerConfig.TimeoutSeconds);

        // ONE dialect decision per loop, from the descriptor catalogue —
        // replaces the three duplicated Equals("anthropic") branches
        // (compaction summarizer, main loop, repair ring). Unknown providers
        // keep the legacy OpenAI-compatible fallback.
        var dialect = ProviderCatalog.ResolveDialect(providerName);

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
                            var summaryResponse = dialect == ProviderWireDialect.Anthropic
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
            if (dialect == ProviderWireDialect.Anthropic)
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

            // ═══ Seam B — the tool-dispatch autonomy gate (Epic 43, Story 43-4) ═══
            // Sited POST-SANITIZATION (the validator above has already applied
            // sanitized arguments onto tc.ArgumentsJson) and PRE-FORK (before the
            // parallel/sequential execution split), and deliberately NOT nested
            // inside the optional validator block — every other dependency on
            // this path is optional-nullable, and a gate that vanished whenever
            // the validator was absent would be the exact siting failure the
            // epic forbids. The gate itself is a required constructor parameter.
            // A denial joins rejectedToolCalls, so the existing machinery below
            // feeds it back to the model as a tool result — no exception, no new
            // plumbing. The outcome is Denied, never RequiresHuman: there is no
            // human wait on this path. The two fail-open allowlist checks
            // further down are untouched — the gate is additive and cannot be
            // defeated by a null allowlist.
            foreach (var tc in response.ToolCalls)
            {
                if (rejectedToolCalls.ContainsKey(tc.Id))
                {
                    continue; // already rejected by the validator — one result per call
                }

                var gateDecision = _autonomyGate.Evaluate(tc.ToolName, tc.ArgumentsJson);
                if (gateDecision.IsDenied)
                {
                    var detail = gateDecision.Reason == "always-human"
                        ? "is configured to always require a person"
                        : $"requires minimum autonomy {gateDecision.MinAutonomy}, above the current autonomy level {gateDecision.Dial}";
                    rejectedToolCalls[tc.Id] =
                        $"Tool call denied by autonomy policy: '{tc.ToolName}'"
                        + (gateDecision.ActionKey is { } k ? $" (action '{k.ToWire()}')" : string.Empty)
                        + $" {detail}. This action cannot run automatically; continue without it.";

                    // A denial under enforcement is never swallowed silently
                    // (epic audit rule; the 43-9 audit event family joins here).
                    _logger?.LogWarning(
                        "Tool call denied by autonomy gate: WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, ToolCallId={ToolCallId}, ToolName={ToolName}, ActionKey={ActionKey}, MinAutonomy={MinAutonomy}, Dial={Dial}, Reason={Reason}",
                        workflowInstanceId, step, tc.Id, tc.ToolName,
                        gateDecision.ActionKey?.ToWire(), gateDecision.MinAutonomy,
                        gateDecision.Dial, gateDecision.Reason);
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

        // ═══ Story 39-9 — deterministic repair ring (AC1, AC2, D3, D9) ═══
        // Runs HERE — the only place the `messages` conversation, the resolved
        // provider config, the model, and the `tools` declarations are all in
        // scope — so validate-then-repair happens inside the SAME conversation
        // before the loop returns. When `repair` is null (no document validation)
        // this whole block is skipped and behaviour is byte-identical to before.
        bool? contentValid = null;
        var repairTurns = 0;
        var repairHistory = new List<RepairTurnRecord>();

        if (repair is not null && lastResponse.Success && !string.IsNullOrEmpty(lastResponse.ResponseText))
        {
            // Preserve the PRODUCED document in the conversation (AC1). The main loop
            // breaks on end_turn WITHOUT appending the final assistant text, so append
            // it here — otherwise the repair turn would ask the model to "fix the
            // document you produced" with that document absent from the history.
            messages.Add(new ConversationMessage { Role = "assistant", Content = lastResponse.ResponseText });

            // Turn 0 — validate the produced document. The validator never throws
            // (a malformed payload yields a synthetic PAYLOAD_NOT_JSON violation).
            var verdict = repair.Validate(lastResponse.ResponseText!);
            contentValid = verdict.IsValid;
            repairHistory.Add(new RepairTurnRecord(0, verdict.IsValid, verdict.Violations));

            while (!verdict.IsValid
                   && repair.RepairEnabled
                   && repairTurns < repair.MaxRepairTurns)
            {
                // Append the harness-generated, redacted repair message to the SAME
                // conversation (D9 — redaction at the append site, since violation
                // messages may quote model output). No system prompt / prior turns
                // are dropped — the conversation is not restarted (AC1).
                var repairMessage = ToolOutputHelper.RedactSecrets(
                    RepairMessageComposer.Compose(verdict.Violations));
                messages.Add(new ConversationMessage { Role = "user", Content = repairMessage });

                // Re-invoke ONCE (D3 — a repair turn is one model call; NO tool
                // execution). Same client/config/tools declarations (Anthropic
                // requires the tools when history carries tool blocks). Repair turns
                // NEVER touch loopConfig.MaxSteps / completedTurns.
                var repairResponse = dialect == ProviderWireDialect.Anthropic
                    ? await CallAnthropicMultiTurn(
                        httpClient, providerConfig, model, messages, maxTokens, temperature, tools)
                    : await CallOpenAiMultiTurn(
                        httpClient, providerConfig, model, messages, maxTokens, temperature, tools);

                repairTurns++;

                // A transport failure DURING a repair turn is orthogonal: it is a
                // provider failure, surfaced exactly as today (breaker/retry
                // semantics apply upstream), and it ends the ring. The content
                // verdict axis is not conflated with the transport axis.
                if (!repairResponse.Success)
                {
                    lastResponse = repairResponse;
                    break;
                }

                // Accumulate the repair-turn token spend so budget accounting stays
                // truthful (these land on the cumulative totals written below).
                totalPromptTokens += repairResponse.PromptTokens;
                totalCompletionTokens += repairResponse.CompletionTokens;
                lastResponse = repairResponse;

                // Preserve the repaired document in the conversation for any further
                // turn (again the produce loop's end_turn break would drop it).
                messages.Add(new ConversationMessage
                {
                    Role = "assistant",
                    Content = repairResponse.ResponseText,
                });

                // Re-validate. A ToolUse stop with no usable text simply re-validates
                // as invalid and consumes the turn (D3) — bounded by MaxRepairTurns.
                verdict = repair.Validate(repairResponse.ResponseText ?? string.Empty);
                contentValid = verdict.IsValid;
                repairHistory.Add(new RepairTurnRecord(repairTurns, verdict.IsValid, verdict.Violations));
            }
        }

        // Update token counts on last response to reflect cumulative totals
        // (INCLUDING any repair-turn spend accumulated above).
        lastResponse.PromptTokens = totalPromptTokens;
        lastResponse.CompletionTokens = totalCompletionTokens;

        var totalTokens = totalPromptTokens + totalCompletionTokens;
        var turns = completedTurns;

        return (lastResponse, totalTokens, turns, exhausted, contentValid, repairTurns, repairHistory);
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

        var descriptor = ProviderCatalog.Resolve(config.Name);
        httpClient.DefaultRequestHeaders.Clear();
        httpClient.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);
        // Version header is per-descriptor DATA (Bedrock would differ); the
        // fallback preserves the legacy constant for descriptor-less configs.
        httpClient.DefaultRequestHeaders.Add(
            descriptor?.VersionHeaderName ?? "anthropic-version",
            descriptor?.VersionHeaderValue ?? "2023-06-01");

        var requestBody = BuildAnthropicMultiTurnBody(messages, model, maxTokens, temperature, tools);
        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var path = ProviderCatalog.ChatPath(descriptor, ProviderWireDialect.Anthropic);
        // F1 — the single path-preserving join (identical bytes here since the
        // Anthropic base URLs carry no path, pinned by the golden tests).
        var response = await httpClient.PostAsync(ProviderCatalog.CombineUrl(baseUrl, path), content);
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
        var descriptor = ProviderCatalog.Resolve(config.Name);
        var (baseUrl, effectiveDescriptor) = ResolveOpenAiCompatibleBase(config, descriptor);

        httpClient.DefaultRequestHeaders.Clear();
        ApplyAuthHeader(httpClient, effectiveDescriptor, config.ApiKey);

        var requestBody = BuildOpenAiMultiTurnBody(messages, model, maxTokens, temperature, tools);
        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var path = ProviderCatalog.ChatPath(effectiveDescriptor, ProviderWireDialect.OpenAiCompatible);
        var response = await httpClient.PostAsync(ProviderCatalog.CombineUrl(baseUrl, path), content);
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
    // Multi-Turn Body Builders — now thin wrappers over the ONE builder per
    // dialect in ProviderRequestShaper (Phase 1 of the provider abstraction).
    // Byte-identity with the pre-refactor inline builders is pinned by
    // ProviderGoldenRequestTests.
    // =======================================================================

    /// <summary>
    /// Build the Anthropic Messages API request body for a multi-turn conversation.
    /// </summary>
    internal Dictionary<string, object?> BuildAnthropicMultiTurnBody(
        List<ConversationMessage> messages,
        string model, int maxTokens, double temperature, IReadOnlyList<ResolvedTool>? tools)
        => ProviderRequestShaper.BuildAnthropicBody(messages, model, maxTokens, temperature, tools);

    /// <summary>
    /// Build the OpenAI Chat Completions API request body for a multi-turn conversation.
    /// </summary>
    internal Dictionary<string, object?> BuildOpenAiMultiTurnBody(
        List<ConversationMessage> messages,
        string model, int maxTokens, double temperature, IReadOnlyList<ResolvedTool>? tools)
        => ProviderRequestShaper.BuildOpenAiCompatibleBody(messages, model, maxTokens, temperature, tools);

    /// <summary>
    /// Apply the descriptor's auth scheme for an OpenAI-compatible call.
    /// Descriptor-less providers keep the legacy Bearer behaviour; the header
    /// is only sent when a key is present (unchanged). Callers pass a null
    /// descriptor when the base URL is a configuration override (see
    /// <see cref="ResolveOpenAiCompatibleBase"/>) so an override always gets
    /// the pre-refactor <c>Authorization: Bearer</c> semantics.
    /// </summary>
    private static void ApplyAuthHeader(
        HttpClient httpClient, ProviderDescriptor? descriptor, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        switch (descriptor?.AuthScheme ?? ProviderAuthScheme.BearerToken)
        {
            case ProviderAuthScheme.AnthropicApiKey:
                httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
                if (descriptor?.VersionHeaderName is not null)
                    httpClient.DefaultRequestHeaders.Add(
                        descriptor.VersionHeaderName, descriptor.VersionHeaderValue);
                break;
            default:
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
                break;
        }
    }

    /// <summary>
    /// F3/F4 — resolve the effective base URL for an OpenAI-compatible call,
    /// plus the descriptor whose <c>ChatEndpointPath</c>/auth-scheme should
    /// shape the request (null ⇒ dialect defaults).
    ///
    /// <para><b>Config-override rule (F3):</b> a descriptor's
    /// <c>ChatEndpointPath</c> and auth scheme describe the provider's OWN
    /// endpoint at its OWN <c>DefaultBaseUrl</c>. When the config supplies an
    /// explicit BaseUrl override (e.g. <c>LlmProviders:gemini:BaseUrl</c>
    /// pointing at an OpenAI-compatible proxy), the pre-refactor semantics are
    /// restored exactly: <c>{base}/v1/chat/completions</c> with
    /// <c>Authorization: Bearer</c> — the descriptor's provider-specific path
    /// and auth scheme apply only when its own default base URL is in use.
    /// (The Anthropic dialect is untouched by this rule: its config-override
    /// behaviour is byte-identical to before.)</para>
    ///
    /// <para><b>Fail-loud rule (F4):</b> a known provider whose descriptor has
    /// no default base URL (azure-openai's per-resource endpoint) and no
    /// configured BaseUrl throws a <see cref="TammaError"/> naming the missing
    /// config key — the legacy silent fallback sent the provider's key to
    /// api.openai.com as a Bearer token. Descriptor-less (config-only)
    /// providers keep the legacy fallback.</para>
    /// </summary>
    private static (string BaseUrl, ProviderDescriptor? EffectiveDescriptor)
        ResolveOpenAiCompatibleBase(LlmProviderConfig config, ProviderDescriptor? descriptor)
    {
        if (!string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            var configured = config.BaseUrl.TrimEnd('/');
            var isOverride = descriptor is null
                || !ProviderCatalog.IsDefaultBaseUrl(descriptor, configured);
            return (configured, isOverride ? null : descriptor);
        }

        if (descriptor is not null)
        {
            if (string.IsNullOrWhiteSpace(descriptor.DefaultBaseUrl))
            {
                throw new TammaError(
                    "PROVIDER.BASE_URL.MISSING",
                    $"Provider '{descriptor.Key}' has no default base URL (its endpoint " +
                    $"is deployment-specific) and none was configured. Set " +
                    $"'LlmProviders:{descriptor.Key}:BaseUrl' (or " +
                    $"'{descriptor.ConfigSection}:BaseUrl' for the dispatch client) " +
                    "before calling this provider.",
                    new Dictionary<string, object?>
                    {
                        ["provider"] = descriptor.Key,
                        ["configKey"] = $"LlmProviders:{descriptor.Key}:BaseUrl",
                    },
                    retryable: false,
                    severity: TammaErrorSeverity.High);
            }

            return (descriptor.DefaultBaseUrl.TrimEnd('/'), descriptor);
        }

        // Descriptor-less (config-only) provider — legacy fallback preserved.
        return ("https://api.openai.com", null);
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

        var descriptor = ProviderCatalog.Resolve(config.Name);
        httpClient.DefaultRequestHeaders.Clear();
        httpClient.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);
        httpClient.DefaultRequestHeaders.Add(
            descriptor?.VersionHeaderName ?? "anthropic-version",
            descriptor?.VersionHeaderValue ?? "2023-06-01");

        var requestBody = ProviderRequestShaper.BuildAnthropicBody(
            new List<ConversationMessage>
            {
                new() { Role = "system", Content = systemPrompt },
                new() { Role = "user", Content = userPrompt },
            },
            model, maxTokens, temperature, ParseToolsJson(toolsJson));

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var path = ProviderCatalog.ChatPath(descriptor, ProviderWireDialect.Anthropic);
        // F1 — the single path-preserving join (identical bytes here since the
        // Anthropic base URLs carry no path, pinned by the golden tests).
        var response = await httpClient.PostAsync(ProviderCatalog.CombineUrl(baseUrl, path), content);
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
        var descriptor = ProviderCatalog.Resolve(config.Name);
        var (baseUrl, effectiveDescriptor) = ResolveOpenAiCompatibleBase(config, descriptor);

        httpClient.DefaultRequestHeaders.Clear();
        ApplyAuthHeader(httpClient, effectiveDescriptor, config.ApiKey);

        var requestBody = ProviderRequestShaper.BuildOpenAiCompatibleBody(
            new List<ConversationMessage>
            {
                new() { Role = "system", Content = systemPrompt },
                new() { Role = "user", Content = userPrompt },
            },
            model, maxTokens, temperature, ParseToolsJson(toolsJson));

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var path = ProviderCatalog.ChatPath(effectiveDescriptor, ProviderWireDialect.OpenAiCompatible);
        var response = await httpClient.PostAsync(ProviderCatalog.CombineUrl(baseUrl, path), content);
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
    /// Platform-scope overload — see <see cref="LoadProviderConfig(string, Guid?)"/>.
    /// </summary>
    internal LlmProviderConfig LoadProviderConfig(string providerName)
        => LoadProviderConfig(providerName, tenantId: null);

    /// <summary>
    /// Story 46-1 (AC3) — tenant-aware provider config load. BaseUrl /
    /// TimeoutSeconds resolution is UNCHANGED from the pre-46-1 shape;
    /// DefaultModel resolves through the four-step precedence in
    /// <see cref="ResolveDefaultModel"/> (tenant/user override → platform DB →
    /// config → descriptor). With no settings store wired — or no rows saved —
    /// the output is byte-identical to the pre-46-1 resolution.
    /// </summary>
    internal LlmProviderConfig LoadProviderConfig(string providerName, Guid? tenantId)
    {
        // F5 — the allowlist carries CANONICAL keys only; catalogue aliases
        // ("kimi" → moonshot, "z.ai"/"zai" → z-ai, "anthropic-claude" →
        // anthropic) are lookup spellings, deliberately NOT allowlist entries.
        // Normalize alias → canonical key via the catalogue BEFORE the
        // allowlist check, and use the canonical key for everything downstream
        // (config section, catalogue fallback, credential resolution).
        var canonicalName = ProviderCatalog.Resolve(providerName)?.Key
            ?? ProviderCatalog.ResolveNonHttp(providerName)?.Key
            ?? providerName;

        // Validate the canonical provider name against the allowlist
        if (!ProviderAllowlist.IsAllowedDefault(canonicalName))
        {
            _logger?.LogWarning("Provider '{Provider}' is not in the allowlist, rejecting", providerName);
            return new LlmProviderConfig { Name = providerName, Enabled = false };
        }

        var resolvedModel = ResolveDefaultModel(canonicalName, tenantId).Model;

        var section = _configuration?.GetSection($"LlmProviders:{canonicalName}");
        if (section != null && section.Exists())
        {
            var config = new LlmProviderConfig { Name = canonicalName };
            config.BaseUrl = section["BaseUrl"] ?? "";
            // ApiKey deliberately NOT read here — resolved via IProviderCredentialResolver.
            config.DefaultModel = resolvedModel;
            if (int.TryParse(section["TimeoutSeconds"], out var t)) config.TimeoutSeconds = t;
            return config;
        }

        // No config section — fall back to the provider catalogue (Phase 1 of
        // the provider abstraction). This replaces a hardcoded three-arm
        // switch (anthropic / openai / openrouter, preserved byte-identical in
        // their descriptors) and is what makes the previously-unreachable
        // allow-listed providers (z-ai/GLM, deepseek, moonshot, together,
        // groq, …) callable through the runner without a config section.
        var descriptor = ProviderCatalog.Resolve(canonicalName);
        if (descriptor is null)
        {
            return new LlmProviderConfig { Name = canonicalName, DefaultModel = resolvedModel };
        }

        return new LlmProviderConfig
        {
            Name = canonicalName,
            BaseUrl = descriptor.DefaultBaseUrl,
            DefaultModel = resolvedModel,
        };
    }

    /// <summary>
    /// Story 46-1 (AC3, plan D4) — THE single implementation of the
    /// default-model precedence (epic 46 D2, binding):
    /// <b>tenant/user override → platform DB row → configuration → descriptor</b>.
    /// Called from <see cref="LoadProviderConfig(string, Guid?)"/> for every
    /// egress path AND surfaced (with provenance) to the settings endpoints —
    /// one implementation, two consumers, no restatement (the 43-1 lesson).
    ///
    /// <para><b>The config step preserves the pre-46-1 shape VERBATIM</b>
    /// (pinned by the no-row golden-comparison test): when the
    /// <c>LlmProviders:{key}</c> section EXISTS, the config answer is
    /// <c>section["DefaultModel"] ?? ""</c> — including the empty string (the
    /// legacy early return never fell through to the descriptor, and "" keeps
    /// meaning "caller must specify"). When the section does NOT exist,
    /// anthropic honours the legacy <c>Anthropic:Model</c> key, then the
    /// descriptor default. DB rows sit ABOVE all of that (epic D2: a UI choice
    /// takes effect without a deploy; config silently outranking the UI would
    /// make the UI a lie) and are never empty (validated on write).</para>
    /// </summary>
    /// <param name="canonicalName">Canonical provider key (alias-normalized).</param>
    /// <param name="tenantId">Tenant context when the caller has one; the
    /// store maps single-user installs to the sole user's row internally.</param>
    internal ProviderDefaultModelResolution ResolveDefaultModel(
        string canonicalName, Guid? tenantId)
    {
        // 1) Principal override (tenant row in SaaS / sole user's row in
        //    single-user mode). Store rows are validated non-empty on write.
        var overrideModel = _settingsStore?.TryGetModel(canonicalName, tenantId);
        if (!string.IsNullOrWhiteSpace(overrideModel))
        {
            return new ProviderDefaultModelResolution(overrideModel!, "tenant-override");
        }

        return ResolveBelowPrincipal(canonicalName);
    }

    /// <summary>
    /// Steps 2–4 of the precedence chain (platform DB → config → descriptor) —
    /// the resolution AS IF no principal override row existed. Extracted
    /// verbatim from <see cref="ResolveDefaultModel"/> so the tenant model
    /// routes can surface the fallback a reset would land on
    /// (<c>fallbackModel</c> — bug
    /// 2026-07-27-tenant-surface-cannot-name-platform-default-under-override)
    /// without restating the chain.
    /// </summary>
    private ProviderDefaultModelResolution ResolveBelowPrincipal(string canonicalName)
    {
        // 2) Platform DB row.
        var platformModel = _settingsStore?.TryGetPlatformModel(canonicalName);
        if (!string.IsNullOrWhiteSpace(platformModel))
        {
            return new ProviderDefaultModelResolution(platformModel!, "platform-db");
        }

        // 3) Configuration — the pre-46-1 resolution, shape-preserved.
        var section = _configuration?.GetSection($"LlmProviders:{canonicalName}");
        if (section != null && section.Exists())
        {
            return new ProviderDefaultModelResolution(section["DefaultModel"] ?? "", "config");
        }

        var descriptor = ProviderCatalog.Resolve(canonicalName);
        if (descriptor is null)
        {
            return new ProviderDefaultModelResolution("", "descriptor");
        }

        if (string.Equals(descriptor.Key, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            // Legacy Anthropic:Model (anthropic-only, no-section branch only —
            // exactly where the pre-46-1 code consulted it).
            var legacy = _configuration?["Anthropic:Model"];
            if (legacy is not null)
            {
                return new ProviderDefaultModelResolution(legacy, "config");
            }
        }

        // 4) Descriptor default (may be "" — "caller must always specify").
        return new ProviderDefaultModelResolution(descriptor.DefaultModel, "descriptor");
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
        // Story 46-1 — the tenant context flows into the default-model
        // resolution (tenant override → platform DB → config → descriptor).
        var config = LoadProviderConfig(providerName, tenantId); // BaseUrl / DefaultModel / Timeout only

        if (_credentialResolver is null)
        {
            // No resolver wired — legacy platform path. The HTTP callers treat an
            // empty ApiKey as "no auth header"; behaviour is identical to today
            // for deployments that never set a per-tenant key.
            return (config, null);
        }

        // F5 — resolve the credential under the CANONICAL key (config.Name is
        // already alias-normalized by LoadProviderConfig), so "kimi"/"z.ai"
        // spellings hit the same cabinet rows and allowlist entry as
        // moonshot/z-ai instead of failing the resolver's own allowlist check.
        var cred = await _credentialResolver.ResolveAsync(tenantId, config.Name, ct)
            .ConfigureAwait(false);

        // Plaintext used immediately for the outbound header; never stored/logged.
        config.ApiKey = cred.ApiKey;
        return (config, cred.Source.ToString().ToLowerInvariant());
    }

    /// <summary>
    /// Resolve the default model for a provider (BaseUrl / DefaultModel / Timeout
    /// only). Public per <see cref="IInlineToolLoopRunner.GetDefaultModel(string)"/>
    /// (Finding I-1) so <c>ManagedAgent</c> can pick the override provider's own
    /// default model instead of a role-resolved model for a different provider.
    /// No-tenant overload (Story 46-1): in SaaS mode this resolves the
    /// platform-DB → config → descriptor legs; in SINGLE-USER mode the store
    /// maps the null tenant id to the sole user's override row first (plan D3,
    /// deliberate) — so "platform-scope only" would be wrong there. See the
    /// interface doc on <see cref="IInlineToolLoopRunner.GetDefaultModel(string)"/>.
    /// </summary>
    public string GetDefaultModel(string providerName)
    {
        return GetDefaultModel(providerName, tenantId: null);
    }

    /// <summary>
    /// Story 46-1 (AC3) — tenant-aware default model: full four-step precedence
    /// (tenant/user override → platform DB → config → descriptor). Same
    /// empty-string contract as the platform overload.
    /// </summary>
    public string GetDefaultModel(string providerName, Guid? tenantId)
    {
        return LoadProviderConfig(providerName, tenantId).DefaultModel;
    }

    /// <summary>
    /// Story 46-1 — the default-model resolution WITH provenance, for the
    /// settings endpoints' <c>source</c> field (one implementation — this is
    /// the same <see cref="ResolveDefaultModel"/> the egress paths use).
    /// Alias-normalizes and allowlist-guards like <see cref="LoadProviderConfig(string, Guid?)"/>;
    /// a non-allowlisted key resolves to an empty model with source
    /// <c>"descriptor"</c>.
    /// </summary>
    public ProviderDefaultModelResolution ResolveDefaultModelWithSource(
        string providerName, Guid? tenantId)
    {
        return ResolveDefaultModelWithSource(providerName, tenantId, skipPrincipal: false);
    }

    /// <summary>
    /// Skip-principal overload (bug
    /// 2026-07-27-tenant-surface-cannot-name-platform-default-under-override):
    /// with <paramref name="skipPrincipal"/> <c>true</c> the principal
    /// (tenant/user override) leg is excluded REGARDLESS of mode — including
    /// the sole user's row in single-user mode, which a mere
    /// <c>tenantId: null</c> would still consult — so the answer is the
    /// platform DB → config → descriptor resolution a removed override would
    /// fall back to. <paramref name="tenantId"/> is then irrelevant (the
    /// remaining legs are principal-agnostic). <c>false</c> is byte-identical
    /// to the two-argument overload.
    /// </summary>
    public ProviderDefaultModelResolution ResolveDefaultModelWithSource(
        string providerName, Guid? tenantId, bool skipPrincipal)
    {
        var canonicalName = ProviderCatalog.Resolve(providerName)?.Key
            ?? ProviderCatalog.ResolveNonHttp(providerName)?.Key
            ?? providerName;
        if (!ProviderAllowlist.IsAllowedDefault(canonicalName))
        {
            return new ProviderDefaultModelResolution("", "descriptor");
        }
        return skipPrincipal
            ? ResolveBelowPrincipal(canonicalName)
            : ResolveDefaultModel(canonicalName, tenantId);
    }

    // =======================================================================
    // Helpers  (moved VERBATIM)
    // =======================================================================

    /// <summary>
    /// Parse the single-turn callers' serialized tools payload. Preserves the
    /// legacy semantics exactly: null/blank or malformed JSON yields no tools
    /// (the request simply omits the <c>tools</c> field).
    /// </summary>
    private static List<ResolvedTool>? ParseToolsJson(string? toolsJson)
    {
        if (string.IsNullOrWhiteSpace(toolsJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<List<ResolvedTool>>(toolsJson);
        }
        catch
        {
            return null; // ignore malformed tools
        }
    }

    private static string Truncate(string? s, int max = 500)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        return s.Length > max ? s[..max] + "..." : s;
    }
}
