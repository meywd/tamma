using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// EF-backed <see cref="ITenantPlatformInstallationRepository"/>
/// bound to <see cref="ControlPlaneDbContext"/>. The
/// <c>tenant_platform_installations</c> table lives on the control
/// plane because routing decisions cross tenant boundaries (a webhook
/// arrives with no tenant context — only an external id — and must be
/// resolved through this table).
///
/// <para>All read methods exclude soft-deleted rows
/// (<c>DeletedAt IS NULL</c>) so callers never accidentally route
/// through a disconnected installation. The
/// <see cref="GetByTenantPrimaryAsync"/> / <see cref="GetByTenantKindAsync"/>
/// pair both prefer the row flagged
/// <see cref="TenantPlatformInstallation.IsPrimary"/>, falling back to
/// the only matching row when no primary is explicitly set — that
/// keeps the resolver path deterministic for the first-cut UI which
/// only writes one installation per tenant.</para>
/// </summary>
public sealed class TenantPlatformInstallationRepository(ControlPlaneDbContext db)
    : ITenantPlatformInstallationRepository
{
    /// <inheritdoc />
    public async Task<TenantPlatformInstallation?> GetByTenantPrimaryAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        // Prefer the explicit primary; fall back to the only-row case.
        var rows = await db.TenantPlatformInstallations
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.DeletedAt == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0) return null;

        var primary = rows.FirstOrDefault(r => r.IsPrimary);
        return primary ?? (rows.Count == 1 ? rows[0] : null);
    }

    /// <inheritdoc />
    public async Task<TenantPlatformInstallation?> GetByTenantKindAsync(
        Guid tenantId,
        string platformKind,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformKind);

        var rows = await db.TenantPlatformInstallations
            .AsNoTracking()
            .Where(r =>
                r.TenantId == tenantId
                && r.PlatformKind == platformKind
                && r.DeletedAt == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0) return null;

        var primary = rows.FirstOrDefault(r => r.IsPrimary);
        return primary ?? rows[0];
    }

    /// <inheritdoc />
    public async Task<TenantPlatformInstallation?> GetByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        return await db.TenantPlatformInstallations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.DeletedAt == null, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TenantPlatformInstallation?> GetByExternalIdAsync(
        string platformKind,
        string installationExternalId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationExternalId);

        return await db.TenantPlatformInstallations
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.PlatformKind == platformKind
                && r.InstallationExternalId == installationExternalId
                && r.DeletedAt == null,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantPlatformInstallation>> ListByTenantAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        return await db.TenantPlatformInstallations
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.DeletedAt == null)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TenantPlatformInstallation> CreateAsync(
        TenantPlatformInstallation installation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var now = DateTime.UtcNow;
        if (installation.Id == Guid.Empty)
        {
            installation.Id = Guid.NewGuid();
        }
        if (installation.CreatedAt == default)
        {
            installation.CreatedAt = now;
        }
        installation.UpdatedAt = now;

        db.TenantPlatformInstallations.Add(installation);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return installation;
    }

    /// <inheritdoc />
    public async Task<TenantPlatformInstallation> UpdateAsync(
        TenantPlatformInstallation installation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var existing = await db.TenantPlatformInstallations
            .FirstOrDefaultAsync(r => r.Id == installation.Id, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"tenant_platform_installations row with id={installation.Id} does not exist.");

        existing.PlatformKind = installation.PlatformKind;
        existing.BaseUrl = installation.BaseUrl;
        existing.InstallationExternalId = installation.InstallationExternalId;
        existing.CredentialSecretScope = installation.CredentialSecretScope;
        existing.CredentialSecretName = installation.CredentialSecretName;
        existing.WebhookSecretScope = installation.WebhookSecretScope;
        existing.WebhookSecretName = installation.WebhookSecretName;
        existing.Status = installation.Status;
        existing.IsPrimary = installation.IsPrimary;
        existing.MetadataJson = installation.MetadataJson;
        existing.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return existing;
    }

    /// <inheritdoc />
    public async Task SoftDeleteAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await db.TenantPlatformInstallations
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            .ConfigureAwait(false);
        if (existing is null || existing.DeletedAt is not null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        existing.DeletedAt = now;
        existing.UpdatedAt = now;
        existing.Status = "disconnected";
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RestoreAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await db.TenantPlatformInstallations
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"tenant_platform_installations row with id={id} does not exist.");

        if (existing.DeletedAt is null)
        {
            return;
        }

        existing.DeletedAt = null;
        existing.Status = "connected";
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
