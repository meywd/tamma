using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Tests;

/// <summary>
/// Epic 31 P2 — <see cref="PlatformResolver.ResolveForMediationAsync"/>:
/// the tenant-installation tier, the <c>Platform:</c> CONFIG-BACKED tier
/// (single-user activation — synthesized in-memory installation, never
/// persisted), and the fail-closed null. Covers the two-scoping answers
/// CLAUDE.md requires and the credential-source LABELS the mediation
/// stamps on results + audit events.
/// </summary>
[TestFixture]
public class PlatformResolverMediationTierTests
{
    private const string ConfigCredential = "ghp_config_token_0000";
    private const string RowCredential = "ghp_row_token_1111";

    private sealed class StubDriverFactory : IGitPlatformDriverFactory
    {
        public PlatformKind Kind { get; }
        public List<(PlatformInstallation Installation, string Plaintext)> Calls { get; } = new();

        public StubDriverFactory(PlatformKind kind) { Kind = kind; }

        public Task<IGitPlatformDriver> CreateAsync(
            PlatformInstallation installation, string credentialPlaintext, CancellationToken ct = default)
        {
            Calls.Add((installation, credentialPlaintext));
            IGitPlatformDriver driver = new StubDriver(Kind);
            return Task.FromResult(driver);
        }
    }

    private sealed class StubDriver(PlatformKind kind) : IGitPlatformDriver
    {
        public PlatformKind Kind { get; } = kind;
        public IGitPlatformClient Client { get; } = new NullGitPlatformDriver().Client;
        public IGitPlatformActionsClient? Actions => null;
        public IReadOnlySet<PlatformCapability> Capabilities { get; } = new HashSet<PlatformCapability>();
    }

    private static (PlatformResolver Resolver,
        Mock<ITenantPlatformInstallationRepository> Repo,
        Mock<IPlatformCredentialReader> Credentials,
        StubDriverFactory Factory)
        Build(SingleUserPlatformOptions? options, PlatformKind kind = PlatformKind.GitHub)
    {
        var repo = new Mock<ITenantPlatformInstallationRepository>(MockBehavior.Loose);
        var credentials = new Mock<IPlatformCredentialReader>(MockBehavior.Loose);
        var factory = new StubDriverFactory(kind);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IGitPlatformDriverFactory>(kind, factory);
        var provider = services.BuildServiceProvider();

        var resolver = new PlatformResolver(
            repo.Object,
            credentials.Object,
            provider,
            new PlatformDriverCache(new PlatformDriverCacheOptions { MaxEntries = 16 }),
            NullLogger<PlatformResolver>.Instance,
            options);

        return (resolver, repo, credentials, factory);
    }

    private static TenantPlatformInstallation Row(Guid tenantId, string kind = "github") => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        PlatformKind = kind,
        BaseUrl = "https://gitea.example",
        InstallationExternalId = "77",
        CredentialSecretScope = "tenant",
        CredentialSecretName = $"{kind}/install-77",
        Status = "connected",
        IsPrimary = true,
        MetadataJson = "{}",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ── single-user scoping answer: config-only activation ──────────────

