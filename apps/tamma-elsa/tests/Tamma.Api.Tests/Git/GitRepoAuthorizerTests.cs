using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Git;
using Tamma.Api.Services.PromptStore;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Git;

/// <summary>
/// Story 38-1 (AC2) + F1 — direct coverage of the load-bearing cross-tenant
/// guard. The guard is the single control that stops a mis-scoped platform token
/// from writing to another tenant's repo, so its null-handling is mode-gated:
/// <list type="bullet">
///   <item><b>single-user</b> — a matched null/null is the sole-user case ⇒ Allow.</item>
///   <item><b>SaaS</b> — a null acting tenant OR a null-<c>TenantId</c> (orphan)
///     install must NOT fail open ⇒ Deny; only both-present-and-equal ⇒ Allow.</item>
/// </list>
/// </summary>
[TestFixture]
public class GitRepoAuthorizerTests
{
    private const string Repo = "acme/widgets";

    private Mock<IInstallationRepository> _installations = null!;
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _installations = new Mock<IInstallationRepository>(MockBehavior.Strict);
    }

    private GitRepoAuthorizer Build(TammaMode mode)
    {
        var modeProvider = new Mock<ITammaModeProvider>();
        modeProvider.SetupGet(m => m.Mode).Returns(mode);
        return new GitRepoAuthorizer(
            _installations.Object, modeProvider.Object, NullLogger<GitRepoAuthorizer>.Instance);
    }

    private void InstallationForRepo(Guid? tenantId)
        => _installations
            .Setup(r => r.GetByRepoFullNameAsync(Repo))
            .ReturnsAsync(new GitHubInstallation
            {
                Id = Guid.NewGuid(),
                InstallationId = 1234,
                AccountLogin = "acme",
                AccountType = "Organization",
                AppId = 1,
                TenantId = tenantId,
            });

    private void NoInstallationForRepo()
        => _installations
            .Setup(r => r.GetByRepoFullNameAsync(Repo))
            .ReturnsAsync((GitHubInstallation?)null);

    // ── single-user ────────────────────────────────────────────────────

    [Test]
    public async Task SingleUser_NullActing_NullInstallTenant_Allows()
    {
        // The legit sole-user case: both sides null (no tenancy) ⇒ Allow.
        InstallationForRepo(tenantId: null);

        var result = await Build(TammaMode.SingleUser).AuthorizeAsync(null, Repo);

        result.Allowed.Should().BeTrue("single-user both-null is the sole-user case");
    }

    [Test]
    public async Task SingleUser_MatchingTenant_Allows()
    {
        InstallationForRepo(tenantId: TenantA);

        var result = await Build(TammaMode.SingleUser).AuthorizeAsync(TenantA, Repo);

        result.Allowed.Should().BeTrue();
    }

    // ── SaaS: the F1 fail-open cases ────────────────────────────────────

    [Test]
    public async Task Saas_NullActingTenant_Denies()
    {
        // A null-X-Tenant-Id engine request must NOT pass in SaaS, even against a
        // tenant-owned install — plain nullable equality would fail open here.
        InstallationForRepo(tenantId: TenantA);

        var result = await Build(TammaMode.SaaS).AuthorizeAsync(null, Repo);

        result.Allowed.Should().BeFalse("a null acting tenant must be denied in SaaS");
    }

    [Test]
    public async Task Saas_OrphanInstall_NullInstallTenant_Denies()
    {
        // Orphan install (TenantId=null) against a real acting tenant: the exact
        // null == null fail-open the F1 fix closes.
        InstallationForRepo(tenantId: null);

        var result = await Build(TammaMode.SaaS).AuthorizeAsync(TenantA, Repo);

        result.Allowed.Should().BeFalse("an orphan (null-TenantId) install must be denied in SaaS");
    }

    [Test]
    public async Task Saas_NullActing_And_OrphanInstall_BothNull_Denies()
    {
        // The precise reported hole: null acting tenant + orphan install = null==null.
        InstallationForRepo(tenantId: null);

        var result = await Build(TammaMode.SaaS).AuthorizeAsync(null, Repo);

        result.Allowed.Should().BeFalse("both-null must NOT fail open in SaaS");
    }

    [Test]
    public async Task Saas_CrossTenant_ActingA_InstallB_Denies()
    {
        InstallationForRepo(tenantId: TenantB);

        var result = await Build(TammaMode.SaaS).AuthorizeAsync(TenantA, Repo);

        result.Allowed.Should().BeFalse("a cross-tenant attempt is the write this guard exists to prevent");
    }

    [Test]
    public async Task Saas_MatchingTenant_Allows()
    {
        InstallationForRepo(tenantId: TenantA);

        var result = await Build(TammaMode.SaaS).AuthorizeAsync(TenantA, Repo);

        result.Allowed.Should().BeTrue("both present and equal ⇒ allow");
    }

    // ── mode-agnostic fail-closed ───────────────────────────────────────

    [Test]
    public async Task NoInstallation_Denies()
    {
        NoInstallationForRepo();

        var result = await Build(TammaMode.SaaS).AuthorizeAsync(TenantA, Repo);

        result.Allowed.Should().BeFalse("no installation grants access ⇒ fail-closed deny");
    }

    [Test]
    public async Task BlankRepo_Denies_WithoutLookup()
    {
        var result = await Build(TammaMode.SaaS).AuthorizeAsync(TenantA, "  ");

        result.Allowed.Should().BeFalse();
        _installations.Verify(r => r.GetByRepoFullNameAsync(It.IsAny<string>()), Times.Never);
    }

    // ── duplicate-installation (see F2): repo resolves to whichever install the
    //    registry returns; the guard still asserts that install's tenant matches ──

    [Test]
    public async Task Saas_DuplicateRepoRegistration_ResolvesToForeignTenant_Denies()
    {
        // If the same repo full-name is registered under an install owned by
        // tenant B, an acting tenant A must still be denied (the guard trusts the
        // registry's resolved install, then re-checks tenancy).
        InstallationForRepo(tenantId: TenantB);

        var result = await Build(TammaMode.SaaS).AuthorizeAsync(TenantA, Repo);

        result.Allowed.Should().BeFalse();
    }
}
