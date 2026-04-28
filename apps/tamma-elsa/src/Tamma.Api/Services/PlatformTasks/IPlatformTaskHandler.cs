using Tamma.Data.Entities;

namespace Tamma.Api.Services.PlatformTasks;

/// <summary>
/// Story 28-6 AC7 — handler contract for one task type processed by the
/// <see cref="PlatformTaskWorker"/>. Implementations are registered via
/// <c>services.AddPlatformTaskHandler&lt;TConcrete&gt;()</c> and resolved
/// at run-time by <see cref="IPlatformTaskHandlerRegistry"/> based on
/// the row's <see cref="PlatformQueuedTask.Type"/>.
///
/// <para><b>Failure semantics</b>: throwing a normal
/// <see cref="Exception"/> signals a retryable failure — the worker
/// records the error, increments retry count, and re-enqueues the row
/// (or moves it to dead-letter when the ceiling is reached). Throw
/// <see cref="PlatformTaskTerminalException"/> to skip retries and move
/// the row directly to <c>dead_letter</c> (e.g. malformed payload that
/// will never succeed).</para>
///
/// <para><b>Cancellation</b>: when the worker is shutting down it
/// cancels the token; handlers that have started Postgres work should
/// honour it so the visibility-timeout reaper can re-claim the task on
/// the next process.</para>
/// </summary>
public interface IPlatformTaskHandler
{
    /// <summary>
    /// Stable task-type identifier. Matches
    /// <see cref="PlatformQueuedTask.Type"/> for routing. Convention is
    /// dot-separated lower-snake-case (e.g.
    /// <c>github.installation.routing</c>,
    /// <c>tenant.welcome_fanout</c>).
    /// </summary>
    string TaskType { get; }

    /// <summary>
    /// Process one task. The worker has already flipped the row to
    /// <c>processing</c> via the repository's
    /// <c>ReserveNextAsync</c>. On normal return the worker marks
    /// <c>completed</c>; on exception it records the failure + retries
    /// per the worker's retry policy.
    /// </summary>
    Task HandleAsync(PlatformQueuedTask task, CancellationToken ct);
}

/// <summary>
/// Throw from <see cref="IPlatformTaskHandler.HandleAsync"/> to signal
/// a non-retryable failure (malformed payload, unknown subject) — the
/// worker moves the row directly to <c>dead_letter</c> instead of
/// counting against the retry budget.
/// </summary>
public sealed class PlatformTaskTerminalException : Exception
{
    public PlatformTaskTerminalException(string message) : base(message) { }
    public PlatformTaskTerminalException(string message, Exception inner)
        : base(message, inner) { }
}
