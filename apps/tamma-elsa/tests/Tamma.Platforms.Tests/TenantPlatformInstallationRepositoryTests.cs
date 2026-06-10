using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Platforms.Tests;

/// <summary>
/// Story 31-2 Step 2 — repository unit tests covering CRUD, primary
/// fallback, soft-delete semantics, and AC9 cross-tenant safety
/// (every read method excludes other tenants' rows by query).
/// </summary>
[TestFixture]
public class TenantPlatformInstallationRepositoryTests
{
    private DbContextOptions<ControlPlaneDbContext> _options = null!;
    private ControlPlaneDbContext _db = null!;
    private TenantPlatformInstallationRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new ControlPlaneDbContext(_options);
        _repo = new TenantPlatformInstallationRepository(_db);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private static TenantPlatformInstallation NewRow(
        Guid tenantId,
        string platformKind = "github",
        string? externalId = "12345",
        bool isPrimary = true) =>
        new()
        {
            TenantId = tenantId,
            PlatformKind = platformKind,
            BaseUrl = "https://api.github.com",
            InstallationExternalId = externalId,
            CredentialSecretScope = "tenant",
            CredentialSecretName = "github-installation",
            Status = "connected",
            IsPrimary = isPrimary,
            MetadataJson = "{}",
        };

    // ── Create ────────────────────────────────────────────────────────

    [Test]
    public async Task CreateAsync_AssignsId_AndTimestamps()
    {
        var tenantId = Guid.NewGuid();
        var row = await _repo.CreateAsync(NewRow(tenantId));

        row.Id.Should().NotBe(Guid.Empty);
        row.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        row.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Registry_Register_AddsRow_AndIsImmediatelyResolvable()
    {
        // Acceptance: a freshly-created row must be visible to
        // GetByTenantPrimaryAsync / GetByTenantKindAsync without an
        // intermediate flush — repository owns the SaveChanges call.
        var tenantId = Guid.NewGuid();
        var created = await _repo.CreateAsync(NewRow(tenantId));

        var fetched = await _repo.GetByTenantPrimaryAsync(tenantId);

        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(created.Id);
    }

    // ── GetByTenantPrimaryAsync ──────────────────────────────────────

    [Test]
    public async Task GetByTenantPrimaryAsync_NoRows_ReturnsNull()
    {
        var result = await _repo.GetByTenantPrimaryAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [Test]
    public async Task GetByTenantPrimaryAsync_WithMultipleRows_PrefersPrimary()
    {
        var tenantId = Guid.NewGuid();
        await _repo.CreateAsync(NewRow(tenantId, "gitea", "1", isPrimary: false));
        var primary = await _repo.CreateAsync(
            NewRow(tenantId, "github", "2", isPrimary: true));

        var result = await _repo.GetByTenantPrimaryAsync(tenantId);

        result.Should().NotBeNull();
        result!.Id.Should().Be(primary.Id);
    }

    [Test]
    public async Task GetByTenantPrimaryAsync_SingleRow_ReturnedRegardlessOfFlag()
    {
        var tenantId = Guid.NewGuid();
        var only = await _repo.CreateAsync(
            NewRow(tenantId, isPrimary: false));

        var result = await _repo.GetByTenantPrimaryAsync(tenantId);

        result.Should().NotBeNull();
        result!.Id.Should().Be(only.Id);
    }

    // ── GetByTenantKindAsync ──────────────────────────────────────────

    [Test]
    public async Task GetByTenantKindAsync_FiltersByKind()
    {
        var tenantId = Guid.NewGuid();
        var ghRow = await _repo.CreateAsync(NewRow(tenantId, "github", "1"));
        var giteaRow = await _repo.CreateAsync(NewRow(tenantId, "gitea", "2", isPrimary: false));

        var gh = await _repo.GetByTenantKindAsync(tenantId, "github");
        var gitea = await _repo.GetByTenantKindAsync(tenantId, "gitea");

        gh!.Id.Should().Be(ghRow.Id);
        gitea!.Id.Should().Be(giteaRow.Id);
    }

    [Test]
    public async Task GetByTenantKindAsync_UnknownKind_ReturnsNull()
    {
        var tenantId = Guid.NewGuid();
        await _repo.CreateAsync(NewRow(tenantId, "github"));

        var gitlab = await _repo.GetByTenantKindAsync(tenantId, "gitlab");

        gitlab.Should().BeNull();
    }

    // ── GetByExternalIdAsync ─────────────────────────────────────────

    [Test]
    public async Task GetByExternalIdAsync_FiltersByPlatformKind()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // Two platforms emitting colliding external ids must NOT
        // resolve to each other's installations.
        var ghA = await _repo.CreateAsync(
            NewRow(tenantA, "github", "9999"));
        var giteaB = await _repo.CreateAsync(
            NewRow(tenantB, "gitea", "9999"));

        var resolvedGh = await _repo.GetByExternalIdAsync("github", "9999");
        var resolvedGitea = await _repo.GetByExternalIdAsync("gitea", "9999");

        resolvedGh!.Id.Should().Be(ghA.Id);
        resolvedGitea!.Id.Should().Be(giteaB.Id);
    }

    [Test]
    public async Task GetByExternalIdAsync_NoMatch_ReturnsNull()
    {
        var result = await _repo.GetByExternalIdAsync("github", "does-not-exist");
        result.Should().BeNull();
    }

    // ── ListByTenantAsync ─────────────────────────────────────────────

    [Test]
    public async Task Registry_ListInstallations_ReturnsAllForTenant()
    {
        var tenantId = Guid.NewGuid();
        await _repo.CreateAsync(NewRow(tenantId, "github", "1"));
        await _repo.CreateAsync(NewRow(tenantId, "gitea", "2", isPrimary: false));
        await _repo.CreateAsync(NewRow(tenantId, "gitlab", "3", isPrimary: false));

        var list = await _repo.ListByTenantAsync(tenantId);

        list.Should().HaveCount(3);
        list.Select(r => r.PlatformKind).Should().BeEquivalentTo(
            new[] { "github", "gitea", "gitlab" });
    }

    [Test]
    public async Task ListByTenantAsync_ExcludesOtherTenants()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await _repo.CreateAsync(NewRow(tenantA, "github", "1"));
        await _repo.CreateAsync(NewRow(tenantB, "github", "2"));

        var rowsA = await _repo.ListByTenantAsync(tenantA);

        rowsA.Should().HaveCount(1);
        rowsA[0].TenantId.Should().Be(tenantA);
    }

