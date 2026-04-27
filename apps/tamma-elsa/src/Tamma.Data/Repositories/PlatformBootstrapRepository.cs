using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// PF-S9 — implementation of the single-row bootstrap-claim guard.
/// Relies on the <c>PK</c> + <c>CHECK (Id = 1)</c> constraint on
/// <c>platform_bootstrap</c> to make concurrent inserts mutually
/// exclusive at the schema level.
/// </summary>
public class PlatformBootstrapRepository(ControlPlaneDbContext db)
    : IPlatformBootstrapRepository
{
    public async Task<bool> TryClaimAsync(Guid userId, CancellationToken ct = default)
    {
        // Re-check: if the row already exists we shouldn't even attempt
        // the insert (it would noisily fail with a unique-violation).
        // This is a fast-path optimisation — the actual race-safety
        // guarantee comes from the constraint catch below.
        var alreadyClaimed = await db.PlatformBootstraps
            .AnyAsync(p => p.Id == PlatformBootstrap.SentinelId, ct)
            .ConfigureAwait(false);
        if (alreadyClaimed) return false;

        var row = new PlatformBootstrap
        {
            Id = PlatformBootstrap.SentinelId,
            UserId = userId,
            ClaimedAt = DateTime.UtcNow,
        };
        db.PlatformBootstraps.Add(row);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            // Concurrent claimant won the race. Detach so the next
            // SaveChanges on this context doesn't resubmit the row.
            db.Entry(row).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<bool> HasBeenClaimedAsync(CancellationToken ct = default)
        => await db.PlatformBootstraps
            .AnyAsync(p => p.Id == PlatformBootstrap.SentinelId, ct)
            .ConfigureAwait(false);
}
