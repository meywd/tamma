using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.Tests.LlmCall; // FakeTimeProvider (local test helper)
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Providers;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Tests.Providers;

/// <summary>
/// Story 46-1 (AC2) — the provider-settings snapshot store: sync reads,
/// invalidate-on-write, the pinned 60 s lazy-refresh TTL, per-mode principal
/// resolution (SaaS tenant-keyed vs single-user user-keyed), and the
/// no-repository degradation (reads answer "no row"; writes fail loud).
/// </summary>
[TestFixture]
public class ProviderSettingsStoreTests
{
    private sealed class FakeRepository : IProviderSettingsRepository
    {
        public List<ProviderSetting> Rows { get; } = new();
        public int LoadCount;

        public Task<IReadOnlyList<ProviderSetting>> LoadAllAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref LoadCount);
            return Task.FromResult<IReadOnlyList<ProviderSetting>>(Rows.ToList());
        }

        public Task<ProviderSetting> UpsertAsync(
            Guid? tenantId, Guid? userId, string providerKey, string? model, bool? enabled,
            Guid? updatedBy, CancellationToken ct = default)
        {
            var row = Rows.FirstOrDefault(r =>
                r.TenantId == tenantId && r.UserId == userId && r.ProviderKey == providerKey);
            if (row is null)
            {
                row = new ProviderSetting
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    UserId = userId,
                    Scope = tenantId is null && userId is null ? "platform" : "principal",
                    ProviderKey = providerKey,
                };
                Rows.Add(row);
            }
            if (model is not null) row.DefaultModel = model;
            if (enabled is not null) row.Enabled = enabled.Value;
            row.UpdatedAt = DateTime.UtcNow;
            row.UpdatedBy = updatedBy;
            return Task.FromResult(row);
        }

        public Task<bool> DeleteAsync(
            Guid? tenantId, Guid? userId, string providerKey, CancellationToken ct = default)
        {
            var row = Rows.FirstOrDefault(r =>
                r.TenantId == tenantId && r.UserId == userId && r.ProviderKey == providerKey);
            if (row is null) return Task.FromResult(false);
            Rows.Remove(row);
            return Task.FromResult(true);
        }
    }

    private sealed class FixedMode : ITammaModeProvider
    {
        public FixedMode(TammaMode mode) => Mode = mode;
        public TammaMode Mode { get; }
    }

    private static ProviderSettingsStore Store(
        FakeRepository? repo, TammaMode mode = TammaMode.SaaS, TimeProvider? time = null) =>
        new(repo, new FixedMode(mode), NullLogger<ProviderSettingsStore>.Instance, time);

    // ── AC9 test 5: write → same-instance read is immediate ────────────────

    [Test]
    public async Task Write_ThenRead_IsImmediateOnTheWritingInstance()
    {
        var repo = new FakeRepository();
        var store = Store(repo);
        var tenant = Guid.NewGuid();

        await store.SetPlatformModelAsync("openai", "gpt-4o-mini", null);
        store.TryGetPlatformModel("openai").Should().Be("gpt-4o-mini");

        await store.SetPrincipalModelAsync("openai", tenant, null, "gpt-4.1-tenant", null);
        store.TryGetModel("openai", tenant).Should().Be("gpt-4.1-tenant");
        store.HasOverride("openai", tenant).Should().BeTrue();

        (await store.RemovePrincipalModelAsync("openai", tenant, null)).Should().BeTrue();
        store.TryGetModel("openai", tenant).Should().BeNull();

        (await store.RemovePlatformAsync("openai")).Should().BeTrue();
        store.TryGetPlatformModel("openai").Should().BeNull();
        (await store.RemovePlatformAsync("openai")).Should().BeFalse("nothing left to remove");
    }

    // ── AC9 test 6: the TTL value is pinned + expiry triggers a refresh ────

    [Test]
    public void RefreshTtl_IsSixtySeconds_TheDocumentedMultiInstanceBound()
    {
        ProviderSettingsStore.RefreshTtl.Should().Be(TimeSpan.FromSeconds(60),
            "the multi-instance staleness bound is documented as 60 s (the BYOK cache posture); "
            + "changing it silently would falsify the IProviderSettingsStore XML docs");
    }

    [Test]
    public async Task TtlExpiry_TriggersBackgroundRefresh_ReadersNeverBlock()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-27T12:00:00Z"));
        var repo = new FakeRepository();
        var store = Store(repo, TammaMode.SaaS, time);

        await store.RefreshAsync();
        var loadsAfterPrime = repo.LoadCount;

        // Within TTL: no refetch.
        store.TryGetPlatformModel("openai");
        repo.LoadCount.Should().Be(loadsAfterPrime);

        // Another instance writes directly to the "DB".
        repo.Rows.Add(new ProviderSetting
        {
            Id = Guid.NewGuid(),
            Scope = "platform",
            ProviderKey = "openai",
            DefaultModel = "written-elsewhere",
            Enabled = true,
            UpdatedAt = DateTime.UtcNow,
        });

        // After TTL expiry a read schedules the background rebuild.
        time.Advance(TimeSpan.FromSeconds(61));
        store.TryGetPlatformModel("openai"); // fire-and-forget refresh kicks off

        await WaitForAsync(() => repo.LoadCount > loadsAfterPrime);
        await WaitForAsync(() => store.TryGetPlatformModel("openai") == "written-elsewhere");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        condition().Should().BeTrue("the background refresh should converge");
    }

    // ── AC9 test 7 + plan D3: per-mode principal resolution ────────────────

    [Test]
    public async Task SingleUser_UserKeyedRow_ResolvesRegardlessOfTenantArgument()
    {
        var repo = new FakeRepository();
        var store = Store(repo, TammaMode.SingleUser);
        var soleUser = Guid.NewGuid();

        await store.SetPrincipalModelAsync("anthropic", null, soleUser, "claude-opus-4-7", soleUser);

        // The egress path may pass null OR the personal-tenant id — the sole
        // user's row is the principal leg either way (plan D3).
        store.TryGetModel("anthropic", null).Should().Be("claude-opus-4-7");
        store.TryGetModel("anthropic", Guid.NewGuid()).Should().Be("claude-opus-4-7");
    }

    [Test]
    public async Task SaaS_TenantKeyedRows_AreIsolatedPerTenant()
    {
        var repo = new FakeRepository();
        var store = Store(repo, TammaMode.SaaS);
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await store.SetPrincipalModelAsync("openai", tenantA, null, "model-a", null);

        store.TryGetModel("openai", tenantA).Should().Be("model-a");
        store.TryGetModel("openai", tenantB).Should().BeNull("tenant B has no override");
        store.TryGetModel("openai", null).Should().BeNull("no tenant context → no principal leg");
    }

    [Test]
    public void PrincipalWrites_RequireExactlyOnePrincipalId()
    {
        var store = Store(new FakeRepository());
        var id = Guid.NewGuid();

        var both = () => store.SetPrincipalModelAsync("openai", id, id, "m", null);
        var neither = () => store.SetPrincipalModelAsync("openai", null, null, "m", null);

        both.Should().ThrowAsync<ArgumentException>();
        neither.Should().ThrowAsync<ArgumentException>();
    }

    // ── enabled flag ────────────────────────────────────────────────────────

    [Test]
    public async Task Enabled_DefaultsTrue_AndPersistsTheOffSwitch()
    {
        var repo = new FakeRepository();
        var store = Store(repo);

        store.IsEnabled("groq").Should().BeTrue("no platform row means enabled");

        await store.SetEnabledAsync("groq", false, null);
        store.IsEnabled("groq").Should().BeFalse();
        store.TryGetPlatformModel("groq").Should().BeNull(
            "a flag-only platform row carries no model");

        await store.SetEnabledAsync("groq", true, null);
        store.IsEnabled("groq").Should().BeTrue();
    }

    // ── alias canonicalization ──────────────────────────────────────────────

    [Test]
    public async Task Writes_AndReads_CanonicalizeAliases()
    {
        var repo = new FakeRepository();
        var store = Store(repo);

        await store.SetPlatformModelAsync("kimi", "kimi-k3-turbo", null);

        repo.Rows.Should().ContainSingle().Which.ProviderKey.Should().Be(
            "moonshot", "settings rows are keyed by the CANONICAL provider key");
        store.TryGetPlatformModel("moonshot").Should().Be("kimi-k3-turbo");
        store.TryGetPlatformModel("kimi").Should().Be("kimi-k3-turbo");
    }

    // ── no-repository degradation ───────────────────────────────────────────

    [Test]
    public void NoRepository_ReadsAnswerNoRow_WritesFailLoud()
    {
        var store = Store(repo: null);

        store.TryGetModel("openai", Guid.NewGuid()).Should().BeNull();
        store.TryGetPlatformModel("openai").Should().BeNull();
        store.IsEnabled("openai").Should().BeTrue();

        var write = () => store.SetPlatformModelAsync("openai", "m", null);
        write.Should().ThrowAsync<InvalidOperationException>();
    }
}
