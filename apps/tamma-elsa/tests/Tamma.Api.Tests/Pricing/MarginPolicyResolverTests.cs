using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Pricing;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-5 (AC4, AC7-time-travel, AC13) — <see cref="MarginPolicyResolver"/>
/// resolution order (provider &gt; plan &gt; global), timestamp-effective
/// selection (an event resolves the policy active at its OccurredAt, not the
/// latest), and the fail-loud no-policy error. Uses the InMemory provider — the
/// resolution logic is provider-agnostic; the SQL CHECK/unique-index invariants
/// are pinned by the Postgres-backed schema tests.
/// </summary>
[TestFixture]
public class MarginPolicyResolverTests
{
    private static readonly DateTime Epoch = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static MarginPolicyResolver NewResolver(ControlPlaneDbContext db) =>
        new(db, NullLogger<MarginPolicyResolver>.Instance);

    private static MarginPolicy P(
        string scope, string? refKey, decimal mult, DateTime effectiveFrom, string status = "active") => new()
    {
        Id = Guid.NewGuid(),
        Scope = scope,
        RefKey = refKey,
        MarkupMultiplier = mult,
        EffectiveFrom = effectiveFrom,
        Status = status,
        CreatedAt = effectiveFrom,
        UpdatedAt = effectiveFrom,
    };

    [Test]
    public async Task ResolveAsync_PrefersProviderOverride_ThenPlan_ThenGlobal()
    {
        await using var db = NewContext();
        db.MarginPolicies.AddRange(
            P("global", null, 1.3m, Epoch),
            P("plan", "pro", 1.5m, Epoch),
            P("provider", "anthropic", 2.0m, Epoch));
        await db.SaveChangesAsync();

        var resolver = NewResolver(db);

        var providerHit = await resolver.ResolveAsync("anthropic", "pro", Epoch.AddYears(1), default);
        providerHit.Scope.Should().Be("provider");
        providerHit.MarkupMultiplier.Should().Be(2.0m);

        // No provider override for openai ⇒ falls to the plan policy.
        var planHit = await resolver.ResolveAsync("openai", "pro", Epoch.AddYears(1), default);
        planHit.Scope.Should().Be("plan");
        planHit.MarkupMultiplier.Should().Be(1.5m);

        // No plan slug ⇒ falls straight to global.
        var globalHit = await resolver.ResolveAsync("openai", null, Epoch.AddYears(1), default);
        globalHit.Scope.Should().Be("global");
        globalHit.MarkupMultiplier.Should().Be(1.3m);
    }

    [Test]
    public async Task ResolveAsync_SelectsPolicyActiveAtTimestamp_NotLatest()
    {
        await using var db = NewContext();
        var v1From = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var v2From = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        db.MarginPolicies.AddRange(
            P("global", null, 1.3m, v1From, status: "superseded"),
            P("global", null, 1.5m, v2From, status: "active"));
        await db.SaveChangesAsync();

        var resolver = NewResolver(db);

        // An event in March 2026 prices under v1 (1.3x) — the policy active then,
        // even though v1 is now superseded.
        var march = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        (await resolver.ResolveAsync("anthropic", null, march, default))
            .MarkupMultiplier.Should().Be(1.3m);

        // An event in July 2026 prices under v2 (1.5x).
        var july = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        (await resolver.ResolveAsync("anthropic", null, july, default))
            .MarkupMultiplier.Should().Be(1.5m);
    }

    [Test]
    public async Task ResolveAsync_BeforeAnyEffectiveFrom_FallsThroughToError()
    {
        await using var db = NewContext();
        db.MarginPolicies.Add(P("global", null, 1.3m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var resolver = NewResolver(db);

        // A timestamp before the only policy's EffectiveFrom ⇒ no policy applies.
        var before = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var act = async () => await resolver.ResolveAsync("anthropic", null, before, default);

        (await act.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("PRICING.MARGIN.NO_POLICY");
    }

    [Test]
    public async Task ResolveAsync_NoPolicyAtAnyScope_ThrowsNoPolicy()
    {
        await using var db = NewContext();
        var resolver = NewResolver(db);

        var act = async () => await resolver.ResolveAsync("anthropic", "pro", Epoch.AddYears(1), default);

        (await act.Should().ThrowAsync<TammaError>())
            .Which.Severity.Should().Be(TammaErrorSeverity.High);
    }
}
