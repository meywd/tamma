using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.GitHub;

namespace Tamma.Platforms.GitHub.Tests;

/// <summary>
/// Story 31-3 — factory-level tests covering AC4 (DI factory pulls
/// scoped <see cref="IGitHubActionsClient"/> from DI per-call) and
/// AC1 (kind / capability surface).
/// </summary>
[TestFixture]
public sealed class GitHubPlatformDriverFactoryTests
{
    private static IServiceProvider BuildServices(IGitHubActionsClient inner)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(inner);
        services.AddGitHubPlatformDriver();
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Factory_CreateAsync_returns_driver_bound_to_installation()
    {
        var inner = Mock.Of<IGitHubActionsClient>();
        var sp = BuildServices(inner);

        var factory = sp.GetRequiredKeyedService<IGitPlatformDriverFactory>(PlatformKind.GitHub);
        var installation = new PlatformInstallation(
            Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            Kind: PlatformKind.GitHub,
            BaseUrl: "https://api.github.com",
            InstallationExternalId: "12345");

        var driver = await factory.CreateAsync(installation, "ghp-test-token");

        driver.Should().NotBeNull();
        driver.Kind.Should().Be(PlatformKind.GitHub);
        driver.Client.Should().NotBeNull();
        driver.Actions.Should().NotBeNull();
        driver.Capabilities.Should().BeEquivalentTo(
            PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.GitHub));
    }

    [Test]
    public async Task Factory_CreateAsync_rejects_wrong_kind()
    {
        var inner = Mock.Of<IGitHubActionsClient>();
        var sp = BuildServices(inner);

        var factory = sp.GetRequiredKeyedService<IGitPlatformDriverFactory>(PlatformKind.GitHub);
        var installation = new PlatformInstallation(
            Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            Kind: PlatformKind.Gitea, // wrong kind
            BaseUrl: "https://gitea.example.com",
            InstallationExternalId: null);

        Func<Task> act = () => factory.CreateAsync(installation, "x");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task Factory_CreateAsync_accepts_empty_credential()
    {
        // GitHub uses App-level keys held by the inner Octokit
        // singleton; the per-tenant plaintext is not consumed.
        var inner = Mock.Of<IGitHubActionsClient>();
        var sp = BuildServices(inner);

        var factory = sp.GetRequiredKeyedService<IGitPlatformDriverFactory>(PlatformKind.GitHub);
        var installation = new PlatformInstallation(
            Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            Kind: PlatformKind.GitHub,
            BaseUrl: "https://github.com",
            InstallationExternalId: "0");

        var driver = await factory.CreateAsync(installation, string.Empty);

        driver.Should().NotBeNull();
    }

    [TestCase("https://api.github.com", "api.github.com")]
    [TestCase("https://github.acme.corp/api/v3", "github.acme.corp")]
    [TestCase("github.acme.corp", "github.acme.corp")]
    [TestCase("", "github.com")]
    [TestCase(null, "github.com")]
    public void ExtractHost_normalises_base_urls(string? baseUrl, string expected)
    {
        var host = GitHubPlatformDriverFactory.ExtractHost(baseUrl);
        host.Should().Be(expected);
    }

    [Test]
    public void Factory_constructor_rejects_null_services()
    {
        Action act = () => new GitHubPlatformDriverFactory(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task Factory_uses_DefaultHost_when_BaseUrl_is_empty()
    {
        var inner = Mock.Of<IGitHubActionsClient>();
        var sp = BuildServices(inner);

        var factory = sp.GetRequiredKeyedService<IGitPlatformDriverFactory>(PlatformKind.GitHub);
        var installation = new PlatformInstallation(
            Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            Kind: PlatformKind.GitHub,
            BaseUrl: string.Empty,
            InstallationExternalId: null);

        var driver = await factory.CreateAsync(installation, "ghp-test");
        var client = driver.Client.Should().BeOfType<GitHubPlatformClient>().Subject;
        client.Host.Should().Be(GitHubPlatformDriverFactory.DefaultHost);
    }
}
