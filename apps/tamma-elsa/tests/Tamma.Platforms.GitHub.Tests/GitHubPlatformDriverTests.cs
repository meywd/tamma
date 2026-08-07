using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.GitHub.Tests;

/// <summary>
/// Epic 31 P1 stage 2 — driver facade + capability-computation +
/// registration tests.
/// </summary>
[TestFixture]
public sealed class GitHubPlatformDriverTests
{
    private static GitHubPlatformClient Client()
    {
        var http = new GitHubHttpClient(
            new HttpClient(new FakeHttpMessageHandler()),
            "https://api.github.com",
            new GitHubAuth.Pat("t"));
        return new GitHubPlatformClient(http, "github.com");
    }

    [Test]
    public void Driver_reports_github_kind_and_collaborators()
    {
        var client = Client();
        var driver = new GitHubPlatformDriver(client, actions: null);

        driver.Kind.Should().Be(PlatformKind.GitHub);
        driver.Client.Should().BeSameAs(client);
        driver.Actions.Should().BeNull();
        driver.Capabilities.Should().BeEquivalentTo(
            PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.GitHub));
    }

    [Test]
    public void Matrix_defaults_advertise_the_stage2_capabilities()
    {
        var defaults = PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.GitHub);

        defaults.Should().Contain(
        [
            PlatformCapability.PrLifecycle,
            PlatformCapability.IssueLifecycle,
            PlatformCapability.Releases,
            PlatformCapability.CommitReads,
            PlatformCapability.PrReviewCommentRead,
            PlatformCapability.PrFileReview,
            PlatformCapability.ListAccessibleRepos,
        ]);
    }

    [Test]
    public void ComputeCapabilities_pat_mode_drops_only_app_installation_auth()
    {
        var pat = GitHubPlatformDriver.ComputeCapabilities(new GitHubAuth.Pat("t"));
        var expected = new HashSet<PlatformCapability>(
            PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.GitHub));
        expected.Remove(PlatformCapability.PerAppInstallationAuth);

        pat.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void ComputeCapabilities_app_mode_keeps_matrix_defaults()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var app = GitHubPlatformDriver.ComputeCapabilities(
            new GitHubAuth.App(1, rsa.ExportRSAPrivateKeyPem(), 2));

        app.Should().BeEquivalentTo(
            PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.GitHub));
    }

    [Test]
    public void Driver_constructor_rejects_null_client()
    {
        Action act = () => new GitHubPlatformDriver(null!, null);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Registration_extension_registers_keyed_factory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGitHubPlatformDriver();
        var sp = services.BuildServiceProvider();

        var factory = sp.GetKeyedService<IGitPlatformDriverFactory>(PlatformKind.GitHub);

        factory.Should().NotBeNull();
        factory!.Kind.Should().Be(PlatformKind.GitHub);
    }

    [Test]
    public void Registration_extension_rejects_null_services()
    {
        Action act = () => GitHubDriverRegistrationExtensions.AddGitHubPlatformDriver(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
