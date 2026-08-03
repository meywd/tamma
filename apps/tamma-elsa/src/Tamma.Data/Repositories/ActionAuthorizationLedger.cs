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
        // that group. Expired or consumed grants do not cover (AC4).
        var candidateIds = await db.ActionAuthorizations
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.UserId == userId
                && a.CorrelationId == correlationId
                && a.State == "granted"
                && a.ConsumedAtUtc == null
                && (a.ExpiresAtUtc == null || a.ExpiresAtUtc > now)
                && ((a.TargetKind == "action" && a.TargetKey == actionKeyWire)
                    || (groupWire != null && a.TargetKind == "group" && a.TargetKey == groupWire)))
            .OrderBy(a => a.TargetKind) // deterministic: an action grant wins over a group grant
            .Select(a => a.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var id in candidateIds)
        {
            // F1 CAS: only the caller whose conditional UPDATE affects the row
            // consumes it — a concurrent consumer of the same grant loses
            // (affected == 0) and falls through to the next candidate / null.
            var affected = await db.ActionAuthorizations
                .Where(a => a.Id == id
                    && a.State == "granted"
                    && a.ConsumedAtUtc == null
                    && (a.ExpiresAtUtc == null || a.ExpiresAtUtc > now))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ConsumedAtUtc, now), ct)
                .ConfigureAwait(false);
            if (affected == 1)
            {
                return await db.ActionAuthorizations
                    .AsNoTracking()
                    .FirstAsync(a => a.Id == id, ct)
                    .ConfigureAwait(false);
            }
        }

        return null;
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
