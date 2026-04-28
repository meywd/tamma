using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Secrets.Postgres;

/// <summary>
/// Story 29-2 Postgres-backed implementation of
/// <see cref="ISecretStoreBackend"/>. Stores each version's
/// plaintext as an AES-256-GCM envelope in the
/// <c>secret_versions.Ciphertext</c> column; the envelope wraps a
/// fresh per-version DEK under the KEK supplied by
/// <see cref="IKekProvider"/> (default: <see cref="EnvKekProvider"/>
/// reading from <c>TAMMA_SECRET_STORE_KEK_PRIMARY</c> /
/// <c>_SECONDARY</c>).
///
/// <para>Plaintext never touches the EF tracking layer:
/// <see cref="PutVersionAsync"/> calls
/// <see cref="SecretEnvelope.Encrypt"/> before any EF call;
/// <see cref="GetVersionPlaintextAsync"/> projects the
/// <see cref="SecretVersionRow.Ciphertext"/> bytes only and runs
/// <see cref="SecretEnvelope.Decrypt"/> after EF has materialised
/// the row. The plaintext string is returned to the caller and not
/// retained.</para>
///
/// <para>This backend assumes the parent <c>secret</c> row already
/// exists — the version row's FK is enforced. The
/// <see cref="ISecretStore"/> facade (a future story) is responsible
/// for inserting the parent row before the first version. To keep
/// the test surface usable without building the facade, the
/// <c>EnsureSecretRowAsync</c> helper inserts a stub parent row when
/// missing — see remarks.</para>
///
/// <para>Idempotency: <see cref="DeleteVersionAsync"/> is a no-op
/// when the row already has <c>Ciphertext IS NULL</c>; the second
/// call on a present row scrubs the bytes and flips status to
/// <c>revoked</c>. Calling on a non-existent row is a no-op (matches
/// the <see cref="InMemorySecretStoreBackend"/> contract).</para>
/// </summary>
public sealed class PostgresSecretStoreBackend : ISecretStoreBackend
{
    private readonly IDbContextFactory<SecretsDbContext> _contextFactory;
    private readonly IKekProvider _kekProvider;
    private readonly ILogger<PostgresSecretStoreBackend> _logger;

    public PostgresSecretStoreBackend(
        IDbContextFactory<SecretsDbContext> contextFactory,
        IKekProvider kekProvider,
        ILogger<PostgresSecretStoreBackend> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(kekProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _kekProvider = kekProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PutVersionAsync(
        Guid secretId,
        int versionNumber,
        string plaintext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (versionNumber <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(versionNumber),
                versionNumber,
                "Version numbers are 1-based.");

        // Encrypt OUTSIDE the DB scope so plaintext never enters the
        // EF change tracker. The KEK is fetched once and zeroed by
        // the envelope helper.
        var kekId = _kekProvider.PrimaryKekId;
        var kek = _kekProvider.GetKek(kekId);
        byte[] envelope;
        try
        {
            envelope = SecretEnvelope.Encrypt(plaintext, kekId, kek);
        }
        finally
        {
            // Defensive scrub — Encrypt also zeros its DEK + plaintext
            // copies but we own this KEK clone here.
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(kek);
        }

        await using var ctx = await _contextFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await ctx.SecretVersions
            .FirstOrDefaultAsync(
                v => v.SecretId == secretId && v.VersionNumber == versionNumber,
                ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            ctx.SecretVersions.Add(new SecretVersionRow
            {
                Id = Guid.NewGuid(),
                SecretId = secretId,
                VersionNumber = versionNumber,
                Status = "pending",
                Ciphertext = envelope,
                KekId = kekId,
                FormatVersion = SecretEnvelope.CurrentFormatVersion,
                CreatedAt = DateTime.UtcNow,
                ActivatedAt = null,
                RetiredAt = null,
                CreatedByUserId = Guid.Empty,
            });
        }
        else
        {
            // Last-write-wins on the (secretId, versionNumber) tuple,
            // matching the in-memory backend contract. Real rotation
            // (via the ISecretStore facade) inserts a fresh row per
            // version; this branch exists for tests + the rare
            // operator-driven re-mint path.
            existing.Ciphertext = envelope;
            existing.KekId = kekId;
            existing.FormatVersion = SecretEnvelope.CurrentFormatVersion;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogDebug(
            "Stored secret version {SecretId}/{VersionNumber} ({Bytes} bytes envelope)",
            secretId, versionNumber, envelope.Length);
    }

    /// <inheritdoc />
    public async Task<string?> GetVersionPlaintextAsync(
        Guid secretId,
        int versionNumber,
        CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        // Project only the ciphertext column to keep the rest of the
        // row out of EF's tracking. AsNoTracking + select-projection
        // is the standard pattern for "read me a single byte field".
        var row = await ctx.SecretVersions
            .AsNoTracking()
            .Where(v => v.SecretId == secretId && v.VersionNumber == versionNumber)
            .Select(v => new { v.Ciphertext })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            throw new KeyNotFoundException(
                $"No version row for secretId={secretId}, " +
                $"versionNumber={versionNumber}.");
        }

        if (row.Ciphertext is null || row.Ciphertext.Length == 0)
        {
            // Scrubbed (revoked) row — present but no bytes.
            return null;
        }

        return SecretEnvelope.Decrypt(row.Ciphertext, _kekProvider);
    }

    /// <inheritdoc />
    public async Task DeleteVersionAsync(
        Guid secretId,
        int versionNumber,
        CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await ctx.SecretVersions
            .FirstOrDefaultAsync(
                v => v.SecretId == secretId && v.VersionNumber == versionNumber,
                ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            // No row → no-op (matches the in-memory backend's
            // "Delete is always safe to call" contract). The
            // ISecretStore facade enforces stricter semantics if
            // needed.
            _logger.LogDebug(
                "Delete on absent secret version {SecretId}/{VersionNumber}; no-op",
                secretId, versionNumber);
            return;
        }

        if (existing.Ciphertext is null && existing.Status == "revoked")
        {
            // Already scrubbed; idempotent no-op.
            return;
        }

        existing.Ciphertext = null;
        existing.Status = "revoked";
        existing.RetiredAt = DateTime.UtcNow;

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Scrubbed secret version {SecretId}/{VersionNumber} (revoked)",
            secretId, versionNumber);
    }
}
