# Story 12.2: Agentic Tool Loop in CallLlm — Implementation Plan

## Overview

This plan adds the multi-turn agentic tool loop to `CallLlmInlineActivity`. The existing single-turn code path is preserved verbatim behind an `EnableToolLoop=false` guard. The new loop calls the LLM, parses tool calls, executes them via `IToolExecutorRegistry`, appends results to the conversation history, and repeats until the LLM produces a text-only response or `maxSteps` is reached.

---

## Step-by-Step Implementation Tasks

### Task 1: Inject IToolExecutorRegistry into CallLlmInlineActivity

**File**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs`

**Current constructor** (lines 52-59):

```csharp
public CallLlmInlineActivity(
    ILogger<CallLlmInlineActivity>? logger,
    IHttpClientFactory? httpClientFactory,
    IConfiguration? configuration)
```

**Change to**:

```csharp
private readonly IToolExecutorRegistry? _toolRegistry;

public CallLlmInlineActivity(
    ILogger<CallLlmInlineActivity>? logger,
    IHttpClientFactory? httpClientFactory,
    IConfiguration? configuration,
    IToolExecutorRegistry? toolRegistry = null)
{
    _logger = logger;
    _httpClientFactory = httpClientFactory;
    _configuration = configuration;
    _toolRegistry = toolRegistry;
}
```

The `toolRegistry` parameter is nullable with default `null` for backward compatibility with the `[JsonConstructor]` parameterless constructor.

Add the using directive at the top:
```csharp
using Tamma.Activities.ToolExecution;
```

---

### Task 2: Add Input Properties for Tool Loop Configuration

**File**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs`

Add after line 41 (after `AttemptNumberProp`):

```csharp
[Input(Description = "Whether to enable the agentic tool loop")]
public Input<bool> EnableToolLoopProp { get; set; } = new(false);

[Input(Description = "Tool loop configuration JSON (serialized ToolLoopConfig)")]
public Input<string?> ToolLoopConfigJsonProp { get; set; } = default!;
```

---

### Task 3: Extract Single-Turn Logic into a Private Method

