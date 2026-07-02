using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Analytics;

/// <summary>
/// Story 36-1 — model-shape + InMemory/Npgsql parity tests for the per-tenant
/// dimensional analytics fact tables (<c>analytics_usage_hourly</c> +
/// <c>analytics_usage_daily</c>). Pure model-metadata inspection — no Postgres
/// connection is opened. The Npgsql leg asserts the relational store types
/// (text / bigint / numeric(20,4)); the InMemory leg asserts the same
/// CLR-shape so the two providers agree on columns, nullability, and the
/// <see cref="Tamma.Core.Enums.CostBasis"/>→text conversion (AC10).
///
/// <para>The constraint/idempotency proofs that InMemory cannot honour
/// (NULLS NOT DISTINCT, search-path isolation) live in
/// <see cref="AnalyticsUsageMigrationTests"/> against a real Postgres 17
/// container.</para>
/// </summary>
[TestFixture]
public class AnalyticsUsageModelTests
{
    private static TenantDbContext CreateNpgsqlContext() =>
        new(new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql("Host=localhost;Database=tenant_test;Username=tamma;Password=tamma")
            .Options);

    private static TenantDbContext CreateInMemoryContext() =>
        new(new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase($"analytics_usage_{Guid.NewGuid():N}")
            .Options);

    // ── AC4 — both fact tables are mapped on TenantDbContext ──

    [Test]
    public void TenantDbContext_Maps_BothFactTables()
    {
        using var ctx = CreateNpgsqlContext();
        var tables = ctx.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .ToHashSet();

        tables.Should().Contain("analytics_usage_hourly");
        tables.Should().Contain("analytics_usage_daily");
    }

    // ── AC1/AC2 — identical dimension + measure shape; only the bucket differs ──

    [Test]
    public void Hourly_And_Daily_Share_IdenticalShape_ExceptBucket()
    {
        using var ctx = CreateNpgsqlContext();
        var hourly = ctx.Model.FindEntityType(typeof(AnalyticsUsageHourly))!
            .GetProperties().Select(p => p.Name).ToHashSet();
        var daily = ctx.Model.FindEntityType(typeof(AnalyticsUsageDaily))!
            .GetProperties().Select(p => p.Name).ToHashSet();

        // Drop the bucket column; everything else must match exactly.
        hourly.Remove("Hour").Should().BeTrue();
        daily.Remove("Day").Should().BeTrue();
        hourly.Should().BeEquivalentTo(daily,
            "the daily roll-up must be a lossless GROUP BY of the hourly grain");

        // The shared (non-bucket) contract — pin it so a measure can't silently vanish.
        hourly.Should().BeEquivalentTo(new[]
        {
            "Id", "Provider", "AgentId", "WorkflowDefinitionId", "RepoId", "CostBasis",
            "TokensIn", "TokensOut", "CostUsd", "PlatformBilledUsd",
            "WorkflowsStarted", "WorkflowsCompleted", "WorkflowsFailed", "AgentDispatches",
            "ComputedAt",
        });
    }

    // ── AC3/AC10 — CostBasis persists as text on BOTH providers ──

    [TestCase(typeof(AnalyticsUsageHourly))]
    [TestCase(typeof(AnalyticsUsageDaily))]
    public void CostBasis_Is_Converted_To_Text_OnNpgsql(Type clr)
    {
        using var ctx = CreateNpgsqlContext();
        var prop = ctx.Model.FindEntityType(clr)!.FindProperty("CostBasis")!;

        prop.GetValueConverter().Should().NotBeNull(
            "CostBasis must round-trip through a string converter, never the ordinal");
        prop.ClrType.Should().Be(typeof(Tamma.Core.Enums.CostBasis));
        prop.GetValueConverter()!.ProviderClrType.Should().Be(typeof(string),
            "the store-facing type for CostBasis is string/text on Npgsql");
        prop.GetRelationalTypeMapping().StoreType.Should().Be("character varying(20)",
            "CostBasis is a 20-char text discriminator on Npgsql");
        prop.IsNullable.Should().BeFalse("CostBasis is a required dimension");
    }

    [TestCase(typeof(AnalyticsUsageHourly))]
    [TestCase(typeof(AnalyticsUsageDaily))]
    public void CostBasis_Is_Converted_To_String_OnInMemory(Type clr)
    {
        using var ctx = CreateInMemoryContext();
        var prop = ctx.Model.FindEntityType(clr)!.FindProperty("CostBasis")!;

        prop.GetValueConverter().Should().NotBeNull(
            "the same string conversion applies on InMemory — parity with Npgsql");
        prop.GetValueConverter()!.ProviderClrType.Should().Be(typeof(string));
    }

