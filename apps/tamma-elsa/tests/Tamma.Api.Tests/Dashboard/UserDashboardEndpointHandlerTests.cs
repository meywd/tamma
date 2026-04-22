using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Authorization;
using Tamma.Api.Endpoints;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Dashboard;

/// <summary>
/// Story 18-5 — user-facing dashboard endpoints. These run under
/// <c>/api/v1/orgs/{tenantId}/dashboard/*</c> (tenant-scoped, guarded by
/// <see cref="RequireTenantMembershipFilter"/>). Unlike the operator-focused
/// <c>/api/dashboard/*</c> admin surface, these MUST be scoped to the path
/// tenant — a member of org A must never see events / runs of org B.
///
/// Tests call handlers directly so in-handler invariants are verified
/// without going through the full HTTP pipeline (same pattern as
/// <see cref="Tamma.Api.Tests.Orgs.OrgEndpointHandlerTests"/>).
/// </summary>
[TestFixture]
public class UserDashboardEndpointHandlerTests
{
    private IServiceScope _scope = null!;
#pragma warning disable NUnit1032 // Disposed via _scope
    private ControlPlaneDbContext _db = null!;
#pragma warning restore NUnit1032
    private ITenantRepository _tenantRepo = null!;
    private ITenantMembershipRepository _membershipRepo = null!;
    private IUserRepository _userRepo = null!;
    private IEventRepository _events = null!;
    private IWorkflowRepository _workflowRepo = null!;

    [SetUp]
    public async Task Setup()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        _tenantRepo = _scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        _membershipRepo = _scope.ServiceProvider.GetRequiredService<ITenantMembershipRepository>();
        _userRepo = _scope.ServiceProvider.GetRequiredService<IUserRepository>();
        _events = _scope.ServiceProvider.GetRequiredService<IEventRepository>();
        _workflowRepo = _scope.ServiceProvider.GetRequiredService<IWorkflowRepository>();
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    // ── GetOrgSummary ────────────────────────────────────────────────────────

    [Test]
    public async Task GetOrgSummary_ReturnsTenantScopedCounts_IgnoringOtherTenants()
    {
        var (tenantA, _, _) = await SeedTenantWithOwnerAndMember("acme");
        var (tenantB, _, _) = await SeedTenantWithOwnerAndMember("zeta");

        // Seed 3 events + 2 workflow instances for A, 5 events + 4 instances for B.
        var defA = await _workflowRepo.UpsertDefinitionAsync(new WorkflowDefinition
        {
            Name = "llm-call", Description = "x", TenantId = tenantA, Steps = "[]"
        });
        var defB = await _workflowRepo.UpsertDefinitionAsync(new WorkflowDefinition
        {
            Name = "llm-call", Description = "x", TenantId = tenantB, Steps = "[]"
        });

        await SeedEvents(tenantA, 3);
        await SeedEvents(tenantB, 5);
        await SeedInstances(defA.Id, tenantA, 2);
        await SeedInstances(defB.Id, tenantB, 4);

        var result = await UserDashboardEndpoints.GetOrgSummary(
            tenantA, _db, _events, _workflowRepo);
        var (status, payload) = (await ExecuteAndCapture(result));
        status.Should().Be(StatusCodes.Status200OK);

        var root = JsonDocument.Parse(payload).RootElement;
        root.GetProperty("tenantId").GetGuid().Should().Be(tenantA);
        root.GetProperty("totalEvents").GetInt32().Should().Be(3);
        root.GetProperty("totalWorkflows").GetInt32().Should().Be(2);
        root.GetProperty("workflowDefinitions").GetInt32().Should().BeGreaterThan(0);
        root.GetProperty("recentEvents").GetArrayLength().Should().Be(3);
    }

