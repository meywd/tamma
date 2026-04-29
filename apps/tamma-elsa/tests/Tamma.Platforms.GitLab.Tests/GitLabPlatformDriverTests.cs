using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.GitLab;
using Tamma.Platforms.GitLab.Tests.Support;

namespace Tamma.Platforms.GitLab.Tests;

[TestFixture]
public sealed class GitLabPlatformDriverTests
{
    [Test]
    public void Driver_kind_is_GitLab()
    {
        var (client, _) = TestFactory.BuildClient();
        var (actions, _) = TestFactory.BuildActions();
        var driver = new GitLabPlatformDriver(client, actions);
        driver.Kind.Should().Be(PlatformKind.GitLab);
    }

    [Test]
    public void Driver_capabilities_match_matrix_defaults()
    {
        var (client, _) = TestFactory.BuildClient();
        var (actions, _) = TestFactory.BuildActions();
        var driver = new GitLabPlatformDriver(client, actions);

        var defaults = PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.GitLab);
        driver.Capabilities.Should().BeEquivalentTo(defaults);
    }

    [Test]
    public void Driver_advertises_GitLab_specific_capabilities()
    {
        var (client, _) = TestFactory.BuildClient();
        var (actions, _) = TestFactory.BuildActions();
        var driver = new GitLabPlatformDriver(client, actions);

        driver.Capabilities.Should().Contain(PlatformCapability.Actions);
        driver.Capabilities.Should().Contain(PlatformCapability.Artifacts);
        driver.Capabilities.Should().Contain(PlatformCapability.Secrets);
        driver.Capabilities.Should().Contain(PlatformCapability.MaskedVariables);
        driver.Capabilities.Should().Contain(PlatformCapability.ProtectedVariables);
        driver.Capabilities.Should().Contain(PlatformCapability.WebhookStaticToken);
        driver.Capabilities.Should().Contain(PlatformCapability.PrFileReview);
        driver.Capabilities.Should().Contain(PlatformCapability.ListAccessibleRepos);

        // GitLab does NOT use libsodium-sealed secrets like GitHub.
        driver.Capabilities.Should().NotContain(PlatformCapability.LibsodiumSecrets);
        // GitLab webhook is static-token, not HMAC.
        driver.Capabilities.Should().NotContain(PlatformCapability.WebhookHmac);
    }

    [Test]
    public void Driver_Actions_surface_is_non_null_when_actions_capability_set()
    {
        var (client, _) = TestFactory.BuildClient();
        var (actions, _) = TestFactory.BuildActions();
        var driver = new GitLabPlatformDriver(client, actions);
        driver.Actions.Should().NotBeNull();
    }

    [Test]
    public async Task AddGitLabPlatform_registers_factory_under_GitLab_key()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGitLabPlatform();

        await using var sp = services.BuildServiceProvider();

        var factory = sp.GetKeyedService<IGitPlatformDriverFactory>(PlatformKind.GitLab);
        factory.Should().NotBeNull();
        factory!.Kind.Should().Be(PlatformKind.GitLab);
    }

    [Test]
    public async Task Factory_CreateAsync_returns_driver_bound_to_installation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGitLabPlatform();
        await using var sp = services.BuildServiceProvider();

        var factory = sp.GetRequiredKeyedService<IGitPlatformDriverFactory>(PlatformKind.GitLab);
        var installation = new PlatformInstallation(
            Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            Kind: PlatformKind.GitLab,
            BaseUrl: "https://gitlab.example.com",
            InstallationExternalId: "12345");

        var driver = await factory.CreateAsync(installation, "glpat-test-token");

        driver.Should().NotBeNull();
        driver.Kind.Should().Be(PlatformKind.GitLab);
        driver.Client.Should().NotBeNull();
        driver.Actions.Should().NotBeNull();
    }

    [Test]
    public async Task Factory_CreateAsync_rejects_wrong_kind()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGitLabPlatform();
        await using var sp = services.BuildServiceProvider();

        var factory = sp.GetRequiredKeyedService<IGitPlatformDriverFactory>(PlatformKind.GitLab);
        var installation = new PlatformInstallation(
            Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            Kind: PlatformKind.GitHub, // wrong kind
            BaseUrl: "https://github.com",
            InstallationExternalId: null);

        Func<Task> act = () => factory.CreateAsync(installation, "x");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public void GitLabPlatformDriver_constructor_rejects_null_client()
    {
        Action act = () => new GitLabPlatformDriver(null!, null);
        act.Should().Throw<ArgumentNullException>();
    }
}
