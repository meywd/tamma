using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;
using Tamma.Activities.TenantLifecycle;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Story 28-5 item #4 / AC4 Step I — tests for
/// <see cref="CleanupTenantRelationshipsActivity"/>. Exercises the pure-DI
/// static <see cref="CleanupTenantRelationshipsActivity.CleanupRelationshipsAsync"/>
/// against an EF InMemory <see cref="ControlPlaneDbContext"/> so the
/// disposition policy (delete vs. null vs. keep-for-audit) is covered
/// without standing up the Elsa runtime.
/// </summary>
[TestFixture]
public class CleanupTenantRelationshipsActivityTests
{
    private ControlPlaneDbContext _db = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new ControlPlaneDbContext(options);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public void Activity_HasCorrectStepName_AndIsContinueOnError()
    {
        var activity = new CleanupTenantRelationshipsActivity();
        activity.StepName.Should().Be(CleanupSteps.CleanupRelationships);
        activity.StepName.Should().Be("cleanup-cp-relationships");
        activity.Should().BeAssignableTo<CleanupStepActivity>(
            "a row-delete failure must record into the accumulator, not abort the run");
    }

    [Test]
    public void Activity_EmitsDeleteStepEvents()
    {
        // CleanupStepActivity emits the DELETE.* family — confirms item #6
        // alignment for this delete-only step.
        new CleanupTenantRelationshipsActivity().EventType
            .Should().Be("TENANT.CLEANUP.CLEANUP_CP_RELATIONSHIPS");
    }

    [Test]
    public async Task CleanupRelationships_DeletesMembershipsInvitesAndTenantKeyedRows()
    {
        var tenantId = Guid.NewGuid();
        SeedDeletableRows(tenantId);
        await _db.SaveChangesAsync();

        var result = await CleanupTenantRelationshipsActivity.CleanupRelationshipsAsync(
            _db, tenantId, CancellationToken.None);

        result.Memberships.Should().Be(1);
        result.Invites.Should().Be(1);
        result.QueuedTasks.Should().Be(1);
        result.Enablements.Should().Be(1);
        result.AlertChannels.Should().Be(1);
        result.ApiKeys.Should().Be(1);

        (await _db.TenantMemberships.IgnoreQueryFilters()
            .CountAsync(m => m.TenantId == tenantId)).Should().Be(0);
        (await _db.UserInvites.IgnoreQueryFilters()
            .CountAsync(i => i.TenantId == tenantId)).Should().Be(0);
        (await _db.TenantAgentEnablements.IgnoreQueryFilters()
            .CountAsync(e => e.TenantId == tenantId)).Should().Be(0);
        (await _db.AlertChannels.IgnoreQueryFilters()
            .CountAsync(a => a.TenantId == tenantId)).Should().Be(0);
        (await _db.ApiKeys.IgnoreQueryFilters()
            .CountAsync(k => k.TenantId == tenantId)).Should().Be(0);
    }

    [Test]
    public async Task CleanupRelationships_NullsGithubInstallationTenantId_WithoutDeletingRow()
    {
        var tenantId = Guid.NewGuid();
        var installation = new GitHubInstallation
        {
            Id = Guid.NewGuid(),
            InstallationId = 42,
            AccountLogin = "acme",
            AccountType = "Organization",
            AppId = 1,
            TenantId = tenantId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.GitHubInstallations.Add(installation);
        await _db.SaveChangesAsync();

        var result = await CleanupTenantRelationshipsActivity.CleanupRelationshipsAsync(
            _db, tenantId, CancellationToken.None);

        result.InstallationsReleased.Should().Be(1);
        var reloaded = await _db.GitHubInstallations.IgnoreQueryFilters()
            .FirstAsync(g => g.Id == installation.Id);
        reloaded.TenantId.Should().BeNull("the org-owned installation is released, not destroyed");
    }

    [Test]
    public async Task CleanupRelationships_KeepsCompletedQueuedTasks_AndAuditTables()
    {
        var tenantId = Guid.NewGuid();
        // Completed task — kept as its own audit trail (only pending rows go).
        _db.PlatformQueuedTasks.Add(new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = "tenant.move",
            TenantId = tenantId,
            Status = "completed",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        // Billing customer — financial record, keep-for-audit.
        _db.BillingCustomers.Add(new BillingCustomer
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var result = await CleanupTenantRelationshipsActivity.CleanupRelationshipsAsync(
            _db, tenantId, CancellationToken.None);

        result.QueuedTasks.Should().Be(0, "only pending queued tasks are deleted");
        (await _db.PlatformQueuedTasks.CountAsync(q => q.TenantId == tenantId))
            .Should().Be(1, "completed queued tasks are retained for audit");
        (await _db.BillingCustomers.IgnoreQueryFilters()
            .CountAsync(b => b.TenantId == tenantId))
            .Should().Be(1, "billing customers are financial records — keep for audit");
    }

    [Test]
    public async Task CleanupRelationships_OnlyTouchesTargetTenant()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        SeedDeletableRows(tenantId);
        SeedDeletableRows(otherTenantId);
        await _db.SaveChangesAsync();

        await CleanupTenantRelationshipsActivity.CleanupRelationshipsAsync(
            _db, tenantId, CancellationToken.None);

        (await _db.TenantMemberships.IgnoreQueryFilters()
            .CountAsync(m => m.TenantId == otherTenantId)).Should().Be(1);
        (await _db.ApiKeys.IgnoreQueryFilters()
            .CountAsync(k => k.TenantId == otherTenantId)).Should().Be(1);
        (await _db.AlertChannels.IgnoreQueryFilters()
            .CountAsync(a => a.TenantId == otherTenantId)).Should().Be(1,
            "the neighbour tenant's relationship rows must survive");
    }

    [Test]
    public async Task CleanupRelationships_IsIdempotent()
    {
        var tenantId = Guid.NewGuid();
        SeedDeletableRows(tenantId);
        await _db.SaveChangesAsync();

        await CleanupTenantRelationshipsActivity.CleanupRelationshipsAsync(
            _db, tenantId, CancellationToken.None);
        // Second run (replay after a crash) — nothing left to delete.
        var second = await CleanupTenantRelationshipsActivity.CleanupRelationshipsAsync(
            _db, tenantId, CancellationToken.None);

        second.Memberships.Should().Be(0);
        second.ApiKeys.Should().Be(0);
        second.InstallationsReleased.Should().Be(0);
    }

    private void SeedDeletableRows(Guid tenantId)
    {
        _db.TenantMemberships.Add(new TenantMembership
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = Guid.NewGuid(),
            Role = "member",
            JoinedAt = DateTime.UtcNow,
        });
        _db.UserInvites.Add(new UserInvite
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Email = "x@example.com",
            InviteTokenHash = "hash",
            InvitedBy = Guid.NewGuid(),
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            CreatedAt = DateTime.UtcNow,
        });
        _db.PlatformQueuedTasks.Add(new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = "tenant.move",
            TenantId = tenantId,
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.TenantAgentEnablements.Add(new TenantAgentEnablement
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AgentId = Guid.NewGuid(),
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        });
        _db.AlertChannels.Add(new AlertChannel
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "ops",
            ChannelType = "email",
        });
        _db.ApiKeys.Add(new ApiKey
        {
            Id = Guid.NewGuid(),
            Scope = "tenant",
            OwnerId = Guid.NewGuid().ToString(),
            KeyHash = "h",
            KeyPrefix = "tk_",
            Label = "k",
            TenantId = tenantId,
            CreatedAt = DateTime.UtcNow,
        });
    }
}
