using Tamma.Core.Actions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Actions;

/// <summary>
/// Story 43-5 (AC12, amended) — the in-process snapshot of the whole
/// <c>action_assignments</c> table that every gate read rides, exposed as SYNC
/// projections so the Seam B tool-loop gate (a sync per-tool-call path) never
/// blocks on a database.
///
/// <para><b>Lifetime/staleness (deviation from the story's scoped-provider
/// sketch, by coordinator direction):</b> this store reuses the HARDENED
/// <see cref="Providers.ProviderSettingsStore"/> patterns verbatim — a
/// singleton <c>volatile</c> whole-snapshot swapped atomically, a lazy
/// 60-second TTL refresh (readers never block), a cold-start priming hosted
/// service (<see cref="GovernancePolicySnapshotPrimingService"/>) so the first
/// requests after a restart honour persisted policy — and, since the 43-5 F6
/// close (2026-07-30), an explicit <see cref="GovernancePolicySnapshot.IsAuthoritative"/>
/// bit so that a store which has NEVER loaded is distinguishable from one that
/// loaded an empty table (the former makes every gate fail CLOSED; the latter is
/// the ordinary zero-config deployment), MONOTONIC version-gated
/// installs (a slow load that began before a write can never swap the
/// pre-write snapshot back in), and invalidate-on-write (the policy endpoints
/// call <see cref="RefreshAsync"/> after every repository write, so the
/// writing instance is consistent immediately; other instances converge
/// within <see cref="RefreshTtl"/>). Consequence, stated honestly: in a
/// multi-instance deployment a policy change may take up to 60 s to be
/// honoured by other instances — the same bound provider settings already
/// live with. Story AC12's one-read-per-request property holds a fortiori:
/// all gate calls within a request (and within the TTL) share ONE read.</para>
/// </summary>
public interface IGovernancePolicySnapshotProvider
{
    /// <summary>The per-principal projection the pure evaluator consumes:
    /// platform rows + exactly this principal's rows (a platform-only
    /// principal gets platform rows and nothing else).</summary>
    GovernancePolicySnapshot GetSnapshot(GovernancePrincipal principal);

    /// <summary>
    /// SYNC ambient projection for the Seam B tool-loop gate, which has the
    /// scoped <c>ITenantContext</c> but no resolved user id: SaaS uses the
    /// tenant's rows; single-user uses ALL user-keyed rows collapsed
    /// last-write-wins per target (the ProviderSettingsStore F7 posture — a
    /// genuine single-user install has exactly one user id; more than one is
    /// warned at refresh time).
    /// </summary>
    GovernancePolicySnapshot GetSnapshotForAmbient(Guid? tenantId);

