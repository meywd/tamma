using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Gitea.Tests;

/// <summary>
/// Tests for <see cref="GiteaPlatformDriverFactory"/> — version
/// detection + capability narrowing.
/// </summary>
[TestFixture]
public class GiteaPlatformDriverFactoryTests
{
    [Test]
    public void ComputeCapabilities_OmitsActions_WhenVersionUnknown()
    {
        var caps = GiteaPlatformDriver.ComputeCapabilities(detectedVersion: null);

        caps.Should().NotContain(PlatformCapability.Actions);
        caps.Should().NotContain(PlatformCapability.Artifacts);
        caps.Should().NotContain(PlatformCapability.Secrets);
        caps.Should().Contain(PlatformCapability.PrFileReview);
        caps.Should().Contain(PlatformCapability.WebhookHmac);
        caps.Should().Contain(PlatformCapability.ListAccessibleRepos);
    }

    [Test]
    public void ComputeCapabilities_OmitsActions_OnPre1_21()
    {
        var caps = GiteaPlatformDriver.ComputeCapabilities(new Version(1, 20, 5));

        caps.Should().NotContain(PlatformCapability.Actions);
        caps.Should().NotContain(PlatformCapability.Artifacts);
    }

    [Test]
    public void ComputeCapabilities_IncludesActions_On1_21Plus()
    {
        var caps = GiteaPlatformDriver.ComputeCapabilities(new Version(1, 21, 0));

        caps.Should().Contain(PlatformCapability.Actions);
        caps.Should().Contain(PlatformCapability.Artifacts);
        caps.Should().Contain(PlatformCapability.Secrets);
    }

    [Test]
    public void ComputeCapabilities_IncludesActions_On1_22Plus()
    {
        var caps = GiteaPlatformDriver.ComputeCapabilities(new Version(1, 22, 1));

        caps.Should().Contain(PlatformCapability.Actions);
    }

