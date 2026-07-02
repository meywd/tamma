using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Seeders;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-5 (AC1-AC3) — <see cref="MarginPolicySeeder"/> + the SQL invariants
/// on <c>margin_policies</c>, against a real Postgres testcontainer so the CHECK
/// constraints and the partial <c>NULLS NOT DISTINCT</c> unique index behave like
/// production.
/// </summary>
[TestFixture]
public class MarginPolicySeederTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("margin_policy_seeder_test")
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
        await ctx.Database.ExecuteSqlRawAsync("TRUNCATE margin_policies CASCADE;");
    }

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    [Test]
    public async Task SeedAsync_FreshDb_InsertsGlobal1Point3xPolicy()
    {
        await using var ctx = NewContext();

        var inserted = await MarginPolicySeeder.SeedAsync(ctx);

        inserted.Should().Be(1);
        var row = await NewContext().MarginPolicies.AsNoTracking().SingleAsync();
        row.Scope.Should().Be("global");
        row.RefKey.Should().BeNull();
        row.MarkupMultiplier.Should().Be(1.3m);
        row.Status.Should().Be("active");
        row.Id.Should().Be(MarginPolicySeeder.DeterministicId("global"));
    }

    [Test]
    public async Task SeedAsync_SecondRun_IsNoOp()
    {
        await using (var ctx = NewContext()) { await MarginPolicySeeder.SeedAsync(ctx); }
        await using var ctx2 = NewContext();

        var inserted = await MarginPolicySeeder.SeedAsync(ctx2);

        inserted.Should().Be(0);
        (await NewContext().MarginPolicies.CountAsync()).Should().Be(1);
    }

    [Test]
    public async Task SeedAsync_NeverRevertsAdminEditedMultiplier()
    {
        await using (var ctx = NewContext()) { await MarginPolicySeeder.SeedAsync(ctx); }

        // Admin re-prices the seeded global row in place (simulating an edit).
        await using (var edit = NewContext())
        {
            var row = await edit.MarginPolicies.SingleAsync();
            row.MarkupMultiplier = 1.9m;
            await edit.SaveChangesAsync();
        }

        // Re-run must NOT revert the edit (insert-missing-only, keyed by id).
        await using (var reseed = NewContext()) { await MarginPolicySeeder.SeedAsync(reseed); }

        (await NewContext().MarginPolicies.SingleAsync()).MarkupMultiplier.Should().Be(1.9m);
    }

    [Test]
    public async Task Schema_RejectsAllNullKnobPolicy()
    {
        await using var ctx = NewContext();
        ctx.MarginPolicies.Add(new MarginPolicy
        {
            Id = Guid.NewGuid(),
            Scope = "global",
            RefKey = null,
            MarkupMultiplier = null,
            FixedUsdPer1M = null,
            EffectiveFrom = DateTime.UtcNow,
            Status = "active",
        });

        var act = async () => await ctx.SaveChangesAsync();

        var ex = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        ex.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be("ck_margin_policies_has_knob");
    }

    [Test]
    public async Task Schema_RejectsTwoActivePoliciesPerScopeRef()
    {
        await using (var ctx = NewContext())
        {
            await MarginPolicySeeder.SeedAsync(ctx); // one active global row
        }

        await using var ctx2 = NewContext();
        ctx2.MarginPolicies.Add(new MarginPolicy
        {
            Id = Guid.NewGuid(),
            Scope = "global",
            RefKey = null, // NULLS NOT DISTINCT ⇒ collides with the existing global
            MarkupMultiplier = 1.4m,
            EffectiveFrom = DateTime.UtcNow,
            Status = "active",
        });

        var act = async () => await ctx2.SaveChangesAsync();

        var ex = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        ex.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }
}
