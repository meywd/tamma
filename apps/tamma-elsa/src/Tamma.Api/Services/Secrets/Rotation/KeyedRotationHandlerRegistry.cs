using Microsoft.Extensions.DependencyInjection;
using Tamma.Activities.SecretsRotation.Contracts;

namespace Tamma.Api.Services.Secrets.Rotation;

/// <summary>
/// Story 29-6 AC9 — default <see cref="IRotationHandlerRegistry"/>
/// that resolves handlers via Microsoft.Extensions.DependencyInjection's
/// keyed services. Registering a new handler is one call:
/// <c>services.AddKeyedSingleton&lt;IRotationHandler, MyHandler&gt;("my-system")</c>.
/// </summary>
public sealed class KeyedRotationHandlerRegistry : IRotationHandlerRegistry
{
    private readonly IServiceProvider _services;

    public KeyedRotationHandlerRegistry(IServiceProvider services) => _services = services;

    public IRotationHandler? Resolve(string system) =>
        string.IsNullOrWhiteSpace(system)
            ? null
            : _services.GetKeyedService<IRotationHandler>(system);
}