**File**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs`

Extract lines 91-155 (the try/catch block inside `ExecuteAsync`) into a new private method:

```csharp
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
    string model)
{
    // ... exact existing code from lines 72-155, unchanged ...
}
```

---

### Task 4: Implement Multi-Turn Conversation History Builder

The conversation history must be serialized differently for Anthropic vs OpenAI. This is the most format-sensitive part.

**Add private methods to `CallLlmInlineActivity`**:

```csharp
/// <summary>
/// Build the Anthropic Messages API request body for a multi-turn conversation.
///
/// Anthropic format:
///   - system prompt is a top-level "system" field (NOT a message)
///   - messages alternate user/assistant
///   - tool results are sent as user-role messages with content blocks:
///     { "role": "user", "content": [{ "type": "tool_result", "tool_use_id": "...", "content": "..." }] }
///   - assistant messages with tool calls have content blocks:
///     [{ "type": "text", "text": "..." }, { "type": "tool_use", "id": "...", "name": "...", "input": {...} }]
/// </summary>
private Dictionary<string, object> BuildAnthropicMultiTurnBody(
    List<ConversationMessage> messages,
    string model, int maxTokens, double temperature, List<ResolvedTool>? tools)
{
    var body = new Dictionary<string, object>
    {
        ["model"] = model,
        ["max_tokens"] = maxTokens,
        ["temperature"] = temperature,
    };

    // System prompt is the first message, goes to top-level "system" field
    var systemMsg = messages.FirstOrDefault(m => m.Role == "system");
    if (systemMsg != null)
        body["system"] = systemMsg.Content ?? "";

    // Build messages array (skip system message)
    var apiMessages = new List<object>();

    foreach (var msg in messages.Where(m => m.Role != "system"))
    {
        if (msg.Role == "user" && msg.ToolCallId == null)
        {
            // Plain user message
            apiMessages.Add(new Dictionary<string, object>
            {
                ["role"] = "user",
                ["content"] = msg.Content ?? ""
            });
        }
        else if (msg.Role == "assistant")
        {
            // Assistant message — may contain text + tool_use blocks
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
                    contentBlocks.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "tool_use",
                        ["id"] = tc.Id,
                        ["name"] = tc.Name,
                        ["input"] = JsonSerializer.Deserialize<object>(tc.ArgumentsJson)
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
            // Tool result — Anthropic: user-role message with tool_result content blocks
            // Multiple tool results for the same turn are batched into one user message
            // Check if previous apiMessage is already a user tool_result batch
            var toolResultBlock = new Dictionary<string, object>
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = msg.ToolCallId ?? "",
                ["content"] = msg.Content ?? ""
            };

            // Anthropic requires all tool_result blocks for one turn in a single user message
            if (apiMessages.Count > 0 &&
                apiMessages.Last() is Dictionary<string, object> lastMsg &&
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

    // Tools definition
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
///
/// OpenAI format:
///   - system prompt is a message with role "system"
///   - tool results use role "tool" with tool_call_id field
///   - assistant messages with tool calls have a "tool_calls" array:
///     [{ "id": "...", "type": "function", "function": { "name": "...", "arguments": "..." } }]
/// </summary>
private Dictionary<string, object> BuildOpenAiMultiTurnBody(
    List<ConversationMessage> messages,
    string model, int maxTokens, double temperature, List<ResolvedTool>? tools)
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
```

---

### Task 5: Implement StopReason Parsing

**Add to the existing Anthropic/OpenAI response parsing code** (or as separate helper methods):

```csharp
/// <summary>
/// Parse Anthropic stop_reason to normalized StopReason enum.
/// Anthropic values: "end_turn", "tool_use", "max_tokens", "stop_sequence"
/// </summary>
private static StopReason ParseAnthropicStopReason(JsonElement result)
{
    if (!result.TryGetProperty("stop_reason", out var sr))
        return StopReason.Unknown;

    return sr.GetString() switch
    {
        "end_turn" => StopReason.EndTurn,
        "tool_use" => StopReason.ToolUse,
        "max_tokens" => StopReason.MaxTokens,
        "stop_sequence" => StopReason.EndTurn,
        _ => StopReason.Unknown
    };
}

/// <summary>
/// Parse OpenAI finish_reason to normalized StopReason enum.
/// OpenAI values: "stop", "tool_calls", "length", "content_filter"
/// </summary>
private static StopReason ParseOpenAiStopReason(JsonElement result)
{
    if (!result.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        return StopReason.Unknown;

    var firstChoice = choices[0];
    if (!firstChoice.TryGetProperty("finish_reason", out var fr))
        return StopReason.Unknown;

    return fr.GetString() switch
    {
        "stop" => StopReason.EndTurn,
        "tool_calls" => StopReason.ToolUse,
        "length" => StopReason.MaxTokens,
        "content_filter" => StopReason.EndTurn,
        _ => StopReason.Unknown
    };
}
```

**Modify the existing `CallAnthropicMessages` and `CallOpenAiCompatible` methods** to populate `StopReason` on the returned `NormalizedLlmResponse`.

In `CallAnthropicMessages` (around line 246), add to the return statement:
```csharp
StopReason = ParseAnthropicStopReason(result)
```

In `CallOpenAiCompatible` (around line 361), add to the return statement:
```csharp
StopReason = ParseOpenAiStopReason(result)
```

---

### Task 6: Create New Multi-Turn LLM Call Methods

These methods replace single-turn calls during the loop. They accept a `List<ConversationMessage>` instead of a single system+user prompt pair.

```csharp
/// <summary>
/// Call Anthropic Messages API with a multi-turn conversation history.
/// </summary>
private async Task<NormalizedLlmResponse> CallAnthropicMultiTurn(
    HttpClient httpClient, LlmProviderConfig config, string model,
    List<ConversationMessage> messages, int maxTokens, double temperature,
    List<ResolvedTool>? tools)
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
    // ... same response parsing as existing CallAnthropicMessages ...
    // ... but with StopReason populated ...
}

/// <summary>
/// Call OpenAI-compatible API with a multi-turn conversation history.
/// </summary>
private async Task<NormalizedLlmResponse> CallOpenAiMultiTurn(
    HttpClient httpClient, LlmProviderConfig config, string model,
    List<ConversationMessage> messages, int maxTokens, double temperature,
    List<ResolvedTool>? tools)
{
    // ... analogous to CallAnthropicMultiTurn but using OpenAI format ...
}
```

**Implementation note**: To avoid duplicating the HTTP call + response parsing logic, consider refactoring the existing `CallAnthropicMessages`/`CallOpenAiCompatible` methods to accept a pre-built request body dictionary. The single-turn methods would build a single-message body and delegate. The multi-turn methods would build a multi-message body and delegate. This reduces code duplication.

---

### Task 7: Implement the Agentic Tool Loop

**This is the core method**. Add to `CallLlmInlineActivity`:

```csharp
/// <summary>
/// Multi-turn agentic tool loop. Calls LLM, executes tools, feeds results back, repeats.
/// </summary>
private async Task<(NormalizedLlmResponse Response, int TotalTokens, int Turns, bool Exhausted)>
    AgenticToolLoop(
        ActivityExecutionContext context,
        string providerName,
        LlmProviderConfig providerConfig,
        string model,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        double temperature,
        List<ResolvedTool>? tools,
        ToolLoopConfig loopConfig)
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
    var exhausted = false;
    NormalizedLlmResponse lastResponse = new() { Success = false, ErrorMessage = "No LLM call made" };

    for (var step = 0; step < loopConfig.MaxSteps; step++)
    {
        _logger?.LogInformation(
            "Tool loop turn {Turn}/{MaxTurns}, messages={MsgCount}, provider={Provider}",
            step + 1, loopConfig.MaxSteps, messages.Count, providerName);

        // Call LLM with full conversation history
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

        lastResponse = response;

        if (!response.Success)
        {
            _logger?.LogWarning("Tool loop LLM call failed on turn {Turn}: {Error}",
                step + 1, response.ErrorMessage);
            break;
        }

        // Accumulate tokens
        totalPromptTokens += response.PromptTokens;
        totalCompletionTokens += response.CompletionTokens;

        // Check if LLM is done (no tool calls, or explicit end_turn)
        if (response.StopReason != StopReason.ToolUse ||
            response.ToolCalls == null ||
            response.ToolCalls.Count == 0)
        {
            _logger?.LogInformation(
                "Tool loop completed on turn {Turn}, stop_reason={StopReason}",
                step + 1, response.StopReason);
            break;
        }

        // Append assistant message to conversation history
        messages.Add(new ConversationMessage
        {
            Role = "assistant",
            Content = response.ResponseText,
            ToolCalls = response.ToolCalls.Select(tc =>
                new ToolCallInfo(tc.Id, tc.ToolName, tc.ArgumentsJson)).ToArray()
        });

        // Execute each tool call
        foreach (var toolCall in response.ToolCalls)
        {
            ToolExecutionResult result;

            if (_toolRegistry == null)
            {
                result = new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                    "Tool execution not available (registry not configured)", 0);
            }
            else if (!_toolRegistry.IsAllowed(toolCall.ToolName, loopConfig.AllowedTools))
            {
                _logger?.LogWarning("Tool '{Tool}' not in allowlist, returning error to LLM",
                    toolCall.ToolName);
                result = new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                    $"Tool '{toolCall.ToolName}' is not allowed. Available tools: {string.Join(", ", loopConfig.AllowedTools ?? Array.Empty<string>())}",
                    0);
            }
            else
            {
                var executor = _toolRegistry.GetExecutor(toolCall.ToolName);
                if (executor == null)
                {
                    result = new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                        $"Unknown tool: '{toolCall.ToolName}'", 0);
                }
                else
                {
                    try
                    {
                        result = await executor.ExecuteAsync(
                            toolCall.Id, toolCall.ArgumentsJson, context.CancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Tool execution failed: {Tool}", toolCall.ToolName);
                        result = new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                            $"Tool execution error: {ex.Message}", 0);
                    }
                }
            }

            // Append tool result to conversation history
            messages.Add(new ConversationMessage
            {
                Role = "tool",
                Content = result.Output,
                ToolCallId = toolCall.Id,
                ToolName = toolCall.ToolName
            });
        }

        // Check if this is the last iteration
        if (step == loopConfig.MaxSteps - 1)
        {
            exhausted = true;
            _logger?.LogWarning(
                "Tool loop reached maxSteps ({MaxSteps}), returning last response",
                loopConfig.MaxSteps);
        }
    }

    // Update token counts on last response
    lastResponse.PromptTokens = totalPromptTokens;
    lastResponse.CompletionTokens = totalCompletionTokens;

    var totalTokens = totalPromptTokens + totalCompletionTokens;
    var turns = messages.Count(m => m.Role == "assistant");

    return (lastResponse, totalTokens, turns, exhausted);
}
```

---

### Task 8: Wire the Tool Loop into ExecuteAsync

**File**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs`

