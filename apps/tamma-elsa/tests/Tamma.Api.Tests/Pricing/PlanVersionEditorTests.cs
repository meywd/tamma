using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Core;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Seeders;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-1 (AC6, AC11, AC13) — <see cref="PlanVersionEditor"/> against a real
/// Postgres testcontainer. Covers the v1→v2→v3 supersede chain with correct
/// <c>SupersedesPlanId</c> links, the prior version flipping to
/// <c>deprecated</c>, the one-active-version invariant being preserved by the
/// flip+insert transaction, and the <c>PLAN.VERSION.CREATED</c> /
/// <c>PLAN.DEPRECATED</c> events being emitted with the spec'd tags.
/// </summary>
[TestFixture]
public class PlanVersionEditorTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("plan_editor_test")
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

    [Test]
    public async Task CreateNewVersion_Supersedes_Prior_And_Deprecates_It()
    {
        var publisher = new RecordingPlatformEventPublisher();
        Guid v1Id;

        await using (var ctx = NewContext())
        {
            v1Id = (await ctx.Plans.FirstAsync(p => p.Slug == "team" && p.Status == "active")).Id;
            var editor = new PlanVersionEditor(
                ctx, publisher, TimeProvider.System, NullLogger<PlanVersionEditor>.Instance);

            var v2 = await editor.CreateNewVersionAsync(
                "team",
                new PlanDraftSpec(DisplayName: "Team v2", MonthlyPriceUsd: 59m),
                new PlanEditorPrincipal("user-123", "owner@x.io"));

            v2.Version.Should().Be(2);
            v2.SupersedesPlanId.Should().Be(v1Id);
            v2.Status.Should().Be("active");
        }

        await using var verify = NewContext();
        var v1 = await verify.Plans.FirstAsync(p => p.Id == v1Id);
        v1.Status.Should().Be("deprecated");

        var active = await verify.Plans.CountAsync(p => p.Slug == "team" && p.Status == "active");
        active.Should().Be(1, "exactly one active version per slug");
    }

    [Test]
    public async Task CreateNewVersion_Emits_Created_And_Deprecated_Events_With_Tags()
    {
        var publisher = new RecordingPlatformEventPublisher();
        Guid v1Id;

        await using (var ctx = NewContext())
        {
            v1Id = (await ctx.Plans.FirstAsync(p => p.Slug == "team" && p.Status == "active")).Id;
            var editor = new PlanVersionEditor(
                ctx, publisher, TimeProvider.System, NullLogger<PlanVersionEditor>.Instance);
            await editor.CreateNewVersionAsync(
                "team", new PlanDraftSpec(), new PlanEditorPrincipal("user-123", "owner@x.io"));
        }

        publisher.Events.Should().HaveCount(2);

        var created = publisher.Events.Single(e => e.Type == PlanCatalogEventTypes.VersionCreated);
        var createdTags = ParseTags(created.Tags);
        createdTags["slug"].Should().Be("team");
        createdTags["version"].Should().Be("2");
        createdTags["supersedesPlanId"].Should().Be(v1Id.ToString("D"));
        createdTags["source"].Should().Be("admin");
        createdTags["actorUserId"].Should().Be("user-123");

        var deprecated = publisher.Events.Single(e => e.Type == PlanCatalogEventTypes.Deprecated);
        var depTags = ParseTags(deprecated.Tags);
        depTags["slug"].Should().Be("team");
        depTags["version"].Should().Be("1");
        depTags["planId"].Should().Be(v1Id.ToString("D"));
        depTags.Should().ContainKey("supersededByPlanId");
    }

    [Test]
    public async Task CreateNewVersion_Builds_A_Correct_3_Version_Chain()
    {
        var publisher = new RecordingPlatformEventPublisher();
        var v2Id = Guid.Empty;
        var v3Id = Guid.Empty;
        Guid v1Id;

        await using (var ctx = NewContext())
        {
            v1Id = (await ctx.Plans.FirstAsync(p => p.Slug == "team" && p.Status == "active")).Id;
        }

        await using (var ctx = NewContext())
        {
            var editor = new PlanVersionEditor(ctx, publisher, TimeProvider.System, NullLogger<PlanVersionEditor>.Instance);
            v2Id = (await editor.CreateNewVersionAsync("team", new PlanDraftSpec(), new PlanEditorPrincipal("u", null))).Id;
        }

        await using (var ctx = NewContext())
        {
            var editor = new PlanVersionEditor(ctx, publisher, TimeProvider.System, NullLogger<PlanVersionEditor>.Instance);
            v3Id = (await editor.CreateNewVersionAsync("team", new PlanDraftSpec(), new PlanEditorPrincipal("u", null))).Id;
        }

        await using var verify = NewContext();
        var v2 = await verify.Plans.FirstAsync(p => p.Id == v2Id);
        var v3 = await verify.Plans.FirstAsync(p => p.Id == v3Id);

        v2.Version.Should().Be(2);
        v2.SupersedesPlanId.Should().Be(v1Id);
        v3.Version.Should().Be(3);
        v3.SupersedesPlanId.Should().Be(v2Id);
        v3.Status.Should().Be("active");

        (await verify.Plans.CountAsync(p => p.Slug == "team")).Should().Be(3);
        (await verify.Plans.CountAsync(p => p.Slug == "team" && p.Status == "active")).Should().Be(1);
    }

    [Test]
    public async Task CreateNewVersion_Copies_Children_When_Draft_Omits_Them()
    {
        var publisher = new RecordingPlatformEventPublisher();

        await using (var ctx = NewContext())
        {
            var editor = new PlanVersionEditor(ctx, publisher, TimeProvider.System, NullLogger<PlanVersionEditor>.Instance);
            await editor.CreateNewVersionAsync("team", new PlanDraftSpec(), new PlanEditorPrincipal("u", null));
        }

        await using var verify = NewContext();
        var v2 = await verify.Plans
            .Include(p => p.Features)
            .Include(p => p.Entitlements)
            .Include(p => p.Prices)
            .FirstAsync(p => p.Slug == "team" && p.Status == "active");

        v2.Features.Should().NotBeEmpty("children copy forward when the draft omits them");
        v2.Entitlements.Should().Contain(e => e.MetricKey == EntitlementMetricKey.LlmTokens);
        v2.Prices.Select(p => p.PricingMode).Should().BeEquivalentTo(new[] { "platform_provided", "byok" });
    }

    [Test]
    public async Task CreateNewVersion_Unknown_Slug_Throws()
    {
        var publisher = new RecordingPlatformEventPublisher();
        await using var ctx = NewContext();
        var editor = new PlanVersionEditor(ctx, publisher, TimeProvider.System, NullLogger<PlanVersionEditor>.Instance);

        var act = async () => await editor.CreateNewVersionAsync(
            "ghost", new PlanDraftSpec(), new PlanEditorPrincipal("u", null));

        await act.Should().ThrowAsync<TammaError>()
            .Where(e => e.Code == "PLAN.VERSION.NO_ACTIVE");
        publisher.Events.Should().BeEmpty("no event on a failed (no-op) operation");
    }

    private static Dictionary<string, string?> ParseTags(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string?>>(json)!;
}
