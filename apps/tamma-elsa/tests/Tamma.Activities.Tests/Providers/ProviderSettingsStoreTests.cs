using FluentAssertions;
using Microsoft.Extensions.Logging;
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
/// resolution (SaaS tenant-keyed vs single-user user-keyed), the
/// no-repository degradation (reads answer "no row"; writes fail loud),
/// startup priming (review F1), stale-load discard (review F2), and the
/// single-user multi-user-row collapse warning (review F7).
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

    /// <summary>Review F2 harness — delegates to <see cref="FakeRepository"/>
    /// but holds the FIRST LoadAllAsync open (after it has captured the rows
    /// as they were when the read began) until the test releases the gate —
    /// simulating a slow background TTL load racing a write refresh.</summary>
    private sealed class GatedRepository : IProviderSettingsRepository
    {
        private readonly FakeRepository _inner = new();
        private int _loadCalls;

        public List<ProviderSetting> Rows => _inner.Rows;
        public TaskCompletionSource FirstLoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstLoadGate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<ProviderSetting>> LoadAllAsync(
            CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _loadCalls);
            // Capture the rows AS OF the moment the read began — the stale
            // load must return the pre-write state, like a DB read would.
            var rows = await _inner.LoadAllAsync(ct);
            if (call == 1)
            {
                FirstLoadStarted.TrySetResult();
                await FirstLoadGate.Task;
            }
            return rows;
        }

        public Task<ProviderSetting> UpsertAsync(
            Guid? tenantId, Guid? userId, string providerKey, string? model, bool? enabled,
            Guid? updatedBy, CancellationToken ct = default) =>
            _inner.UpsertAsync(tenantId, userId, providerKey, model, enabled, updatedBy, ct);

        public Task<bool> DeleteAsync(
            Guid? tenantId, Guid? userId, string providerKey, CancellationToken ct = default) =>
            _inner.DeleteAsync(tenantId, userId, providerKey, ct);
    }

    private sealed class ThrowingRepository : IProviderSettingsRepository
    {
        public Task<IReadOnlyList<ProviderSetting>> LoadAllAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("db unavailable");
        public Task<ProviderSetting> UpsertAsync(
            Guid? t, Guid? u, string p, string? m, bool? e, Guid? by, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> DeleteAsync(
            Guid? t, Guid? u, string p, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FixedMode : ITammaModeProvider
    {
        public FixedMode(TammaMode mode) => Mode = mode;
        public TammaMode Mode { get; }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static ProviderSettingsStore Store(
        IProviderSettingsRepository? repo, TammaMode mode = TammaMode.SaaS,
        TimeProvider? time = null) =>
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
    public async Task PrincipalWrites_RequireExactlyOnePrincipalId()
    {
        // Review F5 — these assertions were previously un-awaited
        // (Should().ThrowAsync returns a Task that was dropped), so the test
        // passed even with the guard removed. Awaited, it fails without the
        // guard: the fake repository accepts any id combination, so no
        // ArgumentException would surface.
        var store = Store(new FakeRepository());
        var id = Guid.NewGuid();

        var both = () => store.SetPrincipalModelAsync("openai", id, id, "m", null);
        var neither = () => store.SetPrincipalModelAsync("openai", null, null, "m", null);

        await both.Should().ThrowAsync<ArgumentException>();
        await neither.Should().ThrowAsync<ArgumentException>();
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
    public async Task NoRepository_ReadsAnswerNoRow_WritesFailLoud()
    {
        var store = Store(repo: null);

        store.TryGetModel("openai", Guid.NewGuid()).Should().BeNull();
        store.TryGetPlatformModel("openai").Should().BeNull();
        store.IsEnabled("openai").Should().BeTrue();

        // Review F5 — awaited (was a dropped Task, i.e. vacuously green).
        // Without RequireRepository the null repository would surface as a
        // NullReferenceException, failing the typed assertion below.
        var write = () => store.SetPlatformModelAsync("openai", "m", null);
        await write.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── review F1: startup priming ──────────────────────────────────────────

    [Test]
    public async Task Priming_HostedServiceStart_MakesTheFirstReadSeeThePersistedRow()
    {
        var repo = new FakeRepository();
        repo.Rows.Add(new ProviderSetting
        {
            Id = Guid.NewGuid(),
            Scope = "platform",
            ProviderKey = "openai",
            DefaultModel = "persisted-before-restart",
            Enabled = true,
            UpdatedAt = DateTime.UtcNow,
        });
        var store = Store(repo);
        var priming = new ProviderSettingsStorePrimingService(
            store, NullLogger<ProviderSettingsStorePrimingService>.Instance);

        await priming.StartAsync(CancellationToken.None);

        // The FIRST snapshot read after startup sees the row — synchronously,
        // with no TTL wait and no background refresh needed.
        store.TryGetPlatformModel("openai").Should().Be("persisted-before-restart",
            "the cold-start snapshot must be primed before the app serves traffic (F1)");
        repo.LoadCount.Should().Be(1, "priming itself performed the load; the read was served "
            + "from the primed snapshot without scheduling another refresh");
    }

    [Test]
    public async Task Priming_DbUnavailable_FailsSoft_HostStartupProceeds()
    {
        var store = Store(new ThrowingRepository());
        var priming = new ProviderSettingsStorePrimingService(
            store, NullLogger<ProviderSettingsStorePrimingService>.Instance);

        var start = () => priming.StartAsync(CancellationToken.None);

        await start.Should().NotThrowAsync(
            "a briefly-unavailable DB must not crash the host (F1 fail-soft); "
            + "the lazy TTL refresh remains the fallback");
        store.TryGetPlatformModel("openai").Should().BeNull("the empty snapshot is served until "
            + "a later refresh succeeds");
    }

    // ── review F2: stale background load vs write refresh ───────────────────

    [Test]
    public async Task StaleLoad_FinishingAfterAWriteRefresh_IsDiscardedNotInstalled()
    {
        var repo = new GatedRepository();
        var store = Store(repo);

        // A slow load whose DB read began BEFORE the write — it captured the
        // pre-write (empty) row set and is now stuck mid-flight.
        var staleLoad = store.RefreshAsync();
        await repo.FirstLoadStarted.Task;

        // The write lands; its own refresh installs the post-write snapshot.
        await store.SetPlatformModelAsync("openai", "post-write-model", null);
        store.TryGetPlatformModel("openai").Should().Be("post-write-model");

        // The stale load completes AFTER the write's refresh.
        repo.FirstLoadGate.SetResult();
        await staleLoad;

        store.TryGetPlatformModel("openai").Should().Be("post-write-model",
            "a load versioned before the write's refresh must be discarded on completion — "
            + "installing it would revive the pre-write snapshot for up to the 60 s TTL (F2)");
    }

    // ── review F7: single-user multi-user-row collapse warning ──────────────

    [Test]
    public async Task SingleUser_RowsForMultipleUserIds_CollapseLastWriteWins_AndWarn()
    {
        var repo = new FakeRepository();
        repo.Rows.Add(new ProviderSetting
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Scope = "principal",
            ProviderKey = "openai",
            DefaultModel = "older-users-model",
            Enabled = true,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
        });
        repo.Rows.Add(new ProviderSetting
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Scope = "principal",
            ProviderKey = "openai",
            DefaultModel = "newest-users-model",
            Enabled = true,
            UpdatedAt = DateTime.UtcNow,
        });
        var logger = new RecordingLogger<ProviderSettingsStore>();
        var store = new ProviderSettingsStore(
            repo, new FixedMode(TammaMode.SingleUser), logger);

        await store.RefreshAsync();

        store.TryGetModel("openai", null).Should().Be("newest-users-model",
            "the documented collapse is deterministic: most recently updated wins");
        logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("2 distinct user ids"),
            "F7: shadowing another user's override must be surfaced, not silent");
    }

    [Test]
    public async Task SaaS_RowsForMultipleUserIds_NoCollapseWarning()
    {
        // The collapse (and its warning) is a single-user-mode concern; user
        // rows are simply not part of the SaaS lookup.
        var repo = new FakeRepository();
        repo.Rows.Add(new ProviderSetting
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Scope = "principal",
            ProviderKey = "openai", DefaultModel = "a", Enabled = true, UpdatedAt = DateTime.UtcNow,
        });
        repo.Rows.Add(new ProviderSetting
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Scope = "principal",
            ProviderKey = "openai", DefaultModel = "b", Enabled = true, UpdatedAt = DateTime.UtcNow,
        });
        var logger = new RecordingLogger<ProviderSettingsStore>();
        var store = new ProviderSettingsStore(repo, new FixedMode(TammaMode.SaaS), logger);

        await store.RefreshAsync();

        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }
}
