using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Git;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Tests.Git;

/// <summary>
/// Story 38-1 (AC3) + F1 — direct coverage of the per-tenant git-token resolver
/// (tenant BYOK → platform default → fail-closed null). The token itself is
/// load-bearing sensitive; these tests assert only the SOURCE label and the
/// fail-closed contract (never an empty/default token).
/// </summary>
[TestFixture]
public class GitTokenResolverTests
{
    private const string Repo = "acme/widgets";
    private const string ByokToken = "ghp-byok-SECRET-aaaa";
    private const string PlatformToken = "ghp-platform-SECRET-bbbb";

    private Mock<ITenantPlatformInstallationRepository> _installations = null!;
    private Mock<IPlatformCredentialReader> _credentialReader = null!;
    private static readonly Guid Tenant = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _installations = new Mock<ITenantPlatformInstallationRepository>();
        _credentialReader = new Mock<IPlatformCredentialReader>();
    }

    private GitTokenResolver Build(params (string Key, string Value)[] config)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config.Select(c =>
                new KeyValuePair<string, string?>(c.Key, c.Value)))
            .Build();

        return new GitTokenResolver(
            _installations.Object, _credentialReader.Object, configuration,
            NullLogger<GitTokenResolver>.Instance);
    }

    private void ByokInstallation()
        => _installations
            .Setup(r => r.GetByTenantKindAsync(Tenant, "github", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantPlatformInstallation
            {
                TenantId = Tenant,
                PlatformKind = "github",
                CredentialSecretScope = "tenant",
                CredentialSecretName = "github-installation",
            });

    // ── BYOK (tenant tier) ──────────────────────────────────────────────

    [Test]
    public async Task Byok_Present_ReturnsByokSource()
    {
        ByokInstallation();
        _credentialReader
            .Setup(c => c.ReadActivePlaintextAsync("tenant", Tenant, "github-installation", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ByokToken);

        var result = await Build(("GitHub:Token", PlatformToken)).ResolveAsync(Tenant, Repo);

        result.Should().NotBeNull();
        result!.Source.Should().Be(GitCredentialSources.Byok);
        result.Token.Should().Be(ByokToken, "BYOK wins over the platform default when present");
    }

    // ── platform (system tier) ──────────────────────────────────────────

    [Test]
    public async Task ByokAbsent_PlatformTokenSet_ReturnsPlatformSource()
    {
        // No BYOK installation for the tenant → falls to the platform default.
        _installations
            .Setup(r => r.GetByTenantKindAsync(Tenant, "github", It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantPlatformInstallation?)null);

        var result = await Build(("GitHub:Token", PlatformToken)).ResolveAsync(Tenant, Repo);

        result.Should().NotBeNull();
        result!.Source.Should().Be(GitCredentialSources.Platform);
        result.Token.Should().Be(PlatformToken);
    }

    [Test]
    public async Task NullTenant_PlatformTokenSet_ReturnsPlatformSource()
    {
        // A null acting tenant skips the BYOK tier entirely and uses the platform
        // default (system tier).
        var result = await Build(("GitHub:Token", PlatformToken)).ResolveAsync(null, Repo);

        result.Should().NotBeNull();
        result!.Source.Should().Be(GitCredentialSources.Platform);
        _installations.Verify(
            r => r.GetByTenantKindAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "a null tenant has no BYOK tier to consult");
    }

    // ── error tier (fail-closed) ────────────────────────────────────────

    [Test]
    public async Task Neither_ReturnsNull_FailClosed()
    {
        // No BYOK, no platform GitHub:Token → null ⇒ the caller returns 503
        // GIT_TOKEN_UNAVAILABLE, never an empty/default token.
        _installations
            .Setup(r => r.GetByTenantKindAsync(Tenant, "github", It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantPlatformInstallation?)null);

        var result = await Build(/* no GitHub:Token */).ResolveAsync(Tenant, Repo);

        result.Should().BeNull("neither tier resolvable ⇒ fail-closed null");
    }

    [Test]
    public async Task Byok_InstallationPresent_ButBlankSecret_FallsBackToPlatform()
    {
        // An installation row whose credential read yields blank is NOT a valid
        // BYOK token → fall through to the platform tier (never a blank token).
        ByokInstallation();
        _credentialReader
            .Setup(c => c.ReadActivePlaintextAsync("tenant", Tenant, "github-installation", It.IsAny<CancellationToken>()))
            .ReturnsAsync("   ");

        var result = await Build(("GitHub:Token", PlatformToken)).ResolveAsync(Tenant, Repo);

        result.Should().NotBeNull();
        result!.Source.Should().Be(GitCredentialSources.Platform);
    }
}