    // ── Soft-delete ───────────────────────────────────────────────────

    [Test]
    public async Task Registry_RemoveInstallation_DoesNotImpactOtherTenants()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var rowA = await _repo.CreateAsync(NewRow(tenantA, "github", "1"));
        var rowB = await _repo.CreateAsync(NewRow(tenantB, "github", "2"));

        await _repo.SoftDeleteAsync(rowA.Id);

        // Tenant A's row gone; Tenant B unaffected.
        (await _repo.GetByTenantPrimaryAsync(tenantA)).Should().BeNull();
        (await _repo.GetByTenantPrimaryAsync(tenantB))!.Id.Should().Be(rowB.Id);
    }

    [Test]
    public async Task SoftDeleteAsync_HidesRowFromReadMethods()
    {
        var tenantId = Guid.NewGuid();
        var row = await _repo.CreateAsync(NewRow(tenantId));
        await _repo.SoftDeleteAsync(row.Id);

        (await _repo.GetByTenantPrimaryAsync(tenantId)).Should().BeNull();
        (await _repo.GetByTenantKindAsync(tenantId, "github")).Should().BeNull();
        (await _repo.GetByIdAsync(row.Id)).Should().BeNull();
        (await _repo.GetByExternalIdAsync("github", "12345")).Should().BeNull();
        (await _repo.ListByTenantAsync(tenantId)).Should().BeEmpty();
    }

    [Test]
    public async Task SoftDeleteAsync_OnUnknownRow_IsNoOp()
    {
        var act = async () => await _repo.SoftDeleteAsync(Guid.NewGuid());
        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task RestoreAsync_ResurrectsSoftDeletedRow()
    {
        var tenantId = Guid.NewGuid();
        var row = await _repo.CreateAsync(NewRow(tenantId));
        await _repo.SoftDeleteAsync(row.Id);
        await _repo.RestoreAsync(row.Id);

        var fetched = await _repo.GetByTenantPrimaryAsync(tenantId);
        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(row.Id);
        fetched.Status.Should().Be("connected");
    }

    [Test]
    public async Task RestoreAsync_OnUnknownRow_Throws()
    {
        var act = async () => await _repo.RestoreAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Update ────────────────────────────────────────────────────────

    [Test]
    public async Task UpdateAsync_PersistsMutableFields()
    {
        var tenantId = Guid.NewGuid();
        var row = await _repo.CreateAsync(NewRow(tenantId));

        row.Status = "suspended";
        row.BaseUrl = "https://github.example.com";
        row.IsPrimary = false;
        row.MetadataJson = "{\"orgSlug\":\"acme\"}";

        var updated = await _repo.UpdateAsync(row);

        updated.Status.Should().Be("suspended");
        updated.BaseUrl.Should().Be("https://github.example.com");
        updated.IsPrimary.Should().BeFalse();
        updated.MetadataJson.Should().Be("{\"orgSlug\":\"acme\"}");
    }

    [Test]
    public async Task UpdateAsync_OnUnknownRow_Throws()
    {
        var phantom = NewRow(Guid.NewGuid());
        phantom.Id = Guid.NewGuid();

        var act = async () => await _repo.UpdateAsync(phantom);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Misc input guards ────────────────────────────────────────────

    [Test]
    public async Task GetByTenantKindAsync_RejectsBlankKind()
    {
        var act = async () =>
            await _repo.GetByTenantKindAsync(Guid.NewGuid(), "");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task GetByExternalIdAsync_RejectsBlankInputs()
    {
        var act1 = async () =>
            await _repo.GetByExternalIdAsync("", "1");
        var act2 = async () =>
            await _repo.GetByExternalIdAsync("github", "");

        await act1.Should().ThrowAsync<ArgumentException>();
        await act2.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task CreateAsync_PreservesExplicitId()
    {
        var explicitId = Guid.NewGuid();
        var row = NewRow(Guid.NewGuid());
        row.Id = explicitId;

        var created = await _repo.CreateAsync(row);
        created.Id.Should().Be(explicitId);
    }
}