**Replace the current `ExecuteAsync` method** (lines 62-156) with:

```csharp
protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
{
    var inputJson = InputJsonProp.Get(context);
    var providerName = ProviderNameProp.Get(context);
    var systemPrompt = SystemPromptProp.Get(context);
    var toolsJson = ToolsJsonProp.Get(context);
    var attemptNumber = AttemptNumberProp.Get(context);
    var enableToolLoop = EnableToolLoopProp.Get(context);
    var toolLoopConfigJson = ToolLoopConfigJsonProp.Get(context);

    var input = ParseInput(inputJson);

    // ═══ Backward-compatible guard ═══
    // When EnableToolLoop is false, execute the EXACT existing single-turn code path.
    if (!enableToolLoop)
    {
        await SingleTurnCall(context, input, providerName, systemPrompt, toolsJson, attemptNumber,
            input.ModelOverrides.TryGetValue(providerName, out var mo) ? mo : GetDefaultModel(providerName));
        return;
    }

    // ═══ Agentic Tool Loop ═══
    var model = input.ModelOverrides.TryGetValue(providerName, out var mo2) ? mo2 : GetDefaultModel(providerName);
    var providerConfig = LoadProviderConfig(providerName);

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

    try
    {
        var (response, totalTokens, turns, exhausted) = await AgenticToolLoop(
            context, providerName, providerConfig, model, systemPrompt,
            input.UserPrompt, input.MaxTokens, input.Temperature, tools, loopConfig);

        sw.Stop();

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
            CompletionTokens = response.CompletionTokens
        };

        context.SetVariable("LastDiagnostic", JsonSerializer.Serialize(diagnostic));
        context.SetVariable("LastResponse", JsonSerializer.Serialize(response));
        context.SetVariable("ToolLoopTokens", totalTokens);
        context.SetVariable("ToolLoopTurns", turns);
        context.SetVariable("ToolLoopExhausted", exhausted);
    }
    catch (Exception ex)
    {
        sw.Stop();
        _logger?.LogError(ex, "Agentic tool loop failed for {Provider}", providerName);

        var diagnostic = new ProviderAttemptDiagnostic
        {
            ProviderName = providerName,
            Model = model,
            AttemptNumber = attemptNumber,
            Succeeded = false,
            ErrorMessage = $"Tool loop error: {ex.Message}",
            DurationMs = sw.ElapsedMilliseconds,
            StartedAtUtc = startedAt
        };

        context.SetVariable("LastDiagnostic", JsonSerializer.Serialize(diagnostic));
        context.SetVariable("LastResponse", JsonSerializer.Serialize(new NormalizedLlmResponse
        {
            Success = false,
            ErrorMessage = diagnostic.ErrorMessage
        }));
    }
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
```