    [Test]
    public async Task SingleUser_ConfigOnly_NoRow_ResolvesWorkingDriver_NothingPersisted()
    {
        // Fresh single-user deployment: ONLY the Platform: section, no
        // onboarding call, no DB row.
        var (resolver, repo, _, factory) = Build(new SingleUserPlatformOptions
        {
            Kind = "github",
            BaseUrl = "https://api.github.com",
            Credential = ConfigCredential,
        });

        var resolution = await resolver.ResolveForMediationAsync(tenantId: null);

        resolution.Should().NotBeNull("config alone must activate the platform (owner point 1)");
        resolution!.Source.Should().Be(MediationCredentialSource.PlatformDefault);
        resolution.Driver.Kind.Should().Be(PlatformKind.GitHub);

        factory.Calls.Should().ContainSingle();
        factory.Calls[0].Plaintext.Should().Be(ConfigCredential);
        factory.Calls[0].Installation.BaseUrl.Should().Be("https://api.github.com");

        // NEVER persisted — no config↔DB drift, idempotent by construction.
        repo.Verify(r => r.CreateAsync(It.IsAny<TenantPlatformInstallation>(), It.IsAny<CancellationToken>()), Times.Never);
        repo.Verify(r => r.UpdateAsync(It.IsAny<TenantPlatformInstallation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task SingleUser_ConfigOnly_SecondResolve_IsCached_OneFactoryCall()
    {
        var (resolver, _, _, factory) = Build(new SingleUserPlatformOptions
        {
            Kind = "github",
            Credential = ConfigCredential,
        });

        var first = await resolver.ResolveForMediationAsync(null);
        var second = await resolver.ResolveForMediationAsync(null);

        first!.Driver.Should().BeSameAs(second!.Driver, "the config-tier driver is cached");
        factory.Calls.Should().HaveCount(1);
    }

    [Test]
    public async Task ConfigCredential_FromSecretCabinet_WhenNotInlined()
    {
        var (resolver, _, credentials, factory) = Build(new SingleUserPlatformOptions
        {
            Kind = "github",
            CredentialSecretName = "github/op-token",
            CredentialSecretScope = "platform",
        });
        credentials
            .Setup(c => c.ReadActivePlaintextAsync("platform", null, "github/op-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfigCredential);

        var resolution = await resolver.ResolveForMediationAsync(null);

        resolution.Should().NotBeNull();
        factory.Calls.Should().ContainSingle().Which.Plaintext.Should().Be(ConfigCredential);
    }

    // ── SaaS scoping answer: the tenant row wins; config is the system tier ──

    [Test]
    public async Task Tenant_WithPrimaryRow_ResolvesThroughTheRow_SourceIsTenantInstallation()
    {
        var tenant = Guid.NewGuid();
        var (resolver, repo, credentials, factory) = Build(new SingleUserPlatformOptions
        {
            Kind = "gitea",
            Credential = ConfigCredential,
        }, PlatformKind.Gitea);

        repo.Setup(r => r.GetByTenantPrimaryAsync(tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Row(tenant, "gitea"));
        credentials
            .Setup(c => c.ReadActivePlaintextAsync("tenant", tenant, "gitea/install-77", It.IsAny<CancellationToken>()))
            .ReturnsAsync(RowCredential);

        var resolution = await resolver.ResolveForMediationAsync(tenant);

        resolution.Should().NotBeNull();
        resolution!.Source.Should().Be(MediationCredentialSource.TenantInstallation,
            "the tenant's own installation (BYOK) always wins over the deployment config");
        factory.Calls.Should().ContainSingle().Which.Plaintext.Should().Be(RowCredential);
    }

    [Test]
    public async Task Tenant_NonGitHubPrimaryRow_ResolvesItsOwnKind()
    {
        // A BYOK Gitea-only tenant resolves a usable Gitea driver — the pre-P2
        // hardcoded "github" filter is gone.
        var tenant = Guid.NewGuid();
        var (resolver, repo, credentials, factory) = Build(options: null, PlatformKind.Gitea);
        repo.Setup(r => r.GetByTenantPrimaryAsync(tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Row(tenant, "gitea"));
        credentials
            .Setup(c => c.ReadActivePlaintextAsync("tenant", tenant, "gitea/install-77", It.IsAny<CancellationToken>()))
            .ReturnsAsync(RowCredential);

        var resolution = await resolver.ResolveForMediationAsync(tenant);

        resolution.Should().NotBeNull();
        resolution!.Driver.Kind.Should().Be(PlatformKind.Gitea);
        resolution.Source.Should().Be(MediationCredentialSource.TenantInstallation);
    }

    [Test]
    public async Task Tenant_WithoutRow_FallsBackToConfigTier_SourceIsPlatformDefault()
    {
        var tenant = Guid.NewGuid();
        var (resolver, repo, _, _) = Build(new SingleUserPlatformOptions
        {
            Kind = "github",
            Credential = ConfigCredential,
        });
        repo.Setup(r => r.GetByTenantPrimaryAsync(tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantPlatformInstallation?)null);

        var resolution = await resolver.ResolveForMediationAsync(tenant);

        resolution.Should().NotBeNull("the config tier is the SaaS system tier — the same "
            + "semantics the pre-P2 GitHub:Token fallback had");
        resolution!.Source.Should().Be(MediationCredentialSource.PlatformDefault);
    }

    // ── integration: config-only activation over the REAL GitHub driver ──

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"name\":\"widgets\",\"full_name\":\"acme/widgets\",\"default_branch\":\"main\","
                    + "\"private\":false,\"owner\":{\"login\":\"acme\"}}",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    [Test]
    public async Task Integration_FreshSingleUserConfig_RealGitHubFactory_WorkingDriver()
    {
        // P2 acceptance: a fresh single-user deployment with ONLY the Platform:
        // config (no onboarding API call, no DB row) resolves a driver whose
        // client actually speaks the platform protocol.
        var handler = new ScriptedHandler();
        var services = new ServiceCollection();
        services.AddHttpClient(
                Tamma.Platforms.GitHub.GitHubPlatformDriverFactory.GitHubHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddSingleton<IGitPlatformDriverFactory>(sp =>
            new Tamma.Platforms.GitHub.GitHubPlatformDriverFactory(
                sp.GetRequiredService<IHttpClientFactory>()));
        services.AddKeyedSingleton<IGitPlatformDriverFactory>(PlatformKind.GitHub,
            (sp, _) => sp.GetRequiredService<IGitPlatformDriverFactory>());
        var provider = services.BuildServiceProvider();

        var repo = new Mock<ITenantPlatformInstallationRepository>(MockBehavior.Loose);
        var resolver = new PlatformResolver(
            repo.Object,
            new Mock<IPlatformCredentialReader>(MockBehavior.Loose).Object,
            provider,
            new PlatformDriverCache(new PlatformDriverCacheOptions { MaxEntries = 16 }),
            NullLogger<PlatformResolver>.Instance,
            new SingleUserPlatformOptions
            {
                Kind = "github",
                Credential = "ghp_single_user_pat",
            });

        var resolution = await resolver.ResolveForMediationAsync(null);

        resolution.Should().NotBeNull();
        resolution!.Driver.Kind.Should().Be(PlatformKind.GitHub);
        resolution.Driver.Capabilities.Should().Contain(PlatformCapability.PrLifecycle,
            "the real P1 driver advertises its real capability set");

        var repoRead = await resolution.Driver.Client.GetRepoAsync("acme", "widgets");
        repoRead.IsOk.Should().BeTrue("the config-activated driver makes real platform calls");
        repoRead.GetValueOrDefault()!.Name.Should().Be("widgets");
        handler.Requests.Should().BeGreaterThan(0);
    }

    // ── fail-closed ─────────────────────────────────────────────────────

    [Test]
    public async Task NoRow_NoConfig_ReturnsNull_FailClosed()
    {
        var tenant = Guid.NewGuid();
        var (resolver, repo, _, _) = Build(options: null);
        repo.Setup(r => r.GetByTenantPrimaryAsync(tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantPlatformInstallation?)null);

        (await resolver.ResolveForMediationAsync(tenant)).Should().BeNull(
            "neither tier resolvable ⇒ null ⇒ mediation returns 503 GIT_TOKEN_UNAVAILABLE");
        (await resolver.ResolveForMediationAsync(null)).Should().BeNull();
    }

    [Test]
    public async Task Config_WithUnknownKind_IsIgnored_FailClosed()
    {
        var (resolver, _, _, _) = Build(new SingleUserPlatformOptions
        {
            Kind = "subversion",
            Credential = ConfigCredential,
        });

        (await resolver.ResolveForMediationAsync(null)).Should().BeNull();
    }

    [Test]
    public async Task Config_WithNoResolvableCredential_FailsClosed()
    {
        var (resolver, _, credentials, _) = Build(new SingleUserPlatformOptions
        {
            Kind = "github",
            CredentialSecretName = "github/missing",
        });
        credentials
            .Setup(c => c.ReadActivePlaintextAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        (await resolver.ResolveForMediationAsync(null)).Should().BeNull();
    }
}
