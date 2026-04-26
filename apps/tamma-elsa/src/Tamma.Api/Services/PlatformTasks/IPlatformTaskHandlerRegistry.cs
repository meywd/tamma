namespace Tamma.Api.Services.PlatformTasks;

/// <summary>
/// Story 28-6 AC7 — registry that resolves an
/// <see cref="IPlatformTaskHandler"/> for a given task type.
/// Registered as a singleton; the registry takes a snapshot of every
/// <c>IPlatformTaskHandler</c> registered via DI at construction time
/// so resolution is lock-free dictionary lookup.
///
/// <para>An unknown task type returns <c>null</c>; the worker handles
/// the null case by moving the row to <c>dead_letter</c> with a
/// "no handler registered" reason — operators can then either ship a
/// handler or manually clear the queue.</para>
/// </summary>
public interface IPlatformTaskHandlerRegistry
{
    /// <summary>
    /// Resolve a handler for <paramref name="taskType"/>. Returns
    /// <c>null</c> when no handler is registered for that type.
    /// </summary>
    IPlatformTaskHandler? Resolve(string taskType);

    /// <summary>
    /// All registered task type identifiers. Used by the admin
    /// diagnostics endpoint to surface "you have a queue full of X but
    /// no handler" mismatches.
    /// </summary>
    IReadOnlyCollection<string> RegisteredTypes { get; }
}

/// <summary>
/// Default <see cref="IPlatformTaskHandlerRegistry"/> backed by a
/// snapshot dictionary built from all <c>IPlatformTaskHandler</c>
/// registrations at construction.
/// </summary>
public sealed class PlatformTaskHandlerRegistry : IPlatformTaskHandlerRegistry
{
    private readonly IReadOnlyDictionary<string, IPlatformTaskHandler> _byType;

    public PlatformTaskHandlerRegistry(IEnumerable<IPlatformTaskHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        var dict = new Dictionary<string, IPlatformTaskHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            if (string.IsNullOrWhiteSpace(handler.TaskType))
                throw new InvalidOperationException(
                    $"IPlatformTaskHandler '{handler.GetType().FullName}' " +
                    "returned an empty TaskType.");
            if (dict.ContainsKey(handler.TaskType))
                throw new InvalidOperationException(
                    $"Duplicate IPlatformTaskHandler registration for task " +
                    $"type '{handler.TaskType}': " +
                    $"{dict[handler.TaskType].GetType().FullName} vs " +
                    $"{handler.GetType().FullName}.");
            dict[handler.TaskType] = handler;
        }
        _byType = dict;
    }

    public IPlatformTaskHandler? Resolve(string taskType) =>
        _byType.TryGetValue(taskType ?? string.Empty, out var h) ? h : null;

    public IReadOnlyCollection<string> RegisteredTypes => _byType.Keys.ToArray();
}
