using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Defaults;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-1 PR A — verifies that the three repositories whose
/// "<c>tenant_id IS NULL</c>" CP rows previously carried platform defaults
/// (<see cref="AgentConfigRepository"/>, <see cref="BudgetConfigRepository"/>,
/// <see cref="SanitizationRepository"/>) now resolve to the in-code
/// <see cref="AgentConfigDefaults"/> / <see cref="BudgetConfigDefaults"/> /
/// <see cref="SanitizationRuleDefaults"/> values per Decision #1 of the
/// Story 28-1 design ADR (<c>.dev/decisions/story-28-1-design-calls.md</c>).
///
/// <para>
/// Two-stage shape per repo:
/// <list type="number">
///   <item><b>No-row → defaults</b>: with no per-tenant row stored, calling
///   the repo with <c>tenantId == null</c> returns the code-resident default
///   (or <c>null</c>, where the original contract was "no row found").</item>
///   <item><b>Tenant row shadows default</b>: when a per-tenant row exists,
///   it shadows the default for that tenant.</item>
/// </list>
/// </para>
///
/// <para>
/// Uses the EF in-memory provider via <see cref="InMemoryDbFixture"/> so the
/// tests stay hermetic — the platform-default-row pattern only exercises the
/// repository's plane-switching logic, not Postgres-specific behaviour.
/// </para>
/// </summary>
[TestFixture]
public class PlatformDefaultRowRepositoryTests
{
    // ════════════════════════════════════════════════════════════════════
    // AgentConfigRepository
    // ════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AgentConfig_GetAsync_NullTenant_ReturnsNull()
    {
        await using var fx = new InMemoryDbFixture();
        var repo = new AgentConfigRepository(fx.Factory);

        var result = await repo.GetAsync(null);

        // Story 28-1 PR A: platform default lives in code; the legacy CP
        // "TenantId IS NULL" row no longer exists, so the read returns null.
        result.Should().BeNull();
    }

    [Test]
    public async Task AgentConfig_ResolveAsync_TenantWithoutRow_FallsBackToDefaultsSentinel()
    {
        await using var fx = new InMemoryDbFixture();
        var repo = new AgentConfigRepository(fx.Factory);
        var tid = Guid.NewGuid();

        var (cfg, source) = await repo.ResolveAsync(tid);

        // No tenant row, no CP row → defaults sentinel, "default" source.
        source.Should().Be("default");
        cfg.Should().NotBeNull();
        cfg.Config.Should().Be(AgentConfigDefaults.ConfigJson);
    }

