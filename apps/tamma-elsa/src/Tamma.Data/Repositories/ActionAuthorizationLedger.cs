using Microsoft.EntityFrameworkCore;
using Tamma.Core.Actions;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <inheritdoc />
/// <remarks>
/// <para>CP context directly (<see cref="IDbContextFactory{TContext}"/>) — see
/// <see cref="EfActionAssignmentRepository"/>'s remarks; the same residency
/// rules apply. The default TTL matches Story 43-5 AC4 (+24h,
/// config <c>Tamma:Governance:AuthorizationTtlHours</c> resolved by the
/// caller in Tamma.Api — Tamma.Data carries no IConfiguration dependency).</para>
///
/// <para><b>Every state transition is a conditional single-statement UPDATE
/// (CAS)</b> — the <c>ScheduledTriggerRepository.TryClaimManualFireForDispatchAsync</c>
/// posture (adversarial review F1, 2026-07-29): a load-then-SaveChanges
/// transition is check-then-write, and two contexts that both read before
/// either writes would double-consume a grant (or let a concurrent grant and
/// deny both report success, last write winning). Postgres arbitrates via the
/// UPDATE's WHERE predicate: affected-rows 1 = this caller owns the
/// transition, 0 = it lost. Pinned by the race tests in
/// <c>ActionAssignmentStorageTests</c>.</para>
/// </remarks>
public sealed class EfActionAuthorizationLedger : IActionAuthorizationLedger
{
    /// <summary>AC4's default grant TTL.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    private readonly IDbContextFactory<ControlPlaneDbContext> _factory;

    public EfActionAuthorizationLedger(IDbContextFactory<ControlPlaneDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc />
    public async Task<ActionAuthorization> RequestAsync(
        Guid? tenantId,
        Guid? userId,
        string correlationId,
        string targetKind,
        string targetKey,
        string? reason,
        int? autonomyLevelAtRequest,
        TimeSpan? ttl = null,
        string scope = "single-use",
        CancellationToken ct = default)
    {
        if (tenantId is not null && userId is not null)
        {
            throw new ArgumentException("At most one principal key may be set.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        var wantsStanding = string.Equals(scope, "correlation-standing", StringComparison.Ordinal);

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Bounded retry: each iteration either returns a LIVE open row, or
        // closes a time-expired one and races to insert a fresh row. Losing
        // the unique-index race re-reads the winner on the next pass.
        for (var attempt = 0; ; attempt++)
        {
            var now = DateTime.UtcNow;
            var open = await db.ActionAuthorizations
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    a => a.TenantId == tenantId && a.UserId == userId
                        && a.CorrelationId == correlationId
                        && a.TargetKind == targetKind && a.TargetKey == targetKey
                        && (a.State == "pending" || a.State == "granted"),
                    ct)
                .ConfigureAwait(false);
            if (open is not null)
            {
                if (open.ExpiresAtUtc is not DateTime exp || exp > now)
                {
                    // Review 5c — a STANDING request over a live weaker row for
                    // the same key ELEVATES it, so a tool-loop "cover the run"
                    // ask cannot be left riding a coexisting single-use grant (a
                    // Seam-C 409 that landed first, or a repeat). A single-use
                    // request never downgrades a standing row (idempotent return).
                    if (wantsStanding
                        && !string.Equals(open.Scope, "correlation-standing", StringComparison.Ordinal))
                    {
                        var elevated = await db.ActionAuthorizations
                            .Where(a => a.Id == open.Id
                                && a.Scope != "correlation-standing"
                                && (a.State == "pending" || a.State == "granted")
                                && (a.ExpiresAtUtc == null || a.ExpiresAtUtc > now))
                            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Scope, "correlation-standing"), ct)
                            .ConfigureAwait(false);
                        if (elevated == 1)
                        {
                            return await db.ActionAuthorizations
                                .AsNoTracking()
                                .FirstAsync(a => a.Id == open.Id, ct)
                                .ConfigureAwait(false);
                        }
                        // Lost the race (decided/consumed/expired concurrently) — re-loop.
                        if (attempt >= 2)
                        {
                            throw new InvalidOperationException(
                                "Authorization request repeatedly lost the scope-elevation race.");
                        }
                        continue;
                    }

                    return open; // idempotent: one LIVE open request per (principal, run, target)
                }

                // Adversarial review F3 (2026-07-29): a time-expired open row
                // would otherwise deadlock this key forever — the partial
                // unique index (State IN pending/granted) blocks a fresh row,
                // this method idempotently returned the stale one, and
                // DecideAsync refuses it. Close it with a CAS (WHERE still
                // open AND still past expiry) so the index frees the slot;
                // whether this caller or a concurrent one wins the CAS, the
                // insert below (re)arbitrates via the unique index.
                await db.ActionAuthorizations
                    .Where(a => a.Id == open.Id
                        && (a.State == "pending" || a.State == "granted")
                        && a.ExpiresAtUtc != null && a.ExpiresAtUtc <= now)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, "expired"), ct)
                    .ConfigureAwait(false);
            }

