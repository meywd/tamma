using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Tamma.Activities.SecretsRotation.Contracts;
using Tamma.Api.Services.Platforms;
using Tamma.Api.Services.Secrets.Rotation;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Tests.Platforms;

/// <summary>
/// Epic 31 P4 M4 — the SECOND severed point of seam 11 closes:
/// <see cref="CiSecretsRotationHandler"/> existed since Story 31-8 but was
/// never registered as a keyed <c>IRotationHandler</c> (the execution plan's
/// corrected fact: <c>SecretRotationServiceCollectionExtensions</c>
/// registered only generic-http / postgres / cranl). These tests are the
/// red-first proof: resolving <c>"ci-secrets"</c> from the PRODUCTION
/// registration fails on the pre-milestone tree and succeeds now.
/// </summary>
[TestFixture]
public class CiSecretsRotationRegistrationTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTammaSecretRotation();
        // The handler's collaborators — fakes are fine; registration shape is
        // what's under test.
        services.AddSingleton(Mock.Of<IPlatformResolver>());
        services.AddScoped(_ => Mock.Of<IRotationAuditEmitter>());
        return services.BuildServiceProvider();
    }

    [Test]
    public void ProductionRegistration_ResolvesTheCiSecretsHandler()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();

        var handler = scope.ServiceProvider
            .GetKeyedService<IRotationHandler>(CiSecretsRotationHandler.SystemKey);

        handler.Should().NotBeNull(
            "Epic 31 P4 M4 registers CiSecretsRotationHandler under 'ci-secrets' — "
            + "before this milestone the handler existed but was never keyed in");
        handler.Should().BeOfType<CiSecretsRotationHandler>();
        handler!.System.Should().Be("ci-secrets");
    }

    [Test]
    public void Registry_ResolvesCiSecrets_AlongsideTheFallbackHandler()
    {
        // generic-http is the always-resolvable fallback (its typed client
        // needs no extra collaborators); postgres/cranl need their own
        // infrastructure fakes and are covered by their own fixtures.
        using var provider = Build();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        foreach (var key in new[] { "generic-http", "ci-secrets" })
        {
            sp.GetKeyedService<IRotationHandler>(key).Should().NotBeNull(
                $"'{key}' must resolve from the production rotation registration");
        }
    }
}
