using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Npgsql;
using NUnit.Framework;
using Tamma.Activities.Analytics;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Analytics;

/// <summary>
/// Story 36-2 (AC7/AC8/AC15) — Postgres 17 Testcontainer proof for the
/// dimensional projection: per-tenant schema isolation, a tenant-A failure
/// leaving tenant B intact, the NULLS-NOT-DISTINCT concurrent-replay backstop,
/// and checkpoint resume. Follows <c>AnalyticsUsageMigrationTests</c> /
/// <c>SchemaPerTenantMigrationTests</c> (two tenant schemas in one DB,
/// search-path isolation). EF InMemory honours neither NULLS NOT DISTINCT nor
/// the checkpoint migration, so a real Postgres is the only proof.
/// </summary>
[TestFixture]
public class DimensionalRollupIsolationTests
{
    private static readonly DateTime Hour = new(2026, 04, 18, 12, 00, 00, DateTimeKind.Utc);

    private PostgreSqlContainer _postgres = null!;
    private string _baseConnectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("dim_rollup_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _baseConnectionString = _postgres.GetConnectionString();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _postgres.DisposeAsync();

    private string CsFor(string schema) =>
        new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = schema }.ConnectionString;

    private static Mock<IPlatformEventPublisher> Publisher()
    {
        var p = new Mock<IPlatformEventPublisher>();
        p.Setup(x => x.AppendAndPublishAsync(It.IsAny<PlatformEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlatformEvent e, CancellationToken _) => e);
        return p;
    }