---

### Task 9: Update LlmCallWorkflow to Propagate Tool Loop Fields

**File**: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs`

**9a. Add workflow variables** (after line 82, in the variables section):

```csharp
var enableToolLoopVar = builder.WithVariable<bool>("EnableToolLoop", false);
var toolLoopConfigJsonVar = builder.WithVariable<string>("ToolLoopConfigJson", "");
var toolLoopTokensVar = builder.WithVariable<int>("ToolLoopTokens", 0);
var toolLoopTurnsVar = builder.WithVariable<int>("ToolLoopTurns", 0);
var toolLoopExhaustedVar = builder.WithVariable<bool>("ToolLoopExhausted", false);
```

**9b. Initialize from input** (in the `initInputs` lambda, around line 99):

Add after `systemPromptOverrideVar.Set(...)`:
```csharp
// Tool loop config
var enableLoop = context.GetInput<bool?>("enableToolLoop") ?? false;
enableToolLoopVar.Set(context, enableLoop);
var loopConfigJson = context.GetInput<string>("toolLoopConfig") ?? "";
toolLoopConfigJsonVar.Set(context, loopConfigJson);
```

**9c. Pass to CallLlmInlineActivity** (in `BuildRetryLoop`, around line 641):

Update the `CallLlmInlineActivity` instantiation:
```csharp
WithLabel(new CallLlmInlineActivity
{
    Id = "CallLlm",
    Name = "Call LLM",
    InputJsonProp = new(context => inputVar.Get(context)),
    ProviderNameProp = new(context => currentProviderVar.Get(context)),
    SystemPromptProp = new(context => resolvedSystemPromptVar.Get(context)),
    ToolsJsonProp = new(context => resolvedToolsJsonVar.Get(context)),
    AttemptNumberProp = new(context => attemptNumberVar.Get(context)),
    EnableToolLoopProp = new(context => enableToolLoopVar.Get(context)),         // NEW
    ToolLoopConfigJsonProp = new(context => toolLoopConfigJsonVar.Get(context))  // NEW
}, "Call LLM"),
```

**9d. Include tool loop fields in workflow output** (in `BuildSuccessOutput`, around line 607):

Add to the `LlmCallWorkflowOutput` construction:
```csharp
ToolLoopTokens = toolLoopTokensVar.Get(context),
ToolLoopTurns = toolLoopTurnsVar.Get(context),
ToolLoopExhausted = toolLoopExhaustedVar.Get(context),
```

**9e. Add output setters** (in the `SetOutputs` sequence, around line 460):

```csharp
WithLabel(new SetOutput
{
    Id = "OutputToolLoopTurns",
    Name = "Output: toolLoopTurns",
    OutputName = new("toolLoopTurns"),
    OutputValue = new(context =>
    {
        var outputJson = workflowOutputVar.Get(context);
        var output = SafeDeserialize<LlmCallWorkflowOutput>(outputJson);
        return (object)(output?.ToolLoopTurns ?? 0);
    })
}, "Output: toolLoopTurns"),
```

**Note**: `BuildRetryLoop` method signature needs new parameters for `enableToolLoopVar` and `toolLoopConfigJsonVar`. Add them to the parameter list and thread them through.

---

### Task 10: Update Response Parsing to Populate StopReason

**File**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs`

