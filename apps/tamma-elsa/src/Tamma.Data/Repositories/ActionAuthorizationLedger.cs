using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <inheritdoc />
/// <remarks>
/// CP context directly (<see cref="IDbContextFactory{TContext}"/>) — see
/// <see cref="EfActionAssignmentRepository"/>'s remarks; the same residency
/// rules apply. The default TTL matches Story 43-5 AC4 (+24h,
/// config <c>Tamma:Governance:AuthorizationTtlHours</c> resolved by the
/// caller in Tamma.Api — Tamma.Data carries no IConfiguration dependency).
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

        var open = await db.ActionAuthorizations
            .FirstOrDefaultAsync(
                a => a.TenantId == tenantId && a.UserId == userId
                    && a.CorrelationId == correlationId
                    && a.TargetKind == targetKind && a.TargetKey == targetKey
                    && (a.State == "pending" || a.State == "granted"),
                ct)
            .ConfigureAwait(false);
        if (open is not null)
        {
            return open; // idempotent: one open request per (principal, run, target)
        }

        var now = DateTime.UtcNow;
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
            // A concurrent request won the partial unique index — return the
            // winner's open row.
            db.Entry(row).State = EntityState.Detached;
            var winner = await db.ActionAuthorizations
                .FirstOrDefaultAsync(
                    a => a.TenantId == tenantId && a.UserId == userId
                        && a.CorrelationId == correlationId
                        && a.TargetKind == targetKind && a.TargetKey == targetKey
                        && (a.State == "pending" || a.State == "granted"),
                    ct)
                .ConfigureAwait(false);
            return winner ?? throw new InvalidOperationException(
                "Authorization request lost a unique-index race but the winning row is gone.");
        }
    }

    /// <inheritdoc />
    public async Task<ActionAuthorization?> TryConsumeAsync(
        Guid? tenantId,
        Guid? userId,
        string correlationId,
        string actionKeyWire,
        string groupWire,
        CancellationToken ct = default)
    {
        if (tenantId is not null && userId is not null)
        {
            throw new ArgumentException("At most one principal key may be set.");
        }

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        // An action grant covers itself; a group grant covers every member of
        // that group. Expired or consumed grants do not cover (AC4).
        var grant = await db.ActionAuthorizations
            .Where(a => a.TenantId == tenantId && a.UserId == userId
                && a.CorrelationId == correlationId
                && a.State == "granted"
                && a.ConsumedAtUtc == null
                && (a.ExpiresAtUtc == null || a.ExpiresAtUtc > now)
                && ((a.TargetKind == "action" && a.TargetKey == actionKeyWire)
                    || (a.TargetKind == "group" && a.TargetKey == groupWire)))
            .OrderBy(a => a.TargetKind) // deterministic: an action grant wins over a group grant
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (grant is null) return null;

        grant.ConsumedAtUtc = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return grant;
    }

    /// <inheritdoc />
    public async Task<ActionAuthorization?> DecideAsync(
        Guid id, bool granted, Guid decidedByUserId, string? reason,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.ActionAuthorizations
            .FirstOrDefaultAsync(a => a.Id == id, ct)
            .ConfigureAwait(false);
        var now = DateTime.UtcNow;
        if (row is null
            || row.State != "pending"
            || (row.ExpiresAtUtc is DateTime exp && exp <= now))
        {
            return null; // missing, already decided, or expired → the caller 409s
        }

        row.State = granted ? "granted" : "denied";
        row.DecidedAtUtc = now;
        row.DecidedByUserId = decidedByUserId;
        if (reason is not null) row.Reason = reason;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505";
}