    [Test]
    public async Task GetOrgSummary_RecentEvents_IsCappedAtTenAndDescendingByCreatedAt()
    {
        var (tenantA, _, _) = await SeedTenantWithOwnerAndMember("acme-cap");
        await SeedEvents(tenantA, 25);

        var result = await UserDashboardEndpoints.GetOrgSummary(
            tenantA, _db, _events, _workflowRepo);
        var (status, payload) = (await ExecuteAndCapture(result));
        status.Should().Be(StatusCodes.Status200OK);

        var root = JsonDocument.Parse(payload).RootElement;
        var events = root.GetProperty("recentEvents");
        events.GetArrayLength().Should().Be(10);

        // Must be descending by createdAt.
        DateTime? previous = null;
        foreach (var evt in events.EnumerateArray())
        {
            var created = evt.GetProperty("createdAt").GetDateTime();
            if (previous is not null)
            {
                created.Should().BeOnOrBefore(previous.Value);
            }
            previous = created;
        }
    }

    [Test]
    public async Task GetOrgSummary_ReturnsZeroes_WhenTenantHasNoActivity()
    {
        var (tenantA, _, _) = await SeedTenantWithOwnerAndMember("empty");

        var result = await UserDashboardEndpoints.GetOrgSummary(
            tenantA, _db, _events, _workflowRepo);
        var (status, payload) = (await ExecuteAndCapture(result));
        status.Should().Be(StatusCodes.Status200OK);

        var root = JsonDocument.Parse(payload).RootElement;
        root.GetProperty("totalEvents").GetInt32().Should().Be(0);
        root.GetProperty("totalWorkflows").GetInt32().Should().Be(0);
        root.GetProperty("recentEvents").GetArrayLength().Should().Be(0);
    }

    // ── GetRecentRuns ────────────────────────────────────────────────────────

    [Test]
    public async Task GetRecentRuns_ReturnsOnlyInstancesOfPathTenant()
    {
        var (tenantA, _, _) = await SeedTenantWithOwnerAndMember("acme-runs");
        var (tenantB, _, _) = await SeedTenantWithOwnerAndMember("zeta-runs");

        var defA = await _workflowRepo.UpsertDefinitionAsync(new WorkflowDefinition
        {
            Name = "llm-call", Description = "x", TenantId = tenantA, Steps = "[]"
        });
        var defB = await _workflowRepo.UpsertDefinitionAsync(new WorkflowDefinition
        {
            Name = "llm-call", Description = "x", TenantId = tenantB, Steps = "[]"
        });

        await SeedInstances(defA.Id, tenantA, 3);
        await SeedInstances(defB.Id, tenantB, 5);

        var result = await UserDashboardEndpoints.GetRecentRuns(
            tenantA, _workflowRepo, limit: null);
        var (status, payload) = (await ExecuteAndCapture(result));
        status.Should().Be(StatusCodes.Status200OK);

        var root = JsonDocument.Parse(payload).RootElement;
        root.GetProperty("total").GetInt32().Should().Be(3);
        root.GetProperty("runs").GetArrayLength().Should().Be(3);
    }

    [Test]
    public async Task GetRecentRuns_RespectsLimitParameter_AndClampsTo100()
    {
        var (tenantA, _, _) = await SeedTenantWithOwnerAndMember("limit");
        var defA = await _workflowRepo.UpsertDefinitionAsync(new WorkflowDefinition
        {
            Name = "llm-call", Description = "x", TenantId = tenantA, Steps = "[]"
        });
        await SeedInstances(defA.Id, tenantA, 30);

        var result5 = await UserDashboardEndpoints.GetRecentRuns(tenantA, _workflowRepo, limit: 5);
        var (_, payload5) = await ExecuteAndCapture(result5);
        JsonDocument.Parse(payload5).RootElement.GetProperty("runs").GetArrayLength().Should().Be(5);

        // Clamp: 500 → 100 max.
        var result500 = await UserDashboardEndpoints.GetRecentRuns(tenantA, _workflowRepo, limit: 500);
        var (_, payload500) = await ExecuteAndCapture(result500);
        JsonDocument.Parse(payload500).RootElement.GetProperty("runs").GetArrayLength().Should().Be(30);
    }

