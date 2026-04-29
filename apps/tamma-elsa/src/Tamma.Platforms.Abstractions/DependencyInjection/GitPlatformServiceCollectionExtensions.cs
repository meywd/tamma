using Microsoft.Extensions.DependencyInjection;

namespace Tamma.Platforms.Abstractions.DependencyInjection;

/// <summary>
/// Story 31-1 — DI registration helpers. Story 31-2's platform
/// registry consumes
/// <see cref="IKeyedServiceProvider.GetKeyedService{T}(object?)"/>
/// against the <see cref="PlatformKind"/> key, so every driver MUST
/// register through one of these helpers (or call
/// <see cref="ServiceCollectionKeyedServiceExtensions.AddKeyedSingleton"/>
/// directly with the same key shape).
///
/// <para>Operating-modes note: a single Tamma process registers
/// drivers for all <see cref="PlatformKind"/> values it cares about.
/// In single-user mode the host typically resolves the same driver
/// for every request; in SaaS mode the registry uses keyed services
/// to look up the driver for the request's tenant binding. Story
/// 31-2 ships the per-tenant resolver — 31-1 only ships the
/// keyed-by-PlatformKind layer.</para>
/// </summary>
public static class GitPlatformServiceCollectionExtensions
{
    /// <summary>
    /// Register a driver as a keyed singleton. Equivalent to:
    /// <code>
    /// services.AddKeyedSingleton&lt;IGitPlatformDriver, TDriver&gt;(kind);
    /// </code>
    /// </summary>
    public static IServiceCollection AddGitPlatformDriver<TDriver>(
        this IServiceCollection services,
        PlatformKind kind)
        where TDriver : class, IGitPlatformDriver
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddKeyedSingleton<IGitPlatformDriver, TDriver>(kind);
        return services;
    }

    /// <summary>
    /// Register a pre-built driver instance as a keyed singleton.
    /// Mostly useful for tests and the
    /// <see cref="NullGitPlatformDriver"/> fallback.
    /// </summary>
    public static IServiceCollection AddGitPlatformDriver(
        this IServiceCollection services,
        PlatformKind kind,
        IGitPlatformDriver instance)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(instance);
        services.AddKeyedSingleton(kind, instance);
        return services;
    }

    /// <summary>
    /// Register the <see cref="NullGitPlatformDriver"/> as the keyed
    /// fallback for a platform kind. Story 31-2 calls this once per
    /// kind it knows about so unconfigured platforms degrade to
    /// <see cref="PlatformResult{T}.ServiceUnavailable"/> rather than
    /// throwing on <see cref="IKeyedServiceProvider.GetRequiredKeyedService"/>.
    /// </summary>
    public static IServiceCollection AddNullGitPlatformDriver(
        this IServiceCollection services,
        PlatformKind kind)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddKeyedSingleton<IGitPlatformDriver>(
            kind,
            (_, _) => new NullGitPlatformDriver { Kind = kind });
        return services;
    }
}
