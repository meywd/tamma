# Story 12.4: Streaming & Parallel Tools — Implementation Plan

## Overview

This plan adds two capabilities on top of the agentic tool loop (Story 12.2):

1. **SSE Streaming**: Real-time progress events during tool loop execution (`TOOL_LOOP.TURN_STARTED`, `TOOL_LOOP.TOOL_EXECUTING`, `TOOL_LOOP.TOOL_COMPLETED`, `TOOL_LOOP.TURN_COMPLETED`)
2. **Parallel Tool Execution**: When the LLM returns multiple tool calls in one turn, independent tools execute concurrently via `Task.WhenAll`, with filesystem tools serialized via per-path semaphores

Both features are opt-in and disabled by default to maintain backward compatibility.

---

## Step-by-Step Implementation Tasks

### Task 1: Define IFileSystemTool Marker Interface

Tools that access the filesystem need to be serialized on the same path to prevent race conditions. Add a marker interface.

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/IFileSystemTool.cs`

```csharp
namespace Tamma.Activities.ToolExecution;

/// <summary>
/// Marker interface for tool executors that access the filesystem.
/// ParallelToolExecutor uses this to serialize access via per-path semaphores.
/// </summary>
public interface IFileSystemTool
{
    /// <summary>
    /// Extract the target file path from the tool call arguments.
    /// Used for per-path locking during parallel execution.
    /// </summary>
    /// <param name="argumentsJson">JSON arguments from the LLM.</param>
    /// <returns>The file path that this tool will access.</returns>
    string GetTargetPath(string argumentsJson);
}
```

**Modify existing tools** to implement `IFileSystemTool`:

- `FileReadTool : IToolExecutor, IFileSystemTool`
- `FileWriteTool : IToolExecutor, IFileSystemTool`

```csharp
// In FileReadTool:
public class FileReadTool : IToolExecutor, IFileSystemTool
{
    // ... existing code ...

    public string GetTargetPath(string argumentsJson)
    {
        var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        return args.GetProperty("path").GetString() ?? "";
    }
}

// In FileWriteTool:
public class FileWriteTool : IToolExecutor, IFileSystemTool
{
    // ... existing code ...

    public string GetTargetPath(string argumentsJson)
    {
        var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        return args.GetProperty("path").GetString() ?? "";
    }
}
```

---

### Task 2: Create ToolLoopEventEmitter

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/ToolLoopEventEmitter.cs`

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Tamma.Activities.ToolExecution;

/// <summary>
/// Emits SSE events for tool loop progress. Events are written to an
/// IToolLoopEventSink (injected). If no sink is active, events are silently dropped.
///
/// Events:
///   TOOL_LOOP.TURN_STARTED   — beginning of a new LLM call turn
///   TOOL_LOOP.TOOL_EXECUTING — tool execution starting
///   TOOL_LOOP.TOOL_COMPLETED — tool execution finished
///   TOOL_LOOP.TURN_COMPLETED — all tools for this turn finished
/// </summary>
public class ToolLoopEventEmitter
{
    private readonly IToolLoopEventSink? _sink;
    private readonly ILogger<ToolLoopEventEmitter> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ToolLoopEventEmitter(
        ILogger<ToolLoopEventEmitter> logger,
        IToolLoopEventSink? sink = null)
    {
        _logger = logger;
        _sink = sink;
    }

    public async Task EmitTurnStarted(int turnNumber, int messageCount, int estimatedTokens)
    {
        await Emit("TOOL_LOOP.TURN_STARTED", new
        {
            turnNumber,
            messageCount,
            estimatedTokens
        });
    }

    public async Task EmitToolExecuting(int turnNumber, string toolName, string toolCallId)
    {
        await Emit("TOOL_LOOP.TOOL_EXECUTING", new
        {
            turnNumber,
            toolName,
            toolCallId
        });
    }

    public async Task EmitToolCompleted(
        int turnNumber, string toolName, string toolCallId,
        bool success, long durationMs)
    {
        await Emit("TOOL_LOOP.TOOL_COMPLETED", new
        {
            turnNumber,
            toolName,
            toolCallId,
            success,
            durationMs
        });
    }

    public async Task EmitTurnCompleted(
        int turnNumber, int totalTools, long totalDurationMs, int cumulativeTokens)
    {
        await Emit("TOOL_LOOP.TURN_COMPLETED", new
        {
            turnNumber,
            totalTools,
            totalDurationMs,
            cumulativeTokens
        });
    }

    private async Task Emit(string eventType, object data)
    {
        if (_sink == null)
        {
            _logger.LogDebug("No SSE sink active, dropping event {EventType}", eventType);
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(data, JsonOptions);
            await _sink.WriteEventAsync(eventType, json);
        }
        catch (Exception ex)
        {
            // SSE failures should never crash the tool loop
            _logger.LogWarning(ex, "Failed to emit SSE event {EventType}", eventType);
        }
    }
}
```

---

### Task 3: Create IToolLoopEventSink Interface

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/IToolLoopEventSink.cs`

