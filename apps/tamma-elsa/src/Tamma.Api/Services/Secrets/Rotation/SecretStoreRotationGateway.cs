using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Activities.SecretsRotation.Contracts;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Secrets.Rotation;

/// <summary>
/// Story 29-6 — bridges the rotation activities'
/// <see cref="ISecretRotationGateway"/> port to the existing
/// <see cref="ISecretStoreBackend"/> (29-1/2) plus a thin EF query
/// layer over <see cref="SecretsDbContext"/>. Splits responsibility:
///
/// <list type="bullet">
///   <item><description>Metadata + version-row state transitions live
///     against <see cref="SecretsDbContext"/> + the entity rows.</description></item>
///   <item><description>Plaintext put/get/delete delegates to the
///     registered <see cref="ISecretStoreBackend"/> so
///     envelope encryption / scrubbing / decryption is reused.</description></item>
/// </list>
///
/// <para>All mutations are idempotent on <c>rotationCorrelationId</c>:
/// a replayed mint checks whether a pending version row already exists
/// for the same correlation and returns its number rather than
/// creating a duplicate. Activate / retire / delete check the target
/// status before mutating so repeats are no-ops.</para>
/// </summary>
public sealed class SecretStoreRotationGateway : ISecretRotationGateway
{
    private readonly IServiceProvider _services;
    private readonly ISecretStoreBackend _backend;
    private readonly ILogger<SecretStoreRotationGateway> _logger;

    public SecretStoreRotationGateway(
        IServiceProvider services,
        ISecretStoreBackend backend,
        ILogger<SecretStoreRotationGateway> logger)
    {
        _services = services;
        _backend = backend;
        _logger = logger;
    }

    public async Task<SecretRotationSnapshot?> GetSnapshotAsync(Guid secretId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecretsDbContext>();
        var row = await db.Secrets
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == secretId, ct)
            .ConfigureAwait(false);
        if (row is null) return null;

