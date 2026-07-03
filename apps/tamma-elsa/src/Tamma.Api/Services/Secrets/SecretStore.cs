using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Story 29-1 concrete <see cref="ISecretStore"/> facade. Composes the
/// three seams the interface documents:
///
/// <list type="bullet">
///   <item><description>metadata CRUD + version-row lifecycle against
///     the standalone <see cref="SecretsDbContext"/> (secret rows +
///     version rows, their status transitions, the active-version
///     pointer);</description></item>
///   <item><description>plaintext minting / scrubbing via
///     <see cref="ISecretStoreBackend"/> (envelope encryption for the
///     Postgres driver; volatile map for the in-memory placeholder) —
///     the facade owns the version ROW and hands the backend only the
///     <c>(secretId, versionNumber)</c> tuple + bytes, never a
///     <see cref="SecretMetadata"/>;</description></item>
///   <item><description>an <see cref="ISecretAccessAuditor"/> emission
///     on every mutating call + every ref-addressable read.</description></item>
/// </list>
///
/// <para><b>Invariants enforced</b> (Story 29-1 AC1–AC3):</para>
/// <list type="bullet">
///   <item><description><b>Plaintext never surfaced</b>: none of the
///     public methods return plaintext — they project
///     <see cref="SecretMetadata"/> / <see cref="SecretVersion"/> only.
///     Plaintext leaves the store solely via the reveal-once pipeline
///     (Story 29-3) or a rotation handler callback, neither of which is
///     on this surface.</description></item>
///   <item><description><b>Exactly one active version</b>: a create
///     that supplies an initial plaintext mints version 1 and activates
///     it (status <see cref="SecretVersionStatus.Active"/>); the facade
///     never leaves two versions active at once.</description></item>
///   <item><description><b>Rotation state machine</b>: <see cref="RotateAsync"/>
///     mints the successor version as
///     <see cref="SecretVersionStatus.Pending"/> and LEAVES the prior
///     active version <see cref="SecretVersionStatus.Active"/> with the
///     <c>ActiveVersionNumber</c> pointer unchanged. It does NOT demote the
///     prior active and does NOT advance the pointer — mirroring
///     <c>SecretStoreRotationGateway</c>. Every read seam resolves by
///     <c>ActiveVersionNumber</c>, so advancing to a not-yet-propagated
///     pending version would serve an un-pushed secret. The pending → active
///     flip (pointer advance + prior-active → RetiredGrace) is the saga's
///     later step via <c>SecretStoreRotationGateway.ActivateVersionAsync</c>;
///     it is deliberately NOT part of this handler-less call.</description></item>
///   <item><description><b>Version retirement</b>: <see cref="RetireVersionAsync"/>
///     refuses the active version, then scrubs the ciphertext (via the
///     backend) and flips the version to
///     <see cref="SecretVersionStatus.Revoked"/> while retaining the row
///     for audit.</description></item>
/// </list>
///
/// <para>Registered by
/// <c>SecretsServiceCollectionExtensions.AddTammaPostgresSecrets</c> as a
/// scoped service (it opens short-lived <see cref="SecretsDbContext"/>
/// instances via the singleton factory). The direct-seam readers
/// (<c>CabinetTenantProviderKeyReader</c>, the reveal service, the
/// rotation gateway) are intentionally NOT re-pointed at this facade yet
/// — that cleanup is a later story.</para>
/// </summary>
public sealed class SecretStore : ISecretStore
{
    private readonly IDbContextFactory<SecretsDbContext> _dbFactory;
    private readonly ISecretStoreBackend _backend;
    private readonly ISecretAccessAuditor _auditor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SecretStore> _logger;

