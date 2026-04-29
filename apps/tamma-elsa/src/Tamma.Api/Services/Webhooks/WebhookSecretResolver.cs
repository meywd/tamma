using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Services.Webhooks;

/// <summary>
/// Story 31-7 — production implementation. Composes
/// <see cref="ITenantPlatformInstallationRepository"/> +
/// <see cref="IPlatformCredentialReader"/> (the same Story 29 secret
/// store seam <c>PlatformResolver</c> uses for installation tokens).
/// </summary>
public sealed class WebhookSecretResolver : IWebhookSecretResolver
{
    private readonly ITenantPlatformInstallationRepository _repo;
    private readonly IPlatformCredentialReader _credentials;

    public WebhookSecretResolver(
        ITenantPlatformInstallationRepository repo,
        IPlatformCredentialReader credentials)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(credentials);
        _repo = repo;
        _credentials = credentials;
    }

    /// <inheritdoc />
    public async Task<PlatformInstallation?> ResolveInstallationAsync(
        PlatformKind kind,
        string installationExternalId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationExternalId);

        var row = await _repo
            .GetByExternalIdAsync(PlatformKindWire.ToWire(kind), installationExternalId, ct)
            .ConfigureAwait(false);
        if (row is null) return null;

        if (!PlatformKindWire.TryParse(row.PlatformKind, out var rowKind))
        {
            // Defence in depth — the row's PlatformKind is constrained by
            // a CHECK at the DB level; an unknown value here means the
            // schema drifted. Refuse to mint an installation record.
            return null;
        }

        // Belt-and-suspenders cross-tenant safety: the repository
        // already filters by (kind, externalId), but assert the row's
        // kind matches the requested kind. Mismatched rows would
        // indicate a data-integrity bug, not an attack surface, but
        // we'd rather refuse than dispatch.
        if (rowKind != kind) return null;

        return new PlatformInstallation(
            Id: row.Id,
            TenantId: row.TenantId,
            Kind: rowKind,
            BaseUrl: row.BaseUrl,
            InstallationExternalId: row.InstallationExternalId);
    }

    /// <inheritdoc />
    public async Task<string?> ReadWebhookSecretAsync(
        PlatformInstallation installation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);

        // Re-read the row to get the WebhookSecret{Scope,Name} fields —
        // PlatformInstallation as a model doesn't carry them, and we
        // need them here. Could be plumbed through but that's a
        // wider refactor than 31-7 scope.
        var row = await _repo.GetByIdAsync(installation.Id, ct).ConfigureAwait(false);
        if (row is null) return null;

        return await ReadByRefAsync(row, ct).ConfigureAwait(false);
    }

    private async Task<string?> ReadByRefAsync(TenantPlatformInstallation row, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(row.WebhookSecretScope) ||
            string.IsNullOrEmpty(row.WebhookSecretName))
        {
            return null;
        }

        return await _credentials
            .ReadActivePlaintextAsync(
                row.WebhookSecretScope,
                row.WebhookSecretScope == "tenant" ? row.TenantId : (Guid?)null,
                row.WebhookSecretName,
                ct)
            .ConfigureAwait(false);
    }
}
