using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Platforms;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Reveal;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Tests.Platforms;

/// <summary>
/// Epic 31 P2 (seam 14) — the registry-unification bridge: when the GitHub App
/// callback links an installation to a tenant, a
/// <c>tenant_platform_installations</c> row is ALSO upserted (kind=github,
/// external id = the installation id, credential = the <c>{"kind":"app",…}</c>
/// App-installation REFERENCE — never a plaintext PAT), idempotently. Plus the
/// P2 acceptance that an App-installed tenant then RESOLVES through the
/// bridged row (the real GitHub factory parses the reference into App mode).
/// </summary>
[TestFixture]
public class GitHubInstallationBridgeTests
{
    private const long InstallationId = 987654;

    /// <summary>A REAL RSA key — the GitHub App token minter fails loud at
    /// construction on a malformed PEM, so the resolve-through-the-bridge test
    /// needs a parseable one.</summary>
    private static readonly string Pem =
        System.Security.Cryptography.RSA.Create(2048).ExportPkcs8PrivateKeyPem();

    private Mock<ITenantPlatformInstallationRepository> _installations = null!;
    private Mock<ISecretRevealService> _secrets = null!;
    private Mock<IPlatformInstallationEventEmitter> _events = null!;
    private readonly Guid _tenant = Guid.NewGuid();
    private List<TenantPlatformInstallation> _created = null!;
    private List<(string Name, string Plaintext)> _secretsWritten = null!;