    [Test]
    public async Task GetRecentRuns_DefaultLimitIsTen()
    {
        var (tenantA, _, _) = await SeedTenantWithOwnerAndMember("def-limit");
        var defA = await _workflowRepo.UpsertDefinitionAsync(new WorkflowDefinition
        {
            Name = "llm-call", Description = "x", TenantId = tenantA, Steps = "[]"
        });
        await SeedInstances(defA.Id, tenantA, 25);

        var result = await UserDashboardEndpoints.GetRecentRuns(tenantA, _workflowRepo, limit: null);
        var (_, payload) = await ExecuteAndCapture(result);
        JsonDocument.Parse(payload).RootElement.GetProperty("runs").GetArrayLength().Should().Be(10);
    }

    // ── GetStats ─────────────────────────────────────────────────────────────

    [Test]
    public async Task GetStats_ComputesSuccessRateOverAllTenantInstances()
    {
        var (tenantA, _, _) = await SeedTenantWithOwnerAndMember("stats");
        var def = await _workflowRepo.UpsertDefinitionAsync(new WorkflowDefinition
        {
            Name = "llm-call", Description = "x", TenantId = tenantA, Steps = "[]"
        });
        // 7 completed, 2 failed, 1 running → success rate 7/9 = 77.77%.
        await SeedInstance(def.Id, tenantA, "completed", TimeSpan.FromMinutes(2));
        await SeedInstance(def.Id, tenantA, "completed", TimeSpan.FromMinutes(3));
        await SeedInstance(def.Id, tenantA, "completed", TimeSpan.FromMinutes(4));
        await SeedInstance(def.Id, tenantA, "completed", TimeSpan.FromMinutes(5));
        await SeedInstance(def.Id, tenantA, "completed", TimeSpan.FromMinutes(6));
        await SeedInstance(def.Id, tenantA, "completed", TimeSpan.FromMinutes(7));
        await SeedInstance(def.Id, tenantA, "completed", TimeSpan.FromMinutes(8));
        await SeedInstance(def.Id, tenantA, "failed", TimeSpan.FromMinutes(1));
        await SeedInstance(def.Id, tenantA, "failed", TimeSpan.FromMinutes(1));
        await SeedInstance(def.Id, tenantA, "running", null);

        var result = await UserDashboardEndpoints.GetStats(tenantA, _db);
        var (status, payload) = (await ExecuteAndCapture(result));
        status.Should().Be(StatusCodes.Status200OK);

        var root = JsonDocument.Parse(payload).RootElement;
        root.GetProperty("totalRuns").GetInt32().Should().Be(10);
        root.GetProperty("completedRuns").GetInt32().Should().Be(7);
        root.GetProperty("failedRuns").GetInt32().Should().Be(2);
        root.GetProperty("runningRuns").GetInt32().Should().Be(1);
        // Success rate is over terminal runs only: 7/9 ≈ 0.7778.
        var successRate = root.GetProperty("successRate").GetDouble();
        successRate.Should().BeApproximately(7.0 / 9.0, 0.01);
        root.GetProperty("avgDurationSeconds").GetDouble().Should().BeGreaterThan(0);
    }

    [Test]
    public async Task GetStats_IgnoresOtherTenants()
    {
        var (tenantA, _, _) = await SeedTenantWithOwnerAndMember("stats-a");
        var (tenantB, _, _) = await SeedTenantWithOwnerAndMember("stats-b");

        var defA = await _workflowRepo.UpsertDefinitionAsync(new WorkflowDefinition
        {
            Name = "llm-call", Description = "x", TenantId = tenantA, Steps = "[]"
        });
        var defB = await _workflowRepo.UpsertDefinitionAsync(new WorkflowDefinition
        {
            Name = "llm-call", Description = "x", TenantId = tenantB, Steps = "[]"
        });

        await SeedInstance(defA.Id, tenantA, "completed", TimeSpan.FromMinutes(2));
        await SeedInstance(defB.Id, tenantB, "completed", TimeSpan.FromMinutes(2));
        await SeedInstance(defB.Id, tenantB, "completed", TimeSpan.FromMinutes(2));
        await SeedInstance(defB.Id, tenantB, "failed", TimeSpan.FromMinutes(2));

        var result = await UserDashboardEndpoints.GetStats(tenantA, _db);
        var (_, payload) = await ExecuteAndCapture(result);
        JsonDocument.Parse(payload).RootElement.GetProperty("totalRuns").GetInt32().Should().Be(1);
    }

