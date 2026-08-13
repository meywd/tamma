using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Tests;

/// <summary>
/// Story 31-2 Step 5 — <see cref="PlatformResolver"/> unit tests.
/// Validates AC3 (resolver hands back drivers via keyed-DI factory),
/// AC4 (caching, credential read goes through the secret-store seam),
/// and AC9 (returns null for unknown tenant; caches between calls;
/// cross-tenant lookups never bleed).
/// </summary>
[TestFixture]
public class PlatformResolverTests
{
    private const string CredentialPlaintext = "ghs_test_token_12345";

    /// <summary>
    /// Stubbed factory that records its construction arguments and
    /// returns a <see cref="StubDriver"/> stamped with the
    /// installation context so tests can assert routing went through
    /// the expected installation row.
    /// </summary>
    private sealed class StubDriverFactory : IGitPlatformDriverFactory
    {
        public PlatformKind Kind { get; }
        public List<(PlatformInstallation Installation, string Plaintext)> Calls
            { get; } = new();

        public StubDriverFactory(PlatformKind kind) { Kind = kind; }

        public Task<IGitPlatformDriver> CreateAsync(
            PlatformInstallation installation,
            string credentialPlaintext,
            CancellationToken ct = default)
        {
            Calls.Add((installation, credentialPlaintext));
            IGitPlatformDriver driver = new StubDriver(Kind, installation);
            return Task.FromResult(driver);
        }
    }

    private sealed class StubDriver(PlatformKind kind, PlatformInstallation installation)
        : IGitPlatformDriver
    {
        public PlatformKind Kind { get; } = kind;
        public PlatformInstallation Installation { get; } = installation;
        public IGitPlatformClient Client { get; } = new NullGitPlatformDriver().Client;
        public IGitPlatformActionsClient? Actions => null;
        public IReadOnlySet<PlatformCapability> Capabilities { get; } =
            new HashSet<PlatformCapability>();
    }

