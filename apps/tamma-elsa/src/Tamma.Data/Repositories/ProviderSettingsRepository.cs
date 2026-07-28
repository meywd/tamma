using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 46-1 — CRUD seam for the CP-resident <c>provider_settings</c> table.
/// Kept deliberately tiny: the caching / precedence / mode-awareness all live
/// in <c>Tamma.Api</c>'s <c>ProviderSettingsStore</c>; this interface exists so
/// the store's snapshot logic is unit-testable without a database (fake this)
/// and so the DB access rides the canonical
/// <see cref="IDbContextFactory{TContext}"/> seam from a singleton.
/// </summary>
public interface IProviderSettingsRepository
{
    /// <summary>All rows — the store rebuilds its whole snapshot from this
    /// (15 providers × principals-that-ever-saved; tiny by construction).</summary>
    Task<IReadOnlyList<ProviderSetting>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Upsert the row for <c>(tenantId, userId, providerKey)</c> (platform row
    /// when both ids are null). <paramref name="model"/> null = leave the
    /// stored model unchanged; <paramref name="enabled"/> null = leave the
    /// stored flag unchanged. Returns the persisted row.
    /// </summary>
    Task<ProviderSetting> UpsertAsync(
        Guid? tenantId,
        Guid? userId,
        string providerKey,
        string? model,
        bool? enabled,
        Guid? updatedBy,
        CancellationToken ct = default);

    /// <summary>Delete the row for <c>(tenantId, userId, providerKey)</c>.
    /// Returns false when no row existed.</summary>
    Task<bool> DeleteAsync(
        Guid? tenantId, Guid? userId, string providerKey, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class EfProviderSettingsRepository : IProviderSettingsRepository
{
    private readonly IDbContextFactory<ControlPlaneDbContext> _factory;

    public EfProviderSettingsRepository(IDbContextFactory<ControlPlaneDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProviderSetting>> LoadAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ProviderSettings.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Review F8 — read-then-insert races: two concurrent PUTs for the same
    /// <c>(tenantId, userId, providerKey)</c> can both miss the read and both
    /// insert, and the <c>UNIQUE NULLS NOT DISTINCT</c> index turns the loser
    /// into a Postgres 23505 (previously an unhandled 500). The unique
    /// violation is caught and retried ONCE as an update of the row the
    /// winning writer inserted — converging on last-write-wins, the same
    /// outcome as if the requests had arrived a moment apart.
    /// </remarks>
    public async Task<ProviderSetting> UpsertAsync(
        Guid? tenantId,
        Guid? userId,
        string providerKey,
        string? model,
        bool? enabled,
        Guid? updatedBy,
        CancellationToken ct = default)
    {
        if (tenantId is not null && userId is not null)
        {
            throw new ArgumentException(
                "A provider-settings row is keyed by AT MOST one principal " +
                "(tenantId XOR userId; both null = platform row).");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.ProviderSettings
            .FirstOrDefaultAsync(
                s => s.TenantId == tenantId && s.UserId == userId && s.ProviderKey == providerKey,
                ct)
            .ConfigureAwait(false);

        var inserting = row is null;
        if (row is null)
        {
            row = new ProviderSetting
            {
                TenantId = tenantId,
                UserId = userId,
                Scope = tenantId is null && userId is null ? "platform" : "principal",
                ProviderKey = providerKey,
            };
            db.ProviderSettings.Add(row);
        }

        Apply(row, model, enabled, updatedBy);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return row;
        }
        catch (DbUpdateException ex) when (inserting && IsUniqueViolation(ex))
        {
            // F8 — a concurrent writer inserted the same key between our read
            // and our insert. Retry once as an update of THAT row.
            db.Entry(row).State = EntityState.Detached;
            var existing = await db.ProviderSettings
                .FirstOrDefaultAsync(
                    s => s.TenantId == tenantId
                        && s.UserId == userId
                        && s.ProviderKey == providerKey,
                    ct)
                .ConfigureAwait(false);
            if (existing is null)
            {
                // The competing row vanished again (insert+delete race) —
                // genuinely unresolvable in one retry; surface the original.
                throw;
            }

            Apply(existing, model, enabled, updatedBy);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return existing;
        }
    }

    private static void Apply(ProviderSetting row, string? model, bool? enabled, Guid? updatedBy)
    {
        if (model is not null) row.DefaultModel = model;
        if (enabled is not null) row.Enabled = enabled.Value;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = updatedBy;
    }

    /// <summary>Postgres unique-index violation (SQLSTATE 23505) — the same
    /// detection shape as <c>ProviderCredentialEndpoints.IsDuplicate</c>.</summary>
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505";

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        Guid? tenantId, Guid? userId, string providerKey, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.ProviderSettings
            .FirstOrDefaultAsync(
                s => s.TenantId == tenantId && s.UserId == userId && s.ProviderKey == providerKey,
                ct)
            .ConfigureAwait(false);
        if (row is null) return false;

        db.ProviderSettings.Remove(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}
