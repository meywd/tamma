using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.GitHub;

/// <summary>
/// Story 38-1 (F2) — <see cref="InstallationRepository.GetByRepoFullNameAsync"/>
/// must match repo full names case-INsensitively. GitHub repo full names are
/// case-insensitive ("Acme/Widget" == "acme/widget"); a case-sensitive DB
/// compare would 404 a legitimate tenant and hard-fail the ADL git-mediation
/// guard with a spurious <c>REPO_NOT_AUTHORIZED</c>.
/// </summary>
[TestFixture]
public class InstallationRepositoryCaseInsensitiveTests
{
    private DbContextOptions<ControlPlaneDbContext> _options = null!;
    private ControlPlaneDbContext _db = null!;
    private InstallationRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new ControlPlaneDbContext(_options);
        _repo = new InstallationRepository(_db);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private async Task<Guid> SeedInstallationWithRepoAsync(string repoFullName, Guid? tenantId = null)
    {
        var installationEntityId = Guid.NewGuid();
        _db.GitHubInstallations.Add(new GitHubInstallation
        {
            Id = installationEntityId,
            InstallationId = 4242,
            AccountLogin = "acme",
            AccountType = "Organization",
            AppId = 1,
            TenantId = tenantId,
        });
        await _db.SaveChangesAsync();

        // AddRepoAsync also derives Owner/Name from the full name.
        await _repo.AddRepoAsync(installationEntityId, repoId: 777, repoFullName: repoFullName);
        return installationEntityId;
    }

    [Test]
    public async Task GetByRepoFullName_LowercaseQuery_MatchesMixedCaseRegistration()
    {
        var installEntityId = await SeedInstallationWithRepoAsync("Acme/Widget");

        var result = await _repo.GetByRepoFullNameAsync("acme/widget");

        result.Should().NotBeNull("GitHub repo names are case-insensitive");
        result!.Id.Should().Be(installEntityId);
    }

    [Test]
    public async Task GetByRepoFullName_MixedCaseQuery_MatchesLowercaseRegistration()
    {
        var installEntityId = await SeedInstallationWithRepoAsync("acme/widget");

        var result = await _repo.GetByRepoFullNameAsync("Acme/Widget");

        result.Should().NotBeNull();
        result!.Id.Should().Be(installEntityId);
    }

    [Test]
    public async Task GetByRepoFullName_ExactMatch_StillWorks()
    {
        var installEntityId = await SeedInstallationWithRepoAsync("acme/widget");

        var result = await _repo.GetByRepoFullNameAsync("acme/widget");

        result.Should().NotBeNull();
        result!.Id.Should().Be(installEntityId);
    }

    [Test]
    public async Task GetByRepoFullName_UnknownRepo_ReturnsNull()
    {
        await SeedInstallationWithRepoAsync("acme/widget");

        var result = await _repo.GetByRepoFullNameAsync("other/repo");

        result.Should().BeNull();
    }

    [Test]
    public async Task GetByRepoFullName_InactiveRepo_ReturnsNull_EvenCaseInsensitive()
    {
        var installEntityId = await SeedInstallationWithRepoAsync("Acme/Widget");
        // Deactivate the repo link (repo removed from the installation).
        await _repo.RemoveRepoAsync(installEntityId, repoId: 777);

        var result = await _repo.GetByRepoFullNameAsync("acme/widget");

        result.Should().BeNull("an inactive repo link must not resolve, regardless of case");
    }
}
