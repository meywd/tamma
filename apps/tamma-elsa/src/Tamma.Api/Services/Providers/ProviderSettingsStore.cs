using Tamma.Api.Services.PromptStore;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Providers;

/// <summary>
/// Story 46-1 — the DB-backed provider-settings layer of the default-model
/// precedence chain (<b>tenant override → platform DB → config → descriptor</b>,
/// epic 46 D2), exposed as SYNC snapshot reads.
///
/// <para><b>Why sync + snapshot:</b> the hot consumers
/// (<c>InlineToolLoopRunner.LoadProviderConfig</c> /
/// <c>IInlineToolLoopRunner.GetDefaultModel</c> → <c>ManagedAgent</c>) are
/// synchronous public surface; a per-call <c>await db</c> is not available
/// there without breaking the interface. The store therefore reads a
/// <c>volatile</c> whole-snapshot that is (a) rebuilt synchronously on every
/// write THROUGH the settings endpoints (same-process — the API serves both
/// the endpoints and the runner), and (b) refreshed lazily on read with a
/// 60-second TTL (matching <c>DefaultProviderCredentialResolver.DefaultCacheTtl</c>)
/// to cover multi-process deployments where another API instance took the
/// write. <b>Consequence, stated honestly: in a multi-instance deployment a
/// UI change may take up to 60 s to be honoured by other instances.</b> That
/// is well within "no redeploy" and matches the existing BYOK cache posture.</para>
///
/// <para><b>Scoping (per mode, CLAUDE.md universal rule):</b> SaaS requests
/// carry a <c>TenantId</c> — <see cref="TryGetModel"/> reads the tenant-keyed
/// row for it. Single-user installs key their override row by <c>user_id</c>;
/// rather than teach the egress path a new principal type, the store resolves
/// the sole user's row INTERNALLY when <c>ITammaModeProvider</c> reports
/// single-user mode (46-1 plan D3) — call sites stay one optional-Guid wide,
/// the same shape as <c>IProviderCredentialResolver.ResolveAsync</c>. Any
/// future scoping change (e.g. per-user rows in SaaS) lands here, in the
/// snapshot lookup — not at the call sites.</para>
/// </summary>
public interface IProviderSettingsStore
{
    /// <summary>The PRINCIPAL leg only (tenant row in SaaS; the sole user's
    /// row in single-user mode). Null when no override row exists. Sync
    /// snapshot read — never blocks, never throws.</summary>
    string? TryGetModel(string providerKey, Guid? tenantId);

    /// <summary>The PLATFORM row's model. Null when no platform row (or the
    /// row carries only the enabled flag). Sync snapshot read.</summary>
    string? TryGetPlatformModel(string providerKey);

    /// <summary>The platform enable flag; true when no platform row exists.
    /// NOT enforced on the egress path in Epic 46 (persisted + reported only —
    /// allowlist inversion is a later phase).</summary>
    bool IsEnabled(string providerKey);

    /// <summary>Whether a principal override row exists (settings endpoints'
    /// <c>hasOverride</c> provenance).</summary>
    bool HasOverride(string providerKey, Guid? tenantId);

    /// <summary>Upsert the platform default model. Invalidate-on-write: the
    /// snapshot is rebuilt before this returns.</summary>
    Task SetPlatformModelAsync(
        string providerKey, string model, Guid? updatedBy, CancellationToken ct = default);

    /// <summary>Set the platform enable flag (platform rows only).</summary>
    Task SetEnabledAsync(
        string providerKey, bool enabled, Guid? updatedBy, CancellationToken ct = default);

    /// <summary>Delete the platform row entirely → resolution falls back to
    /// config/descriptor. Returns false when no row existed.</summary>
    Task<bool> RemovePlatformAsync(string providerKey, CancellationToken ct = default);

    /// <summary>Upsert a principal override row — tenant-keyed (SaaS) or
    /// user-keyed (single-user), exactly one of the two ids non-null.</summary>
    Task SetPrincipalModelAsync(
        string providerKey, Guid? tenantId, Guid? userId, string model, Guid? updatedBy,
        CancellationToken ct = default);

    /// <summary>Delete a principal override row. Returns false when no row
    /// existed.</summary>
    Task<bool> RemovePrincipalModelAsync(
        string providerKey, Guid? tenantId, Guid? userId, CancellationToken ct = default);

    /// <summary>Force a snapshot rebuild (used after out-of-band writes; the
    /// public write methods call it internally).</summary>
    Task RefreshAsync(CancellationToken ct = default);
}

