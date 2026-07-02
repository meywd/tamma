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

    // ── Fix B: a supplied markup multiplier < 1 prices at/below platform cost ──
    [Test]
    public async Task VersionMargin_FatFingeredMultiplier_Returns400()
    {
        await using var db = NewContext();
        // 0.13 = ~87% revenue loss (sell = cost * 0.13) — must be rejected.
        var result = await AdminPricingEndpoints.VersionMargin(
            new VersionMarginRequest("global", null, 0.13m, null),
            db, new RecordingPlatformEventPublisher(), Actor(), TimeProvider.System, default);
        AssertStatus(result, StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task VersionMargin_ZeroMultiplier_Returns400()
    {
        await using var db = NewContext();
        var result = await AdminPricingEndpoints.VersionMargin(
            new VersionMarginRequest("global", null, 0m, null),
            db, new RecordingPlatformEventPublisher(), Actor(), TimeProvider.System, default);
        AssertStatus(result, StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task VersionMargin_ValidMultiplier_Returns200()
    {
        await using var db = NewContext();
        var result = await AdminPricingEndpoints.VersionMargin(
            new VersionMarginRequest("global", null, 1.3m, null),
            db, new RecordingPlatformEventPublisher(), Actor(), TimeProvider.System, default);
        AssertStatus(result, StatusCodes.Status200OK);
    }

    // A null multiplier with only a fixed per-token fee is a legitimate policy —
    // the < 1 guard must NOT reject it.
    [Test]
    public async Task VersionMargin_NullMultiplierWithFixedFee_Returns200()
    {
        await using var db = NewContext();
        var result = await AdminPricingEndpoints.VersionMargin(
            new VersionMarginRequest("global", null, null, 5m),
            db, new RecordingPlatformEventPublisher(), Actor(), TimeProvider.System, default);
        AssertStatus(result, StatusCodes.Status200OK);
    }

    // ── Fix C: concurrent PUTs on the same (scope, refKey) must not 500 ──
    [Test]
    public async Task VersionMargin_ConcurrentPuts_NeverReturn500_AndKeepOneActive()
    {
        // Fire several PUTs for the SAME (scope, refKey) in parallel. The partial
        // unique index (one active row per scope) forces a loser's insert to hit
        // Postgres 23505; the endpoint must translate that to 409 (retryable),
        // never a bare 500. Regardless of interleaving the one-active-per-scope
        // invariant must still hold afterwards.
        var tasks = Enumerable.Range(0, 8).Select(async i =>
        {
            await using var db = NewContext();
            return await AdminPricingEndpoints.VersionMargin(
                new VersionMarginRequest("global", null, 1.0m + (i * 0.1m), null),
                db, new RecordingPlatformEventPublisher(), Actor(), TimeProvider.System, default);
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        var codes = results.Select(r => ((IStatusCodeHttpResult)r).StatusCode).ToArray();
        codes.Should().OnlyContain(c =>
            c == StatusCodes.Status200OK || c == StatusCodes.Status409Conflict);
        codes.Should().Contain(StatusCodes.Status200OK);

        await using var verify = NewContext();
        var active = await verify.MarginPolicies.AsNoTracking()
            .CountAsync(p => p.Scope == "global" && p.Status == "active");
        active.Should().Be(1);
    }
}