In `CallAnthropicMessages` (existing method), add StopReason parsing to the response. Around line 246, add:

```csharp
// After parsing content blocks, before the return statement:
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
    // ... existing fields ...
    StopReason = stopReason  // NEW
};
```

Same for `CallOpenAiCompatible` — parse `choices[0].finish_reason`:

```csharp
var stopReason = StopReason.Unknown;
if (result.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0)
{
    if (ch[0].TryGetProperty("finish_reason", out var frProp))
    {
        stopReason = frProp.GetString() switch
        {
            "stop" => StopReason.EndTurn,
            "tool_calls" => StopReason.ToolUse,
            "length" => StopReason.MaxTokens,
            _ => StopReason.Unknown
        };
    }
}
```

---

## Files to Create

None — this story modifies existing files only.

## Files to Modify

| # | File Path | Specific Changes |
|---|-----------|-----------------|
| 1 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` | Add `IToolExecutorRegistry` injection, `EnableToolLoopProp`/`ToolLoopConfigJsonProp` inputs, `SingleTurnCall()` extraction, `AgenticToolLoop()`, multi-turn body builders, StopReason parsers, multi-turn call methods |
| 2 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs` | Already done in Story 12.1 — verify `EnableToolLoop`, `ToolLoopConfig`, `StopReason`, `ConversationMessage` are present; add `ToolLoopTokens`/`ToolLoopTurns`/`ToolLoopExhausted` to output if not already |
| 3 | `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` | Add variables for tool loop; initialize from input; pass to activity; include in output; add output setters |