    [Test]
    public async Task GetStats_ReturnsZeroes_WhenNoRuns()
    {
        var (tenantA, _, _) = await SeedTenantWithOwnerAndMember("no-runs");

        var result = await UserDashboardEndpoints.GetStats(tenantA, _db);
        var (_, payload) = await ExecuteAndCapture(result);

        var root = JsonDocument.Parse(payload).RootElement;
        root.GetProperty("totalRuns").GetInt32().Should().Be(0);
        root.GetProperty("successRate").GetDouble().Should().Be(0);
        root.GetProperty("avgDurationSeconds").GetDouble().Should().Be(0);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<(Guid TenantId, Guid OwnerId, Guid MemberId)> SeedTenantWithOwnerAndMember(string slugPrefix)
    {
        var owner = await _userRepo.CreateAsync(new User
        {
            Email = $"owner-{Guid.NewGuid():N}@example.com",
            DisplayName = "Owner",
        });
        var member = await _userRepo.CreateAsync(new User
        {
            Email = $"member-{Guid.NewGuid():N}@example.com",
            DisplayName = "Member",
        });
        var tenant = await _tenantRepo.CreateAsync(new Tenant
        {
            Name = $"{slugPrefix}-org",
            Slug = $"{slugPrefix}-{Guid.NewGuid():N}".Substring(0, Math.Min(20, slugPrefix.Length + 6)),
            Type = "org",
            OwnerId = owner.Id,
        });
        await _membershipRepo.AddAsync(tenant.Id, owner.Id, TenantRoleHierarchy.Owner);
        await _membershipRepo.AddAsync(tenant.Id, member.Id, TenantRoleHierarchy.Member);
        return (tenant.Id, owner.Id, member.Id);
    }

    private async Task SeedEvents(Guid tenantId, int count)
    {
        for (int i = 0; i < count; i++)
        {
            await _events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = $"TEST.EVENT.{i}",
                TenantId = tenantId,
                Tags = "{}",
                Metadata = "{}",
                Data = "{}",
                CreatedAt = DateTime.UtcNow.AddSeconds(-i),
            });
        }
    }

    private async Task SeedInstances(Guid definitionId, Guid tenantId, int count)
    {
        for (int i = 0; i < count; i++)
        {
            await SeedInstance(definitionId, tenantId, "completed", TimeSpan.FromMinutes(2));
        }
    }

    private async Task SeedInstance(Guid definitionId, Guid tenantId, string status, TimeSpan? duration)
    {
        var now = DateTime.UtcNow;
        var started = duration.HasValue ? now.Subtract(duration.Value) : (DateTime?)null;
        var completed = duration.HasValue && status is "completed" or "failed" ? now : (DateTime?)null;
        await _workflowRepo.CreateInstanceAsync(new WorkflowInstance
        {
            Id = Guid.NewGuid(),
            DefinitionId = definitionId,
            TenantId = tenantId,
            Status = status,
            StartedAt = started,
            CompletedAt = completed,
        });
    }

    private async Task<(int Status, string Body)> ExecuteAndCapture(IResult result)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = ApiTestFixture.Factory.Services,
        };
        using var stream = new MemoryStream();
        ctx.Response.Body = stream;
        await result.ExecuteAsync(ctx);
        stream.Position = 0;
        var body = new StreamReader(stream).ReadToEnd();
        return (ctx.Response.StatusCode, body);
    }
}
