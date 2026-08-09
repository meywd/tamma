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
    public void Driver_capabilities_match_matrix_defaults_at_or_above_the_lifecycle_floor()
    {
        var (client, _) = TestFactory.BuildClient();
        var (actions, _) = TestFactory.BuildActions();
        var driver = new GitLabPlatformDriver(
            client, actions, detectedVersion: new Version(16, 11));

        var defaults = PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.GitLab);
        driver.Capabilities.Should().BeEquivalentTo(defaults,
            "16.11 is above the 13.9 PR-lifecycle floor so nothing is narrowed away");
        driver.DetectedVersion.Should().Be(new Version(16, 11));
    }

    [Test]
    public void Driver_narrows_PrLifecycle_below_the_floor_or_when_probe_failed()
    {
        // Epic 31 P6 M1 — the version gate, both narrowed shapes.
        GitLabPlatformDriver.ComputeCapabilities(new Version(13, 8))
            .Should().NotContain(PlatformCapability.PrLifecycle,
                "13.8 shipped reviewer_ids but the update endpoint ignored it until 13.9");
        GitLabPlatformDriver.ComputeCapabilities(null)
            .Should().NotContain(PlatformCapability.PrLifecycle,
                "a failed version probe is conservatively unsupported");
        GitLabPlatformDriver.ComputeCapabilities(new Version(13, 9))
            .Should().Contain(PlatformCapability.PrLifecycle, "13.9 is the floor");

        var (client, _) = TestFactory.BuildClient();
        var (actions, _) = TestFactory.BuildActions();
        var driver = new GitLabPlatformDriver(client, actions);
        driver.Capabilities.Should().NotContain(PlatformCapability.PrLifecycle,
            "no detected version = the conservative set");
        driver.DetectedVersion.Should().BeNull();
    }

    [Test]
    public void ParseVersion_handles_edition_and_prerelease_suffixes()
    {
        GitLabPlatformDriverFactory.ParseVersion("16.11.1-ee").Should().Be(new Version(16, 11, 1));
        GitLabPlatformDriverFactory.ParseVersion("17.0.0").Should().Be(new Version(17, 0, 0));
        GitLabPlatformDriverFactory.ParseVersion("16.10.0-pre+abc").Should().Be(new Version(16, 10, 0));
        GitLabPlatformDriverFactory.ParseVersion("").Should().BeNull();
        GitLabPlatformDriverFactory.ParseVersion("not-a-version").Should().BeNull();
        GitLabPlatformDriverFactory.ParseVersion(null).Should().BeNull();
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