---

## Critical Provider Format Details

### Anthropic Multi-Turn Tool Use

**Request** (conversation with tool results):
```json
{
  "model": "claude-sonnet-4-20250514",
  "max_tokens": 4096,
  "system": "You are a helpful assistant.",
  "tools": [
    {
      "name": "file_read",
      "description": "Read a file",
      "input_schema": { "type": "object", "properties": { "path": { "type": "string" } }, "required": ["path"] }
    }
  ],
  "messages": [
    { "role": "user", "content": "Read the README file" },
    {
      "role": "assistant",
      "content": [
        { "type": "text", "text": "I'll read the README file for you." },
        { "type": "tool_use", "id": "toolu_01A", "name": "file_read", "input": { "path": "README.md" } }
      ]
    },
    {
      "role": "user",
      "content": [
        { "type": "tool_result", "tool_use_id": "toolu_01A", "content": "# My Project\nThis is the readme." }
      ]
    }
  ]
}
```

**Response** (with `stop_reason`):
```json
{
  "id": "msg_123",
  "type": "message",
  "role": "assistant",
  "content": [
    { "type": "text", "text": "The README contains..." }
  ],
  "stop_reason": "end_turn",
  "usage": { "input_tokens": 150, "output_tokens": 50 }
}
```

Key Anthropic rules:
- System prompt is NOT a message — it goes in the top-level `"system"` field
- Tool results are `role: "user"` with `content: [{ "type": "tool_result", ... }]`
- Multiple tool results for the same turn MUST be in a single user message with multiple content blocks
- `stop_reason` values: `"end_turn"`, `"tool_use"`, `"max_tokens"`, `"stop_sequence"`
- `tool_use` blocks have `"input"` (object), not `"arguments"` (string)

### OpenAI Multi-Turn Tool Use

**Request**:
```json
{
  "model": "gpt-4o",
  "max_tokens": 4096,
  "tools": [
    {
      "type": "function",
      "function": {
        "name": "file_read",
        "description": "Read a file",
        "parameters": { "type": "object", "properties": { "path": { "type": "string" } }, "required": ["path"] }
      }
    }
  ],
  "messages": [
    { "role": "system", "content": "You are a helpful assistant." },
    { "role": "user", "content": "Read the README file" },
    {
      "role": "assistant",
      "content": "I'll read the README file for you.",
      "tool_calls": [
        {
          "id": "call_abc123",
          "type": "function",
          "function": { "name": "file_read", "arguments": "{\"path\": \"README.md\"}" }
        }
      ]
    },
    {
      "role": "tool",
      "tool_call_id": "call_abc123",
      "content": "# My Project\nThis is the readme."
    }
  ]
}
```

