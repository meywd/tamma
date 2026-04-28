namespace Tamma.Api.Services.PlatformTasks;

/// <summary>
/// Story 28-6 AC7 — registry that resolves an
/// <see cref="IPlatformTaskHandler"/> for a given task type.
///
/// <para>Round-2 M10 — the registry itself is registered as
/// <b>Scoped</b> and so are the concrete handlers, because typical
/// handlers need scoped EF DbContexts. The registry resolves every
/// <c>IPlatformTaskHandler</c> from the same scope it lives in, so
/// every request through <c>PlatformTaskWorker.ProcessOnceAsync</c>
/// (which already opens a per-tick async scope) gets fresh
/// scope-bound handler instances + scope-bound dependencies.</para>
///
/// <para>An unknown task type returns <c>null</c>; the worker parks
/// the row in <c>pending</c> with an <c>UnprocessableAt</c> stamp
/// (Round-2 H8) so a future deploy that registers the handler can
/// pick the work up. After MaxRetries no-handler observations the
/// row falls through to dead-letter.</para>
/// </summary>
public interface IPlatformTaskHandlerRegistry
{
    /// <summary>
    /// Resolve a handler for <paramref name="taskType"/>. Returns
    /// <c>null</c> when no handler is registered for that type.
    /// Handlers may take scoped dependencies (e.g. EF DbContext);
    /// a fresh scope is created per task by
    /// <see cref="PlatformTaskWorker.ProcessOnceAsync"/>.
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
/// snapshot dictionary built per scope from every
/// <c>IPlatformTaskHandler</c> registered with the DI container.
///
/// <para>Round-2 M10 — registered as Scoped so handlers can take
/// scoped dependencies (typically <c>ControlPlaneDbContext</c>). The
/// duplicate-task-type detection still runs on the per-scope
/// snapshot so a misconfiguration is caught on the first claim.</para>
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