```csharp
namespace Tamma.Activities.ToolExecution;

/// <summary>
/// Sink for tool loop SSE events. Implementations:
///   - HttpContextEventSink: writes to HTTP response (SSE)
///   - NullEventSink: discards events (non-streaming mode)
///   - InMemoryEventSink: captures events for testing
///
/// The sink is scoped to a single request/activity execution.
/// </summary>
public interface IToolLoopEventSink
{
    /// <summary>
    /// Write a single SSE event.
    /// </summary>
    /// <param name="eventType">Event type name (e.g. "TOOL_LOOP.TURN_STARTED").</param>
    /// <param name="jsonData">JSON-serialized event data.</param>
    Task WriteEventAsync(string eventType, string jsonData);
}
```

---

### Task 4: Create NullEventSink (Default Non-Streaming Implementation)

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/NullEventSink.cs`

```csharp
namespace Tamma.Activities.ToolExecution;

/// <summary>
/// Default sink that silently discards all events.
/// Used when streaming is not enabled.
/// </summary>
public class NullEventSink : IToolLoopEventSink
{
    public static readonly NullEventSink Instance = new();

    public Task WriteEventAsync(string eventType, string jsonData)
    {
        return Task.CompletedTask;
    }
}
```

---

### Task 5: Create HttpContextEventSink (SSE Implementation)

**File to create**: `apps/tamma-elsa/src/Tamma.ElsaServer/Streaming/HttpContextEventSink.cs`

This lives in ElsaServer (not Activities) because it depends on `HttpContext`.

```csharp
using System.Text;
using Microsoft.AspNetCore.Http;
using Tamma.Activities.ToolExecution;

namespace Tamma.ElsaServer.Streaming;

/// <summary>
/// Writes tool loop events as SSE to the HTTP response stream.
/// Created per-request when streaming is enabled.
/// </summary>
public class HttpContextEventSink : IToolLoopEventSink
{
    private readonly HttpResponse _response;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    public HttpContextEventSink(HttpResponse response)
    {
        _response = response;
    }