    /// <summary>Force a snapshot rebuild (write paths call this; the priming
    /// service awaits it at startup).</summary>
    Task RefreshAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class GovernancePolicySnapshotStore : IGovernancePolicySnapshotProvider
{
    /// <summary>Lazy-refresh TTL — 60 s, matching
    /// <see cref="Providers.ProviderSettingsStore.RefreshTtl"/> (the staleness
    /// bound operators already live with). Pinned by
    /// <c>GovernancePolicySnapshotStoreTests</c>.</summary>
    public static readonly TimeSpan RefreshTtl = TimeSpan.FromSeconds(60);

    private readonly IActionAssignmentRepository? _repository;
    private readonly PromptStore.ITammaModeProvider _mode;
    private readonly ILogger<GovernancePolicySnapshotStore> _logger;
    private readonly TimeProvider _timeProvider;

    private volatile FullSnapshot _snapshot = FullSnapshot.Empty;

    /// <summary>
    /// 0 until a load has SUCCEEDED at least once (43-5 F6 close, 2026-07-30).
    /// Every projection carries it as
    /// <see cref="GovernancePolicySnapshot.IsAuthoritative"/>, so "the table is
    /// empty" and "the table has never been read" stop being the same value on
    /// the wire. A store with no repository is authoritative from birth: with no
    /// control-plane database there are no rows to miss.
    /// </summary>
    private int _everLoaded;

    private long _loadedAtTicks = DateTimeOffset.MinValue.UtcTicks;
    private int _refreshing; // 0|1 — single-flight guard for the lazy refresh

    // ProviderSettingsStore review F2 — monotonic load versioning: every load
    // takes a ticket BEFORE its DB read begins; installs are gated under
    // _installLock so a stale load can never clobber a newer install.
    private long _loadVersion;
    private long _installedVersion;
    private readonly object _installLock = new();

    public GovernancePolicySnapshotStore(
        IActionAssignmentRepository? repository,
        PromptStore.ITammaModeProvider mode,
        ILogger<GovernancePolicySnapshotStore> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _mode = mode;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (repository is null)
        {
            // No control-plane repository — the empty snapshot IS the truth, and
            // it is authoritative (nothing could ever be read).
            _everLoaded = 1;
        }
    }

    /// <summary>
    /// FALSE until a load has succeeded (F6). Exposed for the priming service's
    /// fail-loud logging and for tests; the evaluator consumes it through
    /// <see cref="GovernancePolicySnapshot.IsAuthoritative"/>.
    /// </summary>
    public bool IsAuthoritative => Volatile.Read(ref _everLoaded) == 1;

    /// <inheritdoc />
    public GovernancePolicySnapshot GetSnapshot(GovernancePrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var s = CurrentSnapshot();

        if (principal.TenantId is Guid tid)
        {
            return Project(s, s.TenantRows.GetValueOrDefault(tid));
        }
        if (principal.UserId is Guid uid)
        {
            return Project(s, s.UserRows.GetValueOrDefault(uid));
        }
        return Project(s, principalRows: null); // platform-only: ceiling + shipped defaults
    }

    /// <inheritdoc />
    public GovernancePolicySnapshot GetSnapshotForAmbient(Guid? tenantId)
    {
        var s = CurrentSnapshot();
        if (_mode.Mode == PromptStore.TammaMode.SingleUser)
        {
            return Project(s, s.CollapsedUserRows);
        }
        return tenantId is Guid tid
            ? Project(s, s.TenantRows.GetValueOrDefault(tid))
            : Project(s, principalRows: null);
    }

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_repository is null)
        {
            return; // no CP DB — the empty snapshot IS the truth (shipped defaults)
        }