    [Test]
    public async Task AgentConfig_ResolveAsync_TenantRowShadowsDefault()
    {
        await using var fx = new InMemoryDbFixture();
        var repo = new AgentConfigRepository(fx.Factory);
        var tid = Guid.NewGuid();

        // Seed a tenant-scoped override directly through the factory.
        await using (var tdb = await fx.Factory.CreateAsync(tid))
        {
            tdb.AgentConfigs.Add(new AgentConfig
            {
                TenantId = tid,
                Config = "{\"roles\":{\"developer\":{\"provider\":\"openai\"}}}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await tdb.SaveChangesAsync();
        }

        var (cfg, source) = await repo.ResolveAsync(tid);

        source.Should().Be("tenant");
        cfg.TenantId.Should().Be(tid);
        cfg.Config.Should().Contain("openai");
    }

    [Test]
    public async Task AgentConfig_UpsertAsync_NullTenant_IsNoOp_AndReturnsDefaultsSnapshot()
    {
        await using var fx = new InMemoryDbFixture();
        var repo = new AgentConfigRepository(fx.Factory);

        var returned = await repo.UpsertAsync(
            null, "{\"roles\":{\"developer\":{}}}", userId: null);

        // Story 28-1 PR A: the request is discarded; the returned snapshot is
        // the in-code defaults shape so callers stay non-null.
        returned.Should().NotBeNull();
        returned.TenantId.Should().BeNull();
        returned.Config.Should().Be(AgentConfigDefaults.ConfigJson);

        // Story 28-1 PR D — agent_configs moved to the tenant DB.
        // The null-tenant null-op should NOT have created a row anywhere.
        // Use Guid.Empty as a sentinel to peek at any per-tenant store
        // (the test factory shares one store across tenants).
        await using var tdb = await fx.Factory.CreateAsync(Guid.NewGuid());
        var tenantCount = await tdb.AgentConfigs.IgnoreQueryFilters().CountAsync();
        tenantCount.Should().Be(0);
    }

    // ════════════════════════════════════════════════════════════════════
    // BudgetConfigRepository
    // ════════════════════════════════════════════════════════════════════

    [Test]
    public async Task BudgetConfig_GetAsync_NullTenant_ReturnsNull()
    {
        await using var fx = new InMemoryDbFixture();
        var repo = new BudgetConfigRepository(fx.Factory);

        var result = await repo.GetAsync(null, "account-A");

        // Story 28-1 PR A: no platform-default CP row to fetch; callers fall
        // through to BudgetConfigDefaults / IConfiguration in
        // PostgresBudgetConfigProvider.
        result.Should().BeNull();
    }

    [Test]
    public async Task BudgetConfig_GetAsync_TenantRowReturnsTenantValue()
    {
        await using var fx = new InMemoryDbFixture();
        var repo = new BudgetConfigRepository(fx.Factory);
        var tid = Guid.NewGuid();
        var account = tid.ToString();

        await repo.UpsertAsync(new BudgetConfig
        {
            TenantId = tid,
            AccountId = account,
            LimitUsd = 250m,
            AlertThreshold = 0.6,
            PeriodDays = 14,
        });

        var row = await repo.GetAsync(tid, account);
        row.Should().NotBeNull();
        row!.LimitUsd.Should().Be(250m);
    }

    [Test]
    public async Task BudgetConfig_UpsertAsync_NullTenant_IsNoOp_AndDoesNotPersist()
    {
        await using var fx = new InMemoryDbFixture();
        var repo = new BudgetConfigRepository(fx.Factory);

        var returned = await repo.UpsertAsync(new BudgetConfig
        {
            TenantId = null,
            AccountId = "platform-account",
            LimitUsd = 999m,
            AlertThreshold = 0.5,
            PeriodDays = 7,
        });

        // Defaults snapshot returned, NOT the supplied values.
        returned.LimitUsd.Should().Be(BudgetConfigDefaults.DefaultLimitUsd);
        returned.AlertThreshold.Should().Be(BudgetConfigDefaults.DefaultAlertThreshold);
        returned.PeriodDays.Should().Be(BudgetConfigDefaults.DefaultPeriodDays);

        // Story 28-1 PR D — budget_configs moved to the tenant DB.
        // The null-tenant no-op didn't write anywhere.
        await using var tdb = await fx.Factory.CreateAsync(Guid.NewGuid());
        var tenantCount = await tdb.BudgetConfigs.IgnoreQueryFilters().CountAsync();
        tenantCount.Should().Be(0);
    }

    [Test]
    public async Task BudgetConfig_DeleteAsync_NullTenant_ReturnsFalseAndIsNoOp()
    {
        await using var fx = new InMemoryDbFixture();
        var repo = new BudgetConfigRepository(fx.Factory);

        var removed = await repo.DeleteAsync(null, "platform-account");

        // Nothing to remove because defaults are code-resident; "false" is
        // the natural "no rows affected" signal preserved from the prior API.
        removed.Should().BeFalse();
    }

    // ════════════════════════════════════════════════════════════════════
    // SanitizationRepository
    // ════════════════════════════════════════════════════════════════════

    [Test]
    public async Task SanitizationRepo_GetRulesAsync_NullTenant_ReturnsCodeDefaults()
    {
        await using var fx = new InMemoryDbFixture();
        var defaults = new StubDefaultsProvider(new[]
        {
            new SanitizationRuleDefinition(
                Name: "redact-email",
                Pattern: "\\b\\w+@\\w+\\b",
                Replacement: "[email]",
                Priority: 10,
                Enabled: true,
                CaseSensitive: false),
            new SanitizationRuleDefinition(
                Name: "redact-card",
                Pattern: "\\d{16}",
                Replacement: "[card]",
                Priority: 20,
                Enabled: true,
                CaseSensitive: false),
        });
        var repo = new SanitizationRepository(fx.Factory, new[] { defaults });

        var rules = await repo.GetRulesAsync(null);

        // Defaults pass through verbatim because there is no platform-default
        // override row to merge in any longer.
        rules.Should().HaveCount(2);
        rules.Select(r => r.Name).Should().BeEquivalentTo(
            new[] { "redact-email", "redact-card" });
    }

    [Test]
    public async Task SanitizationRepo_GetRulesAsync_TenantOverridesShadowDefaults()
    {
        await using var fx = new InMemoryDbFixture();
        var defaults = new StubDefaultsProvider(new[]
        {
            new SanitizationRuleDefinition(
                Name: "redact-email",
                Pattern: "\\b\\w+@\\w+\\b",
                Replacement: "[email]",
                Priority: 10,
                Enabled: true,
                CaseSensitive: false),
        });
        var repo = new SanitizationRepository(fx.Factory, new[] { defaults });
        var tid = Guid.NewGuid();

        // Seed a tenant override via the public Upsert API.
        await repo.UpsertRuleAsync(tid, new SanitizationRuleDefinition(
            Name: "redact-email",
            Pattern: "\\b\\w+@\\w+\\b",
            Replacement: "[REDACTED]",
            Priority: 10,
            Enabled: true,
            CaseSensitive: false));

        var rules = await repo.GetRulesAsync(tid);

        rules.Should().HaveCount(1);
        rules[0].Name.Should().Be("redact-email");
        rules[0].Replacement.Should().Be("[REDACTED]");
    }

    [Test]
    public async Task SanitizationRepo_UpsertRuleAsync_NullTenant_IsNoOp()
    {
        await using var fx = new InMemoryDbFixture();
        var defaults = new StubDefaultsProvider(Array.Empty<SanitizationRuleDefinition>());
        var repo = new SanitizationRepository(fx.Factory, new[] { defaults });

        await repo.UpsertRuleAsync(null, new SanitizationRuleDefinition(
            Name: "platform-rule",
            Pattern: "x",
            Replacement: "y",
            Priority: 0,
            Enabled: true,
            CaseSensitive: false));

        // Story 28-1 PR D — sanitization_rules moved to the tenant DB.
        await using var tdb = await fx.Factory.CreateAsync(Guid.NewGuid());
        var tenantCount = await tdb.SanitizationRules.IgnoreQueryFilters().CountAsync();
        tenantCount.Should().Be(0);

        // GetRulesAsync(null) still returns the empty defaults set.
        var rules = await repo.GetRulesAsync(null);
        rules.Should().BeEmpty();
    }

    [Test]
    public async Task SanitizationRepo_GetRawAsync_NullTenant_ReturnsSyntheticSnapshotFromDefaults()
    {
        await using var fx = new InMemoryDbFixture();
        var defaults = new StubDefaultsProvider(new[]
        {
            new SanitizationRuleDefinition(
                Name: "redact-secret",
                Pattern: "secret",
                Replacement: "***",
                Priority: 0,
                Enabled: true,
                CaseSensitive: false),
        });
        var repo = new SanitizationRepository(fx.Factory, new[] { defaults });

        var raw = await repo.GetRawAsync(null);

        raw.Should().NotBeNull();
        raw!.Id.Should().Be(Guid.Empty); // sentinel — synthetic, not persisted
        raw.TenantId.Should().BeNull();
        raw.Rules.Should().Contain("redact-secret");
    }

    // ════════════════════════════════════════════════════════════════════
    // Defaults.Snapshot() — fresh-clone-per-call invariant
    //
    // PR #338 wave-4 review (LOW upgraded to required): the Defaults classes
    // promise a fresh, mutable instance on every call. A future contributor
    // optimising to a cached singleton would silently break thread safety
    // and let one caller's mutation leak into the next caller's view of the
    // platform default. These tests pin that contract.
    // ════════════════════════════════════════════════════════════════════

    [Test]
    public void AgentConfigDefaults_Snapshot_ReturnsFreshInstancePerCall()
    {
        var a = AgentConfigDefaults.Snapshot();
        var b = AgentConfigDefaults.Snapshot();

        a.Should().NotBeSameAs(b);

        // Mutating one snapshot must NOT leak into the next snapshot.
        a.Config = "{\"poison\":true}";
        a.Version = 999;

        var c = AgentConfigDefaults.Snapshot();
        c.Config.Should().Be(AgentConfigDefaults.ConfigJson);
        c.Version.Should().Be(0);
    }

    [Test]
    public void BudgetConfigDefaults_Snapshot_ReturnsFreshInstancePerCall()
    {
        var a = BudgetConfigDefaults.Snapshot("acct-1");
        var b = BudgetConfigDefaults.Snapshot("acct-1");

        a.Should().NotBeSameAs(b);

        // Mutating one snapshot must NOT leak across calls.
        a.LimitUsd = 9_999m;
        a.AlertThreshold = 0.01;
        a.PeriodDays = 1;

        var c = BudgetConfigDefaults.Snapshot("acct-1");
        c.LimitUsd.Should().Be(BudgetConfigDefaults.DefaultLimitUsd);
        c.AlertThreshold.Should().Be(BudgetConfigDefaults.DefaultAlertThreshold);
        c.PeriodDays.Should().Be(BudgetConfigDefaults.DefaultPeriodDays);
    }

    [Test]
    public void SanitizationRuleDefaults_Snapshot_ReturnsFreshInstancePerCall()
    {
        const string json = "[{\"name\":\"x\",\"pattern\":\"y\"}]";
        var a = SanitizationRuleDefaults.Snapshot(json);
        var b = SanitizationRuleDefaults.Snapshot(json);

        a.Should().NotBeSameAs(b);

        // Mutating one snapshot must NOT leak across calls.
        a.Rules = "[{\"name\":\"poison\",\"pattern\":\"z\"}]";

        var c = SanitizationRuleDefaults.Snapshot(json);
        c.Rules.Should().Be(json);
    }

    // ────────────────────────────────────────────────────────────────────
    // helpers
    // ────────────────────────────────────────────────────────────────────

    private sealed class StubDefaultsProvider : ISanitizationDefaultsProvider
    {
        public StubDefaultsProvider(IReadOnlyList<SanitizationRuleDefinition> rules)
        {
            DefaultRules = rules;
        }

        public IReadOnlyList<SanitizationRuleDefinition> DefaultRules { get; }
    }
}