    public async Task WriteEventAsync(string eventType, string jsonData)
    {
        await _writeLock.WaitAsync();
        try
        {
            if (!_initialized)
            {
                _response.ContentType = "text/event-stream";
                _response.Headers.CacheControl = "no-cache";
                _response.Headers.Connection = "keep-alive";
                _initialized = true;
            }

            var eventText = $"event: {eventType}\ndata: {jsonData}\n\n";
            var bytes = Encoding.UTF8.GetBytes(eventText);
            await _response.Body.WriteAsync(bytes);
            await _response.Body.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
```

---

### Task 6: Create ParallelToolExecutor

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/ParallelToolExecutor.cs`

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.ToolExecution;

/// <summary>
/// Executes multiple tool calls in parallel with per-path semaphore serialization
/// for filesystem tools.
///
/// Strategy:
///   - Non-filesystem tools: execute immediately in parallel
///   - Filesystem tools (IFileSystemTool): acquire a per-path semaphore first
///   - Each tool gets its own CancellationTokenSource for individual timeout
///   - Results are collected in the same order as the input tool calls
/// </summary>
public class ParallelToolExecutor
{
    private readonly IToolExecutorRegistry _registry;
    private readonly ILogger<ParallelToolExecutor> _logger;

    /// <summary>
    /// Per-path semaphores for filesystem tool serialization.
    /// Scoped to this executor instance (one per activity execution).
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

    /// <summary>
    /// Per-tool execution timeout in seconds.
    /// </summary>
    private readonly int _perToolTimeoutSeconds;

    public ParallelToolExecutor(
        IToolExecutorRegistry registry,
        ILogger<ParallelToolExecutor> logger,
        int perToolTimeoutSeconds = 60)
    {
        _registry = registry;
        _logger = logger;
        _perToolTimeoutSeconds = perToolTimeoutSeconds;
    }

    /// <summary>
    /// Execute tool calls in parallel, serializing filesystem tools on the same path.
    /// </summary>
    /// <param name="toolCalls">Tool calls from the LLM response.</param>
    /// <param name="allowedTools">Allowlist for tool filtering (null = all allowed).</param>
    /// <param name="eventEmitter">Optional SSE event emitter for progress tracking.</param>
    /// <param name="turnNumber">Current turn number for event emission.</param>
    /// <param name="cancellationToken">Parent cancellation token.</param>
    /// <returns>Results in the same order as the input tool calls.</returns>
    public async Task<ToolExecutionResult[]> ExecuteAsync(
        IReadOnlyList<LlmToolCall> toolCalls,
        string[]? allowedTools,
        ToolLoopEventEmitter? eventEmitter,
        int turnNumber,
        CancellationToken cancellationToken = default)
    {
        if (toolCalls.Count == 0)
            return Array.Empty<ToolExecutionResult>();

        // Single tool: skip parallel overhead
        if (toolCalls.Count == 1)
        {
            var tc = toolCalls[0];
            return new[] { await ExecuteSingleTool(tc, allowedTools, eventEmitter, turnNumber, cancellationToken) };
        }

        _logger.LogInformation(
            "Executing {Count} tools in parallel for turn {Turn}",
            toolCalls.Count, turnNumber);

        var tasks = toolCalls.Select(async (tc, index) =>
        {
            return await ExecuteSingleTool(tc, allowedTools, eventEmitter, turnNumber, cancellationToken);
        }).ToArray();

        return await Task.WhenAll(tasks);
    }

    private async Task<ToolExecutionResult> ExecuteSingleTool(
        LlmToolCall toolCall,
        string[]? allowedTools,
        ToolLoopEventEmitter? eventEmitter,
        int turnNumber,
        CancellationToken cancellationToken)
    {
        // Validate allowlist
        if (!_registry.IsAllowed(toolCall.ToolName, allowedTools))
        {
            _logger.LogWarning("Tool '{Tool}' not in allowlist", toolCall.ToolName);
            return new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                $"Tool '{toolCall.ToolName}' is not allowed", 0);
        }

        // Lookup executor
        var executor = _registry.GetExecutor(toolCall.ToolName);
        if (executor == null)
        {
            return new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                $"Unknown tool: '{toolCall.ToolName}'", 0);
        }

        // Emit executing event
        if (eventEmitter != null)
        {
            await eventEmitter.EmitToolExecuting(turnNumber, toolCall.ToolName, toolCall.Id);
        }

        // Create per-tool timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_perToolTimeoutSeconds));

        try
        {
            ToolExecutionResult result;

            // Filesystem tools: serialize via per-path semaphore
            if (executor is IFileSystemTool fsTool)
            {
                var path = NormalizePath(fsTool.GetTargetPath(toolCall.ArgumentsJson));
                var semaphore = _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

                await semaphore.WaitAsync(cts.Token);
                try
                {
                    result = await executor.ExecuteAsync(toolCall.Id, toolCall.ArgumentsJson, cts.Token);
                }
                finally
                {
                    semaphore.Release();
                }
            }
            else
            {
                result = await executor.ExecuteAsync(toolCall.Id, toolCall.ArgumentsJson, cts.Token);
            }

            // Emit completed event
            if (eventEmitter != null)
            {
                await eventEmitter.EmitToolCompleted(
                    turnNumber, toolCall.ToolName, toolCall.Id,
                    result.Success, result.DurationMs);
            }

            return result;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            var result = new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                $"Tool execution timed out after {_perToolTimeoutSeconds}s", 0);

            if (eventEmitter != null)
            {
                await eventEmitter.EmitToolCompleted(
                    turnNumber, toolCall.ToolName, toolCall.Id, false, 0);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool '{Tool}' execution failed", toolCall.ToolName);

            var result = new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                $"Tool execution error: {ex.Message}", 0);

            if (eventEmitter != null)
            {
                await eventEmitter.EmitToolCompleted(
                    turnNumber, toolCall.ToolName, toolCall.Id, false, 0);
            }

            return result;
        }
    }

    /// <summary>
    /// Normalize a file path for use as a dictionary key.
    /// Ensures consistent locking regardless of path format.
    /// </summary>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar)
            .ToLowerInvariant();
    }

    /// <summary>
    /// Dispose of all acquired semaphores.
    /// Should be called when the tool loop activity completes.
    /// </summary>
    public void Dispose()
    {
        foreach (var semaphore in _fileLocks.Values)
        {
            semaphore.Dispose();
        }
        _fileLocks.Clear();
    }
}
```

---

### Task 7: Create StreamingToolCallAccumulator

When streaming is enabled, tool calls arrive as partial deltas that must be accumulated into complete `LlmToolCall` objects.

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/StreamingToolCallAccumulator.cs`