    public SecretStore(
        IDbContextFactory<SecretsDbContext> dbFactory,
        ISecretStoreBackend backend,
        ISecretAccessAuditor auditor,
        TimeProvider timeProvider,
        ILogger<SecretStore> logger)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(auditor);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _dbFactory = dbFactory;
        _backend = backend;
        _auditor = auditor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SecretMetadata> CreateAsync(
        CreateSecretRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _timeProvider.GetUtcNow();

        // Runs the AC10 invariants (slug, scope×tenant, purpose×scope,
        // owner) and mints the Id + timestamps. Throws ArgumentException
        // on any violation — surfaced to the caller unchanged.
        var metadata = SecretMetadataFactory.Create(
            request.Name,
            request.Scope,
            request.TenantId,
            request.Purpose,
            request.ConsumerRefs,
            request.OwnerUserId,
            request.RotationSchedule,
            now);

        var scopeString = ScopeString(request.Scope);

        await using (var ctx = await _dbFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var exists = await ctx.Secrets
                .AsNoTracking()
                .AnyAsync(
                    s => s.Name == request.Name
                         && s.Scope == scopeString
                         && s.TenantId == request.TenantId,
                    ct)
                .ConfigureAwait(false);
            if (exists)
            {
                throw new InvalidOperationException(
                    $"A secret named '{request.Name}' already exists in scope " +
                    $"{request.Scope}" +
                    (request.TenantId is null
                        ? "."
                        : $" for tenant {request.TenantId}."));
            }

            ctx.Secrets.Add(new SecretRow
            {
                Id = metadata.Id,
                Name = metadata.Name,
                Scope = scopeString,
                TenantId = metadata.TenantId,
                Purpose = metadata.Purpose.ToString(),
                OwnerUserId = metadata.OwnerUserId,
                ConsumerRefsJson = SerializeConsumers(metadata.ConsumerRefs),
                RotationScheduleJson = SerializeSchedule(metadata.RotationSchedule),
                ActiveVersionNumber = 0,
                LastRotatedAt = null,
                NextRotationDueAt = metadata.NextRotationDueAt?.UtcDateTime,
                CreatedAt = metadata.CreatedAt.UtcDateTime,
                UpdatedAt = metadata.UpdatedAt.UtcDateTime,
            });
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // A create with no plaintext is a legal placeholder (Story 29-9
        // import path): the row exists with ActiveVersionNumber = 0 and no
        // version until a later RotateAsync mints one.
        if (string.IsNullOrEmpty(request.InitialPlaintext))
        {
            return metadata;
        }

        // Mint version 1 (pending) so the row exists for BOTH backends —
        // the Postgres backend upserts the ciphertext onto this row; the
        // in-memory backend only holds bytes. Then activate it: exactly
        // one active version after a create-with-plaintext.
        //
        // ATOMIC create (review fix): the metadata row is already committed
        // above, so a failure minting / persisting / activating v1 would
        // otherwise leave an orphan secret (ActiveVersionNumber = 0) whose
        // NAME is locked — every retry hits the exists-check and 409s, and
        // under the fail-closed backend EVERY create-with-plaintext would
        // poison a row. Compensate in the catch (delete the version rows +
        // the secret row + scrub any backend bytes) BEFORE rethrow so a
        // failed create leaves NO row and the name is reusable.
        try
        {
            await MintPendingVersionRowAsync(
                metadata.Id, versionNumber: 1, request.OwnerUserId, now, ct)
                .ConfigureAwait(false);

            await _backend
                .PutVersionAsync(metadata.Id, 1, request.InitialPlaintext, ct)
                .ConfigureAwait(false);

            await ActivateVersionAsync(
                metadata.Id, newVersion: 1, previousVersion: 0, now, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Create failed for secret {SecretId} after the metadata row was " +
                "committed; compensating (deleting the orphan secret + version rows " +
                "+ scrubbing backend bytes) so the name '{Name}' stays reusable.",
                metadata.Id, metadata.Name);
            await CompensateFailedCreateAsync(metadata.Id, versionNumber: 1, ct)
                .ConfigureAwait(false);
            await EmitAsync(
                SecretAuditEventTypes.Write, metadata.ToRef(), request.OwnerUserId,
                versionNumber: 1, SecretAuditOutcome.Failure,
                "create_failed_rolled_back", now, ct)
                .ConfigureAwait(false);
            throw;
        }

        await EmitAsync(
            SecretAuditEventTypes.Write, metadata.ToRef(), request.OwnerUserId,
            versionNumber: 1, SecretAuditOutcome.Success, null, now, ct)
            .ConfigureAwait(false);

        return metadata with { ActiveVersionNumber = 1 };
    }

    /// <inheritdoc />
    public async Task<SecretMetadata?> GetAsync(
        SecretRef reference, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var now = _timeProvider.GetUtcNow();

        await using var ctx = await _dbFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        var row = await ResolveRowAsync(ctx, reference, tracking: false, ct)
            .ConfigureAwait(false);

        await EmitAsync(
            SecretAuditEventTypes.Read, reference, Guid.Empty,
            versionNumber: null,
            row is null ? SecretAuditOutcome.Failure : SecretAuditOutcome.Success,
            row is null ? "not_found" : null,
            now, ct)
            .ConfigureAwait(false);

        return row is null ? null : ProjectMetadata(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SecretMetadata>> ListAsync(
        SecretListFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using var ctx = await _dbFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        IQueryable<SecretRow> q = ctx.Secrets.AsNoTracking();

        if (filter.Scope is { } scope)
        {
            var scopeString = ScopeString(scope);
            q = q.Where(r => r.Scope == scopeString);
            if (scope == SecretScope.Tenant && filter.TenantId is { } tid)
            {
                q = q.Where(r => r.TenantId == tid);
            }
        }
        else if (filter.TenantId is { } tenantId)
        {
            q = q.Where(r => r.TenantId == tenantId);
        }

        if (filter.Purpose is { } purpose)
        {
            var purposeString = purpose.ToString();
            q = q.Where(r => r.Purpose == purposeString);
        }

        if (!string.IsNullOrEmpty(filter.NamePrefix))
        {
            var prefix = filter.NamePrefix;
            q = q.Where(r => r.Name.StartsWith(prefix));
        }

        var rows = await q
            .OrderByDescending(r => r.UpdatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(ProjectMetadata).ToList();
    }

    /// <inheritdoc />
    public async Task<SecretMetadata> RotateAsync(
        SecretRef reference,
        RotateSecretRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(request);

        var newPlaintext = ResolveRotationPlaintext(request);
        var now = _timeProvider.GetUtcNow();

        SecretMetadata current;
        int newVersion;
        int previousActive;

        await using (var ctx = await _dbFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var row = await ResolveRowAsync(ctx, reference, tracking: false, ct)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException(
                    $"No secret matches {reference.ToStorageKey()}.");

            current = ProjectMetadata(row);
            previousActive = row.ActiveVersionNumber;

            var highest = await ctx.SecretVersions
                .Where(v => v.SecretId == row.Id)
                .OrderByDescending(v => v.VersionNumber)
                .Select(v => (int?)v.VersionNumber)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            newVersion = (highest ?? previousActive) + 1;
        }

        await EmitAsync(
            SecretAuditEventTypes.RotateStarted, reference, Guid.Empty,
            newVersion, SecretAuditOutcome.Success, null, now, ct)
            .ConfigureAwait(false);

        // Mint the successor version as PENDING (owns the row for both
        // backends). Gateway-consistency (review fix): the successor stays
        // PENDING and the ActiveVersionNumber pointer stays on the PRIOR
        // active version — do NOT demote the prior active and do NOT advance
        // the pointer here. Every read seam (CabinetTenantProviderKeyReader,
        // SecretStorePlatformCredentialReader, RuntimeSecretResolver)
        // resolves by ActiveVersionNumber, so advancing the pointer to a
        // not-yet-propagated pending version would serve an un-pushed secret.
        // The pending → active flip + prior-active → retired_grace transition
        // is the saga's job (SecretStoreRotationGateway.ActivateVersionAsync).
        try
        {
            await MintPendingVersionRowAsync(
                current.Id, newVersion, actorUserId: Guid.Empty, now, ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // Per-secret one-pending unique-index race: a concurrent rotation
            // won the pending claim. Fail loud — the pending row is the
            // winner's, so do NOT remove it — and record the rejection.
            _logger.LogWarning(ex,
                "Rotation mint lost the per-secret pending-uniqueness race for " +
                "secret {SecretId} (v{Version}); another rotation is in flight.",
                current.Id, newVersion);
            await EmitAsync(
                SecretAuditEventTypes.RotateFailed, reference, Guid.Empty,
                newVersion, SecretAuditOutcome.Failure, "rotation_in_progress", now, ct)
                .ConfigureAwait(false);
            throw;
        }

        try
        {
            await _backend
                .PutVersionAsync(current.Id, newVersion, newPlaintext, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Backend PutVersion failed rotating secret {SecretId} to v{Version}; " +
                "rolling back the pending version row.",
                current.Id, newVersion);
            await RemovePendingVersionRowAsync(current.Id, newVersion, ct)
                .ConfigureAwait(false);
            await EmitAsync(
                SecretAuditEventTypes.RotateFailed, reference, Guid.Empty,
                newVersion, SecretAuditOutcome.Failure,
                "backend_putversion_failed", now, ct)
                .ConfigureAwait(false);
            throw;
        }

        await EmitAsync(
            SecretAuditEventTypes.RotateSucceeded, reference, Guid.Empty,
            newVersion, SecretAuditOutcome.Success, null, now, ct)
            .ConfigureAwait(false);

        // Pointer + prior-active status are unchanged: the returned snapshot
        // still reports the prior active version (activation is the gateway's
        // job) and mirrors the actual persisted SecretRow.
        return current;
    }

    /// <inheritdoc />
    public async Task<SecretMetadata> RetireVersionAsync(
        SecretRef reference,
        int versionNumber,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (versionNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(versionNumber), versionNumber,
                "Version numbers are 1-based.");
        }

        var now = _timeProvider.GetUtcNow();
        SecretMetadata result;

        await using (var ctx = await _dbFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var row = await ResolveRowAsync(ctx, reference, tracking: true, ct)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException(
                    $"No secret matches {reference.ToStorageKey()}.");

            if (row.ActiveVersionNumber == versionNumber)
            {
                throw new InvalidOperationException(
                    "Cannot retire the active version. Rotate first so the " +
                    "successor is in place before the current version is taken away.");
            }

            var versionRow = await ctx.SecretVersions
                .FirstOrDefaultAsync(
                    v => v.SecretId == row.Id && v.VersionNumber == versionNumber, ct)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException(
                    $"No version row for {reference.ToStorageKey()} v{versionNumber}.");

            versionRow.Status = "revoked";
            versionRow.RetiredAt ??= now.UtcDateTime;
            versionRow.Ciphertext = null;
            row.UpdatedAt = now.UtcDateTime;

            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            result = ProjectMetadata(row);
        }

        // Scrub the plaintext bytes out-of-band. Idempotent; a
        // KeyNotFound means the backend never held them (already gone).
        try
        {
            await _backend.DeleteVersionAsync(result.Id, versionNumber, ct)
                .ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            // already scrubbed / never stored
        }

        await EmitAsync(
            SecretAuditEventTypes.VersionRevoked, reference, Guid.Empty,
            versionNumber, SecretAuditOutcome.Success, null, now, ct)
            .ConfigureAwait(false);

        return result;
    }

    /// <inheritdoc />
    public async Task<SecretVersion?> GetVersionAsync(
        SecretRef reference,
        int versionNumber,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (versionNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(versionNumber), versionNumber,
                "Version numbers are 1-based.");
        }

        var now = _timeProvider.GetUtcNow();

        await using var ctx = await _dbFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        var row = await ResolveRowAsync(ctx, reference, tracking: false, ct)
            .ConfigureAwait(false);

        SecretVersionRow? versionRow = null;
        if (row is not null)
        {
            versionRow = await ctx.SecretVersions
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    v => v.SecretId == row.Id && v.VersionNumber == versionNumber, ct)
                .ConfigureAwait(false);
        }

        await EmitAsync(
            SecretAuditEventTypes.Read, reference, Guid.Empty,
            versionNumber,
            versionRow is null ? SecretAuditOutcome.Failure : SecretAuditOutcome.Success,
            versionRow is null ? "not_found" : null,
            now, ct)
            .ConfigureAwait(false);

        return versionRow is null ? null : ProjectVersion(versionRow);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SecretVersion>> ListVersionsAsync(
        SecretRef reference, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var now = _timeProvider.GetUtcNow();

        await using var ctx = await _dbFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        var row = await ResolveRowAsync(ctx, reference, tracking: false, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Array.Empty<SecretVersion>();
        }

        var versions = await ctx.SecretVersions
            .AsNoTracking()
            .Where(v => v.SecretId == row.Id)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        await EmitAsync(
            SecretAuditEventTypes.Read, reference, Guid.Empty,
            versionNumber: null, SecretAuditOutcome.Success, null, now, ct)
            .ConfigureAwait(false);

        return versions.Select(ProjectVersion).ToList();
    }

    // ── version-row lifecycle ────────────────────────────────────────

    private async Task MintPendingVersionRowAsync(
        Guid secretId, int versionNumber, Guid actorUserId,
        DateTimeOffset now, CancellationToken ct)
    {
        await using var ctx = await _dbFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await ctx.SecretVersions
            .FirstOrDefaultAsync(
                v => v.SecretId == secretId && v.VersionNumber == versionNumber, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            // Idempotent re-mint (e.g. a retried create). Leave it pending.
            return;
        }

        ctx.SecretVersions.Add(new SecretVersionRow
        {
            Id = Guid.NewGuid(),
            SecretId = secretId,
            VersionNumber = versionNumber,
            Status = "pending",
            CreatedAt = now.UtcDateTime,
            ActivatedAt = null,
            RetiredAt = null,
            CreatedByUserId = actorUserId,
        });
        try
        {
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsPendingUniqueViolation(ex))
        {
            // The per-secret one-pending partial unique index
            // (UX_secret_versions_OnePendingPerSecret) rejected the INSERT: a
            // CONCURRENT rotation minted its own pending version between our
            // "no existing row" read and this write. Fail loud with a
            // retryable concurrency error rather than silently reusing the
            // other rotation's row (the silent-collapse / double-push bug).
            // Mirrors SecretStoreRotationGateway.MintPendingVersionAsync.
            throw new InvalidOperationException(
                $"rotation_in_progress: secret {secretId} already has an in-flight " +
                "rotation (a concurrent mint won the per-secret pending claim).", ex);
        }
    }

    /// <summary>
    /// True iff <paramref name="ex"/> is the Postgres unique violation raised
    /// by the per-secret one-pending partial index
    /// (<c>UX_secret_versions_OnePendingPerSecret</c>). Mirrors
    /// <c>SecretStoreRotationGateway.IsPendingUniqueViolation</c> — constrained
    /// to that constraint name so an unrelated unique violation (e.g. the
    /// SecretId+VersionNumber index) is NOT swallowed as a concurrency reject.
    /// Internal for unit tests (InternalsVisibleTo Tamma.Api.Tests).
    /// </summary>
    internal static bool IsPendingUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException pg
            && pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation
            && (pg.ConstraintName is null
                || pg.ConstraintName.Contains(
                    "OnePendingPerSecret", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Compensate a create that failed AFTER the metadata row was committed:
    /// delete every version row + the secret row and scrub any backend bytes
    /// that partially landed, so the name is reusable and no orphan
    /// (ActiveVersionNumber = 0) row is left behind. Best-effort — a
    /// compensation failure is logged but does not mask the original error.
    /// </summary>
    private async Task CompensateFailedCreateAsync(
        Guid secretId, int versionNumber, CancellationToken ct)
    {
        try
        {
            await using var ctx = await _dbFactory
                .CreateDbContextAsync(ct).ConfigureAwait(false);

            var versions = await ctx.SecretVersions
                .Where(v => v.SecretId == secretId)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (versions.Count > 0)
            {
                ctx.SecretVersions.RemoveRange(versions);
            }

            var secret = await ctx.Secrets
                .FirstOrDefaultAsync(s => s.Id == secretId, ct)
                .ConfigureAwait(false);
            if (secret is not null)
            {
                ctx.Secrets.Remove(secret);
            }

            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Compensation for failed create of secret {SecretId} did not fully " +
                "clean up the metadata rows; the name may remain locked until manual " +
                "cleanup.", secretId);
        }

        // Scrub any ciphertext bytes that landed before the failure.
        try
        {
            await _backend.DeleteVersionAsync(secretId, versionNumber, ct)
                .ConfigureAwait(false);
        }
        catch (KeyNotFoundException) { /* never stored */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Best-effort backend scrub during create compensation for secret " +
                "{SecretId} v{Version} failed.", secretId, versionNumber);
        }
    }

    private async Task RemovePendingVersionRowAsync(
        Guid secretId, int versionNumber, CancellationToken ct)
    {
        await using var ctx = await _dbFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.SecretVersions
            .FirstOrDefaultAsync(
                v => v.SecretId == secretId
                     && v.VersionNumber == versionNumber
                     && v.Status == "pending", ct)
            .ConfigureAwait(false);
        if (row is null) return;
        ctx.SecretVersions.Remove(row);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task ActivateVersionAsync(
        Guid secretId, int newVersion, int previousVersion,
        DateTimeOffset now, CancellationToken ct)
    {
        await using var ctx = await _dbFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        var newRow = await ctx.SecretVersions
            .FirstOrDefaultAsync(
                v => v.SecretId == secretId && v.VersionNumber == newVersion, ct)
            .ConfigureAwait(false);
        if (newRow is not null && newRow.Status != "active")
        {
            newRow.Status = "active";
            newRow.ActivatedAt = now.UtcDateTime;
        }

        if (previousVersion > 0)
        {
            var oldRow = await ctx.SecretVersions
                .FirstOrDefaultAsync(
                    v => v.SecretId == secretId
                         && v.VersionNumber == previousVersion
                         && v.Status == "active", ct)
                .ConfigureAwait(false);
            if (oldRow is not null)
            {
                oldRow.Status = "retired_grace";
                oldRow.RetiredAt = now.UtcDateTime;
            }
        }

        var secret = await ctx.Secrets
            .FirstOrDefaultAsync(s => s.Id == secretId, ct)
            .ConfigureAwait(false);
        if (secret is not null)
        {
            secret.ActiveVersionNumber = newVersion;
            secret.LastRotatedAt = now.UtcDateTime;
            secret.UpdatedAt = now.UtcDateTime;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ── request helpers ──────────────────────────────────────────────

    private const int MinGenerateLength = 16;
    private const int MaxGenerateLength = 256;

    private static string ResolveRotationPlaintext(RotateSecretRequest request)
    {
        var hasPlaintext = !string.IsNullOrEmpty(request.NewPlaintext);
        var hasGenerate = request.GenerateLength is not null;

        if (hasPlaintext == hasGenerate)
        {
            throw new ArgumentException(
                "Exactly one of NewPlaintext / GenerateLength must be supplied.",
                nameof(request));
        }

        if (hasPlaintext)
        {
            return request.NewPlaintext!;
        }

        var length = request.GenerateLength!.Value;
        if (length is < MinGenerateLength or > MaxGenerateLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), length,
                $"GenerateLength must be in [{MinGenerateLength}, {MaxGenerateLength}].");
        }

        var bytes = RandomNumberGenerator.GetBytes(length);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        var base64 = Convert.ToBase64String(bytes);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // ── resolution + projection ──────────────────────────────────────

    private static async Task<SecretRow?> ResolveRowAsync(
        SecretsDbContext ctx, SecretRef reference, bool tracking, CancellationToken ct)
    {
        var scopeString = ScopeString(reference.Scope);
        IQueryable<SecretRow> q = tracking ? ctx.Secrets : ctx.Secrets.AsNoTracking();
        return await q
            .FirstOrDefaultAsync(
                s => s.Name == reference.Name
                     && s.Scope == scopeString
                     && s.TenantId == reference.TenantId,
                ct)
            .ConfigureAwait(false);
    }

    private static string ScopeString(SecretScope scope) =>
        scope.ToString().ToLowerInvariant();

    private Task EmitAsync(
        string eventType, SecretRef reference, Guid actorUserId,
        int? versionNumber, SecretAuditOutcome outcome, string? detail,
        DateTimeOffset now, CancellationToken ct) =>
        _auditor.EmitAsync(
            new SecretAuditEvent(
                EventType: eventType,
                Reference: reference,
                ActorUserId: actorUserId,
                VersionNumber: versionNumber,
                Outcome: outcome,
                Detail: detail,
                OccurredAt: now),
            ct);

    private static SecretMetadata ProjectMetadata(SecretRow row)
    {
        var scope = Enum.Parse<SecretScope>(row.Scope, ignoreCase: true);
        var purpose = Enum.Parse<SecretPurpose>(row.Purpose, ignoreCase: true);

        IReadOnlyList<ConsumerRef> consumers;
        try
        {
            consumers = JsonSerializer
                .Deserialize<List<ConsumerRef>>(row.ConsumerRefsJson)
                ?? (IReadOnlyList<ConsumerRef>)Array.Empty<ConsumerRef>();
        }
        catch
        {
            consumers = Array.Empty<ConsumerRef>();
        }

        var schedule = DeserializeSchedule(row.RotationScheduleJson);

        DateTimeOffset? lastRotated = row.LastRotatedAt is null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(row.LastRotatedAt.Value, DateTimeKind.Utc));
        DateTimeOffset? nextDue = row.NextRotationDueAt is null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(row.NextRotationDueAt.Value, DateTimeKind.Utc));

        return new SecretMetadata(
            Id: row.Id,
            Name: row.Name,
            Scope: scope,
            TenantId: row.TenantId,
            Purpose: purpose,
            ConsumerRefs: consumers,
            OwnerUserId: row.OwnerUserId,
            RotationSchedule: schedule,
            LastRotatedAt: lastRotated,
            NextRotationDueAt: nextDue,
            ActiveVersionNumber: row.ActiveVersionNumber,
            CreatedAt: new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)),
            UpdatedAt: new DateTimeOffset(DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc)));
    }