        var version = Interlocked.Increment(ref _loadVersion);
        try
        {
            var rows = await _repository.LoadAllAsync(ct).ConfigureAwait(false);
            WarnOnCollapsedSingleUserRows(rows);
            var snapshot = FullSnapshot.Build(rows);
            lock (_installLock)
            {
                if (version >= _installedVersion)
                {
                    _installedVersion = version;
                    _snapshot = snapshot;
                    Volatile.Write(ref _loadedAtTicks, _timeProvider.GetUtcNow().UtcTicks);
                    // A successful read is what makes the snapshot authoritative
                    // (F6) — never set on the failure path below.
                    Volatile.Write(ref _everLoaded, 1);
                }
                // else: a newer load already installed — discard this one.
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A refresh failure AFTER a successful load keeps serving the last
            // good snapshot (a policy-store blip must not break a gate read).
            // A refresh failure BEFORE any successful load leaves the store
            // NON-authoritative, and every gate that reads it fails CLOSED
            // (F6) — which is an error, not a warning: it is an outage of the
            // governance surface, and until 43-5 F6 it was silent.
            if (IsAuthoritative)
            {
                _logger.LogWarning(ex,
                    "action_assignments snapshot refresh failed; serving the previous snapshot.");
            }
            else
            {
                _logger.LogError(ex,
                    "action_assignments snapshot has NEVER loaded successfully; the governance "
                    + "snapshot is NOT authoritative and every autonomy-gate evaluation will fail "
                    + "CLOSED (requires-human / denied) until a refresh succeeds.");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────

    private void WarnOnCollapsedSingleUserRows(IReadOnlyList<ActionAssignment> rows)
    {
        if (_mode.Mode != PromptStore.TammaMode.SingleUser)
        {
            return;
        }
        var distinctUsers = rows
            .Where(r => r.UserId is not null)
            .Select(r => r.UserId!.Value)
            .Distinct()
            .Count();
        if (distinctUsers > 1)
        {
            _logger.LogWarning(
                "action_assignments holds rows for {DistinctUserCount} distinct user ids in "
                + "single-user mode; the ambient projection collapses them last-write-wins per "
                + "target, so all but the most recently updated user's rows are shadowed.",
                distinctUsers);
        }
    }

    private FullSnapshot CurrentSnapshot()
    {
        if (_repository is not null
            && _timeProvider.GetUtcNow().UtcTicks - Volatile.Read(ref _loadedAtTicks)
                > RefreshTtl.Ticks
            && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                try { await RefreshAsync(CancellationToken.None).ConfigureAwait(false); }
                finally { Interlocked.Exchange(ref _refreshing, 0); }
            });
        }
        return _snapshot;
    }

    private GovernancePolicySnapshot Project(FullSnapshot s, PrincipalRows? principalRows) =>
        new(
            s.PlatformRows.ActionRows,
            s.PlatformRows.GroupRows,
            principalRows?.ActionRows ?? PrincipalRows.None.ActionRows,
            principalRows?.GroupRows ?? PrincipalRows.None.GroupRows)
        {
            // F6 — an unprimed store's empty rows are IGNORANCE, not policy.
            IsAuthoritative = this.IsAuthoritative,
        };

    /// <summary>One principal's rows split by target kind.</summary>
    private sealed record PrincipalRows(
        IReadOnlyDictionary<string, ActionAssignmentValue> ActionRows,
        IReadOnlyDictionary<string, ActionAssignmentValue> GroupRows)
    {
        public static PrincipalRows None { get; } = new(
            new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal),
            new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal));
    }

    private sealed class FullSnapshot
    {
        public static readonly FullSnapshot Empty = new(
            PrincipalRows.None,
            new Dictionary<Guid, PrincipalRows>(),
            new Dictionary<Guid, PrincipalRows>(),
            PrincipalRows.None);

        private FullSnapshot(
            PrincipalRows platformRows,
            IReadOnlyDictionary<Guid, PrincipalRows> tenantRows,
            IReadOnlyDictionary<Guid, PrincipalRows> userRows,
            PrincipalRows collapsedUserRows)
        {
            PlatformRows = platformRows;
            TenantRows = tenantRows;
            UserRows = userRows;
            CollapsedUserRows = collapsedUserRows;
        }

        public PrincipalRows PlatformRows { get; }
        public IReadOnlyDictionary<Guid, PrincipalRows> TenantRows { get; }
        public IReadOnlyDictionary<Guid, PrincipalRows> UserRows { get; }

        /// <summary>All user-keyed rows collapsed for the single-user ambient
        /// projection (last write wins per target; refresh warns when more
        /// than one user id contributes).</summary>
        public PrincipalRows CollapsedUserRows { get; }

        public static FullSnapshot Build(IReadOnlyList<ActionAssignment> rows)
        {
            var platformActions = new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal);
            var platformGroups = new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal);
            var tenants = new Dictionary<Guid, (Dictionary<string, ActionAssignmentValue> A, Dictionary<string, ActionAssignmentValue> G)>();
            var users = new Dictionary<Guid, (Dictionary<string, ActionAssignmentValue> A, Dictionary<string, ActionAssignmentValue> G)>();
            var collapsedA = new Dictionary<string, (ActionAssignmentValue V, DateTime At)>(StringComparer.Ordinal);
            var collapsedG = new Dictionary<string, (ActionAssignmentValue V, DateTime At)>(StringComparer.Ordinal);

            foreach (var row in rows)
            {
                // 'mode' rows are schema-admitted but not consumed by the v1
                // ladder; skip them here so they cannot shadow a real target.
                var isAction = string.Equals(row.TargetKind, "action", StringComparison.Ordinal);
                var isGroup = string.Equals(row.TargetKind, "group", StringComparison.Ordinal);
                if (!isAction && !isGroup) continue;

                var value = new ActionAssignmentValue(
                    row.MinAutonomy, row.Enforce, row.Enabled, row.AllowedRoles);

                if (row.TenantId is Guid tid)
                {
                    if (!tenants.TryGetValue(tid, out var t))
                    {
                        t = (new(StringComparer.Ordinal), new(StringComparer.Ordinal));
                        tenants[tid] = t;
                    }
                    (isAction ? t.A : t.G)[row.TargetKey] = value;
                }
                else if (row.UserId is Guid uid)
                {
                    if (!users.TryGetValue(uid, out var u))
                    {
                        u = (new(StringComparer.Ordinal), new(StringComparer.Ordinal));
                        users[uid] = u;
                    }
                    (isAction ? u.A : u.G)[row.TargetKey] = value;

                    var collapsed = isAction ? collapsedA : collapsedG;
                    if (!collapsed.TryGetValue(row.TargetKey, out var existing)
                        || row.UpdatedAt > existing.At)
                    {
                        collapsed[row.TargetKey] = (value, row.UpdatedAt);
                    }
                }
                else
                {
                    (isAction ? platformActions : platformGroups)[row.TargetKey] = value;
                }
            }

            return new FullSnapshot(
                new PrincipalRows(platformActions, platformGroups),
                tenants.ToDictionary(
                    kv => kv.Key, kv => new PrincipalRows(kv.Value.A, kv.Value.G)),
                users.ToDictionary(
                    kv => kv.Key, kv => new PrincipalRows(kv.Value.A, kv.Value.G)),
                new PrincipalRows(
                    collapsedA.ToDictionary(kv => kv.Key, kv => kv.Value.V, StringComparer.Ordinal),
                    collapsedG.ToDictionary(kv => kv.Key, kv => kv.Value.V, StringComparer.Ordinal)));
        }
    }
}

