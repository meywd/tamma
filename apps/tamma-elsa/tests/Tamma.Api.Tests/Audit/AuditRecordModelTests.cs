using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Audit;

/// <summary>
/// Story 37-1 (AC4–AC6, AC12) — model-shape tests for <see cref="AuditRecord"/>
/// + <see cref="AuditProjectorCursor"/>. Pure model-metadata inspection (no
/// Postgres connection opened). The constraint/idempotency proofs InMemory
/// cannot honour (XOR CHECK, UNIQUE, search-path isolation) live in
/// <see cref="AuditRecordMigrationTests"/> against a real Postgres 17 container.
/// </summary>
[TestFixture]
public class AuditRecordModelTests
{
    private static ControlPlaneDbContext Cp() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql("Host=localhost;Database=cp_test;Username=tamma;Password=tamma")
            .Options);

    private static TenantDbContext Tenant() =>
        new(new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql("Host=localhost;Database=tenant_test;Username=tamma;Password=tamma")
            .Options);

    // ── AC4 — audit_records mapped on BOTH contexts ──

    [Test]
    public void AuditRecords_Mapped_On_ControlPlane()
    {
        using var ctx = Cp();
        ctx.Model.GetEntityTypes().Select(t => t.GetTableName())
            .Should().Contain("audit_records");
    }

    [Test]
    public void AuditRecords_Mapped_On_Tenant()
    {
        using var ctx = Tenant();
        ctx.Model.GetEntityTypes().Select(t => t.GetTableName())
            .Should().Contain("audit_records");
    }

    // ── AC4 — the projector cursor lives ONLY on the control plane ──

    [Test]
    public void ProjectorCursor_Mapped_On_ControlPlane_Only()
    {
        using var cp = Cp();
        cp.Model.GetEntityTypes().Select(t => t.GetTableName())
            .Should().Contain("audit_projector_cursor");

        using var tenant = Tenant();
        tenant.Model.GetEntityTypes().Select(t => t.GetTableName())
            .Should().NotContain("audit_projector_cursor",
                "the single projector resumes from one CP-resident cursor row");
    }

    // ── AC4 — the full column set ──

    [Test]
    public void AuditRecord_Has_The_Full_Column_Set()
    {
        using var ctx = Cp();
        var props = ctx.Model.FindEntityType(typeof(AuditRecord))!
            .GetProperties().Select(p => p.Name).ToHashSet();

        props.Should().BeEquivalentTo(new[]
        {
            "Id", "ActionCode", "Category", "Severity",
            "ActorUserId", "ActorEmailSnapshot",
            "TargetType", "TargetId", "Outcome",
            "IpAddress", "UserAgent", "OccurredAt",
            "SourceEventId", "SourceSequenceNumber", "PayloadJson",
            "TenantId", "UserId",
            // Story 37-2 tamper-evidence hash chain.
            "RecordHash", "PrevRecordHash", "ChainSequence",
        });
    }

    // ── AC12 — reserved 37-2 hash columns exist and are nullable ──

    [Test]
    public void Reserved_Hash_Columns_Are_Nullable()
    {
        using var ctx = Cp();
        var entity = ctx.Model.FindEntityType(typeof(AuditRecord))!;
        entity.FindProperty("RecordHash")!.IsNullable.Should().BeTrue();
        entity.FindProperty("PrevRecordHash")!.IsNullable.Should().BeTrue();
        // Story 37-2 — ChainSequence is nullable only for pre-37-2 legacy rows
        // (new inserts always populate it).
        entity.FindProperty("ChainSequence")!.IsNullable.Should().BeTrue();
    }

    // ── AC4 — payload is text (Story 37-2 fix); occurred_at is timestamptz ──

    [Test]
    public void Payload_Is_Text_And_OccurredAt_Is_TimestampTz()
    {
        using var ctx = Cp();
        var entity = ctx.Model.FindEntityType(typeof(AuditRecord))!;
        // Story 37-2 (code-review fix) — PayloadJson is stored as text, not jsonb,
        // so the hash-chain preimage round-trips byte-for-byte (jsonb would reorder
        // keys / normalize and make every chain verify as TAMPERED).
        entity.FindProperty("PayloadJson")!.GetColumnType().Should().Be("text");
        entity.FindProperty("OccurredAt")!.GetColumnType().Should().Be("timestamp with time zone");
    }

    // ── AC5 — the ownership columns are nullable (XOR enforced by CHECK, not NOT NULL) ──

    [Test]
    public void Ownership_Columns_Are_Nullable()
    {
        using var ctx = Cp();
        var entity = ctx.Model.FindEntityType(typeof(AuditRecord))!;
        entity.FindProperty("TenantId")!.IsNullable.Should().BeTrue();
        entity.FindProperty("UserId")!.IsNullable.Should().BeTrue();
    }

    // ── AC5/AC8 — the unique source_event_id index (idempotency key) ──

    [Test]
    public void Has_Unique_SourceEventId_Index()
    {
        using var ctx = Cp();
        var index = ctx.Model.FindEntityType(typeof(AuditRecord))!
            .GetIndexes().Single(i => i.GetDatabaseName() == "UX_audit_records_SourceEventId");
        index.IsUnique.Should().BeTrue("one curated row per raw event (idempotency)");
        index.Properties.Select(p => p.Name).Should().Equal("SourceEventId");
    }

    // ── AC12 — the deterministic-order index for 37-2's chain ──

    [Test]
    public void Has_SourceSequenceNumber_Index()
    {
        using var ctx = Cp();
        ctx.Model.FindEntityType(typeof(AuditRecord))!
            .GetIndexes().Should().Contain(i =>
                i.GetDatabaseName() == "IX_audit_records_SourceSequenceNumber");
    }
}
