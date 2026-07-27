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

        if (model is not null) row.DefaultModel = model;
        if (enabled is not null) row.Enabled = enabled.Value;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = updatedBy;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row;
    }

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
