using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Gitea.Tests;

/// <summary>
/// Story 31-5 — factory + capability tests for the Forgejo compat
/// shim. The shim composes the Gitea driver, so the tests focus on
/// the divergence points: kind == Forgejo, Forgejo's '+forgejo-N'
/// version suffix parses correctly, capability narrowing mirrors
/// Gitea's, and the factory rejects mismatched-kind installations.
/// </summary>
[TestFixture]
public class ForgejoPlatformDriverFactoryTests
{
    private const string ForgejoBaseUrl = "https://forgejo.example.org";

    [Test]
    public void Forgejo_ComputeCapabilities_OmitsActions_WhenVersionUnknown()
    {
        var caps = ForgejoPlatformDriver.ComputeCapabilities(detectedVersion: null);

        caps.Should().NotContain(PlatformCapability.Actions);
        caps.Should().NotContain(PlatformCapability.Artifacts);
        caps.Should().NotContain(PlatformCapability.Secrets);
        caps.Should().Contain(PlatformCapability.PrFileReview);
        caps.Should().Contain(PlatformCapability.WebhookHmac);
        caps.Should().Contain(PlatformCapability.ListAccessibleRepos);
    }

    [Test]
    public void Forgejo_ComputeCapabilities_OmitsActions_OnPre1_21()
    {
        var caps = ForgejoPlatformDriver.ComputeCapabilities(new Version(1, 20, 5));

        caps.Should().NotContain(PlatformCapability.Actions);
        caps.Should().NotContain(PlatformCapability.Artifacts);
    }

    [Test]
    public void Forgejo_ComputeCapabilities_IncludesActions_On1_21Plus()
    {
        var caps = ForgejoPlatformDriver.ComputeCapabilities(new Version(1, 21, 0));

        caps.Should().Contain(PlatformCapability.Actions);
        caps.Should().Contain(PlatformCapability.Artifacts);
        caps.Should().Contain(PlatformCapability.Secrets);
    }

    [Test]
    public void Forgejo_ComputeCapabilities_MatchesGitea_ForEquivalentVersion()
    {
        // Brief §3 — Forgejo's capability set must mirror Gitea's
        // until divergence. Lock that in: any drift here forces a
        // matrix update + intentional acceptance.
        var forgejo = ForgejoPlatformDriver.ComputeCapabilities(new Version(1, 21, 4));
        var gitea = GiteaPlatformDriver.ComputeCapabilities(new Version(1, 21, 4));

        forgejo.Should().BeEquivalentTo(gitea);
    }

    [Test]
    public async Task Forgejo_Version_Parses_Correctly_FromForgejoSuffix()
    {
        // Forgejo's /api/v1/version returns "1.21.5+forgejo-3" — the
        // existing strip-after-'+' logic in DetectVersionAsync handles
        // it identically to Gitea's "1.21.0+gitea-1.21.0".
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Get,
            $"{ForgejoBaseUrl}/api/v1/version",
            HttpStatusCode.OK, """{"version":"1.21.5+forgejo-3"}""");
        var http = new HttpClient(handler);
        var giteaHttp = new GiteaHttpClient(
            http, Guid.NewGuid(), ForgejoBaseUrl,
            new GiteaAuth.BotToken("t"), new GiteaOAuth2TokenCache());

        var version = await GiteaPlatformDriverFactory.DetectVersionAsync(
            giteaHttp, default);