```csharp
using System.Text;
using System.Text.Json;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.ToolExecution;

/// <summary>
/// Accumulates tool call deltas from streaming LLM responses into complete tool calls.
///
/// Anthropic streaming tool calls arrive as:
///   content_block_start: { "type": "tool_use", "id": "toolu_01A", "name": "file_read", "input": {} }
///   content_block_delta: { "type": "input_json_delta", "partial_json": "{\"path\":" }
///   content_block_delta: { "type": "input_json_delta", "partial_json": " \"README.md\"}" }
///   content_block_stop
///
/// OpenAI streaming tool calls arrive as:
///   delta: { "tool_calls": [{ "index": 0, "id": "call_abc", "function": { "name": "file_read", "arguments": "" } }] }
///   delta: { "tool_calls": [{ "index": 0, "function": { "arguments": "{\"path\":" } }] }
///   delta: { "tool_calls": [{ "index": 0, "function": { "arguments": " \"README.md\"}" } }] }
/// </summary>
public class StreamingToolCallAccumulator
{
    private readonly Dictionary<int, AccumulatingToolCall> _toolCalls = new();
    private readonly StringBuilder _textContent = new();
    private string? _stopReason;

    public void AddAnthropicEvent(JsonElement eventData, string eventType)
    {
        switch (eventType)
        {
            case "content_block_start":
                if (eventData.TryGetProperty("content_block", out var block) &&
                    block.TryGetProperty("type", out var type) &&
                    type.GetString() == "tool_use")
                {
                    var index = eventData.TryGetProperty("index", out var idx) ? idx.GetInt32() : _toolCalls.Count;
                    _toolCalls[index] = new AccumulatingToolCall
                    {
                        Id = block.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        Name = block.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                        ArgumentsBuilder = new StringBuilder()
                    };
                }
                break;

            case "content_block_delta":
                if (eventData.TryGetProperty("delta", out var delta))
                {
                    var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : "";
                    if (deltaType == "input_json_delta" &&
                        delta.TryGetProperty("partial_json", out var partial))
                    {
                        var index = eventData.TryGetProperty("index", out var idx2) ? idx2.GetInt32() : _toolCalls.Count - 1;
                        if (_toolCalls.TryGetValue(index, out var tc))
                        {
                            tc.ArgumentsBuilder.Append(partial.GetString());
                        }
                    }
                    else if (deltaType == "text_delta" &&
                             delta.TryGetProperty("text", out var text))
                    {
                        _textContent.Append(text.GetString());
                    }
                }
                break;

            case "message_delta":
                if (eventData.TryGetProperty("delta", out var msgDelta) &&
                    msgDelta.TryGetProperty("stop_reason", out var sr))
                {
                    _stopReason = sr.GetString();
                }
                break;
        }
    }

    public void AddOpenAiDelta(JsonElement delta)
    {
        if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            _textContent.Append(content.GetString());
        }

        if (delta.TryGetProperty("tool_calls", out var toolCallsArr))
        {
            foreach (var tc in toolCallsArr.EnumerateArray())
            {
                var index = tc.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;

                if (!_toolCalls.TryGetValue(index, out var existing))
                {
                    existing = new AccumulatingToolCall { ArgumentsBuilder = new StringBuilder() };
                    _toolCalls[index] = existing;
                }

                if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    existing.Id = id.GetString() ?? "";

                if (tc.TryGetProperty("function", out var fn))
                {
                    if (fn.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                        existing.Name = name.GetString() ?? "";

                    if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
                        existing.ArgumentsBuilder.Append(args.GetString());
                }
            }
        }
    }

    /// <summary>
    /// Get the accumulated tool calls as complete LlmToolCall objects.
    /// </summary>
    public List<LlmToolCall> GetToolCalls()
    {
        return _toolCalls.OrderBy(kv => kv.Key)
            .Select(kv => new LlmToolCall
            {
                Id = kv.Value.Id,
                ToolName = kv.Value.Name,
                ArgumentsJson = kv.Value.ArgumentsBuilder.ToString()
            })
            .Where(tc => !string.IsNullOrEmpty(tc.ToolName))
            .ToList();
    }

    public string GetTextContent() => _textContent.ToString();

    public string? GetStopReason() => _stopReason;

    public void Reset()
    {
        _toolCalls.Clear();
        _textContent.Clear();
        _stopReason = null;
    }

    private class AccumulatingToolCall
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public StringBuilder ArgumentsBuilder { get; set; } = new();
    }
}
```

---

### Task 8: Add Streaming LLM Call Methods