    private static (
        PlatformResolver Resolver,
        Mock<ITenantPlatformInstallationRepository> Repo,
        Mock<IPlatformCredentialReader> Credentials,
        StubDriverFactory Factory,
        PlatformDriverCache Cache,
        ServiceProvider Provider)
        BuildResolver(
            PlatformKind kind = PlatformKind.GitHub,
            string? plaintextOverride = null)
    {
        var repo = new Mock<ITenantPlatformInstallationRepository>(MockBehavior.Strict);
        var credentials = new Mock<IPlatformCredentialReader>(MockBehavior.Strict);
        credentials
            .Setup(c => c.ReadActivePlaintextAsync(
                It.IsAny<string>(),
                It.IsAny<Guid?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(plaintextOverride ?? CredentialPlaintext);
        var factory = new StubDriverFactory(kind);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IGitPlatformDriverFactory>(kind, factory);
        var provider = services.BuildServiceProvider();

        var cache = new PlatformDriverCache(
            new PlatformDriverCacheOptions { MaxEntries = 64 });

        var resolver = new PlatformResolver(
            repo.Object,
            credentials.Object,
            provider,
            cache,
            NullLogger<PlatformResolver>.Instance);

        return (resolver, repo, credentials, factory, cache, provider);
    }

    private static TenantPlatformInstallation MakeRow(
        Guid tenantId, PlatformKind kind = PlatformKind.GitHub) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PlatformKind = PlatformResolver.ToWireKind(kind),
            BaseUrl = "https://api.github.com",
            InstallationExternalId = "12345",
            CredentialSecretScope = "tenant",
            CredentialSecretName = "github-installation/12345",
            Status = "connected",
            IsPrimary = true,
            MetadataJson = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    // ── AC9: null path ────────────────────────────────────────────────

    [Test]
    public async Task ResolveForTenantAsync_NoInstallation_ReturnsNull()
    {
        var (resolver, repo, _, _, cache, provider) = BuildResolver();
        using var _ = cache;
        using (provider)
        {
            var tenantId = Guid.NewGuid();
            repo.Setup(r => r.GetByTenantPrimaryAsync(tenantId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((TenantPlatformInstallation?)null);

            var result = await resolver.ResolveForTenantAsync(tenantId);

            result.Should().BeNull();
        }
    }

    [Test]
    public async Task ResolveForTenantAsync_KindNoRow_ReturnsNull()
    {
        var (resolver, repo, _, _, cache, provider) = BuildResolver();
        using var _ = cache;
        using (provider)
        {
            var tenantId = Guid.NewGuid();
            repo.Setup(r => r.GetByTenantKindAsync(
                    tenantId, "github", It.IsAny<CancellationToken>()))
                .ReturnsAsync((TenantPlatformInstallation?)null);

            var result = await resolver.ResolveForTenantAsync(
                tenantId, PlatformKind.GitHub);

            result.Should().BeNull();
        }
    }

    // ── AC3: hands back a driver via the keyed factory ────────────────

    [Test]
    public async Task Registry_ReturnsDriver_ForRegisteredInstallation()
    {
        var (resolver, repo, credentials, factory, cache, provider) =
            BuildResolver(PlatformKind.GitHub);
        using var _ = cache;
        using (provider)
        {
            var tenantId = Guid.NewGuid();
            var row = MakeRow(tenantId);
            repo.Setup(r => r.GetByTenantPrimaryAsync(tenantId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(row);

            var result = await resolver.ResolveForTenantAsync(tenantId);

            result.Should().NotBeNull();
            result!.Kind.Should().Be(PlatformKind.GitHub);
            (result as StubDriver)!.Installation.TenantId.Should().Be(tenantId);
            (result as StubDriver)!.Installation.BaseUrl.Should().Be(row.BaseUrl);
            factory.Calls.Should().HaveCount(1);
            factory.Calls[0].Plaintext.Should().Be(CredentialPlaintext);
        }
    }

    // ── AC4: cache between calls ──────────────────────────────────────

    [Test]
    public async Task Registry_CachesDriver_BetweenCalls()
    {
        var (resolver, repo, credentials, factory, cache, provider) =
            BuildResolver(PlatformKind.GitHub);
        using var _ = cache;
        using (provider)
        {
            var tenantId = Guid.NewGuid();
            var row = MakeRow(tenantId);
            repo.Setup(r => r.GetByTenantKindAsync(
                    tenantId, "github", It.IsAny<CancellationToken>()))
                .ReturnsAsync(row);

            var first = await resolver.ResolveForTenantAsync(
                tenantId, PlatformKind.GitHub);
            var second = await resolver.ResolveForTenantAsync(
                tenantId, PlatformKind.GitHub);

            first.Should().BeSameAs(second);
            // Factory invoked exactly once — second call hit the cache.
            factory.Calls.Should().HaveCount(1);
            // Credential read also exactly once.
            credentials.Verify(
                c => c.ReadActivePlaintextAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }

    [Test]
    public async Task Registry_LoadsAuth_FromSecretStore()
    {
        var (resolver, repo, credentials, factory, cache, provider) =
            BuildResolver(PlatformKind.GitHub);
        using var _ = cache;
        using (provider)
        {
            var tenantId = Guid.NewGuid();
            var row = MakeRow(tenantId);
            row.CredentialSecretScope = "tenant";
            row.CredentialSecretName = "gh-bot-token";

            repo.Setup(r => r.GetByTenantPrimaryAsync(tenantId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(row);

            var result = await resolver.ResolveForTenantAsync(tenantId);

            result.Should().NotBeNull();
            // The credential reader received exactly the (scope, tenantId, name)
            // tuple stored on the row — no bypass.
            credentials.Verify(
                c => c.ReadActivePlaintextAsync(
                    "tenant",
                    tenantId,
                    "gh-bot-token",
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }

    [Test]
    public async Task Registry_PlatformScopeSecret_PassesNullTenantId()
    {
        var (resolver, repo, credentials, factory, cache, provider) =
            BuildResolver(PlatformKind.GitHub);
        using var _ = cache;
        using (provider)
        {
            var tenantId = Guid.NewGuid();
            var row = MakeRow(tenantId);
            row.CredentialSecretScope = "platform";
            row.CredentialSecretName = "github-app/private-key";

            repo.Setup(r => r.GetByTenantPrimaryAsync(tenantId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(row);

            await resolver.ResolveForTenantAsync(tenantId);

            // Platform-scope secrets MUST pass tenantId=null per
            // ISecretStore invariant.
            credentials.Verify(
                c => c.ReadActivePlaintextAsync(
                    "platform",
                    (Guid?)null,
                    "github-app/private-key",
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }

    // ── AC9: missing credential plaintext ─────────────────────────────

    [Test]
    public async Task Registry_NullPlaintext_ReturnsNullDriver()
    {
        var (resolver, repo, credentials, factory, cache, provider) =
            BuildResolver(PlatformKind.GitHub, plaintextOverride: null);
        using var _ = cache;
        using (provider)
        {
            credentials
                .Setup(c => c.ReadActivePlaintextAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

            var tenantId = Guid.NewGuid();
            var row = MakeRow(tenantId);
            repo.Setup(r => r.GetByTenantPrimaryAsync(tenantId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(row);

            var result = await resolver.ResolveForTenantAsync(tenantId);

            result.Should().BeNull();
            factory.Calls.Should().BeEmpty();
        }
    }

    // ── AC9: cross-tenant safety ─────────────────────────────────────

    [Test]
    public async Task Registry_ThrowsNotRegistered_ForUnknownTenantKind()
    {
        // Misconfigured host: drivers for the requested kind never
        // self-registered. The resolver should fail loud, not silent.
        var repo = new Mock<ITenantPlatformInstallationRepository>(MockBehavior.Strict);
        var credentials = new Mock<IPlatformCredentialReader>(MockBehavior.Strict);
        credentials
            .Setup(c => c.ReadActivePlaintextAsync(
                It.IsAny<string>(),
                It.IsAny<Guid?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CredentialPlaintext);

        var services = new ServiceCollection();
        // Intentionally NO factory registered.
        using var provider = services.BuildServiceProvider();
        using var cache = new PlatformDriverCache();

        var resolver = new PlatformResolver(
            repo.Object,
            credentials.Object,
            provider,
            cache,
            NullLogger<PlatformResolver>.Instance);

        var tenantId = Guid.NewGuid();
        var row = MakeRow(tenantId, PlatformKind.GitLab);
        repo.Setup(r => r.GetByTenantPrimaryAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);

        var act = async () => await resolver.ResolveForTenantAsync(tenantId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No IGitPlatformDriverFactory registered for PlatformKind=GitLab*");
    }

    [Test]
    public async Task Registry_SpoofedCrossTenantId_ReturnsNull()
    {
        var (resolver, repo, _, _, cache, provider) = BuildResolver();
        using var _ = cache;
        using (provider)
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            // Tenant B has no registered installation; even though A
            // has one, asking for B returns null. The repo enforces
            // the tenant boundary at the query level.
            var rowA = MakeRow(tenantA);
            repo.Setup(r => r.GetByTenantPrimaryAsync(tenantA, It.IsAny<CancellationToken>()))
                .ReturnsAsync(rowA);
            repo.Setup(r => r.GetByTenantPrimaryAsync(tenantB, It.IsAny<CancellationToken>()))
                .ReturnsAsync((TenantPlatformInstallation?)null);

            var resultA = await resolver.ResolveForTenantAsync(tenantA);
            var resultB = await resolver.ResolveForTenantAsync(tenantB);

            resultA.Should().NotBeNull();
            resultB.Should().BeNull();
        }
    }

    // ── ListForTenantAsync ────────────────────────────────────────────

    [Test]
    public async Task Registry_ListInstallations_ReturnsAllForTenant()
    {
        var (resolver, repo, _, _, cache, provider) = BuildResolver();
        using var _ = cache;
        using (provider)
        {
            var tenantId = Guid.NewGuid();
            var rows = new List<TenantPlatformInstallation>
            {
                MakeRow(tenantId, PlatformKind.GitHub),
                MakeRow(tenantId, PlatformKind.Gitea),
            };
            repo.Setup(r => r.ListByTenantAsync(tenantId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(rows);

            var list = await resolver.ListForTenantAsync(tenantId);

            list.Should().HaveCount(2);
            list.Select(i => i.Kind).Should().BeEquivalentTo(
                new[] { PlatformKind.GitHub, PlatformKind.Gitea });
            list.Should().AllSatisfy(i => i.TenantId.Should().Be(tenantId));
        }
    }

    [Test]
    public async Task Registry_ListInstallations_SkipsUnknownKinds()
    {
        var (resolver, repo, _, _, cache, provider) = BuildResolver();
        using var _ = cache;
        using (provider)
        {
            var tenantId = Guid.NewGuid();
            var legitRow = MakeRow(tenantId);
            var futureRow = MakeRow(tenantId);
            futureRow.PlatformKind = "future_platform"; // unknown wire kind

            repo.Setup(r => r.ListByTenantAsync(tenantId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<TenantPlatformInstallation>
                {
                    legitRow, futureRow,
                });

            var list = await resolver.ListForTenantAsync(tenantId);

            // Future-kind row dropped; legit row surfaces.
            list.Should().HaveCount(1);
            list[0].Kind.Should().Be(PlatformKind.GitHub);
        }
    }

    // ── ResolveForWebhookAsync ────────────────────────────────────────

    [Test]
    public async Task Registry_ResolveForWebhook_ResolvesByExternalId()
    {
        var (resolver, repo, _, factory, cache, provider) = BuildResolver();
        using var _ = cache;
        using (provider)
        {
            var tenantId = Guid.NewGuid();
            var row = MakeRow(tenantId);
            row.InstallationExternalId = "abcdef-12345";

            repo.Setup(r => r.GetByExternalIdAsync(
                    "github", "abcdef-12345", It.IsAny<CancellationToken>()))
                .ReturnsAsync(row);

            var result = await resolver.ResolveForWebhookAsync(
                PlatformKind.GitHub, "abcdef-12345");

            result.Should().NotBeNull();
            result!.Kind.Should().Be(PlatformKind.GitHub);
        }
    }

    [Test]
    public async Task Registry_ResolveForWebhook_NoMatchReturnsNull()
    {
        var (resolver, repo, _, _, cache, provider) = BuildResolver();
        using var _ = cache;
        using (provider)
        {
            repo.Setup(r => r.GetByExternalIdAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((TenantPlatformInstallation?)null);

            var result = await resolver.ResolveForWebhookAsync(
                PlatformKind.GitHub, "unknown");

            result.Should().BeNull();
        }
    }

    // ── ResolveByInstallationIdAsync ──────────────────────────────────

    [Test]
    public async Task Registry_ResolveById_RoundTrips()
    {
        var (resolver, repo, _, _, cache, provider) = BuildResolver();
        using var _ = cache;
        using (provider)
        {
            var tenantId = Guid.NewGuid();
            var row = MakeRow(tenantId);

            repo.Setup(r => r.GetByIdAsync(row.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(row);

            var result = await resolver.ResolveByInstallationIdAsync(row.Id);

            result.Should().NotBeNull();
            (result as StubDriver)!.Installation.Id.Should().Be(row.Id);
        }
    }

    // ── Cache invalidation ────────────────────────────────────────────

    [Test]
    public async Task Registry_CacheInvalidation_ForcesRecompose()
    {
        var (resolver, repo, credentials, factory, cache, provider) =
            BuildResolver(PlatformKind.GitHub);
        using var _ = cache;
        using (provider)
        {
            var tenantId = Guid.NewGuid();
            var row = MakeRow(tenantId);
            repo.Setup(r => r.GetByTenantKindAsync(
                    tenantId, "github", It.IsAny<CancellationToken>()))
                .ReturnsAsync(row);

            var first = await resolver.ResolveForTenantAsync(
                tenantId, PlatformKind.GitHub);
            await cache.InvalidateTenantAsync(tenantId);
            var second = await resolver.ResolveForTenantAsync(
                tenantId, PlatformKind.GitHub);

            // Both calls returned a driver, but the cache miss
            // re-composed (factory invoked twice).
            first.Should().NotBeNull();
            second.Should().NotBeNull();
            factory.Calls.Should().HaveCount(2);
        }
    }

    // ── Wire kind round-trip ──────────────────────────────────────────

    [Test]
    public void ToWireKind_RoundTripsViaTryParseKind()
    {
        foreach (PlatformKind kind in Enum.GetValues<PlatformKind>())
        {
            var wire = PlatformResolver.ToWireKind(kind);
            PlatformResolver.TryParseKind(wire, out var parsed).Should().BeTrue();
            parsed.Should().Be(kind);
        }
    }

    [Test]
    public void TryParseKind_OnUnknown_ReturnsFalse()
    {
        PlatformResolver.TryParseKind("future_platform", out _).Should().BeFalse();
        PlatformResolver.TryParseKind("", out _).Should().BeFalse();
    }

    // ── Epic 31 review (F-high) — per-repo installation resolution: a
    //    tenant with the App on multiple installations must resolve the
    //    installation that OWNS the repo (an App installation token cannot
    //    see a sibling installation's repos), tenant-scoped, and without
    //    the two installations' drivers ever being served for each other
    //    through the (tenant, kind) cache slot. ──

    [Test]
    public async Task ResolveForRepoInstallation_CrossTenantRow_ReturnsNull()
    {
        var (resolver, repo, _, factory, _, provider) = BuildResolver();
        using var _p = provider;
        var callerTenant = Guid.NewGuid();
        var otherTenantsRow = MakeRow(Guid.NewGuid());
        otherTenantsRow.InstallationExternalId = "999";
        repo.Setup(r => r.GetByExternalIdAsync("github", "999", It.IsAny<CancellationToken>()))
            .ReturnsAsync(otherTenantsRow);

        var driver = await resolver.ResolveForRepoInstallationAsync(
            callerTenant, PlatformKind.GitHub, "999");

        driver.Should().BeNull(
            "a repo registry row pointing at another tenant's installation must never mint "
            + "that tenant's driver for this caller");
        factory.Calls.Should().BeEmpty();
    }

    [Test]
    public async Task ResolveForRepoInstallation_NonPrimaryRow_ComposesThatRow_AndNeverPoisonsThePrimaryCacheSlot()
    {
        var (resolver, repo, _, factory, _, provider) = BuildResolver();
        using var _p = provider;
        var tenant = Guid.NewGuid();
        var primaryRow = MakeRow(tenant);
        primaryRow.InstallationExternalId = "111";
        var siblingRow = MakeRow(tenant);
        siblingRow.InstallationExternalId = "222";

        repo.Setup(r => r.GetByExternalIdAsync("github", "222", It.IsAny<CancellationToken>()))
            .ReturnsAsync(siblingRow);
        repo.Setup(r => r.GetByTenantKindAsync(tenant, "github", It.IsAny<CancellationToken>()))
            .ReturnsAsync(primaryRow);

        var siblingDriver = await resolver.ResolveForRepoInstallationAsync(
            tenant, PlatformKind.GitHub, "222");

        siblingDriver.Should().BeOfType<StubDriver>()
            .Which.Installation.InstallationExternalId.Should().Be("222",
                "the repo's OWN installation must be composed — riding the primary would 404");

        // The (tenant, kind) cache slot must still belong to the PRIMARY
        // row: a tenant-kind resolve after the per-repo one composes the
        // primary installation, never serves the sibling's driver.
        var primaryDriver = await resolver.ResolveForTenantAsync(tenant, PlatformKind.GitHub);
        primaryDriver.Should().BeOfType<StubDriver>()
            .Which.Installation.InstallationExternalId.Should().Be("111",
                "the sibling compose must not have been cached into the primary's slot");
    }

    [Test]
    public async Task ResolveForRepoInstallation_PrimaryRow_RidesTheCache()
    {
        var (resolver, repo, _, factory, _, provider) = BuildResolver();
        using var _p = provider;
        var tenant = Guid.NewGuid();
        var primaryRow = MakeRow(tenant);
        primaryRow.InstallationExternalId = "111";

        repo.Setup(r => r.GetByExternalIdAsync("github", "111", It.IsAny<CancellationToken>()))
            .ReturnsAsync(primaryRow);
        repo.Setup(r => r.GetByTenantKindAsync(tenant, "github", It.IsAny<CancellationToken>()))
            .ReturnsAsync(primaryRow);

        var first = await resolver.ResolveForRepoInstallationAsync(
            tenant, PlatformKind.GitHub, "111");
        var second = await resolver.ResolveForRepoInstallationAsync(
            tenant, PlatformKind.GitHub, "111");

        first.Should().NotBeNull();
        second.Should().BeSameAs(first,
            "the primary row owns the (tenant, kind) cache slot — repeat resolutions hit it");
        factory.Calls.Should().HaveCount(1);
    }
}