**Response** (with `finish_reason`):
```json
{
  "choices": [
    {
      "index": 0,
      "message": {
        "role": "assistant",
        "content": "The README contains..."
      },
      "finish_reason": "stop"
    }
  ],
  "usage": { "prompt_tokens": 150, "completion_tokens": 50 }
}
```

Key OpenAI rules:
- System prompt IS a message with `role: "system"`
- Tool results use `role: "tool"` with `tool_call_id` field
- Each tool result is a separate message (NOT batched like Anthropic)
- `finish_reason` values: `"stop"`, `"tool_calls"`, `"length"`, `"content_filter"`
- `tool_calls[].function.arguments` is a JSON STRING, not a JSON object

---

## Test Cases

**Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/AgenticToolLoopTests.cs`

### Backward Compatibility Tests

| # | Test Method | Verifies |
|---|-------------|----------|
| 1 | `EnableToolLoopFalse_UsesSingleTurnPath_NoLoopExecuted` | `EnableToolLoop=false` produces identical output to pre-loop behavior; no tool executor calls |
| 2 | `EnableToolLoopFalse_ExistingWorkflowUnchanged` | Existing workflow invocation without tool loop fields works identically |
| 3 | `NullToolRegistry_SingleTurnStillWorks` | When `IToolExecutorRegistry` is not registered, single-turn works fine |

### Loop Termination Tests

| # | Test Method | Verifies |
|---|-------------|----------|
| 4 | `TextOnlyResponse_TerminatesLoop` | LLM response with no tool_calls exits loop immediately |
| 5 | `EndTurnStopReason_TerminatesLoop` | `stop_reason=end_turn` / `finish_reason=stop` exits loop |
| 6 | `MaxStepsReached_TerminatesWithExhausted` | Loop runs exactly `maxSteps` times then exits with `ToolLoopExhausted=true` |
| 7 | `EmptyToolCalls_TerminatesLoop` | `ToolCalls=[]` exits loop |

### Tool Execution Tests

| # | Test Method | Verifies |
|---|-------------|----------|
| 8 | `AllowedTool_ExecutesAndResultFedBack` | Tool call executes, result appears in next LLM call's messages |
| 9 | `DisallowedTool_ErrorReturnedToLlm` | Tool not in allowlist returns error message, loop continues |
| 10 | `UnknownTool_ErrorReturnedToLlm` | Tool not in registry returns error message, loop continues |
| 11 | `ToolException_CaughtAndReturnedAsError` | Tool throws exception, caught, error fed back to LLM |
| 12 | `MultipleToolsInOneTurn_AllExecuted` | LLM returns 3 tool calls, all 3 execute, all 3 results fed back |

### Conversation History Tests

| # | Test Method | Verifies |
|---|-------------|----------|
| 13 | `MessagesAccumulate_Correctly` | After 3 turns: system, user, assistant, tool, assistant, tool, assistant, tool messages present |
| 14 | `SystemPrompt_AlwaysFirst` | System prompt is always the first message regardless of turn count |
| 15 | `AnthropicFormat_ToolResultsAsUserBlocks` | Anthropic serialization produces correct `tool_result` content blocks |
| 16 | `OpenAiFormat_ToolResultsAsToolRole` | OpenAI serialization produces correct `role: "tool"` messages |

### Token Tracking Tests

| # | Test Method | Verifies |
|---|-------------|----------|
| 17 | `TokensAccumulated_AcrossTurns` | 3 turns with 100+50 tokens each = 450 total |
| 18 | `TokensReported_InFinalOutput` | `ToolLoopTokens` in workflow output matches accumulated total |
| 19 | `LlmFailure_PartialTokensStillReported` | If LLM fails on turn 3, tokens from turns 1-2 are still reported |

### Provider Format Tests

| # | Test Method | Verifies |
|---|-------------|----------|
| 20 | `AnthropicStopReason_Parsed_ToolUse` | `"stop_reason": "tool_use"` maps to `StopReason.ToolUse` |
| 21 | `AnthropicStopReason_Parsed_EndTurn` | `"stop_reason": "end_turn"` maps to `StopReason.EndTurn` |
| 22 | `OpenAiFinishReason_Parsed_ToolCalls` | `"finish_reason": "tool_calls"` maps to `StopReason.ToolUse` |
| 23 | `OpenAiFinishReason_Parsed_Stop` | `"finish_reason": "stop"` maps to `StopReason.EndTurn` |

### Integration Tests

| # | Test Method | Verifies |
|---|-------------|----------|
| 24 | `MultiTurnSession_ReadFileThenSummarize` | Mock: LLM calls file_read, gets content, produces summary — 2-turn session |
| 25 | `DisallowedToolRecovery_LlmAdaptsToError` | Mock: LLM tries disallowed tool, gets error, tries allowed tool, succeeds |

---

## Verification Steps

1. **Build**: `cd apps/tamma-elsa && dotnet build` — compiles without errors
2. **Existing tests**: `cd apps/tamma-elsa && dotnet test` — all pre-existing tests still pass (backward compat)
3. **New tests**: `cd apps/tamma-elsa && dotnet test --filter "AgenticToolLoop"` — all 25 new tests pass
4. **Manual test (single-turn)**: Invoke LLM Call workflow without `enableToolLoop` — verify identical behavior
5. **Manual test (tool loop)**: Invoke LLM Call workflow with `enableToolLoop=true` and a prompt that triggers tool calls — verify multi-turn execution
6. **Log verification**: Check ELSA server logs for `"Tool loop turn X/Y"` messages during tool loop execution

---

## Risks and Edge Cases

| Risk | Mitigation |
|------|------------|
| **Backward compatibility regression** | `EnableToolLoop=false` guard + extracted `SingleTurnCall()` method with ZERO changes to existing code |
| **Infinite tool loop** | `MaxSteps=20` hard cap; each iteration logged; `ToolLoopExhausted` flag in output |
| **Token explosion** | Token accumulation tracks usage; Story 12.3 adds compaction at 80% threshold |
| **Anthropic tool_result batching** | Must batch multiple tool results into one user message with content blocks — failure causes 400 error |
| **OpenAI arguments string vs object** | OpenAI `arguments` is a JSON string, Anthropic `input` is an object — must handle both in deserialization |
| **LLM call failure mid-loop** | Failure breaks the loop; partial results (tokens, messages) are still reported |
| **Tool executor timeout vs LLM timeout** | Tool timeout (60s) is separate from LLM call timeout (120s); both enforced |
| **Thread safety of conversation history** | `List<ConversationMessage>` is only accessed within a single activity execution — no concurrent access |
| **HttpClient reuse across turns** | Same `HttpClient` instance reused within the loop — no connection pool issues |
| **JSON serialization of nested objects** | Anthropic `input` field must be a JSON object (not string); OpenAI `arguments` must be a JSON string — test both |

---

## Implementation Order

1. Extract `SingleTurnCall()` method — preserves existing behavior
2. Add `StopReason` parsing to existing Anthropic/OpenAI methods
3. Add `EnableToolLoopProp` / `ToolLoopConfigJsonProp` inputs
4. Inject `IToolExecutorRegistry`
5. Implement `BuildAnthropicMultiTurnBody` / `BuildOpenAiMultiTurnBody`
6. Implement `CallAnthropicMultiTurn` / `CallOpenAiMultiTurn`
7. Implement `AgenticToolLoop`
8. Wire into `ExecuteAsync` with guard
9. Update `LlmCallWorkflow` to propagate fields
10. Write tests (in parallel with steps 5-9)