**File**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs`

Add streaming variants of the LLM call methods. These send `stream: true` and parse the SSE response.

```csharp
/// <summary>
/// Call Anthropic Messages API with streaming enabled.
/// Reads SSE events, accumulates tool calls, and returns a complete response.
/// </summary>
private async Task<NormalizedLlmResponse> CallAnthropicStreaming(
    HttpClient httpClient, LlmProviderConfig config, string model,
    List<ConversationMessage> messages, int maxTokens, double temperature,
    List<ResolvedTool>? tools, IToolLoopEventSink? sink)
{
    var baseUrl = !string.IsNullOrWhiteSpace(config.BaseUrl)
        ? config.BaseUrl.TrimEnd('/') : "https://api.anthropic.com";

    httpClient.DefaultRequestHeaders.Clear();
    httpClient.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);
    httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

    var requestBody = BuildAnthropicMultiTurnBody(messages, model, maxTokens, temperature, tools);
    requestBody["stream"] = true;

    var json = JsonSerializer.Serialize(requestBody);
    var content = new StringContent(json, Encoding.UTF8, "application/json");

    var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages")
    {
        Content = content
    };

    var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

    if (!response.IsSuccessStatusCode)
    {
        var errorBody = await response.Content.ReadAsStringAsync();
        return new NormalizedLlmResponse
        {
            Success = false,
            HttpStatusCode = (int)response.StatusCode,
            ErrorMessage = $"Anthropic API error {(int)response.StatusCode}: {Truncate(errorBody)}"
        };
    }

    // Parse SSE stream
    var accumulator = new StreamingToolCallAccumulator();
    var promptTokens = 0;
    var completionTokens = 0;

    using var stream = await response.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(stream);

    while (await reader.ReadLineAsync() is { } line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
            continue;

        var eventLine = line;
        // Read the preceding event type line
        // Anthropic SSE: "event: content_block_delta\ndata: {...}"
        // We need to track the event type

        var dataStr = line[6..]; // strip "data: "
        if (dataStr == "[DONE]") break;

        try
        {
            var eventData = JsonSerializer.Deserialize<JsonElement>(dataStr);
            var eventType = eventData.TryGetProperty("type", out var et)
                ? et.GetString() ?? "" : "";

            accumulator.AddAnthropicEvent(eventData, eventType);

            // Extract usage from message_start and message_delta
            if (eventType == "message_start" &&
                eventData.TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input_tokens", out var it))
                    promptTokens = it.GetInt32();
            }
            else if (eventType == "message_delta" &&
                     eventData.TryGetProperty("usage", out var deltaUsage))
            {
                if (deltaUsage.TryGetProperty("output_tokens", out var ot))
                    completionTokens = ot.GetInt32();
            }
        }
        catch { /* skip malformed events */ }
    }

    var toolCalls = accumulator.GetToolCalls();
    var stopReasonStr = accumulator.GetStopReason();
    var stopReason = stopReasonStr switch
    {
        "end_turn" => StopReason.EndTurn,
        "tool_use" => StopReason.ToolUse,
        "max_tokens" => StopReason.MaxTokens,
        _ => StopReason.Unknown
    };

    return new NormalizedLlmResponse
    {
        Success = true,
        ResponseText = accumulator.GetTextContent(),
        Model = model,
        PromptTokens = promptTokens,
        CompletionTokens = completionTokens,
        HttpStatusCode = (int)response.StatusCode,
        ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
        StopReason = stopReason
    };
}

/// <summary>
/// Call OpenAI Chat Completions API with streaming enabled.
/// </summary>
private async Task<NormalizedLlmResponse> CallOpenAiStreaming(
    HttpClient httpClient, LlmProviderConfig config, string model,
    List<ConversationMessage> messages, int maxTokens, double temperature,
    List<ResolvedTool>? tools, IToolLoopEventSink? sink)
{
    var baseUrl = !string.IsNullOrWhiteSpace(config.BaseUrl)
        ? config.BaseUrl.TrimEnd('/') : "https://api.openai.com";

    httpClient.DefaultRequestHeaders.Clear();
    if (!string.IsNullOrWhiteSpace(config.ApiKey))
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");

    var requestBody = BuildOpenAiMultiTurnBody(messages, model, maxTokens, temperature, tools);
    requestBody["stream"] = true;
    // Request usage in streaming response (OpenAI-specific)
    requestBody["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true };

    var json = JsonSerializer.Serialize(requestBody);
    var content = new StringContent(json, Encoding.UTF8, "application/json");

    var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions")
    {
        Content = content
    };

    var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

    if (!response.IsSuccessStatusCode)
    {
        var errorBody = await response.Content.ReadAsStringAsync();
        return new NormalizedLlmResponse
        {
            Success = false,
            HttpStatusCode = (int)response.StatusCode,
            ErrorMessage = $"OpenAI API error {(int)response.StatusCode}: {Truncate(errorBody)}"
        };
    }

    var accumulator = new StreamingToolCallAccumulator();
    var promptTokens = 0;
    var completionTokens = 0;
    string? finishReason = null;

    using var stream = await response.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(stream);

    while (await reader.ReadLineAsync() is { } line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
            continue;

        var dataStr = line[6..];
        if (dataStr == "[DONE]") break;

        try
        {
            var chunk = JsonSerializer.Deserialize<JsonElement>(dataStr);

            if (chunk.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];

                if (choice.TryGetProperty("finish_reason", out var fr) &&
                    fr.ValueKind == JsonValueKind.String)
                {
                    finishReason = fr.GetString();
                }

                if (choice.TryGetProperty("delta", out var delta))
                {
                    accumulator.AddOpenAiDelta(delta);
                }
            }

            // Usage (in the final chunk when stream_options.include_usage=true)
            if (chunk.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt))
                    promptTokens = pt.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var ct))
                    completionTokens = ct.GetInt32();
            }
        }
        catch { /* skip malformed chunks */ }
    }

    var toolCalls = accumulator.GetToolCalls();
    var stopReason = finishReason switch
    {
        "stop" => StopReason.EndTurn,
        "tool_calls" => StopReason.ToolUse,
        "length" => StopReason.MaxTokens,
        _ => StopReason.Unknown
    };

    return new NormalizedLlmResponse
    {
        Success = true,
        ResponseText = accumulator.GetTextContent(),
        Model = model,
        PromptTokens = promptTokens,
        CompletionTokens = completionTokens,
        HttpStatusCode = (int)response.StatusCode,
        ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
        StopReason = stopReason
    };
}
```

---

### Task 9: Integrate Streaming and Parallel Execution into the Tool Loop

**File**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs`

**Modify `AgenticToolLoop`** (from Story 12.2) to use streaming and parallel execution when enabled:

