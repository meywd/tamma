using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.TenantLifecycle;
using Tamma.Data.Pooling;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Unified-tenancy Phase 2 Task 5 — tests for
/// <see cref="DropTenantSchemaActivity"/> (the schema-scoped replacement
/// for the deleted db-per-tenant <c>DropTenantDatabaseActivity</c>).
/// Wiring assertions cover the runtime-bound surface (base class + step
/// names — <c>ProcessAsync</c> only runs inside the Elsa runtime); the
/// drop logic itself is real-tested through the pure-DI
/// <see cref="DropTenantSchemaActivity.DropSchemaAsync"/> entry point
/// with the recording pool fake.
/// </summary>
[TestFixture]
public class DropTenantSchemaActivityTests
{
    private static readonly Guid Tenant = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid PoolRow = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // ── Wiring (runtime-bound surface) ────────────────────────────────

    [Test]
    public void DropTenantSchemaActivity_HasCorrectStepName()
    {
        new DropTenantSchemaActivity().StepName.Should().Be("drop-schema");
    }

    [Test]
    public void DropTenantSchemaActivity_InheritsTenantLifecycleActivity()
    {
        typeof(DropTenantSchemaActivity)
            .Should()
            .BeDerivedFrom<TenantLifecycleActivity>();
    }

    [Test]
    public void CleanupVariant_HasCorrectStepName_AndCleanupBase()
    {
        var activity = new DropTenantSchemaForCleanupActivity();
        activity.StepName.Should().Be(CleanupSteps.DropSchema);
        activity.StepName.Should().Be("drop-tenant-schema");
        activity.Should().BeAssignableTo<CleanupStepActivity>(
            "the cleanup workflow relies on continue-on-error semantics");
    }

    // ── Real logic via the pure-DI core ───────────────────────────────

    [Test]
    public async Task DropSchemaAsync_NoPlacement_SkipsWithoutTouchingPool()
    {
        var pool = new RecordingTenantDatabasePool();

        var dropped = await DropTenantSchemaActivity.DropSchemaAsync(
            pool, Tenant, databaseId: null, schemaName: null,
            logger: null, logScope: "lifecycle", CancellationToken.None);

        dropped.Should().BeFalse("a pre-placement tenant has no schema to drop");
        pool.ExecutedCommands.Should().BeEmpty(
            "no DDL may be issued for a tenant without a placement");
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task DropSchemaAsync_HalfStampedPlacement_Skips(
        bool hasDatabaseId, bool hasSchemaName)
    {
        // A half-stamped row is treated as unplaced — matching
        // TenantPlacementService.AssignAsync's idempotency rule.
        var pool = new RecordingTenantDatabasePool();

        var dropped = await DropTenantSchemaActivity.DropSchemaAsync(
            pool,
            Tenant,
            hasDatabaseId ? PoolRow : null,
            hasSchemaName ? TenantNaming.SchemaName(Tenant) : null,
            logger: null, logScope: "lifecycle", CancellationToken.None);

        dropped.Should().BeFalse();
        pool.ExecutedCommands.Should().BeEmpty();
    }

    [Test]
    public async Task DropSchemaAsync_Placed_IssuesDropSchemaCascadeOnAssignedRow()
    {
        var pool = new RecordingTenantDatabasePool();
        var schemaName = TenantNaming.SchemaName(Tenant);

        var dropped = await DropTenantSchemaActivity.DropSchemaAsync(
            pool, Tenant, PoolRow, schemaName,
            logger: null, logScope: "lifecycle", CancellationToken.None);

        dropped.Should().BeTrue();
        var (databaseId, sql) = pool.ExecutedCommands.Should().ContainSingle().Subject;
        databaseId.Should().Be(PoolRow,
            "the drop must run on the ASSIGNED pool row, not the central admin connection");
        sql.Should().Be($"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE;",
            "IF EXISTS keeps a replay idempotent; CASCADE keeps the drop O(1) on data volume");
    }
}