        var (system, identifier) = ParseFirstConsumerRef(row.ConsumerRefsJson);
        return new SecretRotationSnapshot(
            row.Id,
            row.Name,
            row.TenantId,
            system,
            identifier,
            row.ActiveVersionNumber);
    }

    public async Task<int> MintPendingVersionAsync(
        Guid secretId,
        string newPlaintext,
        string rotationCorrelationId,
        Guid operatorUserId,
        CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecretsDbContext>();

        // Idempotency — if a pending version already exists (e.g.
        // Elsa replayed the activity), return its number.
        var existing = await db.SecretVersions
            .Where(v => v.SecretId == secretId && v.Status == "pending")
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogInformation(
                "Re-using existing pending version {Version} for secret {Secret} (idempotent mint).",
                existing.VersionNumber, secretId);
            // Persist the (possibly new) plaintext to the backend — the
            // backend is last-write-wins so a replay is safe.
            await _backend.PutVersionAsync(secretId, existing.VersionNumber, newPlaintext, ct)
                .ConfigureAwait(false);
            return existing.VersionNumber;
        }

        var highest = await db.SecretVersions
            .Where(v => v.SecretId == secretId)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => (int?)v.VersionNumber)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        var next = (highest ?? 0) + 1;

        db.SecretVersions.Add(new SecretVersionRow
        {
            Id = Guid.NewGuid(),
            SecretId = secretId,
            VersionNumber = next,
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = operatorUserId,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _backend.PutVersionAsync(secretId, next, newPlaintext, ct).ConfigureAwait(false);

        return next;
    }

    public async Task DeleteVersionAsync(Guid secretId, int versionNumber, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecretsDbContext>();
        var row = await db.SecretVersions
            .FirstOrDefaultAsync(v => v.SecretId == secretId && v.VersionNumber == versionNumber, ct)
            .ConfigureAwait(false);
        if (row is null) return;

        db.SecretVersions.Remove(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        try
        {
            await _backend.DeleteVersionAsync(secretId, versionNumber, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException) { /* already scrubbed */ }
    }

    public async Task ActivateVersionAsync(
        Guid secretId,
        int newVersionNumber,
        int previousVersionNumber,
        CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecretsDbContext>();

        var newRow = await db.SecretVersions
            .FirstOrDefaultAsync(v => v.SecretId == secretId && v.VersionNumber == newVersionNumber, ct)
            .ConfigureAwait(false);
        if (newRow is null)
            throw new InvalidOperationException(
                $"ActivateVersion: version {newVersionNumber} of secret {secretId} not found.");

        // Idempotency — already active? no-op.
        if (newRow.Status == "active") return;

        newRow.Status = "active";
        newRow.ActivatedAt = DateTime.UtcNow;

        if (previousVersionNumber > 0)
        {
            var oldRow = await db.SecretVersions
                .FirstOrDefaultAsync(v => v.SecretId == secretId && v.VersionNumber == previousVersionNumber, ct)
                .ConfigureAwait(false);
            if (oldRow is not null && oldRow.Status == "active")
            {
                oldRow.Status = "retired_grace";
                oldRow.RetiredAt = DateTime.UtcNow;
            }
        }

        var secret = await db.Secrets.FirstOrDefaultAsync(s => s.Id == secretId, ct)
            .ConfigureAwait(false);
        if (secret is not null)
        {
            secret.ActiveVersionNumber = newVersionNumber;
            secret.LastRotatedAt = DateTime.UtcNow;
            secret.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RevertActivationAsync(
        Guid secretId,
        int newVersionNumber,
        int previousVersionNumber,
        CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecretsDbContext>();

        var newRow = await db.SecretVersions
            .FirstOrDefaultAsync(v => v.SecretId == secretId && v.VersionNumber == newVersionNumber, ct)
            .ConfigureAwait(false);
        if (newRow is not null && newRow.Status == "active")
        {
            newRow.Status = "pending";
            newRow.ActivatedAt = null;
        }

        if (previousVersionNumber > 0)
        {
            var oldRow = await db.SecretVersions
                .FirstOrDefaultAsync(v => v.SecretId == secretId && v.VersionNumber == previousVersionNumber, ct)
                .ConfigureAwait(false);
            if (oldRow is not null && oldRow.Status == "retired_grace")
            {
                oldRow.Status = "active";
                oldRow.RetiredAt = null;
            }
        }

        var secret = await db.Secrets.FirstOrDefaultAsync(s => s.Id == secretId, ct)
            .ConfigureAwait(false);
        if (secret is not null)
        {
            secret.ActiveVersionNumber = previousVersionNumber;
            secret.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RetireVersionAsync(Guid secretId, int versionNumber, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecretsDbContext>();
        var row = await db.SecretVersions
            .FirstOrDefaultAsync(v => v.SecretId == secretId && v.VersionNumber == versionNumber, ct)
            .ConfigureAwait(false);
        if (row is null) return;
        if (row.Status == "revoked") return; // idempotent

        row.Status = "revoked";
        row.RetiredAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        try
        {
            await _backend.DeleteVersionAsync(secretId, versionNumber, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException) { /* already scrubbed */ }
    }

    public Task<string?> GetVersionPlaintextAsync(Guid secretId, int versionNumber, CancellationToken ct)
    {
        try
        {
            return _backend.GetVersionPlaintextAsync(secretId, versionNumber, ct);
        }
        catch (KeyNotFoundException)
        {
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Parse the first consumer ref from the secret's stored JSON. When
    /// the JSON is empty / malformed the gateway returns the secret's
    /// <c>Purpose</c> as the system (fallback) so the resolver has
    /// something to key on.
    /// </summary>
    internal static (string System, string Identifier) ParseFirstConsumerRef(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
            return ("generic-http", string.Empty);

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                && doc.RootElement.GetArrayLength() > 0)
            {
                var first = doc.RootElement[0];
                var system = first.TryGetProperty("System", out var sysEl) ? sysEl.GetString() ?? string.Empty
                    : first.TryGetProperty("system", out var sysEl2) ? sysEl2.GetString() ?? string.Empty
                    : string.Empty;
                var identifier = first.TryGetProperty("Identifier", out var idEl) ? idEl.GetString() ?? string.Empty
                    : first.TryGetProperty("identifier", out var idEl2) ? idEl2.GetString() ?? string.Empty
                    : string.Empty;
                return (string.IsNullOrEmpty(system) ? "generic-http" : system, identifier);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // fall through
        }
        return ("generic-http", string.Empty);
    }
}