    [Test]
    public async Task DetectVersionAsync_ParsesCanonicalVersion()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/version",
            HttpStatusCode.OK, """{"version":"1.21.4"}""");
        var http = new HttpClient(handler);
        var giteaHttp = new GiteaHttpClient(
            http, Guid.NewGuid(), GiteaTestFixtures.BaseUrl,
            new GiteaAuth.BotToken("t"), new GiteaOAuth2TokenCache());

        var version = await GiteaPlatformDriverFactory.DetectVersionAsync(
            giteaHttp, default);

        version.Should().Be(new Version(1, 21, 4));
    }

    [Test]
    public async Task DetectVersionAsync_StripsBuildSuffixes()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/version",
            HttpStatusCode.OK, """{"version":"1.22.0+gitea-1.22.0-rc1"}""");
        var http = new HttpClient(handler);
        var giteaHttp = new GiteaHttpClient(
            http, Guid.NewGuid(), GiteaTestFixtures.BaseUrl,
            new GiteaAuth.BotToken("t"), new GiteaOAuth2TokenCache());

        var version = await GiteaPlatformDriverFactory.DetectVersionAsync(
            giteaHttp, default);

        version.Should().Be(new Version(1, 22, 0));
    }

    [Test]
    public async Task DetectVersionAsync_ReturnsNull_OnFailure()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/version",
            HttpStatusCode.InternalServerError, "{}");
        var http = new HttpClient(handler);
        var giteaHttp = new GiteaHttpClient(
            http, Guid.NewGuid(), GiteaTestFixtures.BaseUrl,
            new GiteaAuth.BotToken("t"), new GiteaOAuth2TokenCache());

        var version = await GiteaPlatformDriverFactory.DetectVersionAsync(
            giteaHttp, default);

        version.Should().BeNull();
    }

    [Test]
    public async Task CreateAsync_BuildsDriver_WithActions_ForRecentGitea()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/version",
            HttpStatusCode.OK, """{"version":"1.21.4"}""");

        var services = new ServiceCollection();
        services.AddHttpClient(GiteaPlatformDriverFactory.GiteaHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddGiteaPlatformDriver();
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredKeyedService<IGitPlatformDriverFactory>(
            PlatformKind.Gitea);

        var driver = await factory.CreateAsync(
            new PlatformInstallation(
                Id: Guid.NewGuid(),
                TenantId: Guid.NewGuid(),
                Kind: PlatformKind.Gitea,
                BaseUrl: GiteaTestFixtures.BaseUrl,
                InstallationExternalId: null),
            credentialPlaintext: "t",
            default);

        driver.Kind.Should().Be(PlatformKind.Gitea);
        driver.Actions.Should().NotBeNull();
        driver.Capabilities.Should().Contain(PlatformCapability.Actions);
    }

    [Test]
    public async Task CreateAsync_BuildsReadOnlyDriver_ForLegacyGitea()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/version",
            HttpStatusCode.OK, """{"version":"1.20.5"}""");

        var services = new ServiceCollection();
        services.AddHttpClient(GiteaPlatformDriverFactory.GiteaHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddGiteaPlatformDriver();
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredKeyedService<IGitPlatformDriverFactory>(
            PlatformKind.Gitea);

        var driver = await factory.CreateAsync(
            new PlatformInstallation(
                Id: Guid.NewGuid(),
                TenantId: Guid.NewGuid(),
                Kind: PlatformKind.Gitea,
                BaseUrl: GiteaTestFixtures.BaseUrl,
                InstallationExternalId: null),
            credentialPlaintext: "t",
            default);

        driver.Actions.Should().BeNull();
        driver.Capabilities.Should().NotContain(PlatformCapability.Actions);
        driver.Capabilities.Should().NotContain(PlatformCapability.Artifacts);
        driver.Capabilities.Should().Contain(PlatformCapability.PrFileReview);
    }

    [Test]
    public void CreateAsync_RejectsWrongKind()
    {
        var services = new ServiceCollection();
        services.AddGiteaPlatformDriver();
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredKeyedService<IGitPlatformDriverFactory>(
            PlatformKind.Gitea);

        Func<Task> act = () => factory.CreateAsync(
            new PlatformInstallation(
                Id: Guid.NewGuid(),
                TenantId: Guid.NewGuid(),
                Kind: PlatformKind.GitHub, // mismatched kind
                BaseUrl: GiteaTestFixtures.BaseUrl,
                InstallationExternalId: null),
            credentialPlaintext: "t",
            default);

        act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public void GiteaAuth_Parse_BotTokenFromRawString()
    {
        GiteaAuth.Parse("ghs_abc123").Should().BeOfType<GiteaAuth.BotToken>()
            .Which.Token.Should().Be("ghs_abc123");
    }

    [Test]
    public void GiteaAuth_Parse_OAuth2FromJson()
    {
        var auth = GiteaAuth.Parse(
            """{"kind":"oauth2","clientId":"cid","clientSecret":"cs","refreshToken":"rt"}""");
        var oauth = auth.Should().BeOfType<GiteaAuth.OAuth2>().Subject;
        oauth.ClientId.Should().Be("cid");
        oauth.ClientSecret.Should().Be("cs");
        oauth.RefreshToken.Should().Be("rt");
    }

    [Test]
    public void GiteaAuth_Parse_RejectsBadJson()
    {
        Func<GiteaAuth> act = () => GiteaAuth.Parse("{not json}");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void DriverRegistration_RegistersKeyedFactory()
    {
        var services = new ServiceCollection();
        services.AddGiteaPlatformDriver();
        var sp = services.BuildServiceProvider();

        var factory = sp.GetKeyedService<IGitPlatformDriverFactory>(
            PlatformKind.Gitea);

        factory.Should().NotBeNull();
        factory!.Kind.Should().Be(PlatformKind.Gitea);
    }
}