        version.Should().Be(new Version(1, 21, 5));
    }

    [Test]
    public async Task Forgejo_Version_Parses_Correctly_FromForgejoV15Suffix()
    {
        // Forgejo 15.x calver-style strings — still has '+forgejo-N'.
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Get,
            $"{ForgejoBaseUrl}/api/v1/version",
            HttpStatusCode.OK, """{"version":"1.22.0+forgejo-15"}""");
        var http = new HttpClient(handler);
        var giteaHttp = new GiteaHttpClient(
            http, Guid.NewGuid(), ForgejoBaseUrl,
            new GiteaAuth.BotToken("t"), new GiteaOAuth2TokenCache());

        var version = await GiteaPlatformDriverFactory.DetectVersionAsync(
            giteaHttp, default);

        version.Should().Be(new Version(1, 22, 0));
    }

    [Test]
    public async Task Forgejo_CreateAsync_BuildsDriver_WithActions_ForRecentForgejo()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Get,
            $"{ForgejoBaseUrl}/api/v1/version",
            HttpStatusCode.OK, """{"version":"1.21.5+forgejo-3"}""");

        var services = new ServiceCollection();
        services.AddHttpClient(ForgejoPlatformDriverFactory.ForgejoHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddForgejoPlatformDriver();
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredKeyedService<IGitPlatformDriverFactory>(
            PlatformKind.Forgejo);

        var driver = await factory.CreateAsync(
            new PlatformInstallation(
                Id: Guid.NewGuid(),
                TenantId: Guid.NewGuid(),
                Kind: PlatformKind.Forgejo,
                BaseUrl: ForgejoBaseUrl,
                InstallationExternalId: null),
            credentialPlaintext: "t",
            default);

        driver.Kind.Should().Be(PlatformKind.Forgejo);
        driver.Actions.Should().NotBeNull();
        driver.Capabilities.Should().Contain(PlatformCapability.Actions);
        driver.Should().BeOfType<ForgejoPlatformDriver>()
            .Which.DetectedVersion.Should().Be(new Version(1, 21, 5));
    }

    [Test]
    public async Task Forgejo_CreateAsync_BuildsReadOnlyDriver_ForLegacyForgejo()
    {
        // A pre-Actions Forgejo (v1.20-style) — capabilities narrow.
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Get,
            $"{ForgejoBaseUrl}/api/v1/version",
            HttpStatusCode.OK, """{"version":"1.20.0+forgejo-2"}""");

        var services = new ServiceCollection();
        services.AddHttpClient(ForgejoPlatformDriverFactory.ForgejoHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddForgejoPlatformDriver();
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredKeyedService<IGitPlatformDriverFactory>(
            PlatformKind.Forgejo);

        var driver = await factory.CreateAsync(
            new PlatformInstallation(
                Id: Guid.NewGuid(),
                TenantId: Guid.NewGuid(),
                Kind: PlatformKind.Forgejo,
                BaseUrl: ForgejoBaseUrl,
                InstallationExternalId: null),
            credentialPlaintext: "t",
            default);

        driver.Kind.Should().Be(PlatformKind.Forgejo);
        driver.Actions.Should().BeNull();
        driver.Capabilities.Should().NotContain(PlatformCapability.Actions);
        driver.Capabilities.Should().NotContain(PlatformCapability.Artifacts);
        driver.Capabilities.Should().Contain(PlatformCapability.PrFileReview);
    }

    [Test]
    public void Forgejo_CreateAsync_RejectsWrongKind()
    {
        var services = new ServiceCollection();
        services.AddForgejoPlatformDriver();
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredKeyedService<IGitPlatformDriverFactory>(
            PlatformKind.Forgejo);

        Func<Task> act = () => factory.CreateAsync(
            new PlatformInstallation(
                Id: Guid.NewGuid(),
                TenantId: Guid.NewGuid(),
                Kind: PlatformKind.Gitea, // mismatched kind
                BaseUrl: ForgejoBaseUrl,
                InstallationExternalId: null),
            credentialPlaintext: "t",
            default);

        act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public void Forgejo_DriverRegistration_RegistersKeyedFactory()
    {
        var services = new ServiceCollection();
        services.AddForgejoPlatformDriver();
        var sp = services.BuildServiceProvider();

        var factory = sp.GetKeyedService<IGitPlatformDriverFactory>(
            PlatformKind.Forgejo);

        factory.Should().NotBeNull();
        factory!.Kind.Should().Be(PlatformKind.Forgejo);
        factory.Should().BeOfType<ForgejoPlatformDriverFactory>();
    }

    [Test]
    public void Forgejo_DriverRegistration_AlongsideGitea_RegistersBoth()
    {
        // Both extensions in the same host; each picks up its own
        // keyed factory without trampling the other.
        var services = new ServiceCollection();
        services.AddGiteaPlatformDriver();
        services.AddForgejoPlatformDriver();
        var sp = services.BuildServiceProvider();

        sp.GetKeyedService<IGitPlatformDriverFactory>(PlatformKind.Gitea)
            .Should().BeOfType<GiteaPlatformDriverFactory>();
        sp.GetKeyedService<IGitPlatformDriverFactory>(PlatformKind.Forgejo)
            .Should().BeOfType<ForgejoPlatformDriverFactory>();
    }

    [Test]
    public void Forgejo_DriverRegistration_RegistersForgejoFlavouredVerifier()
    {
        // 31-7's webhook receiver fetches the Forgejo verifier via
        // keyed-DI; assert the keyed singleton has the Forgejo-first
        // header list.
        var services = new ServiceCollection();
        services.AddForgejoPlatformDriver();
        var sp = services.BuildServiceProvider();

        var verifier = sp.GetKeyedService<GiteaWebhookSignatureVerifier>(
            PlatformKind.Forgejo);

        verifier.Should().NotBeNull();
        // Smoke: verifier accepts a Forgejo-signed payload via the
        // Forgejo header — confirms the keyed instance is configured
        // with the Forgejo-first header list.
        var body = System.Text.Encoding.UTF8.GetBytes("payload");
        var secret = "s";
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(body);
        var sb = new System.Text.StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.AppendFormat("{0:x2}", b);
        var sig = sb.ToString();

        var result = verifier!.Verify(body, secret,
            name => name == "X-Forgejo-Signature" ? sig : null);

        result.Should().Be(GiteaWebhookSignatureVerifier.VerificationResult.Valid);
    }
}
