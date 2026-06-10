using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.DependencyInjection;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Abstractions.Tests;

/// <summary>
/// Story 31-1 — DI registration convention. The 31-2 platform
/// registry consumes
/// <see cref="IKeyedServiceProvider.GetKeyedService{T}(object?)"/>
/// against the <see cref="PlatformKind"/> key; verify drivers
/// registered through the helper resolve.
/// </summary>
[TestFixture]
public sealed class GitPlatformDependencyInjectionTests
{
    private sealed class FakeDriver : IGitPlatformDriver
    {
        public PlatformKind Kind => PlatformKind.GitHub;
        public IGitPlatformClient Client { get; } = NullGitPlatformDriver.Instance.Client;
        public IGitPlatformActionsClient? Actions => null;
        public IReadOnlySet<PlatformCapability> Capabilities { get; } =
            PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.GitHub);
    }

    [Test]
    public void Registered_driver_resolves_via_keyed_service_provider()
    {
        var services = new ServiceCollection();
        services.AddGitPlatformDriver<FakeDriver>(PlatformKind.GitHub);

        using var provider = services.BuildServiceProvider();
        var driver = provider.GetKeyedService<IGitPlatformDriver>(PlatformKind.GitHub);

        driver.Should().NotBeNull();
        driver!.Kind.Should().Be(PlatformKind.GitHub);
        driver.Capabilities.Should()
            .Contain(PlatformCapability.LibsodiumSecrets);
    }

    [Test]
    public void Multiple_kinds_resolve_independently()
    {
        var services = new ServiceCollection();
        services.AddNullGitPlatformDriver(PlatformKind.GitHub);
        services.AddNullGitPlatformDriver(PlatformKind.GitLab);
        services.AddNullGitPlatformDriver(PlatformKind.Gitea);

        using var provider = services.BuildServiceProvider();

        var github = provider.GetKeyedService<IGitPlatformDriver>(PlatformKind.GitHub);
        var gitlab = provider.GetKeyedService<IGitPlatformDriver>(PlatformKind.GitLab);
        var gitea = provider.GetKeyedService<IGitPlatformDriver>(PlatformKind.Gitea);

        github!.Kind.Should().Be(PlatformKind.GitHub);
        gitlab!.Kind.Should().Be(PlatformKind.GitLab);
        gitea!.Kind.Should().Be(PlatformKind.Gitea);
    }

    [Test]
    public void Unregistered_kind_resolves_to_null()
    {
        var services = new ServiceCollection();
        services.AddNullGitPlatformDriver(PlatformKind.GitHub);

        using var provider = services.BuildServiceProvider();
        var bitbucket = provider.GetKeyedService<IGitPlatformDriver>(PlatformKind.Bitbucket);

        bitbucket.Should().BeNull("registry decides whether to fall back to Null driver");
    }

    [Test]
    public async Task Null_fallback_returns_ServiceUnavailable_for_repo_lookup()
    {
        var services = new ServiceCollection();
        services.AddNullGitPlatformDriver(PlatformKind.Bitbucket);

        using var provider = services.BuildServiceProvider();
        var driver = provider.GetRequiredKeyedService<IGitPlatformDriver>(PlatformKind.Bitbucket);

        driver.Kind.Should().Be(PlatformKind.Bitbucket);
        driver.Capabilities.Should().BeEmpty();

        var result = await driver.Client.GetRepoAsync("o", "r");
        result.Should().BeOfType<PlatformResult<Repo>.ServiceUnavailable>();
    }

    [Test]
    public void AddGitPlatformDriver_throws_on_null_services()
    {
        ServiceCollection? services = null;
        Action act = () =>
            services!.AddGitPlatformDriver<FakeDriver>(PlatformKind.GitHub);
        act.Should().Throw<ArgumentNullException>();
    }
}
