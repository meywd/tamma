using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Abstractions.Tests;

[TestFixture]
public sealed class NullGitPlatformDriverTests
{
    [Test]
    public void Has_no_capabilities()
    {
        NullGitPlatformDriver.Instance.Capabilities.Should().BeEmpty();
    }

    [Test]
    public void Has_no_actions_surface()
    {
        NullGitPlatformDriver.Instance.Actions.Should().BeNull();
    }

    [Test]
    public async Task GetRepo_returns_ServiceUnavailable()
    {
        var result = await NullGitPlatformDriver.Instance.Client.GetRepoAsync("o", "r");
        result.Should().BeOfType<PlatformResult<Repo>.ServiceUnavailable>();
    }

    [Test]
    public async Task ListPullRequestFiles_returns_ServiceUnavailable()
    {
        var result = await NullGitPlatformDriver.Instance.Client
            .ListPullRequestFilesAsync("o", "r", "1");
        result.Should().BeOfType<PlatformResult<IReadOnlyList<PrFile>>.ServiceUnavailable>();
    }

    [Test]
    public async Task RegisterWebhook_returns_ServiceUnavailable()
    {
        var result = await NullGitPlatformDriver.Instance.Client.RegisterWebhookAsync(
            new RegisterWebhookRequest("o", "r", "https://hook", new[] { "push" }, "s"));
        result.Should().BeOfType<PlatformResult<WebhookRegistration>.ServiceUnavailable>();
    }

    [Test]
    public async Task ListAccessibleRepos_yields_empty()
    {
        var count = 0;
        await foreach (var _ in NullGitPlatformDriver.Instance.Client.ListAccessibleReposAsync())
        {
            count++;
        }
        count.Should().Be(0);
    }

    [Test]
    public void Kind_is_overridable_via_init()
    {
        var driver = new NullGitPlatformDriver { Kind = PlatformKind.Bitbucket };
        driver.Kind.Should().Be(PlatformKind.Bitbucket);
    }

    [Test]
    public void Singleton_default_kind_is_GitHub()
    {
        // Sanity: the Instance singleton uses the default. Tests that
        // need a different kind use the keyed-DI helper which re-builds
        // a per-kind driver.
        NullGitPlatformDriver.Instance.Kind.Should().Be(PlatformKind.GitHub);
    }
}