    // ── AC1/AC2 — nullability of dimensions ──

    [TestCase(typeof(AnalyticsUsageHourly))]
    [TestCase(typeof(AnalyticsUsageDaily))]
    public void NullableDimensions_AreNullable_RequiredDimensions_AreRequired(Type clr)
    {
        using var ctx = CreateNpgsqlContext();
        var entity = ctx.Model.FindEntityType(clr)!;

        entity.FindProperty("AgentId")!.IsNullable.Should().BeTrue();
        entity.FindProperty("WorkflowDefinitionId")!.IsNullable.Should().BeTrue();
        entity.FindProperty("RepoId")!.IsNullable.Should().BeTrue();
        entity.FindProperty("Provider")!.IsNullable.Should().BeTrue(
            "Provider is nullable (Story 36-2) — workflow/dispatch counts bucket under NULL");

        entity.FindProperty("CostBasis")!.IsNullable.Should().BeFalse("CostBasis is required");
    }

    [TestCase(typeof(AnalyticsUsageHourly))]
    [TestCase(typeof(AnalyticsUsageDaily))]
    public void NullableDimensions_AreNullable_OnInMemory_Too(Type clr)
    {
        using var ctx = CreateInMemoryContext();
        var entity = ctx.Model.FindEntityType(clr)!;

        entity.FindProperty("AgentId")!.IsNullable.Should().BeTrue();
        entity.FindProperty("WorkflowDefinitionId")!.IsNullable.Should().BeTrue();
        entity.FindProperty("RepoId")!.IsNullable.Should().BeTrue();
        entity.FindProperty("Provider")!.IsNullable.Should().BeTrue();
    }

    // ── AC8 — counter store types are bigint, costs are numeric(20,4) ──

    [TestCase(typeof(AnalyticsUsageHourly))]
    [TestCase(typeof(AnalyticsUsageDaily))]
    public void Counters_Are_Bigint_And_Costs_Are_Numeric_20_4(Type clr)
    {
        using var ctx = CreateNpgsqlContext();
        var entity = ctx.Model.FindEntityType(clr)!;

        foreach (var counter in new[]
            { "TokensIn", "TokensOut", "WorkflowsStarted", "WorkflowsCompleted",
              "WorkflowsFailed", "AgentDispatches" })
        {
            entity.FindProperty(counter)!.GetRelationalTypeMapping().StoreType
                .Should().Be("bigint", $"{counter} is a long counter");
        }

        foreach (var cost in new[] { "CostUsd", "PlatformBilledUsd" })
        {
            entity.FindProperty(cost)!.GetRelationalTypeMapping().StoreType
                .Should().Be("numeric(20,4)",
                    $"{cost} mirrors PlatformAnalyticsHourly precision for lossless owner-side joins");
        }
    }

    // ── AC1/AC2 — the bucket column is timestamptz ──

    [Test]
    public void Hourly_BucketColumn_Is_TimestampTz()
    {
        using var ctx = CreateNpgsqlContext();
        var prop = ctx.Model.FindEntityType(typeof(AnalyticsUsageHourly))!.FindProperty("Hour")!;
        prop.GetColumnType().Should().Be("timestamp with time zone");
    }

    [Test]
    public void Daily_BucketColumn_Is_TimestampTz()
    {
        using var ctx = CreateNpgsqlContext();
        var prop = ctx.Model.FindEntityType(typeof(AnalyticsUsageDaily))!.FindProperty("Day")!;
        prop.GetColumnType().Should().Be("timestamp with time zone");
    }

    // ── AC9 — measure defaults (0 counters, 0 costs, now(), gen_random_uuid()) ──

    [TestCase(typeof(AnalyticsUsageHourly))]
    [TestCase(typeof(AnalyticsUsageDaily))]
    public void Measures_Default_To_Zero_And_Id_And_ComputedAt_HaveServerDefaults(Type clr)
    {
        using var ctx = CreateNpgsqlContext();
        var entity = ctx.Model.FindEntityType(clr)!;

        entity.FindProperty("Id")!.GetDefaultValueSql().Should().Be("gen_random_uuid()");
        entity.FindProperty("ComputedAt")!.GetDefaultValueSql().Should().Be("now()");

        foreach (var counter in new[]
            { "TokensIn", "TokensOut", "WorkflowsStarted", "WorkflowsCompleted",
              "WorkflowsFailed", "AgentDispatches" })
        {
            entity.FindProperty(counter)!.GetDefaultValue().Should().Be(0L,
                $"{counter} must default to 0L so a partial upsert never writes NULL");
        }

        entity.FindProperty("CostUsd")!.GetDefaultValue().Should().Be(0m);
        entity.FindProperty("PlatformBilledUsd")!.GetDefaultValue().Should().Be(0m);
    }

