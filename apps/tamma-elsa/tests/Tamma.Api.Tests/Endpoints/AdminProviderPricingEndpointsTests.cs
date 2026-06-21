using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Endpoints.Admin;
using Tamma.Api.Services.Providers;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Seeders;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Endpoints;

/// <summary>
/// Story 34-11 (AC4, AC9) — <see cref="AdminProviderPricingEndpoints"/> against a
/// real Postgres testcontainer. Covers the supersede/version chain + the
/// one-active partial-unique-index invariant, the immutability throw, and the
/// PROVIDER.* DCB event tags. (The PlatformOwnerAccess 403 gate is pinned by
/// <c>PlatformOwnerAccessPolicyTests</c>, which adds the /api/admin/providers
/// route to its gated-route list.)
/// </summary>
[TestFixture]
public class AdminProviderPricingEndpointsTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;
    private ServiceProvider _sp = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("admin_provider_pricing_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();

        var services = new ServiceCollection();
        services.AddDbContext<ControlPlaneDbContext>(o => o.UseNpgsql(_cs));
        _sp = services.BuildServiceProvider();

        await using var ctx = NewContext();
        await ctx.Database.MigrateAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_sp is not null) await _sp.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SetUp]
    public async Task SetUp()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(
            "TRUNCATE provider_model_prices, providers CASCADE;");
        await ProviderPricingSeeder.SeedAsync(ctx);
    }

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    private IProviderCostResolver NewResolver() =>
        new ProviderCostResolver(
            _sp.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<ProviderCostResolver>.Instance, TimeSpan.Zero);

    // The admin write paths take IEnumerable<IProviderCostCacheInvalidator> (C1):
    // every snapshot holder is flushed on a mutation. A fresh resolver is a valid
    // (no-op-here) invalidator for the endpoint-level tests.
    private IProviderCostCacheInvalidator[] NewCaches() =>
        new IProviderCostCacheInvalidator[] { NewResolver() };

    private static ClaimsPrincipal Actor(string userId = "user-34-11", string email = "owner@tamma.dev") =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim("platformRole", "platform_admin"),
        }, "test"));

    // The handlers return Results.Ok(anonymousObject) → Ok<TAnon>; assert via
    // the IStatusCodeHttpResult facet rather than the closed generic type.
    private static void AssertStatus(IResult result, int expected)
    {
        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(expected);
    }

    [Test]
    public async Task VersionPrice_Supersedes_Prior_And_Inserts_New_Active()
    {
        var publisher = new RecordingPlatformEventPublisher();

        await using (var db = NewContext())
        {
            var result = await AdminProviderPricingEndpoints.VersionPrice(
                "anthropic",
                new VersionPriceRequest("claude-sonnet-4-20250514", 6.00m, 18.00m),
                db, NewCaches(), publisher, Actor(), TimeProvider.System, default);

            AssertStatus(result, StatusCodes.Status200OK);
        }

        await using var verify = NewContext();
        var rows = await verify.ProviderModelPrices.AsNoTracking()
            .Where(p => p.ProviderKey == "anthropic" && p.Model == "claude-sonnet-4-20250514")
            .ToListAsync();

        rows.Should().HaveCount(2, "v1 seed + v2 admin");
        rows.Count(r => r.Status == "active").Should().Be(1, "exactly one active per (provider, model)");
        var active = rows.Single(r => r.Status == "active");
        active.Source.Should().Be("admin");
        active.InputUsdPer1M.Should().Be(6.00m);
        rows.Single(r => r.Status == "superseded").Source.Should().Be("seed");
    }

    [Test]
    public async Task VersionPrice_Emits_PriceVersioned_Event_WithTags()
    {
        var publisher = new RecordingPlatformEventPublisher();
        Guid seedId;
        await using (var db = NewContext())
        {
            seedId = (await db.ProviderModelPrices.AsNoTracking().SingleAsync(p =>
                p.ProviderKey == "anthropic"
                && p.Model == "claude-sonnet-4-20250514"
                && p.Status == "active")).Id;

            await AdminProviderPricingEndpoints.VersionPrice(
                "anthropic",
                new VersionPriceRequest("claude-sonnet-4-20250514", 6.00m, 18.00m),
                db, NewCaches(), publisher, Actor(), TimeProvider.System, default);
        }

        var evt = publisher.Events.Should().ContainSingle(e =>
            e.Type == ProviderPricingEventTypes.PriceVersioned).Subject;
        var tags = JsonSerializer.Deserialize<Dictionary<string, string>>(evt.Tags)!;
        tags["providerKey"].Should().Be("anthropic");
        tags["model"].Should().Be("claude-sonnet-4-20250514");
        tags["source"].Should().Be("admin");
        tags["actorUserId"].Should().Be("user-34-11");
        tags["supersededPriceId"].Should().Be(seedId.ToString("D"));
        tags.Should().ContainKey("effectiveFrom");
    }

    [Test]
    public async Task VersionPrice_NormalizesAliasKey_OnWrite()
    {
        var publisher = new RecordingPlatformEventPublisher();
        await using (var db = NewContext())
        {
            // "claude" is an alias for "anthropic" — the new row must store the
            // canonical key so the FK resolves and the cost lookup hits.
            await AdminProviderPricingEndpoints.VersionPrice(
                "claude",
                new VersionPriceRequest("claude-3-haiku-20240307", 0.30m, 1.50m),
                db, NewCaches(), publisher, Actor(), TimeProvider.System, default);
        }

        await using var verify = NewContext();
        // Note: claude-3-haiku-20240307 is also seeded under claude-code (which
        // reuses the anthropic model list), so filter on the canonical key the
        // alias resolved to.
        var active = await verify.ProviderModelPrices.AsNoTracking().SingleAsync(p =>
            p.ProviderKey == "anthropic"
            && p.Model == "claude-3-haiku-20240307"
            && p.Status == "active"
            && p.Source == "admin");
        active.ProviderKey.Should().Be("anthropic");
    }

    [Test]
    public async Task EnsureMutableOrThrow_OnSupersededRow_Throws_Immutable()
    {
        await using var db = NewContext();
        // Version once so a superseded row exists.
        await AdminProviderPricingEndpoints.VersionPrice(
            "anthropic",
            new VersionPriceRequest("claude-sonnet-4-20250514", 6.00m, 18.00m),
            db, NewCaches(), new RecordingPlatformEventPublisher(),
            Actor(), TimeProvider.System, default);

        var superseded = await db.ProviderModelPrices.AsNoTracking()
            .FirstAsync(p => p.Status == "superseded");

        var act = () => AdminProviderPricingEndpoints.EnsureMutableOrThrow(superseded);
        act.Should().Throw<TammaError>()
            .Which.Code.Should().Be("PROVIDER.PRICE.IMMUTABLE");
    }

    [Test]
    public async Task OneActiveInvariant_PartialUniqueIndex_Rejects_Second_Active()
    {
        // Insert a second active row for the same (ProviderKey, Model) directly
        // (bypassing the supersede path) — the partial unique index must reject it.
        await using var db = NewContext();
        db.ProviderModelPrices.Add(new ProviderModelPrice
        {
            Id = Guid.NewGuid(),
            ProviderKey = "anthropic",
            Model = "claude-sonnet-4-20250514", // already has an active seed row
            InputUsdPer1M = 1m,
            OutputUsdPer1M = 1m,
            EffectiveFrom = DateTime.UtcNow,
            Status = "active",
            Source = "admin",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "UX_provider_model_prices_OneActivePerModel forbids two active rows per model");
    }

    [Test]
    public async Task RegisterProvider_Inserts_And_Emits_Registered_Event()
    {
        var publisher = new RecordingPlatformEventPublisher();
        await using (var db = NewContext())
        {
            var result = await AdminProviderPricingEndpoints.RegisterProvider(
                new RegisterProviderRequest("xai", "xAI", "api-key"),
                db, NewCaches(), publisher, Actor(), TimeProvider.System, default);
            AssertStatus(result, StatusCodes.Status201Created);
        }

        await using var verify = NewContext();
        (await verify.Providers.AnyAsync(p => p.Key == "xai")).Should().BeTrue();
        publisher.Events.Should().Contain(e => e.Type == ProviderPricingEventTypes.Registered);
    }

    [Test]
    public async Task UpdateProvider_Sets_Status_And_Emits_StatusChanged()
    {
        var publisher = new RecordingPlatformEventPublisher();
        await using (var db = NewContext())
        {
            await AdminProviderPricingEndpoints.UpdateProvider(
                "openai",
                new UpdateProviderRequest(Status: "retired"),
                db, NewCaches(), publisher, Actor(), TimeProvider.System, default);
        }

        await using var verify = NewContext();
        (await verify.Providers.SingleAsync(p => p.Key == "openai")).Status.Should().Be("retired");
        publisher.Events.Should().Contain(e => e.Type == ProviderPricingEventTypes.StatusChanged);
    }

    // I2 — a negative rate must be rejected with 400 (a typo'd admin write must
    // not poison the cost basis with a negative cost).
    [Test]
    public async Task VersionPrice_NegativeInputRate_Returns400()
    {
        await using var db = NewContext();
        var result = await AdminProviderPricingEndpoints.VersionPrice(
            "anthropic",
            new VersionPriceRequest("claude-sonnet-4-20250514", -1.00m, 18.00m),
            db, NewCaches(), new RecordingPlatformEventPublisher(),
            Actor(), TimeProvider.System, default);

        AssertStatus(result, StatusCodes.Status400BadRequest);

        // Nothing was versioned — the seed row is still the sole active row.
        await using var verify = NewContext();
        (await verify.ProviderModelPrices.CountAsync(p =>
            p.ProviderKey == "anthropic" && p.Model == "claude-sonnet-4-20250514"))
            .Should().Be(1, "a rejected write must not insert a row");
    }

    [Test]
    public async Task VersionPrice_NegativeCacheRate_Returns400()
    {
        await using var db = NewContext();
        var result = await AdminProviderPricingEndpoints.VersionPrice(
            "anthropic",
            new VersionPriceRequest(
                "claude-sonnet-4-20250514", 3.00m, 15.00m, CacheWriteUsdPer1M: -0.50m),
            db, NewCaches(), new RecordingPlatformEventPublisher(),
            Actor(), TimeProvider.System, default);

        AssertStatus(result, StatusCodes.Status400BadRequest);
    }

    // I2 — versioning a price on a RETIRED provider must be rejected (409): its
    // cost identity is no longer live.
    [Test]
    public async Task VersionPrice_OnRetiredProvider_Returns409()
    {
        // Retire openai first.
        await using (var db = NewContext())
        {
            await AdminProviderPricingEndpoints.UpdateProvider(
                "openai", new UpdateProviderRequest(Status: "retired"),
                db, NewCaches(), new RecordingPlatformEventPublisher(),
                Actor(), TimeProvider.System, default);
        }

        await using var ctx = NewContext();
        var result = await AdminProviderPricingEndpoints.VersionPrice(
            "openai",
            new VersionPriceRequest("gpt-4o", 1.00m, 2.00m),
            ctx, NewCaches(), new RecordingPlatformEventPublisher(),
            Actor(), TimeProvider.System, default);

        AssertStatus(result, StatusCodes.Status409Conflict);
    }

    // I1 — the AUTHORITATIVE immutability guard lives at the DbContext SaveChanges
    // interceptor (mirrors the Plan EnforcePlanImmutability sibling), NOT only at
    // the pre-flight EnsureMutableOrThrow helper. A raw EF UPDATE of a superseded
    // ProviderModelPrice row (bypassing the admin endpoint entirely) must STILL
    // fail loud with PROVIDER.PRICE.IMMUTABLE.
    [Test]
    public async Task DirectMutation_Of_Superseded_Price_Throws_Immutable()
    {
        // Version once so a superseded row exists.
        await using (var db = NewContext())
        {
            await AdminProviderPricingEndpoints.VersionPrice(
                "anthropic",
                new VersionPriceRequest("claude-sonnet-4-20250514", 6.00m, 18.00m),
                db, NewCaches(), new RecordingPlatformEventPublisher(),
                Actor(), TimeProvider.System, default);
        }

        await using var ctx = NewContext();
        var superseded = await ctx.ProviderModelPrices.FirstAsync(p =>
            p.ProviderKey == "anthropic"
            && p.Model == "claude-sonnet-4-20250514"
            && p.Status == "superseded");

        // Tamper with the content of an already-superseded (immutable) row.
        superseded.InputUsdPer1M = 0.01m;

        var act = async () => await ctx.SaveChangesAsync();
        (await act.Should().ThrowAsync<TammaError>(
            "a content mutation of a superseded cost row must be rejected at the interceptor"))
            .Which.Code.Should().Be("PROVIDER.PRICE.IMMUTABLE");
    }

    // I1 — the legitimate supersede flip (active→superseded) the VersionPrice path
    // performs must STILL be allowed through the interceptor (otherwise versioning
    // would be impossible).
    [Test]
    public async Task LegitimateSupersedeFlip_Is_Allowed_Through_Interceptor()
    {
        await using var db = NewContext();
        var act = async () => await AdminProviderPricingEndpoints.VersionPrice(
            "anthropic",
            new VersionPriceRequest("claude-sonnet-4-20250514", 6.00m, 18.00m),
            db, NewCaches(), new RecordingPlatformEventPublisher(),
            Actor(), TimeProvider.System, default);

        await act.Should().NotThrowAsync(
            "the controlled active→superseded flip + new active insert must clear the interceptor");

        await using var verify = NewContext();
        (await verify.ProviderModelPrices.CountAsync(p =>
            p.ProviderKey == "anthropic"
            && p.Model == "claude-sonnet-4-20250514"
            && p.Status == "active")).Should().Be(1, "exactly one active after the flip");
    }

    [Test]
    public async Task ListPrices_Returns_Active_And_Superseded()
    {
        await using (var db = NewContext())
        {
            await AdminProviderPricingEndpoints.VersionPrice(
                "anthropic",
                new VersionPriceRequest("claude-sonnet-4-20250514", 6.00m, 18.00m),
                db, NewCaches(), new RecordingPlatformEventPublisher(),
                Actor(), TimeProvider.System, default);
        }

        await using var db2 = NewContext();
        var result = await AdminProviderPricingEndpoints.ListPrices("anthropic", db2, default);
        AssertStatus(result, StatusCodes.Status200OK);

        // M3 — assert the response BODY actually carries BOTH the active (admin)
        // and the superseded (seed) rows for the versioned model, not just 200.
        var value = ((IValueHttpResult)result).Value!;
        var pricesProp = value.GetType().GetProperty("prices")!;
        var prices = (System.Collections.IEnumerable)pricesProp.GetValue(value)!;
        var versioned = prices.Cast<ProviderModelPrice>()
            .Where(p => p.Model == "claude-sonnet-4-20250514")
            .ToList();

        versioned.Should().HaveCount(2, "the body must contain both the seed + admin rows");
        versioned.Should().Contain(p => p.Status == "active" && p.Source == "admin");
        versioned.Should().Contain(p => p.Status == "superseded" && p.Source == "seed");
    }
}
