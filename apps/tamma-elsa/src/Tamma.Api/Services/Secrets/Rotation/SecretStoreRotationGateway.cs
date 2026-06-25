using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamma.Activities.SecretsRotation.Contracts;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Secrets.Rotation;

/// <summary>
/// Options for <see cref="SecretStoreRotationGateway"/>. Bound to the
/// <c>SecretRotationGateway</c> configuration section.
/// </summary>
public sealed class SecretRotationGatewayOptions
{
    public const string SectionName = "SecretRotationGateway";

    /// <summary>
    /// Story 29-6 (review fix) — max age of a <c>pending</c> version row
    /// before it is treated as ABANDONED (a saga that crashed after mint).
    /// A stale pending marker older than this is reclaimable: the next
    /// rotation trigger deletes it (+ scrubs its backend bytes) and
    /// proceeds, so a crashed run can't wedge the secret forever. Default
    /// 1 hour — comfortably longer than any healthy rotation saga (mint →
    /// push → probe → activate completes in seconds-to-minutes), so a live
    /// rotation is never mistaken for abandoned.
    /// </summary>
    public TimeSpan StalePendingTtl { get; set; } = TimeSpan.FromHours(1);
}

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
    private readonly IDbContextFactory<SecretsDbContext> _dbFactory;
    private readonly ISecretStoreBackend _backend;
    private readonly SecretRotationGatewayOptions _options;
    private readonly ILogger<SecretStoreRotationGateway> _logger;

    public SecretStoreRotationGateway(
        IDbContextFactory<SecretsDbContext> dbFactory,
        ISecretStoreBackend backend,
        IOptions<SecretRotationGatewayOptions> options,
        ILogger<SecretStoreRotationGateway> logger)
    {
        _dbFactory = dbFactory;
        _backend = backend;
        _options = options?.Value ?? new SecretRotationGatewayOptions();
        _logger = logger;
    }

    public async Task<SecretRotationSnapshot?> GetSnapshotAsync(Guid secretId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
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
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

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
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsPendingUniqueViolation(ex))
        {
            // Story 29-6 (review fix) — the partial unique index
            // UX_secret_versions_OnePendingPerSecret rejected this INSERT:
            // a CONCURRENT rotation won the race and minted its own pending
            // version between our "no existing pending" read and this write.
            // This closes the TOCTOU: we must NOT silently reuse the other
            // rotation's row (that was the silent-collapse + double-push
            // bug). Fail loud with a retryable concurrency error so the saga
            // surfaces SECRET.ROTATION.FAILED(rotation_in_progress) instead.
            _logger.LogWarning(ex,
                "Mint lost the per-secret pending-uniqueness race for secret {Secret} " +
                "(corr {Corr}) — another rotation is in flight.",
                secretId, rotationCorrelationId);
            throw new InvalidOperationException(
                $"rotation_in_progress: secret {secretId} already has an in-flight " +
                "rotation (a concurrent mint won the per-secret pending claim).", ex);
        }

        await _backend.PutVersionAsync(secretId, next, newPlaintext, ct).ConfigureAwait(false);

        return next;
    }

    public async Task DeleteVersionAsync(Guid secretId, int versionNumber, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
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
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

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
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

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
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
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

    public async Task<bool> TryBeginRotationAsync(
        Guid secretId, string rotationCorrelationId, CancellationToken ct)
    {
        // Pre-dispatch guard: a secret with an existing Pending version row
        // is already mid-rotation. The pending row IS the in-flight marker —
        // it clears when the saga activates (Pending→Active) or compensates
        // (deletes it). Two overlapping rotations would otherwise both mint a
        // pending version and race the version-number sequence + double-push.
        //
        // This is a best-effort pre-check (the AUTHORITATIVE TOCTOU close is
        // the partial unique index UX_secret_versions_OnePendingPerSecret +
        // the unique-violation catch in MintPendingVersionAsync — two callers
        // that both pass THIS check still can't both mint). Here we ALSO
        // reclaim a STALE pending marker so a saga that crashed after mint
        // can't wedge the secret forever.
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var pending = await db.SecretVersions
            .Where(v => v.SecretId == secretId && v.Status == "pending")
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (pending is null) return true;

        var age = DateTime.UtcNow - pending.CreatedAt;
        if (age <= _options.StalePendingTtl)
        {
            _logger.LogWarning(
                "Rotation rejected for secret {Secret} (corr {Corr}): a pending version " +
                "(v{Version}, age {AgeSeconds:0}s) already exists — another rotation is in flight.",
                secretId, rotationCorrelationId, pending.VersionNumber, age.TotalSeconds);
            return false;
        }

        // The pending marker is older than the TTL — treat it as ABANDONED
        // (a saga that crashed after mint). Reclaim it: hard-delete the row +
        // scrub its backend bytes so the new rotation can proceed instead of
        // being wedged forever. Then allow the caller. If a racing reclaim
        // already removed it (or the row activated under us), the new mint
        // simply re-checks; the unique index keeps the final state consistent.
        _logger.LogWarning(
            "Reclaiming ABANDONED pending version v{Version} for secret {Secret} " +
            "(corr {Corr}, age {AgeSeconds:0}s > TTL {TtlSeconds:0}s) — a prior rotation " +
            "saga appears to have crashed after mint; clearing the stale claim.",
            pending.VersionNumber, secretId, rotationCorrelationId,
            age.TotalSeconds, _options.StalePendingTtl.TotalSeconds);

        db.SecretVersions.Remove(pending);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        try
        {
            await _backend.DeleteVersionAsync(secretId, pending.VersionNumber, ct)
                .ConfigureAwait(false);
        }
        catch (KeyNotFoundException) { /* already scrubbed */ }

        return true;
    }

    /// <summary>
    /// True iff <paramref name="ex"/> is the Postgres unique violation
    /// raised by the per-secret one-pending partial index
    /// (<c>UX_secret_versions_OnePendingPerSecret</c>). Constrained to that
    /// constraint name so an unrelated unique violation (e.g. the
    /// SecretId+VersionNumber index) is NOT swallowed as a concurrency reject.
    /// </summary>
    private static bool IsPendingUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException pg
            && pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation
            && (pg.ConstraintName is null
                || pg.ConstraintName.Contains("OnePendingPerSecret", StringComparison.OrdinalIgnoreCase));

    public Task EndRotationAsync(
        Guid secretId, string rotationCorrelationId, CancellationToken ct)
    {
        // No-op for the status-check backend: the pending version row is
        // the claim, and it is cleared by activate / compensation. An
        // advisory-lock backend would release its lock here.
        return Task.CompletedTask;
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
