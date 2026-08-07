using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.GitHub.Tests;

/// <summary>
/// Epic 31 P1 stage 2 — factory tests: the factory HONORS its
/// arguments. Both credential modes (PAT plaintext + App JSON) build
/// working drivers whose HTTP goes to the installation's BaseUrl with
/// the installation's credential; junk credentials fail the onboarding
/// probe; the old process-singleton App conditional is gone.
/// </summary>
[TestFixture]
public sealed class GitHubPlatformDriverFactoryTests
{
    private static string TestRsaPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

    private static (GitHubPlatformDriverFactory Factory, FakeHttpMessageHandler Handler)
        BuildFactory()
    {
        var handler = new FakeHttpMessageHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient(GitHubPlatformDriverFactory.GitHubHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddGitHubPlatformDriver();
        var sp = services.BuildServiceProvider();
        var factory = (GitHubPlatformDriverFactory)sp
            .GetRequiredKeyedService<IGitPlatformDriverFactory>(PlatformKind.GitHub);
        return (factory, handler);
    }

    private static PlatformInstallation Installation(
        string baseUrl = "https://api.github.com", string? externalId = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), PlatformKind.GitHub, baseUrl, externalId);

    // ================================================================
    // PAT mode
    // ================================================================

    [Test]
    public async Task PatMode_builds_driver_whose_calls_use_the_row_credential()
    {
        var (factory, handler) = BuildFactory();
        handler.EnqueueJson(HttpMethod.Get, "https://api.github.com/repos/o/r",
            HttpStatusCode.OK, """{ "name": "r", "owner": { "login": "o" } }""");

        var driver = await factory.CreateAsync(Installation(), "ghp_tenant_token");
        var result = await driver.Client.GetRepoAsync("o", "r");

        result.IsOk.Should().BeTrue();
        handler.Requests.Single().Headers["Authorization"].Should().Be(
            "Bearer ghp_tenant_token",
            "the driver must use the per-tenant credential, not a process singleton");
        driver.Kind.Should().Be(PlatformKind.GitHub);
        driver.Actions.Should().NotBeNull("Actions is real in BOTH modes now");
    }

    [Test]
    public async Task PatMode_capabilities_drop_PerAppInstallationAuth()
    {
        var (factory, _) = BuildFactory();

        var driver = await factory.CreateAsync(Installation(), "ghp_tenant_token");

        driver.Capabilities.Should().NotContain(PlatformCapability.PerAppInstallationAuth);
        driver.Capabilities.Should().Contain(PlatformCapability.PrLifecycle);
        driver.Capabilities.Should().Contain(PlatformCapability.IssueLifecycle);
    }

    [Test]
    public async Task PatMode_honors_ghes_base_url()
    {
        var (factory, handler) = BuildFactory();
        handler.EnqueueJson(HttpMethod.Get, "https://github.acme.corp/api/v3/repos/o/r",
            HttpStatusCode.OK, """{ "name": "r" }""");

        var driver = await factory.CreateAsync(
            Installation(baseUrl: "https://github.acme.corp/api/v3"), "ghp_x");
        var result = await driver.Client.GetRepoAsync("o", "r");

        result.IsOk.Should().BeTrue();
        handler.Requests.Single().Url.Should()
            .StartWith("https://github.acme.corp/api/v3/repos/o/r");
    }

    // ================================================================
    // App-installation mode
    // ================================================================

    [Test]
    public async Task AppMode_mints_installation_token_then_calls_with_it()
    {
        var (factory, handler) = BuildFactory();
        handler.EnqueueJson(HttpMethod.Post,
            "https://api.github.com/app/installations/456/access_tokens",
            HttpStatusCode.Created,
            $$"""{ "token": "ghs_minted", "expires_at": "{{DateTimeOffset.UtcNow.AddHours(1):O}}" }""");
        handler.EnqueueJson(HttpMethod.Get, "https://api.github.com/repos/o/r",
            HttpStatusCode.OK, """{ "name": "r" }""");

        var credential = System.Text.Json.JsonSerializer.Serialize(new
        {
            kind = "app",
            appId = 123,
            privateKeyPem = TestRsaPem(),
            installationId = 456,
        });
        var driver = await factory.CreateAsync(Installation(), credential);
        var result = await driver.Client.GetRepoAsync("o", "r");

        result.IsOk.Should().BeTrue();
        var mint = handler.Requests.First();
        mint.Url.Should().EndWith("/app/installations/456/access_tokens");
        mint.Headers["Authorization"].Should().StartWith("Bearer ey",
            "the mint call authenticates with the RS256 App JWT");
        handler.Requests.Last().Headers["Authorization"].Should().Be("Bearer ghs_minted");
    }