/// <inheritdoc />
/// <remarks>
/// 46-1 plan D2 — the snapshot is a single immutable object swapped whole
/// (<c>volatile</c> field): no per-entry locking, readers never block. Writes
/// go DB-first, then rebuild synchronously before returning, so the writing
/// instance is consistent immediately; other instances converge within
/// <see cref="RefreshTtl"/>. Deliberately NOT <c>IMemoryCache</c> — the
/// whole-snapshot swap is simpler to reason about and to test than eviction
/// semantics. When no repository is wired (hosts without a control-plane DB,
/// e.g. the standalone engine or bare unit-test composition), every read
/// answers "no row" and behaviour is byte-identical to pre-46-1.
/// </remarks>
public sealed class ProviderSettingsStore : IProviderSettingsStore
{
    /// <summary>Lazy-refresh TTL — 60 s, matching
    /// <see cref="DefaultProviderCredentialResolver.DefaultCacheTtl"/> (the
    /// BYOK cache posture users already live with). Pinned by a test so a
    /// silent change to the multi-instance staleness bound shows up.</summary>
    public static readonly TimeSpan RefreshTtl = TimeSpan.FromSeconds(60);

    private readonly IProviderSettingsRepository? _repository;
    private readonly ITammaModeProvider _mode;
    private readonly ILogger<ProviderSettingsStore> _logger;
    private readonly TimeProvider _timeProvider;

    private volatile Snapshot _snapshot = Snapshot.Empty;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private int _refreshing; // 0|1 — single-flight guard for the lazy refresh