    private static SecretVersion ProjectVersion(SecretVersionRow row)
    {
        var status = row.Status switch
        {
            "active" => SecretVersionStatus.Active,
            "retired_grace" => SecretVersionStatus.RetiredGrace,
            "revoked" => SecretVersionStatus.Revoked,
            _ => SecretVersionStatus.Pending,
        };
        return new SecretVersion(
            SecretId: row.SecretId,
            VersionNumber: row.VersionNumber,
            Status: status,
            CreatedAt: new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)),
            ActivatedAt: row.ActivatedAt is null
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(row.ActivatedAt.Value, DateTimeKind.Utc)),
            RetiredAt: row.RetiredAt is null
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(row.RetiredAt.Value, DateTimeKind.Utc)),
            CreatedByUserId: row.CreatedByUserId);
    }

    private static string SerializeConsumers(IReadOnlyList<ConsumerRef> consumers) =>
        consumers.Count == 0 ? "[]" : JsonSerializer.Serialize(consumers);

    private static string SerializeSchedule(RotationSchedule schedule) =>
        JsonSerializer.Serialize(new
        {
            Kind = schedule.Kind.ToString(),
            schedule.Days,
            schedule.CronExpression,
        });

    private static RotationSchedule DeserializeSchedule(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Kind", out var kindProp))
                return RotationSchedule.None;
            var kind = kindProp.GetString() ?? "None";
            return kind switch
            {
                "Days" when root.TryGetProperty("Days", out var d)
                    && d.ValueKind == JsonValueKind.Number
                    => RotationSchedule.EveryDays(d.GetInt32()),
                "Cron" when root.TryGetProperty("CronExpression", out var c)
                    && c.ValueKind == JsonValueKind.String
                    => RotationSchedule.Cron(c.GetString()!),
                _ => RotationSchedule.None,
            };
        }
        catch
        {
            return RotationSchedule.None;
        }
    }
}