    // ── AC6 — breakdown index over (bucket, Provider, AgentId, WorkflowDefinitionId, CostBasis) ──

    [Test]
    public void Hourly_Has_BreakdownIndex()
    {
        using var ctx = CreateNpgsqlContext();
        var index = ctx.Model.FindEntityType(typeof(AnalyticsUsageHourly))!
            .GetIndexes().Single(i => i.GetDatabaseName() == "IX_analytics_usage_hourly_breakdown");

        index.IsUnique.Should().BeFalse();
        index.Properties.Select(p => p.Name).Should()
            .Equal("Hour", "Provider", "AgentId", "WorkflowDefinitionId", "CostBasis");
    }

    [Test]
    public void Daily_Has_BreakdownIndex()
    {
        using var ctx = CreateNpgsqlContext();
        var index = ctx.Model.FindEntityType(typeof(AnalyticsUsageDaily))!
            .GetIndexes().Single(i => i.GetDatabaseName() == "IX_analytics_usage_daily_breakdown");

        index.IsUnique.Should().BeFalse();
        index.Properties.Select(p => p.Name).Should()
            .Equal("Day", "Provider", "AgentId", "WorkflowDefinitionId", "CostBasis");
    }

    // ── AC7 — unique business key over the FULL dimension tuple, NULLS NOT DISTINCT ──

    [Test]
    public void Hourly_Has_NullsNotDistinct_UniqueBusinessKey()
    {
        using var ctx = CreateNpgsqlContext();
        // The runtime read-optimized model prunes the Npgsql:NullsDistinct
        // annotation (same as CHECK constraints) — read it from the
        // design-time model where it survives.
        var index = DesignIndex(ctx, typeof(AnalyticsUsageHourly), "UX_analytics_usage_hourly_dims");

        index.IsUnique.Should().BeTrue();
        index.GetAreNullsDistinct().Should().BeFalse(
            "NULL AgentId/WorkflowDefinitionId/RepoId must dedupe to one row per bucket (PG15+)");
        index.Properties.Select(p => p.Name).Should()
            .Equal("Hour", "Provider", "AgentId", "WorkflowDefinitionId", "RepoId", "CostBasis");
    }

    [Test]
    public void Daily_Has_NullsNotDistinct_UniqueBusinessKey()
    {
        using var ctx = CreateNpgsqlContext();
        var index = DesignIndex(ctx, typeof(AnalyticsUsageDaily), "UX_analytics_usage_daily_dims");

        index.IsUnique.Should().BeTrue();
        index.GetAreNullsDistinct().Should().BeFalse();
        index.Properties.Select(p => p.Name).Should()
            .Equal("Day", "Provider", "AgentId", "WorkflowDefinitionId", "RepoId", "CostBasis");
    }

    private static IReadOnlyIndex DesignIndex(TenantDbContext ctx, Type clr, string indexName)
    {
        var designModel = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
            .GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>(ctx).Model;
        return designModel.FindEntityType(clr)!
            .GetIndexes().Single(i => i.GetDatabaseName() == indexName);
    }

    // ── Tenancy — NO TenantId column on either table (Doc 01 §1.4) ──

    [TestCase(typeof(AnalyticsUsageHourly))]
    [TestCase(typeof(AnalyticsUsageDaily))]
    public void FactTables_Have_No_TenantId_Column(Type clr)
    {
        using var ctx = CreateNpgsqlContext();
        ctx.Model.FindEntityType(clr)!.GetProperties().Select(p => p.Name)
            .Should().NotContain("TenantId",
                "isolation is the per-tenant schema, not a column (Doc 01 §1.4)");
    }

    // ── No EF query filter (search-path is the only isolation plane) ──

    [TestCase(typeof(AnalyticsUsageHourly))]
    [TestCase(typeof(AnalyticsUsageDaily))]
    public void FactTables_Have_No_QueryFilter(Type clr)
    {
        using var ctx = CreateNpgsqlContext();
        ctx.Model.FindEntityType(clr)!.GetQueryFilter().Should().BeNull(
            "TenantDbContext carries no query filters — the search-path schema isolates");
    }
}
