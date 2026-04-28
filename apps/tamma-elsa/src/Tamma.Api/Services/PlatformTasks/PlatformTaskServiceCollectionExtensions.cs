using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Tamma.Api.Services.PlatformTasks;

/// <summary>
/// Story 28-6 — DI extension that wires the
/// <see cref="PlatformTaskWorker"/> +
/// <see cref="IPlatformTaskHandlerRegistry"/> with default options. Call
/// <see cref="AddPlatformTaskWorker"/> once in the composition root,
/// then register handlers via <see cref="AddPlatformTaskHandler{T}"/>.
/// </summary>
public static class PlatformTaskServiceCollectionExtensions
{
    /// <summary>
    /// Register the worker + registry. Idempotent — calling twice is
    /// safe. The hosted service registration uses
    /// <see cref="ServiceCollectionDescriptorExtensions.TryAddEnumerable(IServiceCollection, ServiceDescriptor)"/>
    /// semantics so a duplicate call doesn't double-poll.
    /// </summary>
    public static IServiceCollection AddPlatformTaskWorker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PlatformTaskWorkerOptions>()
            .Configure(opts =>
                configuration
                    .GetSection(PlatformTaskWorkerOptions.SectionName)
                    .Bind(opts));

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        // Round-2 M10 — registry is Scoped so it can resolve
        // scope-bound IPlatformTaskHandlers each tick. The worker
        // already opens a per-task async scope, so this is the right
        // shape for handlers that take a DbContext.
        services.TryAddScoped<IPlatformTaskHandlerRegistry, PlatformTaskHandlerRegistry>();

        // Use TryAddEnumerable on a hosted-service descriptor so a
        // double Add doesn't spawn two background pollers.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                Microsoft.Extensions.Hosting.IHostedService,
                PlatformTaskWorker>());

        return services;
    }

    /// <summary>
    /// Register a concrete <see cref="IPlatformTaskHandler"/>. Each call
    /// adds a separate registration so the registry sees every handler
    /// at startup.
    ///
    /// <para>Round-2 M10 — handlers are registered as <b>Scoped</b> so
    /// they may take scoped dependencies (most importantly
    /// <c>ControlPlaneDbContext</c>) without a captive-dependency
    /// surprise. <see cref="PlatformTaskWorker.ProcessOnceAsync"/>
    /// opens a fresh <c>AsyncScope</c> per claimed task so the handler
    /// instance is not shared across ticks.</para>
    /// </summary>
    public static IServiceCollection AddPlatformTaskHandler<THandler>(
        this IServiceCollection services)
        where THandler : class, IPlatformTaskHandler
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IPlatformTaskHandler, THandler>();
        return services;
    }
}
