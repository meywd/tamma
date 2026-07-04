using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Pricing;
using Tamma.Core;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-3 — <see cref="TenantProviderBillingResolver"/> reads the
/// AUTHORITATIVE per-<c>(tenant, provider)</c> mode from the
/// <c>TenantProviderBilling</c> owner. Default is absence (no row / null tenant
/// ⇒ platform); an active byok row flips to byok; a corrupt mode fails loud.
/// InMemory provider — resolution logic is provider-agnostic (the SQL
/// invariants are pinned by the Postgres model tests).
/// </summary>
[TestFixture]
public class TenantProviderBillingResolverTests
{
    private static ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static TenantProviderBillingResolver NewResolver(ControlPlaneDbContext db) =>
        new(db, NullLogger<TenantProviderBillingResolver>.Instance);

    private static TenantProviderBilling Row(
        Guid tenantId, string provider, string mode, string status = "active") => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        ProviderKey = provider,
        Mode = mode,
        SecretName = mode == "byok" ? $"provider/{provider}/api-key" : null,
        Status = status,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Test]
    public async Task ResolveMode_NullTenant_IsPlatformProvided()
    {
        await using var db = NewContext();
        var mode = await NewResolver(db).ResolveModeAsync(null, "anthropic");
        mode.Should().Be(MetricBillingMode.PlatformProvided, "single-user null tenant is always platform (AC8)");
    }

    [Test]
    public async Task ResolveMode_NoRow_IsPlatformProvided()
    {
        await using var db = NewContext();
        var mode = await NewResolver(db).ResolveModeAsync(Guid.NewGuid(), "anthropic");
        mode.Should().Be(MetricBillingMode.PlatformProvided, "absence of an owner row is the safe default");
    }

    [Test]
    public async Task ResolveMode_ActiveByokRow_IsByok_PerProvider()
    {
        var tenantId = Guid.NewGuid();
        await using var db = NewContext();
        db.TenantProviderBillings.AddRange(
            Row(tenantId, "anthropic", "byok"),
            Row(tenantId, "openai", "platform"));
        await db.SaveChangesAsync();

        var resolver = NewResolver(db);
        (await resolver.ResolveModeAsync(tenantId, "anthropic")).Should().Be(MetricBillingMode.Byok);
        // Per-(tenant, provider): openai stays platform even though anthropic is byok.
        (await resolver.ResolveModeAsync(tenantId, "openai")).Should().Be(MetricBillingMode.PlatformProvided);
    }

    [Test]
    public async Task ResolveMode_IsCaseInsensitiveOnProvider()
    {
        var tenantId = Guid.NewGuid();
        await using var db = NewContext();
        db.TenantProviderBillings.Add(Row(tenantId, "anthropic", "byok"));
        await db.SaveChangesAsync();

        (await NewResolver(db).ResolveModeAsync(tenantId, "ANTHROPIC")).Should().Be(MetricBillingMode.Byok);
    }

    // ── The read matches on the RAW provider IDENTITY the proxy passes (Trim + lower,
    //    NO alias-family reduction) — the SAME handle 34-3 persists and 32-3 reads. A
    //    "gemini" BYOK row is read for a "gemini" call, and is DISTINCT from the rate-card
    //    family "google" (keying on the family would silently unbill / mis-tag the wrong
    //    provider). Mixed case still matches (case-insensitive on the identity). ──
    [Test]
    public async Task ResolveMode_KeysOnRawProviderIdentity_NotRateCardFamily()
    {
        var tenantId = Guid.NewGuid();
        await using var db = NewContext();
        // Owner rows keyed on the RAW handle.
        db.TenantProviderBillings.AddRange(
            Row(tenantId, "gemini", "byok"),
            Row(tenantId, "github-copilot", "byok"));
        await db.SaveChangesAsync();

        var resolver = NewResolver(db);

        // The raw handle matches its own row.
        (await resolver.ResolveModeAsync(tenantId, "gemini")).Should().Be(MetricBillingMode.Byok);
        (await resolver.ResolveModeAsync(tenantId, "GEMINI")).Should().Be(
            MetricBillingMode.Byok, "the identity match is case-insensitive");
        (await resolver.ResolveModeAsync(tenantId, "github-copilot")).Should().Be(MetricBillingMode.Byok);

        // The rate-card FAMILY is a DIFFERENT identity — a "google" / "openai" call does
        // NOT pick up the gemini / github-copilot BYOK row (fail-before: the old
        // family-canonicalized read collapsed gemini→google and matched wrongly).
        (await resolver.ResolveModeAsync(tenantId, "google")).Should().Be(
            MetricBillingMode.PlatformProvided, "'gemini' BYOK must not leak onto a 'google' call");
        (await resolver.ResolveModeAsync(tenantId, "openai")).Should().Be(
            MetricBillingMode.PlatformProvided, "'github-copilot' BYOK must not leak onto an 'openai' call");
    }

    [Test]
    public async Task ResolveMode_IgnoresDisabledRow_FallsBackToPlatform()
    {
        var tenantId = Guid.NewGuid();
        await using var db = NewContext();
        db.TenantProviderBillings.Add(Row(tenantId, "anthropic", "byok", status: "disabled"));
        await db.SaveChangesAsync();

        (await NewResolver(db).ResolveModeAsync(tenantId, "anthropic"))
            .Should().Be(MetricBillingMode.PlatformProvided, "only an ACTIVE row counts");
    }

    [Test]
    public async Task ResolveMode_TenantIsolated()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await using var db = NewContext();
        db.TenantProviderBillings.Add(Row(tenantA, "anthropic", "byok"));
        await db.SaveChangesAsync();

        var resolver = NewResolver(db);
        (await resolver.ResolveModeAsync(tenantA, "anthropic")).Should().Be(MetricBillingMode.Byok);
        (await resolver.ResolveModeAsync(tenantB, "anthropic"))
            .Should().Be(MetricBillingMode.PlatformProvided, "tenant B never sees tenant A's mode");
    }

    [Test]
    public async Task ResolveMode_CorruptRowMode_FailsLoud()
    {
        var tenantId = Guid.NewGuid();
        await using var db = NewContext();
        // The DB CHECK prevents this, but a corrupt row must never silently mistag.
        db.TenantProviderBillings.Add(Row(tenantId, "anthropic", "garbage"));
        await db.SaveChangesAsync();

        var act = async () => await NewResolver(db).ResolveModeAsync(tenantId, "anthropic");
        (await act.Should().ThrowAsync<TammaError>()).Which.Code.Should().Be("BILLING_MODE_CORRUPT");
    }
}