/// <summary>
/// Cold-start priming (the <see cref="Providers.ProviderSettingsStorePrimingService"/>
/// review-F1 posture): without this, the snapshot starts empty and the sync
/// gate reads never block, so the first requests after every restart would
/// apply shipped defaults instead of persisted policy until the first lazy TTL
/// refresh landed — for a SAFETY store that window is an admin tightening
/// silently not applied. Fail-soft: a briefly-unavailable DB must not crash
/// the host; the lazy TTL refresh remains the fallback.
///
/// <para><b>Fail-soft is no longer fail-OPEN (43-5 F6, 2026-07-30).</b> Priming
/// still never crashes the host, but an unprimed store now serves a
/// NON-authoritative snapshot, and every autonomy-gate evaluation over it fails
/// CLOSED (requires-human, or denied where no human wait exists) with
/// <c>policy-snapshot-unavailable</c> provenance. The host coming up during a
/// control-plane outage therefore withholds automation instead of silently
/// discarding every admin tightening.</para>
/// </summary>
public sealed class GovernancePolicySnapshotPrimingService : IHostedService
{
    private readonly IGovernancePolicySnapshotProvider _store;
    private readonly ILogger<GovernancePolicySnapshotPrimingService> _logger;

    public GovernancePolicySnapshotPrimingService(
        IGovernancePolicySnapshotProvider store,
        ILogger<GovernancePolicySnapshotPrimingService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _store.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "action_assignments startup priming failed; the governance snapshot is NOT "
                + "authoritative, so every autonomy-gate evaluation FAILS CLOSED "
                + "(requires-human / denied) until the lazy TTL refresh succeeds. This is an "
                + "outage of the governance surface, not a benign default (43-5 F6).");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
