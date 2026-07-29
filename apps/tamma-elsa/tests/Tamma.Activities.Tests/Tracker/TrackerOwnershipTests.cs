using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Tests.Tracker;

/// <summary>
/// Story 44-1 AC7 — pins epic Decision D6 structurally: work items, projects,
/// relations and iterations are tenant-schema CONTENT and carry NO principal
/// ownership plane. No work-item surface filters on a <c>UserId</c> /
/// <c>TenantId</c> plane, no content entity carries a principal column, and
/// the parallel-surface pattern exists on <c>tracker_preferences</c> ONLY.
/// Runs without Docker — this is a reflection pin, not a DB test.
/// </summary>
[TestFixture]
public class TrackerOwnershipTests
{
    private static readonly string[] s_principalNames = ["UserId", "TenantId", "OwnerId"];

    [Test]
    public void Content_entities_carry_no_principal_ownership_column()
    {
        // AssigneeUserId / CreatedByUserId / CreatedBy are attribution (who
        // did or does the work), not ownership — a principal plane would be a
        // property named exactly UserId / TenantId / OwnerId.
        foreach (var entity in new[]
                 {
                     typeof(WorkItemEntity), typeof(ProjectEntity),
                     typeof(WorkItemRelation), typeof(IterationEntity),
                 })
        {
            entity.GetProperties().Select(p => p.Name)
                .Should().NotContain((IEnumerable<string>)s_principalNames,
                    $"{entity.Name} is tenant-schema content (epic D6) — the schema is the "
                    + "isolation plane; a principal column would encode a second ownership "
                    + "plane with no reader");
        }
    }

    [Test]
    public void No_work_item_surface_takes_a_principal_filter_parameter()
    {
        foreach (var contract in new[]
                 {
                     typeof(IWorkItemRepository), typeof(IProjectRepository),
                     typeof(IIterationRepository),
                 })
        {
            foreach (var method in contract.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                method.GetParameters().Select(p => p.Name ?? string.Empty).Should().NotContain(
                    new[] { "userId", "tenantId", "ownerId" },
                    $"{contract.Name}.{method.Name} must not filter on a principal plane "
                    + "(story AC7); createdByUserId-style attribution parameters are allowed");
                method.Name.Should().NotContainAny("ByTenant", "ForTenant", "ByUser", "ForUser");
            }
        }
    }

    [Test]
    public void WorkItemQuery_exposes_assignment_but_no_ownership_plane()
    {
        var names = typeof(WorkItemQuery).GetProperties().Select(p => p.Name).ToList();
        names.Should().Contain("AssigneeUserId", "assignment is a real work-item fact");
        names.Should().NotContain((IEnumerable<string>)s_principalNames,
            "a UserId/TenantId filter on the work-item query would be the second ownership plane D6 rejects");
    }

    [Test]
    public void The_parallel_plane_pattern_lives_on_tracker_preferences_only()
    {
        // Where the pattern DOES apply, it applies exactly: the six paired
        // methods of the IAcceptanceRulesRepository contract shape.
        var methods = typeof(ITrackerPreferenceRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToList();
        methods.Should().BeEquivalentTo(
        [
            "GetAsync", "UpsertAsync", "DeleteAsync",
            "GetByTenantAsync", "UpsertForTenantAsync", "DeleteByTenantAsync",
        ]);

        typeof(TrackerPreference).GetProperties().Select(p => p.Name)
            .Should().Contain(new[] { "UserId", "TenantId" },
                "tracker_preferences is genuine per-principal configuration and takes the "
                + "dual-scoped pattern (strong XOR) exactly");
    }
}
