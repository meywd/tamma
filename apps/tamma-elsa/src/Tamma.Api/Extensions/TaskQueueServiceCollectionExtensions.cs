using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.TaskQueue;
using Tamma.Data.Repositories;

namespace Tamma.Api.Extensions;

/// <summary>
/// DI wiring for the multi-tenant task queue. Ported from the deleted
/// TypeScript in-memory queue; the C# implementation is backed by
/// <c>queued_tasks</c> in Postgres.
///
/// <para>
/// The parent <c>Program.cs</c> is owned by the auth-foundation stream and
/// must not be edited from here — the contract between streams is that
/// the task queue is exposed via this single extension method, which
/// the parent calls once from its composition root.
/// </para>
/// </summary>
public static class TaskQueueServiceCollectionExtensions
{
    /// <summary>
    /// Register:
    /// <list type="bullet">
    ///   <item><description><see cref="IQueuedTaskRepository"/> — persistence port.</description></item>
    ///   <item><description><see cref="ITaskQueue"/> (<see cref="DbTaskQueue"/>) — API surface with tenant scoping.</description></item>
    ///   <item><description><see cref="ITaskHandlerRegistry"/> — DI-backed registry that exposes every registered <see cref="ITaskHandler"/>.</description></item>
    ///   <item><description><see cref="TaskQueueProcessor"/> — hosted polling service.</description></item>
    ///   <item><description><see cref="TaskQueueProcessorOptions"/> — bound from the <c>TaskQueue</c> configuration section (optional).</description></item>
    /// </list>
    /// Idempotent via TryAdd* — safe to call from both tests and production.
    /// </summary>
    public static IServiceCollection AddTaskQueue(this IServiceCollection services)
    {
        services.TryAddScoped<IQueuedTaskRepository, QueuedTaskRepository>();
        services.TryAddScoped<ITaskQueue, DbTaskQueue>();
        services.TryAddSingleton<ITaskHandlerRegistry, DiTaskHandlerRegistry>();
        services.TryAddSingleton(new TaskQueueProcessorOptions());
        services.AddHostedService<TaskQueueProcessor>();
        return services;
    }
}

/// <summary>
/// <see cref="ITaskHandlerRegistry"/> that resolves handlers out of DI on each
/// lookup. Registered as a singleton but creates a scope internally so scoped
/// handlers still work.
///
/// <para>Resolution order: exact <c>TypePrefix</c> match first, then longest
/// prefix match. Handlers are cached per task-type string to avoid paying the
/// scope-creation cost on every poll cycle.</para>
/// </summary>
internal sealed class DiTaskHandlerRegistry : ITaskHandlerRegistry
{
    private readonly IServiceProvider _rootProvider;
    private readonly IReadOnlyList<ITaskHandler> _handlers;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ITaskHandler?> _cache = new();

    public DiTaskHandlerRegistry(IServiceProvider rootProvider)
    {
        _rootProvider = rootProvider;
        using var scope = rootProvider.CreateScope();
        _handlers = scope.ServiceProvider.GetServices<ITaskHandler>().ToList();
    }

    public ITaskHandler? ResolveFor(string taskType)
        => _cache.GetOrAdd(taskType, ResolveUncached);

    private ITaskHandler? ResolveUncached(string taskType)
    {
        // Exact match first.
        var exact = _handlers.FirstOrDefault(h => h.TypePrefix == taskType);
        if (exact is not null) return exact;

        // Longest prefix match.
        return _handlers
            .Where(h => taskType.StartsWith(h.TypePrefix, StringComparison.Ordinal))
            .OrderByDescending(h => h.TypePrefix.Length)
            .FirstOrDefault();
    }
}
