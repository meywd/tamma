using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Tamma.Activities.ToolExecution;

/// <summary>
/// Emits structured progress events during the agentic tool loop.
/// Events are sent to the configured <see cref="IToolLoopEventSink"/>.
/// When no sink is configured, events are silently dropped via <see cref="NullToolLoopEventSink"/>.
/// </summary>
public class ToolLoopEventEmitter
{
    private readonly ILogger<ToolLoopEventEmitter> _logger;
    private readonly IToolLoopEventSink _sink;

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
        _sink = sink ?? NullToolLoopEventSink.Instance;
    }

    /// <summary>
    /// Emit a TOOL_LOOP.TURN_STARTED event at the beginning of each tool loop iteration.
    /// </summary>
    public async Task EmitTurnStarted(
        int turnNumber, int messageCount, int estimatedTokens,
        string? workflowInstanceId = null, CancellationToken cancellationToken = default)
    {
        var data = new
        {
            turnNumber,
            messageCount,
            estimatedTokens
        };

        _logger.LogDebug(
            "SSE event emitted: {EventType}, TurnNumber={TurnNumber}, WorkflowInstanceId={WorkflowInstanceId}",
            "TOOL_LOOP.TURN_STARTED", turnNumber, workflowInstanceId);

        await _sink.WriteEventAsync("TOOL_LOOP.TURN_STARTED", data, cancellationToken);
    }

    /// <summary>
    /// Emit a TOOL_LOOP.TOOL_EXECUTING event when a tool begins execution.
    /// </summary>
    public async Task EmitToolExecuting(
        int turnNumber, string toolName, string toolCallId,
        string? workflowInstanceId = null, CancellationToken cancellationToken = default)
    {
        var data = new
        {
            turnNumber,
            toolName,
            toolCallId
        };

        _logger.LogDebug(
            "SSE event emitted: {EventType}, TurnNumber={TurnNumber}, ToolName={ToolName}, WorkflowInstanceId={WorkflowInstanceId}",
            "TOOL_LOOP.TOOL_EXECUTING", turnNumber, toolName, workflowInstanceId);

        await _sink.WriteEventAsync("TOOL_LOOP.TOOL_EXECUTING", data, cancellationToken);
    }

    /// <summary>
    /// Emit a TOOL_LOOP.TOOL_COMPLETED event when a tool finishes execution.
    /// </summary>
    public async Task EmitToolCompleted(
        int turnNumber, string toolName, string toolCallId,
        bool success, long durationMs,
        string? workflowInstanceId = null, CancellationToken cancellationToken = default)
    {
        var data = new
        {
            turnNumber,
            toolName,
            toolCallId,
            success,
            durationMs
        };

        _logger.LogDebug(
            "SSE event emitted: {EventType}, TurnNumber={TurnNumber}, ToolName={ToolName}, Success={Success}, DurationMs={DurationMs}, WorkflowInstanceId={WorkflowInstanceId}",
            "TOOL_LOOP.TOOL_COMPLETED", turnNumber, toolName, success, durationMs, workflowInstanceId);

        await _sink.WriteEventAsync("TOOL_LOOP.TOOL_COMPLETED", data, cancellationToken);
    }

    /// <summary>
    /// Emit a TOOL_LOOP.TURN_COMPLETED event at the end of each tool loop iteration.
    /// </summary>
    public async Task EmitTurnCompleted(
        int turnNumber, int totalTools, long totalDurationMs, int cumulativeTokens,
        string? workflowInstanceId = null, CancellationToken cancellationToken = default)
    {
        var data = new
        {
            turnNumber,
            totalTools,
            totalDurationMs,
            cumulativeTokens
        };

        _logger.LogDebug(
            "SSE event emitted: {EventType}, TurnNumber={TurnNumber}, TotalTools={TotalTools}, TotalDurationMs={TotalDurationMs}, WorkflowInstanceId={WorkflowInstanceId}",
            "TOOL_LOOP.TURN_COMPLETED", turnNumber, totalTools, totalDurationMs, workflowInstanceId);

        await _sink.WriteEventAsync("TOOL_LOOP.TURN_COMPLETED", data, cancellationToken);
    }

    /// <summary>
    /// Emit a TOOL_LOOP.COMPLETED event when the entire tool loop finishes.
    /// </summary>
    public async Task EmitLoopCompleted(
        int totalTurns, int totalToolCalls, long totalDurationMs, int totalTokens,
        bool exhausted, string? workflowInstanceId = null, CancellationToken cancellationToken = default)
    {
        var data = new
        {
            totalTurns,
            totalToolCalls,
            totalDurationMs,
            totalTokens,
            exhausted
        };

        _logger.LogDebug(
            "SSE event emitted: {EventType}, TotalTurns={TotalTurns}, TotalToolCalls={TotalToolCalls}, Exhausted={Exhausted}, WorkflowInstanceId={WorkflowInstanceId}",
            "TOOL_LOOP.COMPLETED", totalTurns, totalToolCalls, exhausted, workflowInstanceId);

        await _sink.WriteEventAsync("TOOL_LOOP.COMPLETED", data, cancellationToken);
    }
}
