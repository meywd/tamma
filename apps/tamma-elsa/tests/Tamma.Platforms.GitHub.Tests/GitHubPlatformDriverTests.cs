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
/// Story 31-3 — driver-level tests covering the AC1 capability set
/// + driver registration through DI (AC4).
/// </summary>
[TestFixture]
public sealed class GitHubPlatformDriverTests
{
    [Test]
    public void Driver_kind_is_GitHub()
    {
        var inner = Mock.Of<IGitHubActionsClient>();
        var client = new GitHubPlatformClient(inner, host: "github.com");
        var actions = new GitHubActionsPlatformClient(inner);

        var driver = new GitHubPlatformDriver(client, actions);

        driver.Kind.Should().Be(PlatformKind.GitHub);
    }

    [Test]
    public void Driver_capabilities_match_matrix_defaults()
    {
        var inner = Mock.Of<IGitHubActionsClient>();
        var client = new GitHubPlatformClient(inner, host: "github.com");
        var actions = new GitHubActionsPlatformClient(inner);
        var driver = new GitHubPlatformDriver(client, actions);

        var defaults = PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.GitHub);
        driver.Capabilities.Should().BeEquivalentTo(defaults);
    }

    [Test]
    public void Driver_advertises_GitHub_specific_capabilities()
    {
        var inner = Mock.Of<IGitHubActionsClient>();
        var client = new GitHubPlatformClient(inner, host: "github.com");
        var actions = new GitHubActionsPlatformClient(inner);
        var driver = new GitHubPlatformDriver(client, actions);

        driver.Capabilities.Should().Contain(PlatformCapability.Actions);
        driver.Capabilities.Should().Contain(PlatformCapability.Artifacts);
        driver.Capabilities.Should().Contain(PlatformCapability.Secrets);
        driver.Capabilities.Should().Contain(PlatformCapability.LibsodiumSecrets);
        driver.Capabilities.Should().Contain(PlatformCapability.WebhookHmac);
        driver.Capabilities.Should().Contain(PlatformCapability.PerAppInstallationAuth);
        driver.Capabilities.Should().Contain(PlatformCapability.PrFileReview);
        driver.Capabilities.Should().Contain(PlatformCapability.ListAccessibleRepos);

        // GitHub does NOT use static-token webhooks like GitLab.
        driver.Capabilities.Should().NotContain(PlatformCapability.WebhookStaticToken);
        // GitHub does NOT have GitLab-style protected/masked variables.
        driver.Capabilities.Should().NotContain(PlatformCapability.ProtectedVariables);
        driver.Capabilities.Should().NotContain(PlatformCapability.MaskedVariables);
    }

    [Test]
    public void Driver_Actions_surface_is_non_null_when_actions_capability_set()
    {
        var inner = Mock.Of<IGitHubActionsClient>();
        var client = new GitHubPlatformClient(inner, host: "github.com");
        var actions = new GitHubActionsPlatformClient(inner);
        var driver = new GitHubPlatformDriver(client, actions);

        driver.Actions.Should().NotBeNull();
    }

    [Test]
    public void Driver_constructor_rejects_null_client()
    {
        Action act = () => new GitHubPlatformDriver(null!, null);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Driver_constructor_with_custom_capabilities_accepts_narrower_set()
    {
        var inner = Mock.Of<IGitHubActionsClient>();
        var client = new GitHubPlatformClient(inner, host: "github.com");
        var actions = new GitHubActionsPlatformClient(inner);
        var narrowed = new HashSet<PlatformCapability>
        {
            PlatformCapability.PrFileReview,
            PlatformCapability.WebhookHmac,
        };

        var driver = new GitHubPlatformDriver(client, actions, narrowed);

        driver.Capabilities.Should().BeEquivalentTo(narrowed);
        driver.Capabilities.Should().NotContain(PlatformCapability.Actions);
    }

    [Test]
    public void AddGitHubPlatformDriver_registers_factory_under_GitHub_key()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // The factory pulls IGitHubActionsClient from DI on each
        // CreateAsync — register a fake so resolution succeeds.
        services.AddSingleton(Mock.Of<IGitHubActionsClient>());
        services.AddGitHubPlatformDriver();

        using var sp = services.BuildServiceProvider();

        var factory = sp.GetKeyedService<IGitPlatformDriverFactory>(PlatformKind.GitHub);
        factory.Should().NotBeNull();
        factory!.Kind.Should().Be(PlatformKind.GitHub);
    }

    [Test]
    public void AddGitHubPlatformDriver_throws_on_null_services()
    {
        Action act = () => GitHubDriverRegistrationExtensions.AddGitHubPlatformDriver(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
