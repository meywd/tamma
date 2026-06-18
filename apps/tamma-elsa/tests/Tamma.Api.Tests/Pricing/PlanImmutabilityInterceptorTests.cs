using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Seeders;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-1 follow-up (AC6) — the AUTHORITATIVE immutability enforcement: the
/// <see cref="ControlPlaneDbContext"/> <c>SaveChanges</c> interceptor. Unlike
/// <see cref="PlanImmutabilityTests"/> (which only exercise the optional
/// pre-flight <c>EnsureMutableOrThrow</c> helper), these prove a raw EF mutation
/// that bypasses <see cref="PlanVersionEditor"/> entirely STILL fails loud —
/// for both an active/deprecated <c>Plan</c> row AND its child feature /
/// entitlement / price rows. Runs against a real Postgres testcontainer so the
/// untracked-owning-plan DB lookup and the editor's flip-then-insert
/// transaction are exercised against the actual schema + partial unique index.
/// </summary>
[TestFixture]
public class PlanImmutabilityInterceptorTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("plan_immutability_test")
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
            "TRUNCATE plan_prices, plan_entitlements, plan_features, plans CASCADE;");
        await PlansSeeder.SeedAsync(ctx);
    }

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    // (a) Direct mutation of an ACTIVE plan row throws.
    [Test]
    public async Task DirectMutation_Of_Active_Plan_Throws()
    {
        await using var ctx = NewContext();
        var plan = await ctx.Plans.FirstAsync(p => p.Slug == "team" && p.Status == "active");

        plan.MonthlyPriceUsd = 999m;

        var act = async () => await ctx.SaveChangesAsync();
        (await act.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("PLAN.VERSION.IMMUTABLE");
    }

    [Test]
    public async Task DirectMutation_Of_Active_Plan_Is_High_Severity()
    {
        await using var ctx = NewContext();
        var plan = await ctx.Plans.FirstAsync(p => p.Slug == "team" && p.Status == "active");
        plan.DisplayName = "Hacked";

        var ex = Assert.ThrowsAsync<TammaError>(async () => await ctx.SaveChangesAsync());
        ex!.Severity.Should().Be(TammaErrorSeverity.High);
        ex.Context.Should().ContainKey("planId");
    }

    // (b) Direct mutation of a DEPRECATED plan row throws.
    [Test]
    public async Task DirectMutation_Of_Deprecated_Plan_Throws()
    {
        Guid v1Id;
        // Make a deprecated row first via the editor (legitimate path).
        await using (var ctx = NewContext())
        {
            v1Id = (await ctx.Plans.FirstAsync(p => p.Slug == "team" && p.Status == "active")).Id;
            var editor = new PlanVersionEditor(
                ctx, new RecordingPlatformEventPublisher(),
                TimeProvider.System, NullLogger<PlanVersionEditor>.Instance);
            await editor.CreateNewVersionAsync(
                "team", new PlanDraftSpec(), new PlanEditorPrincipal("u", null));
        }

        await using var verify = NewContext();
        var deprecated = await verify.Plans.FirstAsync(p => p.Id == v1Id);
        deprecated.Status.Should().Be("deprecated");

        deprecated.MonthlyPriceUsd = 1m;
        var act = async () => await verify.SaveChangesAsync();
        (await act.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("PLAN.VERSION.IMMUTABLE");
    }

    // (c) Mutation / insert / delete of a CHILD row of an active plan throws.
    [Test]
    public async Task ChildMutation_Of_Active_Plan_Throws()
    {
        await using var ctx = NewContext();
        var planId = (await ctx.Plans.FirstAsync(p => p.Slug == "team" && p.Status == "active")).Id;
        var feature = await ctx.PlanFeatures.FirstAsync(f => f.PlanId == planId);

        feature.StringValue = "tampered";

        var act = async () => await ctx.SaveChangesAsync();
        (await act.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("PLAN.VERSION.IMMUTABLE");
    }

    [Test]
    public async Task ChildInsert_Onto_Active_Plan_Throws()
    {
        await using var ctx = NewContext();
        var planId = (await ctx.Plans.FirstAsync(p => p.Slug == "team" && p.Status == "active")).Id;

        ctx.PlanFeatures.Add(new PlanFeature
        {
            Id = Guid.NewGuid(),
            PlanId = planId,
            FeatureKey = "smuggled_in",
            BoolValue = true,
        });

        var act = async () => await ctx.SaveChangesAsync();
        (await act.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("PLAN.VERSION.IMMUTABLE");
    }

    [Test]
    public async Task ChildDelete_From_Active_Plan_Throws()
    {
        await using var ctx = NewContext();
        var planId = (await ctx.Plans.FirstAsync(p => p.Slug == "team" && p.Status == "active")).Id;
        var price = await ctx.PlanPrices.FirstAsync(p => p.PlanId == planId);

        ctx.PlanPrices.Remove(price);

        var act = async () => await ctx.SaveChangesAsync();
        (await act.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("PLAN.VERSION.IMMUTABLE");
    }

    [Test]
    public async Task ChildMutation_Of_Deprecated_Plan_Throws()
    {
        Guid v1Id;
        await using (var ctx = NewContext())
        {
            v1Id = (await ctx.Plans.FirstAsync(p => p.Slug == "team" && p.Status == "active")).Id;
            var editor = new PlanVersionEditor(
                ctx, new RecordingPlatformEventPublisher(),
                TimeProvider.System, NullLogger<PlanVersionEditor>.Instance);
            await editor.CreateNewVersionAsync(
                "team", new PlanDraftSpec(), new PlanEditorPrincipal("u", null));
        }

        await using var verify = NewContext();
        var entitlement = await verify.PlanEntitlements.FirstAsync(e => e.PlanId == v1Id);
        entitlement.LimitValue = 7777;

        var act = async () => await verify.SaveChangesAsync();
        (await act.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("PLAN.VERSION.IMMUTABLE");
    }

    // (d) Editing a DRAFT plan + its children succeeds.
    [Test]
    public async Task DirectMutation_Of_Draft_Plan_And_Children_Succeeds()
    {
        var draftId = Guid.NewGuid();
        var featureId = Guid.NewGuid();

        await using (var seed = NewContext())
        {
            seed.Plans.Add(new Plan
            {
                Id = draftId,
                Slug = "draft-only",
                DisplayName = "Draft",
                Version = 1,
                Status = "draft",
                BillingInterval = "monthly",
                MonthlyPriceUsd = 1m,
                PlacementPolicy = "shared",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Features =
                {
                    new PlanFeature { Id = featureId, PlanId = draftId, FeatureKey = "x", BoolValue = true },
                },
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var draft = await ctx.Plans.FirstAsync(p => p.Id == draftId);
        var feature = await ctx.PlanFeatures.FirstAsync(f => f.Id == featureId);
        draft.MonthlyPriceUsd = 2m;
        feature.BoolValue = false;

        var act = async () => await ctx.SaveChangesAsync();
        await act.Should().NotThrowAsync("a draft plan and its children are freely editable");
    }

    [Test]
    public async Task ChildInsert_Onto_Draft_Plan_Succeeds()
    {
        var draftId = Guid.NewGuid();
        await using (var seed = NewContext())
        {
            seed.Plans.Add(new Plan
            {
                Id = draftId,
                Slug = "draft-child",
                DisplayName = "Draft",
                Version = 1,
                Status = "draft",
                BillingInterval = "monthly",
                MonthlyPriceUsd = 1m,
                PlacementPolicy = "shared",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        ctx.PlanFeatures.Add(new PlanFeature
        {
            Id = Guid.NewGuid(),
            PlanId = draftId,
            FeatureKey = "added_to_draft",
            BoolValue = true,
        });

        var act = async () => await ctx.SaveChangesAsync();
        await act.Should().NotThrowAsync("children of a draft plan are freely insertable");
    }

    // (e) The editor's version-create + deprecate-flip succeeds THROUGH the
    // interceptor (the controlled active→deprecated flip + the new active
    // plan's children are both allowed).
    [Test]
    public async Task EditorVersionCreate_Succeeds_Through_Interceptor()
    {
        var publisher = new RecordingPlatformEventPublisher();
        Guid v1Id;

        await using (var ctx = NewContext())
        {
            v1Id = (await ctx.Plans.FirstAsync(p => p.Slug == "team" && p.Status == "active")).Id;
            var editor = new PlanVersionEditor(
                ctx, publisher, TimeProvider.System, NullLogger<PlanVersionEditor>.Instance);

            var act = async () => await editor.CreateNewVersionAsync(
                "team",
                new PlanDraftSpec(DisplayName: "Team v2", MonthlyPriceUsd: 59m),
                new PlanEditorPrincipal("user-123", "owner@x.io"));

            await act.Should().NotThrowAsync(
                "the controlled active→deprecated flip + new active version's children "
                + "must clear the immutability interceptor");
        }

        await using var verify = NewContext();
        (await verify.Plans.FirstAsync(p => p.Id == v1Id)).Status.Should().Be("deprecated");
        (await verify.Plans.CountAsync(p => p.Slug == "team" && p.Status == "active"))
            .Should().Be(1, "exactly one active version per slug after the flip");
        var v2 = await verify.Plans
            .Include(p => p.Features)
            .FirstAsync(p => p.Slug == "team" && p.Status == "active");
        v2.Features.Should().NotBeEmpty("the new version's children committed through the interceptor");
    }
}