```csharp
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
    // ... existing setup code ...

    var eventEmitter = loopConfig.EnableStreaming ? _eventEmitter : null;
    var parallelExecutor = _toolRegistry != null
        ? new ParallelToolExecutor(_toolRegistry,
            context.GetRequiredService<ILogger<ParallelToolExecutor>>())
        : null;

    try
    {
        for (var step = 0; step < loopConfig.MaxSteps; step++)
        {
            // ... compaction check (from Story 12.3) ...

            // Emit turn started event
            if (eventEmitter != null)
            {
                var estimatedTokens = TokenEstimator.EstimateTokens(messages);
                await eventEmitter.EmitTurnStarted(step + 1, messages.Count, estimatedTokens);
            }

            // Call LLM (streaming or non-streaming)
            NormalizedLlmResponse response;
            var isAnthropic = providerName.Equals("anthropic", StringComparison.OrdinalIgnoreCase);

            if (loopConfig.EnableStreaming)
            {
                response = isAnthropic
                    ? await CallAnthropicStreaming(httpClient, providerConfig, model,
                        messages, maxTokens, temperature, tools, null)
                    : await CallOpenAiStreaming(httpClient, providerConfig, model,
                        messages, maxTokens, temperature, tools, null);
            }
            else
            {
                response = isAnthropic
                    ? await CallAnthropicMultiTurn(httpClient, providerConfig, model,
                        messages, maxTokens, temperature, tools)
                    : await CallOpenAiMultiTurn(httpClient, providerConfig, model,
                        messages, maxTokens, temperature, tools);
            }

            // ... existing stop check ...

            // Execute tool calls (parallel or sequential)
            var toolCallsList = response.ToolCalls!;

            // Append assistant message
            messages.Add(new ConversationMessage
            {
                Role = "assistant",
                Content = response.ResponseText,
                ToolCalls = toolCallsList.Select(tc =>
                    new ToolCallInfo(tc.Id, tc.ToolName, tc.ArgumentsJson)).ToArray()
            });

            // Execute tools
            ToolExecutionResult[] results;
            if (parallelExecutor != null && toolCallsList.Count > 1)
            {
                // Parallel execution for multiple tools
                results = await parallelExecutor.ExecuteAsync(
                    toolCallsList, loopConfig.AllowedTools,
                    eventEmitter, step + 1, context.CancellationToken);
            }
            else
            {
                // Sequential execution (single tool or no parallel executor)
                var resultsList = new List<ToolExecutionResult>();
                foreach (var toolCall in toolCallsList)
                {
                    // ... existing sequential execution code ...
                    // ... with eventEmitter calls added ...
                }
                results = resultsList.ToArray();
            }

            // Append tool results to conversation
            foreach (var result in results)
            {
                messages.Add(new ConversationMessage
                {
                    Role = "tool",
                    Content = result.Output,
                    ToolCallId = result.ToolCallId,
                    ToolName = result.ToolName
                });
            }

            // Emit turn completed event
            if (eventEmitter != null)
            {
                await eventEmitter.EmitTurnCompleted(
                    step + 1, results.Length,
                    results.Sum(r => r.DurationMs),
                    totalPromptTokens + totalCompletionTokens);
            }

            // ... existing max steps check ...
        }
    }
    finally
    {
        parallelExecutor?.Dispose();
    }

    // ... return ...
}
```

---

### Task 10: Add EnableStreaming to ToolLoopConfig

**File**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs`

Already added in Story 12.1 plan — verify `EnableStreaming` property is present in `ToolLoopConfig`:

```csharp
public record ToolLoopConfig
{
    // ... existing properties ...
    public bool EnableStreaming { get; init; } = false;  // <-- Verify this exists
}
```

---

### Task 11: Register New Services in DI

**File**: `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs`

Add after existing tool execution registrations:

```csharp
// Streaming and parallel tool execution
builder.Services.AddSingleton<Tamma.Activities.ToolExecution.ToolLoopEventEmitter>();
builder.Services.AddScoped<Tamma.Activities.ToolExecution.IToolLoopEventSink>(sp =>
{
    // Default to NullEventSink; HttpContextEventSink is created per-request when streaming
    return Tamma.Activities.ToolExecution.NullEventSink.Instance;
});
```

---

### Task 12: Inject ToolLoopEventEmitter into CallLlmInlineActivity

**File**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs`

Update constructor:

```csharp
private readonly ToolLoopEventEmitter? _eventEmitter;

public CallLlmInlineActivity(
    ILogger<CallLlmInlineActivity>? logger,
    IHttpClientFactory? httpClientFactory,
    IConfiguration? configuration,
    IToolExecutorRegistry? toolRegistry = null,
    ContextCompactor? contextCompactor = null,
    ToolLoopEventEmitter? eventEmitter = null)  // NEW
{
    // ... existing assignments ...
    _eventEmitter = eventEmitter;
}
```

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/IFileSystemTool.cs` | Marker interface for filesystem tools |
| 2 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/IToolLoopEventSink.cs` | SSE event sink interface |
| 3 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/NullEventSink.cs` | No-op sink (non-streaming default) |
| 4 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/ToolLoopEventEmitter.cs` | SSE event emitter for tool loop progress |
| 5 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/ParallelToolExecutor.cs` | Parallel tool execution with file locking |
| 6 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/StreamingToolCallAccumulator.cs` | Accumulates partial tool calls from streaming |
| 7 | `apps/tamma-elsa/src/Tamma.ElsaServer/Streaming/HttpContextEventSink.cs` | HTTP SSE writer |