            var row = new ActionAuthorization
            {
                TenantId = tenantId,
                UserId = userId,
                CorrelationId = correlationId,
                TargetKind = targetKind,
                TargetKey = targetKey,
                State = "pending",
                // Review 5c — the scope the row carries once granted; DecideAsync
                // preserves it, so a standing pending ask becomes a standing grant
                // without a second decision.
                Scope = scope,
                RequestedAtUtc = now,
                ExpiresAtUtc = now + (ttl ?? DefaultTtl),
                Reason = reason,
                AutonomyLevelAtRequest = autonomyLevelAtRequest,
            };
            db.ActionAuthorizations.Add(row);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return row;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // A concurrent request won the partial unique index; loop to
                // return the winner's open row (or expire it in turn).
                db.Entry(row).State = EntityState.Detached;
                if (attempt >= 2)
                {
                    throw new InvalidOperationException(
                        "Authorization request repeatedly lost the open-row unique-index race.");
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<ActionAuthorization?> TryConsumeAsync(
        Guid? tenantId,
        Guid? userId,
        string correlationId,
        string actionKeyWire,
        CancellationToken ct = default)
    {
        if (tenantId is not null && userId is not null)
        {
            throw new ArgumentException("At most one principal key may be set.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKeyWire);

        // Adversarial review F2 (2026-07-29): the covering group is derived
        // from the CATALOG, never supplied by the caller — a group grant must
        // only cover actual members of that group, and trusting a
        // caller-supplied group wire let a deploy-control grant be consumed
        // for tool:shell_execute. An uncatalogued action key has no derivable
        // group, so only an exact action-scoped grant can cover it.
        string? groupWire = null;
        if (ActionKey.TryParse(actionKeyWire, out var key)
            && ActionCatalog.TryGet(key, out var descriptor) && descriptor is not null)
        {
            groupWire = descriptor.Group.ToWire();
        }

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        // An action grant covers itself; a group grant covers every member of
        // that group. Expired grants do not cover (AC4). Story 43-14: a
        // single-use grant that is already consumed does not cover; a
        // correlation-standing grant is never consumed (ConsumedAtUtc stays
        // NULL) so it keeps covering — the predicate admits it regardless.
        var candidates = await db.ActionAuthorizations
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.UserId == userId
                && a.CorrelationId == correlationId
                && a.State == "granted"
                && (a.ExpiresAtUtc == null || a.ExpiresAtUtc > now)
                && (a.Scope == "correlation-standing" || a.ConsumedAtUtc == null)
                && ((a.TargetKind == "action" && a.TargetKey == actionKeyWire)
                    || (groupWire != null && a.TargetKind == "group" && a.TargetKey == groupWire)))
            // Story 43-14 D1 — standing before single-use ("correlation-standing"
            // < "single-use" ordinally) so a repeat ask rides the standing grant
            // instead of burning a coexisting single-use one; then an action
            // grant wins over a group grant (deterministic).
            .OrderBy(a => a.Scope)
            .ThenBy(a => a.TargetKind)
            .Select(a => new { a.Id, a.Scope })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            // Story 43-14 D1 — a correlation-standing grant is SATISFIED without
            // a write: return it as covering, ConsumedAtUtc untouched. It covers
            // every ask in its correlation and dies only by expiry. (No CAS is
            // needed: the only mutations a granted row can undergo are
            // single-use consumption and time expiry, and expiry is checked in
            // the SELECT predicate above.)
            if (string.Equals(candidate.Scope, "correlation-standing", StringComparison.Ordinal))
            {
                return await db.ActionAuthorizations
                    .AsNoTracking()
                    .FirstAsync(a => a.Id == candidate.Id, ct)
                    .ConfigureAwait(false);
            }

            // Single-use — F1 CAS: only the caller whose conditional UPDATE
            // affects the row consumes it; a concurrent consumer of the same
            // grant loses (affected == 0) and falls through to the next
            // candidate / null.
            var affected = await db.ActionAuthorizations
                .Where(a => a.Id == candidate.Id
                    && a.State == "granted"
                    && a.ConsumedAtUtc == null
                    && (a.ExpiresAtUtc == null || a.ExpiresAtUtc > now))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ConsumedAtUtc, now), ct)
                .ConfigureAwait(false);
            if (affected == 1)
            {
                return await db.ActionAuthorizations
                    .AsNoTracking()
                    .FirstAsync(a => a.Id == candidate.Id, ct)
                    .ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<ActionAuthorization> MintStandingGrantAsync(
        Guid? tenantId,
        Guid? userId,
        string correlationId,
        string targetKind,
        string targetKey,
        Guid decidedByUserId,
        string? reason,
        TimeSpan? ttl = null,
        CancellationToken ct = default)
    {
        if (tenantId is not null && userId is not null)
        {
            throw new ArgumentException("At most one principal key may be set.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Bounded retry: each pass tries to (a) upgrade an existing OPEN row for
        // this (principal, correlation, target) to a granted correlation-standing
        // grant, or (b) insert a fresh one. A concurrent minter/requester that
        // wins the open-row unique index is re-read on the next pass.
        for (var attempt = 0; ; attempt++)
        {
            var now = DateTime.UtcNow;
            var open = await db.ActionAuthorizations
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    a => a.TenantId == tenantId && a.UserId == userId
                        && a.CorrelationId == correlationId
                        && a.TargetKind == targetKind && a.TargetKey == targetKey
                        && (a.State == "pending" || a.State == "granted"),
                    ct)
                .ConfigureAwait(false);

            if (open is not null)
            {
                var live = open.ExpiresAtUtc is not DateTime exp || exp > now;
                if (live && string.Equals(open.State, "granted", StringComparison.Ordinal)
                    && string.Equals(open.Scope, "correlation-standing", StringComparison.Ordinal))
                {
                    return open; // idempotent — a second mint in this correlation is a no-op
                }

                if (live && string.Equals(open.State, "pending", StringComparison.Ordinal))
                {
                    // Upgrade the pending row (e.g. one Seam C 409 minted) to a
                    // granted correlation-standing grant with ONE conditional
                    // UPDATE — the DecideAsync CAS shape plus the scope set.
                    var upgraded = await db.ActionAuthorizations
                        .Where(a => a.Id == open.Id && a.State == "pending"
                            && (a.ExpiresAtUtc == null || a.ExpiresAtUtc > now))
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(a => a.State, "granted")
                            .SetProperty(a => a.Scope, "correlation-standing")
                            .SetProperty(a => a.DecidedAtUtc, now)
                            .SetProperty(a => a.DecidedByUserId, decidedByUserId)
                            .SetProperty(a => a.ExpiresAtUtc, now + (ttl ?? DefaultTtl))
                            .SetProperty(a => a.Reason, a => reason ?? a.Reason), ct)
                        .ConfigureAwait(false);
                    if (upgraded == 1)
                    {
                        return await db.ActionAuthorizations
                            .AsNoTracking()
                            .FirstAsync(a => a.Id == open.Id, ct)
                            .ConfigureAwait(false);
                    }
                    // Lost the race (decided/expired concurrently) — re-loop.
                    if (attempt >= 2)
                    {
                        throw new InvalidOperationException(
                            "Standing-grant mint repeatedly lost the open-row transition race.");
                    }
                    continue;
                }

                if (live)
                {
                    // A live granted single-use row occupies the slot. Review 5b —
                    // the mint's INTENT is standing coverage for the whole run, so
                    // UPGRADE the row in place (one CAS) instead of returning the
                    // single-use one. Returning it as-is silently reduced the run
                    // to one-call coverage AND made EmitGrantMintedAsync record a
                    // correlation-standing grant that did not exist. Upgrading is
                    // legal: the open unique index forbids a second ROW, not an
                    // UPDATE of this one; and a standing grant covers regardless of
                    // ConsumedAtUtc, so this also revives a row whose single use
                    // was already spent.
                    var upgraded = await db.ActionAuthorizations
                        .Where(a => a.Id == open.Id
                            && a.State == "granted"
                            && a.Scope != "correlation-standing"
                            && (a.ExpiresAtUtc == null || a.ExpiresAtUtc > now))
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(a => a.Scope, "correlation-standing")
                            .SetProperty(a => a.ExpiresAtUtc, now + (ttl ?? DefaultTtl))
                            .SetProperty(a => a.Reason, a => reason ?? a.Reason), ct)
                        .ConfigureAwait(false);
                    if (upgraded == 1)
                    {
                        return await db.ActionAuthorizations
                            .AsNoTracking()
                            .FirstAsync(a => a.Id == open.Id, ct)
                            .ConfigureAwait(false);
                    }
                    // Already standing (a concurrent minter won), or expired/decided
                    // out from under us — re-loop to re-read the settled row.
                    if (attempt >= 2)
                    {
                        throw new InvalidOperationException(
                            "Standing-grant mint repeatedly lost the granted-row upgrade race.");
                    }
                    continue;
                }

                // Time-expired open row deadlocks the key (the partial unique
                // index blocks a fresh insert). Close it (CAS) so the insert
                // below can arbitrate — the same F3 posture as RequestAsync.
                await db.ActionAuthorizations
                    .Where(a => a.Id == open.Id
                        && (a.State == "pending" || a.State == "granted")
                        && a.ExpiresAtUtc != null && a.ExpiresAtUtc <= now)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, "expired"), ct)
                    .ConfigureAwait(false);
            }

            var row = new ActionAuthorization
            {
                TenantId = tenantId,
                UserId = userId,
                CorrelationId = correlationId,
                TargetKind = targetKind,
                TargetKey = targetKey,
                State = "granted",
                Scope = "correlation-standing",
                RequestedAtUtc = now,
                DecidedAtUtc = now,
                DecidedByUserId = decidedByUserId,
                ExpiresAtUtc = now + (ttl ?? DefaultTtl),
                Reason = reason,
            };
            db.ActionAuthorizations.Add(row);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return row;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                db.Entry(row).State = EntityState.Detached;
                if (attempt >= 2)
                {
                    throw new InvalidOperationException(
                        "Standing-grant mint repeatedly lost the open-row unique-index race.");
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<ActionAuthorization?> DecideAsync(
        Guid? tenantId, Guid? userId,
        Guid id, bool granted, Guid decidedByUserId, string? reason,
        CancellationToken ct = default)
    {
        if (tenantId is not null && userId is not null)
        {
            throw new ArgumentException("At most one principal key may be set.");
        }

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var state = granted ? "granted" : "denied";

        // F1 CAS: WHERE State = 'pending' (and not past expiry — F3: a
        // time-expired pending row can never be decided) makes concurrent
        // grant-vs-deny mutually exclusive: exactly one caller's UPDATE
        // affects the row; the other returns null and the caller 409s.
        //
        // F6 (2026-08-01): the principal predicate rides the SAME conditional
        // UPDATE rather than a preceding SELECT — a check-then-write ownership
        // test is exactly the shape F1 removed from this file, and a foreign
        // decide must be arbitrated by Postgres like every other transition. A
        // non-owner's UPDATE simply affects 0 rows, so it is INDISTINGUISHABLE
        // from "already decided" / "expired" / "no such row": the endpoint
        // answers all four identically and the surface is not an existence
        // oracle for another principal's correlation ids.
        var affected = await db.ActionAuthorizations
            .Where(a => a.Id == id
                && a.TenantId == tenantId
                && a.UserId == userId
                && a.State == "pending"
                && (a.ExpiresAtUtc == null || a.ExpiresAtUtc > now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.State, state)
                .SetProperty(a => a.DecidedAtUtc, now)
                .SetProperty(a => a.DecidedByUserId, decidedByUserId)
                .SetProperty(a => a.Reason, a => reason ?? a.Reason), ct)
            .ConfigureAwait(false);

        return affected == 1
            ? await db.ActionAuthorizations
                .AsNoTracking()
                .FirstAsync(a => a.Id == id, ct)
                .ConfigureAwait(false)
            : null; // missing, already decided, or expired → the caller 409s
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ActionAuthorization>> ListDecidedSinceAsync(
        Guid? tenantId, Guid? userId, DateTime sinceUtc, CancellationToken ct = default)
    {
        if (tenantId is not null && userId is not null)
        {
            throw new ArgumentException("At most one principal key may be set.");
        }

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ActionAuthorizations
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.UserId == userId
                && (a.State == "granted" || a.State == "denied")
                && a.DecidedAtUtc != null && a.DecidedAtUtc >= sinceUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505";
}