    private static DomainEvent Llm(long seq, string provider, decimal cost, long tin, long tout) => new()
    {
        Id = Guid.NewGuid(),
        Type = "LLM.CALL.SUCCESS",
        CreatedAt = Hour.AddSeconds(seq),
        SequenceNumber = seq,
        Tags = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["provider"] = provider,
            ["billing_mode"] = "platform",
        }),
        Metadata = "{}",
        Data = JsonSerializer.Serialize(new { costUsd = cost, inputTokens = tin, outputTokens = tout }),
    };

    [Test]
    public async Task Projection_IsPerTenantIsolated_AndFailureTolerant_AndResumable()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var unreachable = Guid.NewGuid();
        var schemaA = TenantNaming.SchemaName(tenantA);
        var schemaB = TenantNaming.SchemaName(tenantB);

        var migrator = new EfTenantDbMigrator();
        await migrator.MigrateTenantAppAsync(CsFor(schemaA));
        await migrator.MigrateTenantAppAsync(CsFor(schemaB));

        var factory = new SchemaRoutingFactory(_baseConnectionString)
            .Map(tenantA, schemaA)
            .Map(tenantB, schemaB);
        var publisher = Publisher();
        var pricing = new NullAnalyticsPricingConfig();

        // Seed only tenant A with usage events.
        var optsA = new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(CsFor(schemaA)).Options;
        await using (var ctxA = new TenantDbContext(optsA, tenantA))
        {
            ctxA.DomainEvents.AddRange(Llm(1, "anthropic", 0.10m, 100, 50), Llm(2, "anthropic", 0.20m, 200, 100));
            await ctxA.SaveChangesAsync();
        }

        // ── (a) isolation — projecting A writes only into A's schema ──
        await ComputeTenantDimensionalRollupActivity.ComputeAsync(
            factory, publisher.Object, tenantA, Hour, pricing, false, null, CancellationToken.None);

        var optsB = new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(CsFor(schemaB)).Options;
        await using (var ctxB = new TenantDbContext(optsB, tenantB))
        {
            (await ctxB.AnalyticsUsageHourly.AnyAsync()).Should().BeFalse(
                "tenant A's projection must be invisible in tenant B's schema");
        }
        await using (var ctxA = new TenantDbContext(optsA, tenantA))
        {
            var row = await ctxA.AnalyticsUsageHourly.SingleAsync();
            row.Provider.Should().Be("anthropic");
            row.CostUsd.Should().Be(0.30m);
            (await ctxA.AnalyticsProjectionCheckpoints.SingleAsync()).LastSequenceNumber.Should().Be(2);
        }

        // ── (b) failure tolerance — an unreachable tenant throws; B still projects ──
        Func<Task> unreachableRun = () => ComputeTenantDimensionalRollupActivity.ComputeAsync(
            factory, publisher.Object, unreachable, Hour, pricing, false, null, CancellationToken.None);
        await unreachableRun.Should().ThrowAsync<Exception>("an unreachable tenant surfaces to the fan-out catch");

        await ComputeTenantDimensionalRollupActivity.ComputeAsync(
            factory, publisher.Object, tenantB, Hour, pricing, false, null, CancellationToken.None);
        await using (var ctxB = new TenantDbContext(optsB, tenantB))
        {
            // B had no events → zero usage rows but a checkpoint row exists.
            (await ctxB.AnalyticsProjectionCheckpoints.AnyAsync()).Should().BeTrue(
                "tenant B projects independently of tenant A / the unreachable tenant");
        }

        // ── (c) idempotent replay collapses on the business key (no dup) ──
        await ComputeTenantDimensionalRollupActivity.ComputeAsync(
            factory, publisher.Object, tenantA, Hour, pricing, false, null, CancellationToken.None);
        await using (var ctxA = new TenantDbContext(optsA, tenantA))
        {
            (await ctxA.AnalyticsUsageHourly.CountAsync()).Should().Be(1,
                "re-projection upserts on UX_analytics_usage_hourly_dims — never duplicates");
            var row = await ctxA.AnalyticsUsageHourly.SingleAsync();
            row.CostUsd.Should().Be(0.30m, "measures are recomputed, not doubled");
        }
    }

    [Test]
    public async Task NullDimensionTuple_Collides_OnNullsNotDistinct()
    {
        var tenant = Guid.NewGuid();
        var schema = TenantNaming.SchemaName(tenant);
        await new EfTenantDbMigrator().MigrateTenantAppAsync(CsFor(schema));

        // Two events on the SAME provider with NO agent/workflow/repo tags →
        // identical (Hour, provider, NULL, NULL, NULL, platform) tuple. The
        // projection collapses them to one row (whole-bucket aggregate); a
        // manual duplicate insert must violate UX_*_dims (NULLS NOT DISTINCT).
        var factory = new SchemaRoutingFactory(_baseConnectionString).Map(tenant, schema);
        var opts = new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(CsFor(schema)).Options;
        await using (var ctx = new TenantDbContext(opts, tenant))
        {
            ctx.DomainEvents.AddRange(Llm(1, "anthropic", 0.10m, 10, 5), Llm(2, "anthropic", 0.20m, 20, 10));
            await ctx.SaveChangesAsync();
        }

        await ComputeTenantDimensionalRollupActivity.ComputeAsync(
            factory, Publisher().Object, tenant, Hour, new NullAnalyticsPricingConfig(), false, null,
            CancellationToken.None);

        await using (var ctx = new TenantDbContext(opts, tenant))
        {
            (await ctx.AnalyticsUsageHourly.CountAsync()).Should().Be(1,
                "two provider-only events with all-NULL dims collapse to one tuple");
        }

        // Manual duplicate insert of the identical all-NULL tuple must collide.
        await using var conn = new NpgsqlConnection(CsFor(schema));
        await conn.OpenAsync();
        await using var dup = new NpgsqlCommand(
            """
            INSERT INTO analytics_usage_hourly ("Hour", "Provider", "CostBasis")
            VALUES (TIMESTAMPTZ '2026-04-18T12:00:00Z', 'anthropic', 'platform');
            """, conn);
        var act = async () => await dup.ExecuteNonQueryAsync();
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
    }

    /// <summary>Routes each tenant id to its own search-path schema connection string.</summary>
    private sealed class SchemaRoutingFactory : ITenantDbContextFactory
    {
        private readonly string _baseCs;
        private readonly Dictionary<Guid, string> _schemas = new();
        public SchemaRoutingFactory(string baseCs) => _baseCs = baseCs;

        public SchemaRoutingFactory Map(Guid tenantId, string schema)
        {
            _schemas[tenantId] = schema;
            return this;
        }

        public ValueTask<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken ct = default)
        {
            if (!_schemas.TryGetValue(tenantId, out var schema))
                throw new InvalidOperationException($"Tenant {tenantId} not reachable.");
            var cs = new NpgsqlConnectionStringBuilder(_baseCs) { SearchPath = schema }.ConnectionString;
            var opts = new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(cs).Options;
            return new ValueTask<TenantDbContext>(new TenantDbContext(opts, tenantId));
        }
    }
}