    public ProviderSettingsStore(
        IProviderSettingsRepository? repository,
        ITammaModeProvider mode,
        ILogger<ProviderSettingsStore> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _mode = mode;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string? TryGetModel(string providerKey, Guid? tenantId)
    {
        var snapshot = CurrentSnapshot();

        if (_mode.Mode == TammaMode.SingleUser)
        {
            // Single-user: the override row is USER-keyed (the sole user owns
            // it). The egress path may carry the personal-tenant id or null —
            // either way the sole user's row is the principal leg (plan D3).
            return snapshot.UserModelByProvider.TryGetValue(Canonical(providerKey), out var m)
                ? m
                : null;
        }

        if (tenantId is not Guid tid)
        {
            return null; // SaaS with no tenant context → no principal leg
        }
        return snapshot.TenantModels.TryGetValue((tid, Canonical(providerKey)), out var model)
            ? model
            : null;
    }

    /// <inheritdoc />
    public string? TryGetPlatformModel(string providerKey)
    {
        return CurrentSnapshot().PlatformRows.TryGetValue(Canonical(providerKey), out var row)
            ? row.Model
            : null;
    }

    /// <inheritdoc />
    public bool IsEnabled(string providerKey)
    {
        return !CurrentSnapshot().PlatformRows.TryGetValue(Canonical(providerKey), out var row)
            || row.Enabled;
    }

    /// <inheritdoc />
    public bool HasOverride(string providerKey, Guid? tenantId)
        => TryGetModel(providerKey, tenantId) is not null;

    /// <inheritdoc />
    public async Task SetPlatformModelAsync(
        string providerKey, string model, Guid? updatedBy, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        await RequireRepository()
            .UpsertAsync(null, null, Canonical(providerKey), model, enabled: null, updatedBy, ct)
            .ConfigureAwait(false);
        await RefreshAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetEnabledAsync(
        string providerKey, bool enabled, Guid? updatedBy, CancellationToken ct = default)
    {
        await RequireRepository()
            .UpsertAsync(null, null, Canonical(providerKey), model: null, enabled, updatedBy, ct)
            .ConfigureAwait(false);
        await RefreshAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RemovePlatformAsync(string providerKey, CancellationToken ct = default)
    {
        var removed = await RequireRepository()
            .DeleteAsync(null, null, Canonical(providerKey), ct)
            .ConfigureAwait(false);
        await RefreshAsync(ct).ConfigureAwait(false);
        return removed;
    }

    /// <inheritdoc />
    public async Task SetPrincipalModelAsync(
        string providerKey, Guid? tenantId, Guid? userId, string model, Guid? updatedBy,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if ((tenantId is null) == (userId is null))
        {
            throw new ArgumentException(
                "A principal override row is keyed by exactly ONE of tenantId / userId.");
        }
        await RequireRepository()
            .UpsertAsync(tenantId, userId, Canonical(providerKey), model, enabled: null, updatedBy, ct)
            .ConfigureAwait(false);
        await RefreshAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RemovePrincipalModelAsync(
        string providerKey, Guid? tenantId, Guid? userId, CancellationToken ct = default)
    {
        if ((tenantId is null) == (userId is null))
        {
            throw new ArgumentException(
                "A principal override row is keyed by exactly ONE of tenantId / userId.");
        }
        var removed = await RequireRepository()
            .DeleteAsync(tenantId, userId, Canonical(providerKey), ct)
            .ConfigureAwait(false);
        await RefreshAsync(ct).ConfigureAwait(false);
        return removed;
    }

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_repository is null)
        {
            return; // no CP DB — the empty snapshot IS the truth
        }

        try
        {
            var rows = await _repository.LoadAllAsync(ct).ConfigureAwait(false);
            _snapshot = Snapshot.Build(rows);
            _loadedAt = _timeProvider.GetUtcNow();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Never break an egress call over a settings read — keep serving
            // the previous snapshot; the next TTL expiry retries.
            _logger.LogWarning(ex,
                "provider_settings snapshot refresh failed; serving the previous snapshot.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────

    private Snapshot CurrentSnapshot()
    {
        if (_repository is not null
            && _timeProvider.GetUtcNow() - _loadedAt > RefreshTtl
            && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
        {
            // Lazy TTL refresh in the background — readers NEVER block
            // (plan D2). The current (possibly stale-by-≤60s) snapshot is
            // served while the rebuild runs.
            _ = Task.Run(async () =>
            {
                try { await RefreshAsync(CancellationToken.None).ConfigureAwait(false); }
                finally { Interlocked.Exchange(ref _refreshing, 0); }
            });
        }
        return _snapshot;
    }

    private IProviderSettingsRepository RequireRepository() =>
        _repository ?? throw new InvalidOperationException(
            "provider_settings writes require the control-plane database " +
            "(no IProviderSettingsRepository is wired on this host).");

    private static string Canonical(string providerKey)
    {
        var spelled = (providerKey ?? string.Empty).Trim().ToLowerInvariant();
        return ProviderCatalog.Resolve(spelled)?.Key
            ?? ProviderCatalog.ResolveNonHttp(spelled)?.Key
            ?? spelled;
    }

    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new(
            new Dictionary<string, (string?, bool)>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<(Guid, string), string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        private Snapshot(
            IReadOnlyDictionary<string, (string? Model, bool Enabled)> platformRows,
            IReadOnlyDictionary<(Guid TenantId, string Provider), string> tenantModels,
            IReadOnlyDictionary<string, string> userModelByProvider)
        {
            PlatformRows = platformRows;
            TenantModels = tenantModels;
            UserModelByProvider = userModelByProvider;
        }

        public IReadOnlyDictionary<string, (string? Model, bool Enabled)> PlatformRows { get; }
        public IReadOnlyDictionary<(Guid TenantId, string Provider), string> TenantModels { get; }

        /// <summary>Single-user lookup: provider → the sole user's model. If
        /// multiple user rows somehow exist per provider (should not happen in
        /// single-user mode), the most recently updated wins —
        /// deterministically.</summary>
        public IReadOnlyDictionary<string, string> UserModelByProvider { get; }

        public static Snapshot Build(IReadOnlyList<ProviderSetting> rows)
        {
            var platform = new Dictionary<string, (string?, bool)>(StringComparer.OrdinalIgnoreCase);
            var tenants = new Dictionary<(Guid, string), string>();
            var users = new Dictionary<string, (string Model, DateTime UpdatedAt)>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                if (row.TenantId is null && row.UserId is null)
                {
                    platform[row.ProviderKey] = (
                        string.IsNullOrWhiteSpace(row.DefaultModel) ? null : row.DefaultModel,
                        row.Enabled);
                }
                else if (row.TenantId is Guid tid)
                {
                    if (!string.IsNullOrWhiteSpace(row.DefaultModel))
                    {
                        tenants[(tid, row.ProviderKey)] = row.DefaultModel!;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(row.DefaultModel))
                {
                    if (!users.TryGetValue(row.ProviderKey, out var existing)
                        || row.UpdatedAt > existing.UpdatedAt)
                    {
                        users[row.ProviderKey] = (row.DefaultModel!, row.UpdatedAt);
                    }
                }
            }

            return new Snapshot(
                platform,
                tenants,
                users.ToDictionary(
                    kv => kv.Key, kv => kv.Value.Model, StringComparer.OrdinalIgnoreCase));
        }
    }
}
