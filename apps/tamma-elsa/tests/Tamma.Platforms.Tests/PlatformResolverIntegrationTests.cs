using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Tests;

/// <summary>
/// Story 31-2 Step 9 — integration test using two fake driver
/// factories (Gitea + GitLab) wired through keyed DI, an EF InMemory
/// repository, and a stub credential reader. Asserts AC10
/// (resolver returns the correct driver per tenant) plus the cache /
/// invalidation flow end-to-end.
/// </summary>
[TestFixture]
public class PlatformResolverIntegrationTests
{
    private sealed class StubFactory(PlatformKind kind) : IGitPlatformDriverFactory
    {
        public PlatformKind Kind { get; } = kind;
        public int CallCount { get; private set; }

        public Task<IGitPlatformDriver> CreateAsync(
            PlatformInstallation installation,
            string credentialPlaintext,
            CancellationToken ct = default)
        {
            CallCount++;
            IGitPlatformDriver driver = new Stamped(Kind, installation, credentialPlaintext);
            return Task.FromResult(driver);
        }
    }

    private sealed class Stamped(
        PlatformKind kind,
        PlatformInstallation installation,
        string credentialPlaintext) : IGitPlatformDriver
    {
        public PlatformKind Kind { get; } = kind;
        public PlatformInstallation Installation { get; } = installation;
        public string CredentialPlaintext { get; } = credentialPlaintext;
        public IGitPlatformClient Client { get; } = new NullGitPlatformDriver().Client;
        public IGitPlatformActionsClient? Actions => null;
        public IReadOnlySet<PlatformCapability> Capabilities { get; } =
            new HashSet<PlatformCapability>();
    }

    private sealed class StubCredentialReader : IPlatformCredentialReader
    {
        public Dictionary<(string Scope, Guid? TenantId, string Name), string> Values { get; }
            = new();

        public Task<string?> ReadActivePlaintextAsync(
            string scope, Guid? tenantId, string name, CancellationToken ct = default)
        {
            return Task.FromResult(
                Values.TryGetValue((scope, tenantId, name), out var v) ? v : null);
        }
    }

    [Test]
    public async Task TwoTenants_DifferentPlatforms_ResolveToDistinctDrivers()
    {
        // Two tenants, each with a different platform installation.
        // The resolver must hand back the correct driver per tenant.
        var tenantGitea = Guid.NewGuid();
        var tenantGitLab = Guid.NewGuid();

        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var db = new ControlPlaneDbContext(options);
        var repo = new TenantPlatformInstallationRepository(db);

        await repo.CreateAsync(new TenantPlatformInstallation
        {
            TenantId = tenantGitea,
            PlatformKind = "gitea",
            BaseUrl = "https://gitea.example.com",
            InstallationExternalId = "g1",
            CredentialSecretScope = "tenant",
            CredentialSecretName = "gitea-token",
            Status = "connected",
            IsPrimary = true,
            MetadataJson = "{}",
        });
        await repo.CreateAsync(new TenantPlatformInstallation
        {
            TenantId = tenantGitLab,
            PlatformKind = "gitlab",
            BaseUrl = "https://gitlab.com",
            InstallationExternalId = "lab1",
            CredentialSecretScope = "tenant",
            CredentialSecretName = "gitlab-token",
            Status = "connected",
            IsPrimary = true,
            MetadataJson = "{}",
        });

        var credentials = new StubCredentialReader();
        credentials.Values[("tenant", tenantGitea, "gitea-token")] = "gitea-secret";
        credentials.Values[("tenant", tenantGitLab, "gitlab-token")] = "gitlab-secret";

        var giteaFactory = new StubFactory(PlatformKind.Gitea);
        var gitlabFactory = new StubFactory(PlatformKind.GitLab);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IGitPlatformDriverFactory>(
            PlatformKind.Gitea, giteaFactory);
        services.AddKeyedSingleton<IGitPlatformDriverFactory>(
            PlatformKind.GitLab, gitlabFactory);
        await using var provider = services.BuildServiceProvider();

        using var cache = new PlatformDriverCache();
        var resolver = new PlatformResolver(
            repo, credentials, provider, cache,
            NullLogger<PlatformResolver>.Instance);

        var giteaDriver = await resolver.ResolveForTenantAsync(tenantGitea);
        var gitlabDriver = await resolver.ResolveForTenantAsync(tenantGitLab);

        giteaDriver.Should().NotBeNull();
        giteaDriver!.Kind.Should().Be(PlatformKind.Gitea);
        ((Stamped)giteaDriver).CredentialPlaintext.Should().Be("gitea-secret");

        gitlabDriver.Should().NotBeNull();
        gitlabDriver!.Kind.Should().Be(PlatformKind.GitLab);
        ((Stamped)gitlabDriver).CredentialPlaintext.Should().Be("gitlab-secret");

        // Each factory called exactly once — caching works.
        giteaFactory.CallCount.Should().Be(1);
        gitlabFactory.CallCount.Should().Be(1);

        // Cross-tenant attempt: ask for tenantGitea's primary, then
        // ask for the explicit GitLab kind on the same tenant — none
        // exists so the resolver returns null without leaking the
        // GitLab tenant's row.
        var crossLookup = await resolver.ResolveForTenantAsync(
            tenantGitea, PlatformKind.GitLab);
        crossLookup.Should().BeNull();
    }