    [SetUp]
    public void SetUp()
    {
        _installations = new Mock<ITenantPlatformInstallationRepository>(MockBehavior.Loose);
        _secrets = new Mock<ISecretRevealService>(MockBehavior.Loose);
        _events = new Mock<IPlatformInstallationEventEmitter>(MockBehavior.Loose);
        _created = new List<TenantPlatformInstallation>();
        _secretsWritten = new List<(string, string)>();

        _installations
            .Setup(r => r.CreateAsync(It.IsAny<TenantPlatformInstallation>(), It.IsAny<CancellationToken>()))
            .Callback<TenantPlatformInstallation, CancellationToken>((row, _) => _created.Add(row))
            .ReturnsAsync((TenantPlatformInstallation row, CancellationToken _) => row);

        _secrets
            .Setup(s => s.IssueCreateAsync(
                It.IsAny<string>(), It.IsAny<SecretScope>(), It.IsAny<Guid?>(), It.IsAny<SecretPurpose>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyList<ConsumerRef>?>(), It.IsAny<Guid>(),
                It.IsAny<RotationSchedule?>(), It.IsAny<CancellationToken>()))
            .Callback((string name, SecretScope _, Guid? _, SecretPurpose _, string plaintext,
                IReadOnlyList<ConsumerRef>? _, Guid _, RotationSchedule? _, CancellationToken _) =>
                _secretsWritten.Add((name, plaintext)))
            .ReturnsAsync((string name, SecretScope scope, Guid? tenantId, SecretPurpose purpose,
                string _, IReadOnlyList<ConsumerRef>? _, Guid owner, RotationSchedule? _, CancellationToken _) =>
                new RevealTokenIssueResult(
                    new SecretMetadata(
                        Guid.NewGuid(), name, scope, tenantId, purpose,
                        Array.Empty<ConsumerRef>(), owner, RotationSchedule.None,
                        null, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                    "reveal-token", DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    private GitHubInstallationBridge Build(bool appConfigured = true, bool withSecrets = true)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(appConfigured
                ? new Dictionary<string, string?>
                {
                    ["GitHub:AppId"] = "4242",
                    ["GitHub:PrivateKey"] = Pem,
                }
                : new Dictionary<string, string?>())
            .Build();

        return new GitHubInstallationBridge(
            _installations.Object,
            _events.Object,
            config,
            TimeProvider.System,
            NullLogger<GitHubInstallationBridge>.Instance,
            withSecrets ? _secrets.Object : null);
    }

    [Test]
    public async Task Bridge_CreatesRow_WithAppReferenceCredential_AndEmitsConnected()
    {
        var ok = await Build().EnsureBridgedAsync(_tenant, InstallationId);

        ok.Should().BeTrue();
        _created.Should().ContainSingle();
        var row = _created.Single();
        row.TenantId.Should().Be(_tenant);
        row.PlatformKind.Should().Be("github");
        row.InstallationExternalId.Should().Be("987654");
        row.CredentialSecretScope.Should().Be("tenant");
        row.Status.Should().Be("connected");
        row.IsPrimary.Should().BeTrue("no pre-existing github installation for the tenant");

        // The secret plaintext is the GitHubAuth App-installation REFERENCE
        // wire format — NOT a PAT.
        _secretsWritten.Should().ContainSingle();
        var (name, plaintext) = _secretsWritten.Single();
        name.Should().Be(row.CredentialSecretName);
        using var doc = JsonDocument.Parse(plaintext);
        doc.RootElement.GetProperty("kind").GetString().Should().Be("app");
        doc.RootElement.GetProperty("appId").GetInt64().Should().Be(4242);
        doc.RootElement.GetProperty("privateKeyPem").GetString().Should().Be(Pem);
        doc.RootElement.GetProperty("installationId").GetInt64().Should().Be(InstallationId);

        _events.Verify(e => e.EmitConnectedAsync(
            _tenant, PlatformKind.GitHub, row.Id, "987654", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Bridge_HonorsConfiguredGhesApiBaseUrl_OnTheRow()
    {
        // Epic 31 review (F-high) — a GHES deployment's bridged rows must
        // carry GitHub:ApiBaseUrl, not the hardcoded public API host: a
        // driver composed from the row would otherwise send the enterprise
        // credential to api.github.com.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:AppId"] = "4242",
                ["GitHub:PrivateKey"] = Pem,
                ["GitHub:ApiBaseUrl"] = "https://ghe.corp/api/v3",
            })
            .Build();
        var bridge = new GitHubInstallationBridge(
            _installations.Object,
            _events.Object,
            config,
            TimeProvider.System,
            NullLogger<GitHubInstallationBridge>.Instance,
            _secrets.Object);

        var ok = await bridge.EnsureBridgedAsync(_tenant, InstallationId);

        ok.Should().BeTrue();
        _created.Single().BaseUrl.Should().Be("https://ghe.corp/api/v3");
    }

    [Test]
    public async Task Bridge_IsIdempotent_ExistingRowShortCircuits()
    {
        _installations
            .Setup(r => r.GetByExternalIdAsync("github", "987654", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantPlatformInstallation { TenantId = _tenant, PlatformKind = "github" });

        var ok = await Build().EnsureBridgedAsync(_tenant, InstallationId);

        ok.Should().BeTrue("the row already exists — that IS the desired end state");
        _created.Should().BeEmpty();
        _secretsWritten.Should().BeEmpty();
    }

    [Test]
    public async Task Bridge_TenantWithExistingGithubRow_NewRowIsNotPrimary()
    {
        _installations
            .Setup(r => r.GetByTenantKindAsync(_tenant, "github", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantPlatformInstallation { TenantId = _tenant, PlatformKind = "github", IsPrimary = true });

        await Build().EnsureBridgedAsync(_tenant, InstallationId);

        _created.Should().ContainSingle().Which.IsPrimary.Should().BeFalse(
            "an operator-connected BYOK row keeps its primacy");
    }

    [Test]
    public async Task Bridge_WithoutAppConfig_DegradesToLoggedNoOp()
    {
        var ok = await Build(appConfigured: false).EnsureBridgedAsync(_tenant, InstallationId);

        ok.Should().BeFalse();
        _created.Should().BeEmpty();
    }

    [Test]
    public async Task Bridge_WithoutSecretCabinet_DegradesToLoggedNoOp()
    {
        var ok = await Build(withSecrets: false).EnsureBridgedAsync(_tenant, InstallationId);

        ok.Should().BeFalse();
        _created.Should().BeEmpty();
    }

    // ================================================================
    // P2 acceptance — an App-installed tenant RESOLVES through the
    // bridged row: the real GitHub factory parses the bridge's credential
    // reference into App mode (no process-level App singleton involved).
    // ================================================================

    [Test]
    public async Task BridgedRow_ResolvesThroughTheRealGitHubFactory_InAppMode()
    {
        await Build().EnsureBridgedAsync(_tenant, InstallationId);
        var row = _created.Single();
        var (_, credentialJson) = _secretsWritten.Single();

        var services = new ServiceCollection();
        services.AddHttpClient(Tamma.Platforms.GitHub.GitHubPlatformDriverFactory.GitHubHttpClientName);
        services.AddKeyedSingleton<IGitPlatformDriverFactory>(PlatformKind.GitHub, (sp, _) =>
            new Tamma.Platforms.GitHub.GitHubPlatformDriverFactory(
                sp.GetRequiredService<System.Net.Http.IHttpClientFactory>()));
        var provider = services.BuildServiceProvider();

        var repo = new Mock<ITenantPlatformInstallationRepository>(MockBehavior.Loose);
        repo.Setup(r => r.GetByTenantPrimaryAsync(_tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);
        var credentials = new Mock<IPlatformCredentialReader>(MockBehavior.Loose);
        credentials
            .Setup(c => c.ReadActivePlaintextAsync("tenant", _tenant, row.CredentialSecretName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(credentialJson);

        var resolver = new PlatformResolver(
            repo.Object, credentials.Object, provider,
            new PlatformDriverCache(new PlatformDriverCacheOptions { MaxEntries = 8 }),
            NullLogger<PlatformResolver>.Instance);

        var resolution = await resolver.ResolveForMediationAsync(_tenant);

        resolution.Should().NotBeNull("the bridged row makes the App tenant visible to the driver plane");
        resolution!.Source.Should().Be(MediationCredentialSource.TenantInstallation);
        resolution.Driver.Kind.Should().Be(PlatformKind.GitHub);
        resolution.Driver.Capabilities.Should().Contain(PlatformCapability.PerAppInstallationAuth,
            "the factory parsed the {\"kind\":\"app\",...} reference into App-installation mode");
    }
}
