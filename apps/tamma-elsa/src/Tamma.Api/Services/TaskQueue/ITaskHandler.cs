using Tamma.Data.Entities;

namespace Tamma.Api.Services.TaskQueue;

/// <summary>
/// Pluggable handler invoked by <see cref="TaskQueueProcessor"/> when a task
/// of a given type is claimed. Handlers decide their own idempotency, retry
/// policy, and downstream side effects; the processor only owns the state
/// transitions on the <see cref="QueuedTask"/> row.
///
/// <para>Throwing an exception from <see cref="HandleAsync"/> signals a
/// failure that the processor will count against the retry budget.</para>
/// </summary>
public interface ITaskHandler
{
    /// <summary>
    /// The <see cref="QueuedTask.Type"/> prefix or exact string this handler
    /// matches. Registry resolution is "exact match wins; else longest prefix
    /// match wins", so a handler registered for <c>github.</c> catches every
    /// github event unless a more specific handler (e.g. <c>github.push</c>)
    /// is also registered.
    /// </summary>
    string TypePrefix { get; }

    /// <summary>Run the handler's business logic for the claimed task.</summary>
    Task HandleAsync(QueuedTask task, CancellationToken ct);
}

/// <summary>
/// Registry exposing the collection of <see cref="ITaskHandler"/> instances
/// to the processor. Registered as a singleton so handlers (typically scoped)
/// are resolved lazily from DI on each dispatch.
/// </summary>
public interface ITaskHandlerRegistry
{
    /// <summary>
    /// Resolve the handler for a task type, or <c>null</c> when no handler
    /// matches (the processor will mark the task failed with a clear error).
    /// </summary>
    ITaskHandler? ResolveFor(string taskType);
}