## Files to Modify

| # | File Path | Changes |
|---|-----------|---------|
| 1 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/FileReadTool.cs` | Implement `IFileSystemTool`, add `GetTargetPath()` |
| 2 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/FileWriteTool.cs` | Implement `IFileSystemTool`, add `GetTargetPath()` |
| 3 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` | Add streaming call methods, inject `ToolLoopEventEmitter`, use `ParallelToolExecutor` in tool loop |
| 4 | `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` | Register `ToolLoopEventEmitter`, `IToolLoopEventSink` |

---

## Anthropic Streaming Format Reference

**Request**: Same as non-streaming but with `"stream": true`.

**Response SSE events** (in order):

```
event: message_start
data: {"type":"message_start","message":{"id":"msg_123","type":"message","role":"assistant","content":[],"model":"claude-sonnet-4-20250514","stop_reason":null,"usage":{"input_tokens":100,"output_tokens":0}}}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"I'll read"}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" the file."}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: content_block_start
data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_01A","name":"file_read","input":{}}}

event: content_block_delta
data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}

event: content_block_delta
data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":" \"README.md\"}"}}

event: content_block_stop
data: {"type":"content_block_stop","index":1}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":50}}

event: message_stop
data: {"type":"message_stop"}
```

Key Anthropic streaming specifics:
- Each content block (text or tool_use) has `start`, `delta`(s), and `stop` events
- Tool call arguments arrive as `input_json_delta` partial strings
- `stop_reason` comes in the `message_delta` event
- Usage `input_tokens` comes in `message_start`, `output_tokens` in `message_delta`

## OpenAI Streaming Format Reference

**Request**: Same but with `"stream": true` and optionally `"stream_options": {"include_usage": true}`.

**Response SSE chunks**:

```
data: {"id":"chatcmpl-abc","object":"chat.completion.chunk","model":"gpt-4o","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

data: {"id":"chatcmpl-abc","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"I'll read"},"finish_reason":null}]}

data: {"id":"chatcmpl-abc","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_xyz","type":"function","function":{"name":"file_read","arguments":""}}]},"finish_reason":null}]}

data: {"id":"chatcmpl-abc","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":"}}]},"finish_reason":null}]}

data: {"id":"chatcmpl-abc","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":" \"README.md\"}"}}]},"finish_reason":null}]}

data: {"id":"chatcmpl-abc","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":100,"completion_tokens":50,"total_tokens":150}}

data: [DONE]
```

Key OpenAI streaming specifics:
- Each chunk has `choices[0].delta` with incremental content
- `tool_calls` arrive as indexed deltas — `index` field links partial updates
- First delta for a tool call includes `id` and `function.name`; subsequent deltas only include `function.arguments`
- `finish_reason` appears in the final chunk before `[DONE]`
- Usage only appears if `stream_options.include_usage` is true

---

## Test Cases

### ParallelToolExecutor Tests

**Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/ParallelToolExecutorTests.cs`

| # | Test Method | Verifies |
|---|-------------|----------|
| 1 | `ExecuteAsync_IndependentTools_RunInParallel` | 3 non-filesystem tools with 100ms delay each complete in ~100ms (not 300ms) |
| 2 | `ExecuteAsync_SameFileTools_Serialized` | 2 FileReadTools on same path execute sequentially (verify ordering via timestamps) |
| 3 | `ExecuteAsync_DifferentFileTools_RunInParallel` | FileRead "a.cs" + FileRead "b.cs" run in parallel (timing check) |
| 4 | `ExecuteAsync_TimeoutCancelsIndividual_OthersComplete` | Tool A takes 200ms, tool B times out at 50ms. Tool A succeeds, Tool B returns timeout error |
| 5 | `ExecuteAsync_AllResultsCollected_InOrder` | 5 tools run, results array matches input order |
| 6 | `ExecuteAsync_DisallowedTool_ErrorWithoutExecution` | Disallowed tool returns error, not called |
| 7 | `ExecuteAsync_SingleTool_NoParallelOverhead` | Single tool skips Task.WhenAll path |
| 8 | `ExecuteAsync_EmptyToolCalls_ReturnsEmpty` | Empty input returns empty array |

### ToolLoopEventEmitter Tests

**Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/ToolLoopEventEmitterTests.cs`

| # | Test Method | Verifies |
|---|-------------|----------|
| 9 | `EmitTurnStarted_WritesToSink` | Sink receives event with correct type and data |
| 10 | `EmitToolExecuting_WritesToSink` | Sink receives TOOL_EXECUTING event |
| 11 | `EmitToolCompleted_WritesToSink` | Sink receives TOOL_COMPLETED event with timing |
| 12 | `EmitTurnCompleted_WritesToSink` | Sink receives TURN_COMPLETED with aggregates |
| 13 | `NullSink_SilentlyDropsEvents` | No exceptions when sink is null |
| 14 | `SinkError_DoesNotCrash` | Exception in sink is caught, logged, not propagated |

### StreamingToolCallAccumulator Tests

**Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/StreamingToolCallAccumulatorTests.cs`

| # | Test Method | Verifies |
|---|-------------|----------|
| 15 | `Anthropic_SingleToolCall_AccumulatesCorrectly` | content_block_start + deltas -> complete tool call |
| 16 | `Anthropic_MultipleToolCalls_AccumulatesByIndex` | Two tool calls with interleaved deltas |
| 17 | `Anthropic_TextAndToolCalls_BothCaptured` | Text deltas and tool deltas accumulated separately |
| 18 | `OpenAi_SingleToolCall_AccumulatesCorrectly` | delta.tool_calls deltas -> complete tool call |
| 19 | `OpenAi_MultipleToolCalls_AccumulatesByIndex` | Multiple indexed tool calls |
| 20 | `Reset_ClearsAllState` | After reset, accumulator is empty |

### Backward Compatibility Tests

| # | Test Method | Verifies |
|---|-------------|----------|
| 21 | `StreamingDisabled_NoSseEvents` | `EnableStreaming=false` produces no SSE events |
| 22 | `SingleToolCall_NoParallelOverhead` | Single tool call takes sequential path |

---

## Verification Steps

1. **Build**: `cd apps/tamma-elsa && dotnet build` — compiles without errors
2. **All tests**: `cd apps/tamma-elsa && dotnet test` — all tests pass including pre-existing
3. **Streaming test**: `dotnet test --filter "Streaming"` — all streaming accumulator tests pass
4. **Parallel test**: `dotnet test --filter "ParallelToolExecutor"` — timing-based tests pass
5. **Manual SSE test**: Invoke tool loop workflow with `enableStreaming=true` via curl:
   ```bash
   curl -N -H "Accept: text/event-stream" \
     http://localhost:5000/api/workflows/llm-call/dispatch \
     -d '{"enableToolLoop": true, "enableStreaming": true, "userPrompt": "Read README.md"}'
   ```
   Verify SSE events appear in the stream.
6. **Parallel timing test**: Trigger a prompt that results in 3 tool calls. Verify wall-clock time is closer to `max(tool_times)` than `sum(tool_times)`.

---

## Risks and Edge Cases

| Risk | Mitigation |
|------|------------|
| **Streaming adds latency to non-streaming callers** | `EnableStreaming=false` by default; when false, non-streaming code paths are used (zero overhead) |
| **SSE connection drops mid-stream** | `ToolLoopEventEmitter` catches and logs all sink exceptions; tool loop continues even if SSE fails |
| **Partial tool call JSON** | `StreamingToolCallAccumulator` accumulates partial JSON; only emits complete tool calls via `GetToolCalls()`. Malformed JSON is skipped. |
| **Race condition on same file** | `SemaphoreSlim(1,1)` per normalized path ensures serialized access; `ConcurrentDictionary` for thread-safe semaphore lookup |
| **Semaphore leak** | `ParallelToolExecutor.Dispose()` in `finally` block cleans up all semaphores |
| **Task.WhenAll partial failure** | Individual tool failures are caught per-task; `Task.WhenAll` collects all results including failures |
| **OpenAI stream_options compatibility** | `stream_options` is supported by OpenAI API; for non-OpenAI providers (OpenRouter, local), usage may not be returned — handle gracefully with 0 tokens |
| **Anthropic SSE event type format** | Anthropic uses `type` field inside data (not SSE `event:` prefix for most events); the accumulator reads from the data JSON `type` field |
| **High-frequency SSE events** | Rate limiting (max 10/sec) can be added later if needed; current implementation emits per-tool events which are typically <5/sec |
| **HttpCompletionOption.ResponseHeadersRead** | Required for streaming — reads response body incrementally. Forgetting this causes the entire response to be buffered, defeating streaming. |

---

## Implementation Order

1. `IFileSystemTool` interface + modify FileReadTool/FileWriteTool
2. `IToolLoopEventSink` + `NullEventSink`
3. `ToolLoopEventEmitter`
4. `StreamingToolCallAccumulator` — pure data accumulation, well-testable
5. `ParallelToolExecutor` — depends on registry + file system tool interface
6. Streaming LLM call methods (`CallAnthropicStreaming`, `CallOpenAiStreaming`)
7. `HttpContextEventSink` (ElsaServer project)
8. Wire into `AgenticToolLoop` — streaming + parallel + events
9. DI registration
10. Tests (accumulator tests first since they're pure logic, then parallel, then integration)
