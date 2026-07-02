using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Pricing;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-6 (AC9, AC13) — the control-plane gauge-metric usage reader against
/// a real Postgres testcontainer (so the FK + partial unique index behave like
/// production): <c>Seats</c> = membership Total, <c>Agents</c> = tenant
/// <c>AgentConfig</c> count, <c>Repos</c> = active <c>GitHubInstallationRepo</c>
/// count, metering-only metrics = <c>null</c>, plus tenant isolation. Also
/// covers the interim <see cref="TenantShadowColumnPlanAssignmentSource"/>
/// (34-4 seam) reading the Epic-28 <c>PlanId</c> shadow column.
/// </summary>
[TestFixture]
public class ControlPlaneEntitlementUsageReaderTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("entitlement_usage_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();

        await using var ctx = NewContext();
        await ctx.Database.MigrateAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SetUp]
    public async Task SetUp()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(
            "TRUNCATE agents, github_installation_repos, github_installations, plans, tenants CASCADE;");
    }

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    private static Tenant NewTenant(Guid id) => new()
    {
        Id = id,
        Name = "T-" + id.ToString("N")[..6],
        Slug = "t-" + id.ToString("N")[..6],
        Type = "team",
        Plan = "free",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private ControlPlaneEntitlementUsageReader BuildReader(
        ControlPlaneDbContext ctx, ITenantMembershipRepository memberships) =>
        new(ctx, memberships, NullLogger<ControlPlaneEntitlementUsageReader>.Instance);

    [Test]
    public async Task Reads_Seats_Agents_Repos_And_Nulls_MeteringMetrics()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var installA = Guid.NewGuid();

        await using (var setup = NewContext())
        {
            setup.Tenants.Add(NewTenant(tenantA));
            setup.Tenants.Add(NewTenant(tenantB));

            // Tenant A: 2 private (tenant-owned) agent identities.
            setup.Agents.Add(OwnedAgent(tenantA, "atlas"));
            setup.Agents.Add(OwnedAgent(tenantA, "nova"));
            // A public/system agent is NOT owned by any tenant → not counted.
            setup.Agents.Add(PublicAgent("claude"));

            // Tenant A: 1 installation with 3 repos (2 active, 1 inactive).
            setup.GitHubInstallations.Add(new GitHubInstallation
            {
                Id = installA, InstallationId = 111, AccountLogin = "acme",
                AccountType = "Organization", AppId = 1, TenantId = tenantA,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            setup.GitHubInstallationRepos.AddRange(
                Repo(installA, 1, "acme/a", active: true),
                Repo(installA, 2, "acme/b", active: true),
                Repo(installA, 3, "acme/c", active: false));

            await setup.SaveChangesAsync();
        }

        var memberships = new Mock<ITenantMembershipRepository>();
        memberships.Setup(m => m.ListByTenantAsync(tenantA, It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<TenantMembership>(), 4));

        await using var ctx = NewContext();
        var reader = BuildReader(ctx, memberships.Object);

        (await reader.GetCurrentAsync(tenantA, EntitlementMetricKey.Seats)).Should().Be(4);
        (await reader.GetCurrentAsync(tenantA, EntitlementMetricKey.Agents)).Should().Be(2,
            "only tenant-owned private agents count, not the public system agent");
        (await reader.GetCurrentAsync(tenantA, EntitlementMetricKey.Repos)).Should().Be(2,
            "only active repos count");

        (await reader.GetCurrentAsync(tenantA, EntitlementMetricKey.LlmTokens)).Should().BeNull();
        (await reader.GetCurrentAsync(tenantA, EntitlementMetricKey.WorkflowRuns)).Should().BeNull();
        (await reader.GetCurrentAsync(tenantA, EntitlementMetricKey.RagStorageMb)).Should().BeNull();
        (await reader.GetCurrentAsync(tenantA, EntitlementMetricKey.BenchmarkRetentionDays)).Should().BeNull();
    }

    [Test]
    public async Task TenantIsolation_OtherTenant_HasZeroCounts()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var installA = Guid.NewGuid();

        await using (var setup = NewContext())
        {
            setup.Tenants.Add(NewTenant(tenantA));
            setup.Tenants.Add(NewTenant(tenantB));
            setup.Agents.Add(OwnedAgent(tenantA, "atlas"));
            setup.GitHubInstallations.Add(new GitHubInstallation
            {
                Id = installA, InstallationId = 222, AccountLogin = "acme",
                AccountType = "Organization", AppId = 1, TenantId = tenantA,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            setup.GitHubInstallationRepos.Add(Repo(installA, 9, "acme/z", active: true));
            await setup.SaveChangesAsync();
        }

        var memberships = new Mock<ITenantMembershipRepository>();
        memberships.Setup(m => m.ListByTenantAsync(tenantB, It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<TenantMembership>(), 0));

        await using var ctx = NewContext();
        var reader = BuildReader(ctx, memberships.Object);

        (await reader.GetCurrentAsync(tenantB, EntitlementMetricKey.Agents)).Should().Be(0);
        (await reader.GetCurrentAsync(tenantB, EntitlementMetricKey.Repos)).Should().Be(0);
        (await reader.GetCurrentAsync(tenantB, EntitlementMetricKey.Seats)).Should().Be(0);
    }

    [Test]
    public async Task ShadowColumnAssignmentSource_ReadsPlanId_And_NullWhenUnset()
    {
        var withPlan = Guid.NewGuid();
        var withoutPlan = Guid.NewGuid();
        var planId = Guid.NewGuid();

        await using (var setup = NewContext())
        {
            // A plan row for the FK (plans.Id) the shadow column points at.
            setup.Plans.Add(new Plan
            {
                Id = planId, Slug = "team", DisplayName = "Team", Version = 1,
                Status = "active", IsCustom = false, BillingInterval = "monthly",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });

            var t1 = NewTenant(withPlan);
            var t2 = NewTenant(withoutPlan);
            setup.Tenants.Add(t1);
            setup.Tenants.Add(t2);
            setup.Entry(t1).Property("PlanId").CurrentValue = planId;
            await setup.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var source = new TenantShadowColumnPlanAssignmentSource(
            ctx, NullLogger<TenantShadowColumnPlanAssignmentSource>.Instance);

        var active = await source.GetActiveAsync(withPlan);
        active.Should().NotBeNull();
        active!.PlanId.Should().Be(planId);

        (await source.GetActiveAsync(withoutPlan)).Should().BeNull("no PlanId shadow value → no assignment");
        (await source.GetActiveAsync(Guid.NewGuid())).Should().BeNull("unknown tenant → no assignment");
    }

    private static Agent OwnedAgent(Guid tenantId, string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Visibility = AgentVisibility.Private,
        OwnerTenantId = tenantId,
        Status = AgentStatus.Active,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Agent PublicAgent(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Visibility = AgentVisibility.Public,
        Status = AgentStatus.Active,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static GitHubInstallationRepo Repo(Guid installId, long repoId, string full, bool active) => new()
    {
        Id = Guid.NewGuid(),
        InstallationEntityId = installId,
        RepoId = repoId,
        Owner = full.Split('/')[0],
        Name = full.Split('/')[1],
        RepoFullName = full,
        IsActive = active,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
