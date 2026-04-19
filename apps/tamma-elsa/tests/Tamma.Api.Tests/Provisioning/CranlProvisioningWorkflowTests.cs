using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Provisioning.Cranl;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Provisioning;

/// <summary>
/// Tests for <see cref="CranlProvisioningWorkflow"/>. Mocks
/// <see cref="ICranlApiClient"/> end-to-end so the state machine is
/// exercised without any HTTP traffic; the DbContext comes from the
/// shared <see cref="ApiTestFixture"/> Postgres container so jsonb
/// columns and other Postgres-specific features behave like prod.
/// Each test seeds + cleans its own tenant row.
/// </summary>
[TestFixture]
public class CranlProvisioningWorkflowTests
{
    private IServiceScope _scope = null!;
    // Owned by _scope; disposing the scope cascades.
#pragma warning disable NUnit1032
    private TammaDbContext _db = null!;
#pragma warning restore NUnit1032
    private Mock<ICranlApiClient> _cranl = null!;
    private CranlOptions _options = null!;
    private TenantSecretProtector _protector = null!;
    private IConfiguration _configuration = null!;
    private CranlProvisioningWorkflow _workflow = null!;

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<TammaDbContext>();

        _cranl = new Mock<ICranlApiClient>(MockBehavior.Strict);
        _options = new CranlOptions
        {
            ApiKey = "cranl_sk_TESTTESTTESTTESTTESTTESTTESTTEST",
            OrganizationId = "org-1",
            RepositoryId = "repo-1",
            DefaultRegion = "germany-1",
            DefaultBuildType = "dockerfile",
            AppBuildPath = "/apps/tamma-elsa",
            DefaultBranch = "main"
        };
        _protector = new TenantSecretProtector(new byte[32]);
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tamma:ControlPlaneUrl"] = "https://api.tamma.test",
                ["Tamma:TenantSharedSecret"] = "shared-secret-for-tests"
            })
            .Build();

        _workflow = new CranlProvisioningWorkflow(
            _db, _cranl.Object, _options, _protector, _configuration,
            NullLogger<CranlProvisioningWorkflow>.Instance);
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    private async Task<Tenant> SeedTenantAsync()
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Acme",
            Slug = "acme-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            ProvisioningState = "pending",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();
        return tenant;
    }

    private async Task<Tenant> ReloadAsync(Guid tenantId)
    {
        // Detach the cached instance so we re-read from the DB; otherwise
        // EF returns the entity we just modified in the workflow's own
        // SaveChanges via change-tracking.
        foreach (var entry in _db.ChangeTracker.Entries<Tenant>().ToList())
            entry.State = EntityState.Detached;
        return await _db.Tenants.IgnoreQueryFilters().FirstAsync(t => t.Id == tenantId);
    }

    // ─── Happy path ──────────────────────────────────────────────────────────

    [Test]
    public async Task ProvisionAsync_HappyPath_WalksFullStateMachine()
    {
        var tenant = await SeedTenantAsync();

        _cranl.Setup(c => c.CreateProjectAsync(
                It.Is<string>(s => s.StartsWith("tamma-tenant-")),
                "org-1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlProject { Id = "proj-1", Name = "tamma-tenant-x" });

        _cranl.Setup(c => c.CreateDatabaseAsync(
                It.Is<CreateDatabaseRequest>(r =>
                    r.ProjectId == "proj-1" && r.ServerId == "germany-1" && r.Type == "postgresql"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlDatabase { Id = "db-1", Status = "pending" });

        _cranl.Setup(c => c.GetDatabaseAsync("db-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlDatabase
            {
                Id = "db-1",
                Status = "running",
                Host = "db1.cranl.internal",
                Port = 5432,
                Username = "admin",
                Password = "s3cret",
                Database = "tamma-x"
            });

        _cranl.Setup(c => c.CreateApplicationAsync(
                It.Is<CreateApplicationRequest>(r =>
                    r.ProjectId == "proj-1" && r.RepositoryId == "repo-1"
                    && r.BuildType == "dockerfile" && r.BuildPath == "/apps/tamma-elsa"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlApplication { Id = "app-1", Status = "pending" });

        _cranl.Setup(c => c.PutEnvironmentAsync("app-1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _cranl.Setup(c => c.DeployApplicationAsync("app-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _cranl.Setup(c => c.GetApplicationAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlApplication { Id = "app-1", Status = "running" });

        _cranl.Setup(c => c.GetApplicationDomainsAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlAppDomains
            {
                DefaultDomain = "tamma-engine-x.cranl.net",
                Domains = new List<CranlAppDomain>
                {
                    new() { DomainId = "d1", Host = "tamma-engine-x.cranl.net", Https = true }
                }
            });

        await _workflow.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);

        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("ready");
        refreshed.CranlProjectId.Should().Be("proj-1");
        refreshed.CranlDatabaseId.Should().Be("db-1");
        refreshed.CranlAppId.Should().Be("app-1");
        refreshed.CranlAppUrl.Should().Be("tamma-engine-x.cranl.net");
        refreshed.CranlDatabaseUrlEncrypted.Should().NotBeNull();
        var decrypted = _protector.Decrypt(refreshed.CranlDatabaseUrlEncrypted!);
        decrypted.Should().Be("postgresql://admin:s3cret@db1.cranl.internal:5432/tamma-x");

        _cranl.Verify(c => c.PutEnvironmentAsync(
            "app-1",
            It.Is<string>(s =>
                s.Contains("DATABASE_URL=postgresql://admin:s3cret@")
                && s.Contains("TAMMA_CONTROL_PLANE_URL=https://api.tamma.test")
                && s.Contains($"TAMMA_TENANT_ID={tenant.Id:D}")
                && s.Contains("TAMMA_SHARED_SECRET=shared-secret-for-tests")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── Resume from existing project / db ───────────────────────────────────

    [Test]
    public async Task ProvisionAsync_ResumesWhenProjectAlreadyExists_DoesNotCreateProject()
    {
        var tenant = await SeedTenantAsync();
        tenant.CranlProjectId = "proj-existing";
        await _db.SaveChangesAsync();

        _cranl.Setup(c => c.CreateDatabaseAsync(
                It.Is<CreateDatabaseRequest>(r => r.ProjectId == "proj-existing"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlDatabase { Id = "db-1", Status = "pending" });
        _cranl.Setup(c => c.GetDatabaseAsync("db-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlDatabase
            {
                Id = "db-1", Status = "running",
                Host = "h", Port = 5432, Username = "u", Password = "p", Database = "d"
            });
        _cranl.Setup(c => c.CreateApplicationAsync(It.IsAny<CreateApplicationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlApplication { Id = "app-1", Status = "pending" });
        _cranl.Setup(c => c.PutEnvironmentAsync("app-1", It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _cranl.Setup(c => c.DeployApplicationAsync("app-1", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _cranl.Setup(c => c.GetApplicationAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlApplication { Id = "app-1", Status = "running" });
        _cranl.Setup(c => c.GetApplicationDomainsAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlAppDomains { DefaultDomain = "h.cranl.net" });

        await _workflow.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);

        _cranl.Verify(c => c.CreateProjectAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("ready");
    }

    // ─── Failure modes ───────────────────────────────────────────────────────

    [Test]
    public async Task ProvisionAsync_DatabaseEntersErrorState_FlipsToFailed()
    {
        var tenant = await SeedTenantAsync();

        _cranl.Setup(c => c.CreateProjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlProject { Id = "proj-1" });
        _cranl.Setup(c => c.CreateDatabaseAsync(It.IsAny<CreateDatabaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlDatabase { Id = "db-1", Status = "pending" });
        _cranl.Setup(c => c.GetDatabaseAsync("db-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlDatabase { Id = "db-1", Status = "error" });

        await _workflow.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);

        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("failed");
        refreshed.ProvisioningDetail.Should().Contain("database_did_not_report_connection_string");
    }

    [Test]
    public async Task ProvisionAsync_CranlApiException_FlipsToFailedAndRethrows()
    {
        var tenant = await SeedTenantAsync();

        _cranl.Setup(c => c.CreateProjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CranlApiException(HttpStatusCode.Forbidden, "plan_limit_reached", "no more"));

        var act = async () => await _workflow.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);
        await act.Should().ThrowAsync<CranlApiException>();

        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("failed");
        refreshed.ProvisioningDetail.Should().Contain("cranl_api_error:403");
    }

    // ─── Deprovisioning ──────────────────────────────────────────────────────

    [Test]
    public async Task DeprovisionAsync_DeletesInOrder_AppThenDbThenProject()
    {
        var tenant = await SeedTenantAsync();
        tenant.CranlProjectId = "proj-1";
        tenant.CranlDatabaseId = "db-1";
        tenant.CranlAppId = "app-1";
        tenant.CranlAppUrl = "x.cranl.net";
        tenant.CranlDatabaseUrlEncrypted = _protector.Encrypt("postgresql://h");
        await _db.SaveChangesAsync();

        var sequence = new List<string>();
        _cranl.Setup(c => c.DeleteApplicationAsync("app-1", It.IsAny<CancellationToken>()))
            .Callback(() => sequence.Add("app"))
            .Returns(Task.CompletedTask);
        _cranl.Setup(c => c.DeleteDatabaseAsync("db-1", It.IsAny<CancellationToken>()))
            .Callback(() => sequence.Add("db"))
            .Returns(Task.CompletedTask);
        _cranl.Setup(c => c.DeleteProjectAsync("proj-1", It.IsAny<CancellationToken>()))
            .Callback(() => sequence.Add("project"))
            .Returns(Task.CompletedTask);

        await _workflow.DeprovisionAsync(tenant.Id, CancellationToken.None);

        sequence.Should().Equal("app", "db", "project");
        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("deprovisioned");
        refreshed.CranlProjectId.Should().BeNull();
        refreshed.CranlDatabaseId.Should().BeNull();
        refreshed.CranlAppId.Should().BeNull();
        refreshed.CranlAppUrl.Should().BeNull();
        refreshed.CranlDatabaseUrlEncrypted.Should().BeNull();
    }

    [Test]
    public async Task DeprovisionAsync_404OnApp_TreatedAsAlreadyAbsent()
    {
        var tenant = await SeedTenantAsync();
        tenant.CranlProjectId = "proj-1";
        tenant.CranlDatabaseId = "db-1";
        tenant.CranlAppId = "app-1";
        await _db.SaveChangesAsync();

        _cranl.Setup(c => c.DeleteApplicationAsync("app-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CranlApiException(HttpStatusCode.NotFound, "not found", "gone"));
        _cranl.Setup(c => c.DeleteDatabaseAsync("db-1", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _cranl.Setup(c => c.DeleteProjectAsync("proj-1", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await _workflow.DeprovisionAsync(tenant.Id, CancellationToken.None);

        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("deprovisioned");
    }

    // ─── Naming ──────────────────────────────────────────────────────────────

    [Test]
    public void ShortenForName_TakesFirst8HexChars()
    {
        var id = Guid.Parse("11112222-3333-4444-5555-666677778888");
        CranlProvisioningWorkflow.ShortenForName(id).Should().Be("11112222");
    }
}