    [Test]
    public async Task RotationFlow_InvalidateThenResolve_RefreshesCredential()
    {
        var tenantId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var db = new ControlPlaneDbContext(options);
        var repo = new TenantPlatformInstallationRepository(db);

        await repo.CreateAsync(new TenantPlatformInstallation
        {
            TenantId = tenantId,
            PlatformKind = "github",
            BaseUrl = "https://api.github.com",
            InstallationExternalId = "1",
            CredentialSecretScope = "tenant",
            CredentialSecretName = "gh-token",
            Status = "connected",
            IsPrimary = true,
            MetadataJson = "{}",
        });

        var credentials = new StubCredentialReader();
        credentials.Values[("tenant", tenantId, "gh-token")] = "old-token";

        var factory = new StubFactory(PlatformKind.GitHub);
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IGitPlatformDriverFactory>(
            PlatformKind.GitHub, factory);
        await using var provider = services.BuildServiceProvider();
        using var cache = new PlatformDriverCache();

        var resolver = new PlatformResolver(
            repo, credentials, provider, cache,
            NullLogger<PlatformResolver>.Instance);

        var first = await resolver.ResolveForTenantAsync(tenantId, PlatformKind.GitHub);
        ((Stamped)first!).CredentialPlaintext.Should().Be("old-token");

        // Simulate Story 29-7 rotation: secret store now returns
        // the new plaintext; an invalidation event fires for the
        // tenant.
        credentials.Values[("tenant", tenantId, "gh-token")] = "new-token";
        await cache.InvalidateTenantAsync(tenantId);

        var second = await resolver.ResolveForTenantAsync(tenantId, PlatformKind.GitHub);
        ((Stamped)second!).CredentialPlaintext.Should().Be("new-token");
        factory.CallCount.Should().Be(2);
    }

    [Test]
    public async Task EntityRoundTrip_RegistryRowMapsCorrectly()
    {
        // Step 1 — entity mapping round-trips JSONB metadata + the
        // soft-delete column survives a save/load cycle.
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var write = new ControlPlaneDbContext(options);
        var repo = new TenantPlatformInstallationRepository(write);

        var tenantId = Guid.NewGuid();
        var saved = await repo.CreateAsync(new TenantPlatformInstallation
        {
            TenantId = tenantId,
            PlatformKind = "github",
            BaseUrl = "https://api.github.com",
            InstallationExternalId = "42",
            CredentialSecretScope = "tenant",
            CredentialSecretName = "gh-token",
            WebhookSecretScope = "tenant",
            WebhookSecretName = "gh-webhook",
            Status = "connected",
            IsPrimary = true,
            MetadataJson = "{\"orgSlug\":\"acme\",\"plan\":\"enterprise\"}",
        });

        // Open a fresh context to defeat tracking.
        await using var read = new ControlPlaneDbContext(options);
        var refetch = await read.TenantPlatformInstallations
            .AsNoTracking()
            .FirstAsync(r => r.Id == saved.Id);

        refetch.MetadataJson.Should().Be("{\"orgSlug\":\"acme\",\"plan\":\"enterprise\"}");
        refetch.WebhookSecretName.Should().Be("gh-webhook");
        refetch.IsPrimary.Should().BeTrue();
        refetch.Status.Should().Be("connected");
        refetch.DeletedAt.Should().BeNull();

        await repo.SoftDeleteAsync(saved.Id);
        await using var read2 = new ControlPlaneDbContext(options);
        var afterDelete = await read2.TenantPlatformInstallations
            .AsNoTracking()
            .FirstAsync(r => r.Id == saved.Id);
        afterDelete.DeletedAt.Should().NotBeNull();
        afterDelete.Status.Should().Be("disconnected");
    }
}
