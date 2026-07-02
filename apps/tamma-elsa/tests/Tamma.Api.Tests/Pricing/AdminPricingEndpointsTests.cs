using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Api.Endpoints.Admin;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Data;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-5 (AC9) — <see cref="AdminPricingEndpoints"/> against a real Postgres
/// testcontainer. Covers the supersede/version chain + the one-active partial-
/// unique-index invariant, the GET active+history read, the PRICING.MARGIN.UPDATED
/// DCB emit, and request validation. (The PlatformOwnerAccess 403 gate is pinned
/// by <c>PlatformOwnerAccessPolicyTests</c>, which lists /api/admin/pricing/margins.)
/// </summary>
[TestFixture]
public class AdminPricingEndpointsTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("admin_pricing_test")
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

    private static ClaimsPrincipal Actor(string userId = "user-34-5", string email = "owner@tamma.dev") =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim("platformRole", "platform_admin"),
        }, "test"));

    private static void AssertStatus(IResult result, int expected)
    {
        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(expected);
    }

    [Test]
    public async Task VersionMargin_Supersedes_Prior_And_Inserts_New_Active()
    {
        var publisher = new RecordingPlatformEventPublisher();

        await using (var db = NewContext())
        {
            await AdminPricingEndpoints.VersionMargin(
                new VersionMarginRequest("global", null, 1.3m, null),
                db, publisher, Actor(), TimeProvider.System, default);
        }

        await using (var db = NewContext())
        {
            var result = await AdminPricingEndpoints.VersionMargin(
                new VersionMarginRequest("global", null, 1.5m, null),
                db, publisher, Actor(), TimeProvider.System, default);
            AssertStatus(result, StatusCodes.Status200OK);
        }

        await using var verify = NewContext();
        var rows = await verify.MarginPolicies.AsNoTracking()
            .Where(p => p.Scope == "global")
            .OrderBy(p => p.EffectiveFrom)
            .ToListAsync();

        rows.Should().HaveCount(2);
        rows.Count(r => r.Status == "active").Should().Be(1);
        rows.Single(r => r.Status == "active").MarkupMultiplier.Should().Be(1.5m);
        rows.Single(r => r.Status == "superseded").MarkupMultiplier.Should().Be(1.3m);

        publisher.Events.Should().Contain(e => e.Type == PricingEventTypes.MarginUpdated);
    }

    [Test]
    public async Task ListMargins_Returns_Active_And_History()
    {
        var publisher = new RecordingPlatformEventPublisher();
        await using (var db = NewContext())
        {
            await AdminPricingEndpoints.VersionMargin(
                new VersionMarginRequest("global", null, 1.3m, null),
                db, publisher, Actor(), TimeProvider.System, default);
        }
        await using (var db = NewContext())
        {
            await AdminPricingEndpoints.VersionMargin(
                new VersionMarginRequest("global", null, 1.5m, null),
                db, publisher, Actor(), TimeProvider.System, default);
        }

        await using var db2 = NewContext();
        var result = await AdminPricingEndpoints.ListMargins(db2, default);

        AssertStatus(result, StatusCodes.Status200OK);
        var value = ((IValueHttpResult)result).Value!;
        var policies = (System.Collections.IEnumerable)value.GetType()
            .GetProperty("policies")!.GetValue(value)!;
        policies.Cast<MarginPolicyDto>().Should().HaveCount(2);
    }

    [Test]
    public async Task VersionMargin_ProviderScope_CanonicalizesRefKey()
    {
        var publisher = new RecordingPlatformEventPublisher();
        await using (var db = NewContext())
        {
            var result = await AdminPricingEndpoints.VersionMargin(
                new VersionMarginRequest("provider", "Anthropic", 2.0m, null),
                db, publisher, Actor(), TimeProvider.System, default);
            AssertStatus(result, StatusCodes.Status200OK);
        }

        var row = await NewContext().MarginPolicies.AsNoTracking()
            .SingleAsync(p => p.Scope == "provider");
        row.RefKey.Should().Be("anthropic");
    }

    [Test]
    public async Task VersionMargin_AllNullKnobs_Returns400()
    {
        await using var db = NewContext();
        var result = await AdminPricingEndpoints.VersionMargin(
            new VersionMarginRequest("global", null, null, null),
            db, new RecordingPlatformEventPublisher(), Actor(), TimeProvider.System, default);
        AssertStatus(result, StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task VersionMargin_GlobalWithRefKey_Returns400()
    {
        await using var db = NewContext();
        var result = await AdminPricingEndpoints.VersionMargin(
            new VersionMarginRequest("global", "pro", 1.3m, null),
            db, new RecordingPlatformEventPublisher(), Actor(), TimeProvider.System, default);
        AssertStatus(result, StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task VersionMargin_PlanWithoutRefKey_Returns400()
    {
        await using var db = NewContext();
        var result = await AdminPricingEndpoints.VersionMargin(
            new VersionMarginRequest("plan", null, 1.3m, null),
            db, new RecordingPlatformEventPublisher(), Actor(), TimeProvider.System, default);
        AssertStatus(result, StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task VersionMargin_NegativeMultiplier_Returns400()
    {
        await using var db = NewContext();
        var result = await AdminPricingEndpoints.VersionMargin(
            new VersionMarginRequest("global", null, -1.0m, null),
            db, new RecordingPlatformEventPublisher(), Actor(), TimeProvider.System, default);
        AssertStatus(result, StatusCodes.Status400BadRequest);
    }
}