    [Test]
    public async Task AppMode_falls_back_to_row_external_id_for_installation_id()
    {
        var (factory, handler) = BuildFactory();
        handler.EnqueueJson(HttpMethod.Post,
            "https://api.github.com/app/installations/789/access_tokens",
            HttpStatusCode.Created,
            $$"""{ "token": "ghs_minted", "expires_at": "{{DateTimeOffset.UtcNow.AddHours(1):O}}" }""");
        handler.EnqueueJson(HttpMethod.Get, "https://api.github.com/repos/o/r",
            HttpStatusCode.OK, """{ "name": "r" }""");

        var credential = System.Text.Json.JsonSerializer.Serialize(new
        {
            kind = "app",
            appId = 123,
            privateKeyPem = TestRsaPem(),
        });
        var driver = await factory.CreateAsync(
            Installation(externalId: "789"), credential);
        var result = await driver.Client.GetRepoAsync("o", "r");

        result.IsOk.Should().BeTrue();
        handler.Requests.First().Url.Should().Contain("/app/installations/789/");
    }

    [Test]
    public async Task AppMode_without_any_installation_id_fails_loud_at_the_factory()
    {
        var (factory, _) = BuildFactory();
        var credential = System.Text.Json.JsonSerializer.Serialize(new
        {
            kind = "app",
            appId = 123,
            privateKeyPem = TestRsaPem(),
        });

        Func<Task> act = () => factory.CreateAsync(Installation(externalId: null), credential);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*installation id*");
    }

    [Test]
    public async Task AppMode_capabilities_keep_PerAppInstallationAuth()
    {
        var (factory, _) = BuildFactory();
        var credential = System.Text.Json.JsonSerializer.Serialize(new
        {
            kind = "app",
            appId = 123,
            privateKeyPem = TestRsaPem(),
            installationId = 456,
        });

        var driver = await factory.CreateAsync(Installation(), credential);

        driver.Capabilities.Should().Contain(PlatformCapability.PerAppInstallationAuth);
    }

    // ================================================================
    // The probe contract, factory-level (red-first for the old stub)
    // ================================================================

    [Test]
    public async Task Probe_fails_on_junk_credential_against_401_server()
    {
        var (factory, handler) = BuildFactory();
        handler.EnqueueRepeating(HttpMethod.Get, "https://api.github.com/user/repos",
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{ "message": "Bad credentials" }"""),
            });

        var driver = await factory.CreateAsync(Installation(), "junk-token");
        var act = async () =>
        {
            await foreach (var _ in driver.Client.ListAccessibleReposAsync())
            {
                break;
            }
        };

        await act.Should().ThrowAsync<GitHubPlatformApiException>(
            "the onboarding probe enumerates accessible repos; a junk token must FAIL it "
            + "instead of the old stub's silent empty sequence");
    }

    // ================================================================
    // Guards + helpers
    // ================================================================

    [Test]
    public async Task Factory_rejects_wrong_kind()
    {
        var (factory, _) = BuildFactory();
        var wrong = new PlatformInstallation(
            Guid.NewGuid(), Guid.NewGuid(), PlatformKind.Gitea,
            "https://gitea.example.com", null);

        Func<Task> act = () => factory.CreateAsync(wrong, "x");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task Factory_rejects_blank_credential()
    {
        var (factory, _) = BuildFactory();

        Func<Task> act = () => factory.CreateAsync(Installation(), "  ");

        await act.Should().ThrowAsync<ArgumentException>(
            "the credential is no longer discarded — an empty credential is a config error");
    }

    [Test]
    public void Factory_rejects_malformed_credential_json()
    {
        var (factory, _) = BuildFactory();

        Func<Task> act = () => factory.CreateAsync(Installation(), "{ not json");

        act.Should().ThrowAsync<ArgumentException>().GetAwaiter().GetResult();
    }

    [TestCase("https://api.github.com", "api.github.com")]
    [TestCase("https://github.acme.corp/api/v3", "github.acme.corp")]
    [TestCase("github.acme.corp", "github.acme.corp")]
    [TestCase("", "github.com")]
    [TestCase(null, "github.com")]
    public void ExtractHost_normalises_base_urls(string? baseUrl, string expected)
    {
        GitHubPlatformDriverFactory.ExtractHost(baseUrl).Should().Be(expected);
    }

    [Test]
    public void Factory_constructor_rejects_null_http_factory()
    {
        Action act = () => new GitHubPlatformDriverFactory(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
