namespace Tamma.Activities.SecretsRotation.Contracts;

/// <summary>
/// Story 29-6 AC9 — thin facade over keyed DI resolution for
/// <see cref="IRotationHandler"/>. The Api layer registers handlers
/// via <c>AddKeyedSingleton&lt;IRotationHandler, ...&gt;("postgres")</c>
/// etc.; this registry resolves them at activity-execution time.
///
/// <para>Abstracted (vs. raw keyed DI) so activities don't have to
/// take a dependency on Microsoft.Extensions.DependencyInjection's
/// keyed-service types and so tests can stub the registry with a
/// simple dictionary.</para>
/// </summary>
public interface IRotationHandlerRegistry
{
    /// <summary>
    /// Resolve the handler for a consumer-system key. Returns null
    /// when no handler is registered for the key — the activity emits
    /// <c>SECRET.ROTATION.FAILED</c> with detail
    /// <c>handler_not_registered</c>. A fallback
    /// <c>GenericHttpRotationHandler</c> is wired at key
    /// <c>generic-http</c> and is picked up when a secret's
    /// consumer ref's system matches that key.
    /// </summary>
    IRotationHandler? Resolve(string system);
}
