namespace Tamma.Activities.ToolExecution;

/// <summary>
/// Sink for tool loop progress events. Implementations may write events to SSE,
/// message queues, or simply discard them.
/// </summary>
public interface IToolLoopEventSink
{
    /// <summary>
    /// Write a structured event. The eventType follows the pattern
    /// TOOL_LOOP.TURN_STARTED, TOOL_LOOP.TOOL_EXECUTING, etc.
    /// </summary>
    /// <param name="eventType">Event type name.</param>
    /// <param name="data">Serializable event payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteEventAsync(string eventType, object data, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default no-op event sink that silently discards all events.
/// Used when no SSE connection or event consumer is active.
/// </summary>
public class NullToolLoopEventSink : IToolLoopEventSink
{
    /// <summary>Singleton instance.</summary>
    public static readonly NullToolLoopEventSink Instance = new();

    /// <inheritdoc/>
    public Task WriteEventAsync(string eventType, object data, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
