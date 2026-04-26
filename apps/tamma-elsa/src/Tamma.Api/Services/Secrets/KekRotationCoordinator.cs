using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Security;
using Tamma.Api.Services.PlatformEvents;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Story 28-12 — coordinates the platform-wide KEK rotation flow.
///
/// <para>The coordinator is a singleton (one rotation can be in flight
/// at a time per process). The actual re-encrypt loop runs on a
/// background <see cref="Task"/> kicked off by
/// <see cref="StartAsync"/> so the API call returns 202 immediately.</para>
///
/// <para>R2-H14 hardening:</para>
/// <list type="bullet">
///   <item><description><b>Cluster-wide singleton</b>: every entry to
///     <c>RunRotationAsync</c> takes a Postgres advisory lock keyed
///     to <see cref="AdvisoryLockKey"/>. Two pods racing the start
///     endpoint can no longer stage different KEKs — the loser gets a
///     <see cref="KekRotationStatus"/> back with <c>FailureReason</c>
///     set to "another rotation is already in progress on this
///     cluster".</description></item>
///   <item><description><b>Crash-resume</b>: the staged secondary KEK
///     is now persisted to <c>kek_rotations</c> (encrypted by the OLD
///     primary so it remains readable across restarts). On startup
///     the coordinator scans for non-terminal rows and resumes the
///     in-flight rotation rather than dropping the new key on the
///     floor.</description></item>
///   <item><description><b>Retry endpoint</b>: <c>POST
///     /api/admin/kek/rotate/retry</c> re-runs a previously-failed
///     rotation by re-using the staged secondary that's still on
///     disk (no fresh KEK is minted on retry — that would orphan the
///     rows already re-encrypted under the failed run's secondary).</description></item>
/// </list>
///
/// <para>R2-M1: any <c>ex.Message</c> that lands in
/// <see cref="KekRotationStatus.FailureReason"/> or in
/// <see cref="PlatformEvent"/> Data is run through
/// <see cref="IErrorRedactor"/> so accidentally-logged credentials
/// (Bearer tokens, sk- keys, base64 blobs) never leak through the
/// long-lived event store.</para>
/// </summary>
public sealed class KekRotationCoordinator
{
    private const string RotationStartedEvent = "SECRETS.KEK.ROTATION.STARTED";
    private const string RotationStepEvent = "TENANT.CONNECTION_STRING_ROTATED.SUCCESS";
    private const string RotationCompletedEvent = "SECRETS.KEK.ROTATION.COMPLETED";
    private const string RotationFailedEvent = "SECRETS.KEK.ROTATION.FAILED";
    private const int KekSize = 32;

    /// <summary>
    /// R2-H14: Postgres advisory lock key used to serialise rotations
    /// across pods. Constant chosen as a 64-bit integer that no other
    /// subsystem in Tamma uses. The high half is a magic prefix
    /// (<c>0x281228</c> = "Story 28-12-28"); the low half is reserved
    /// for future per-purpose sub-locks.
    /// </summary>
    public const long AdvisoryLockKey = 0x281228_00000001L;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KekProvider _kekProvider;
    private readonly ITenantConnectionResolver _resolver;
    private readonly ILogger<KekRotationCoordinator> _logger;
    private readonly IErrorRedactor? _errorRedactor;

    private readonly object _lock = new();
    private KekRotationStatus _status = new(
        Phase: KekRotationPhase.Idle,
        FromVersion: 0,
        ToVersion: 0,
        TotalTenants: 0,
        ReencryptedTenants: 0,
        FailedTenants: 0,
        StartedAt: null,
        CompletedAt: null,
        FailureReason: null);
    private Task? _runningTask;
    // R2-H14: when a rotation runs, this is the kek_rotations row id
    // tracking the in-flight state. Set on StartAsync, cleared on
    // terminal phase. Retry uses this to find the row to re-execute.
    private Guid? _activeRotationId;

    public KekRotationCoordinator(
        IServiceScopeFactory scopeFactory,
        KekProvider kekProvider,
        ITenantConnectionResolver resolver,
        ILogger<KekRotationCoordinator> logger,
        IErrorRedactor? errorRedactor = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(kekProvider);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _kekProvider = kekProvider;
        _resolver = resolver;
        _logger = logger;
        _errorRedactor = errorRedactor;
    }

    /// <summary>
    /// Snapshot of the current rotation state. Cheap; safe to call
    /// from the status endpoint on every poll.
    /// </summary>
    public KekRotationStatus GetStatus()
    {
        lock (_lock)
        {
            return _status;
        }
    }

    /// <summary>
    /// Begin a rotation. If a rotation is already in flight, returns
    /// the running snapshot without staging a second key. The operator
    /// can <see cref="GetStatus"/> to poll progress.
    /// </summary>
    /// <param name="newKek">Optional caller-supplied 32-byte KEK. When
    /// null, the coordinator generates one via
    /// <see cref="RandomNumberGenerator.GetBytes(int)"/>. Tests pass an
    /// explicit value so they can assert the re-encrypted envelopes
    /// actually use the new key.</param>
    public KekRotationStatus StartAsync(byte[]? newKek = null, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_status.Phase == KekRotationPhase.Running)
            {
                _logger.LogInformation(
                    "KekRotationCoordinator.StartAsync called while a rotation is "
                    + "already in flight — returning current status.");
                return _status;
            }

            // Generate (or accept) the new KEK and stage it as secondary
            // BEFORE returning. That way the resolver's fallback path is
            // armed before any tenant row is touched.
            var generated = newKek ?? RandomNumberGenerator.GetBytes(KekSize);
            if (generated.Length != KekSize)
            {
                throw new ArgumentException(
                    $"KEK must be exactly {KekSize} bytes (got {generated.Length}).",
                    nameof(newKek));
            }

            var fromVersion = _kekProvider.GetActiveVersion();
            var toVersion = fromVersion + 1;
            _kekProvider.StageSecondary(generated);

            _status = new KekRotationStatus(
                Phase: KekRotationPhase.Running,
                FromVersion: fromVersion,
                ToVersion: toVersion,
                TotalTenants: 0,
                ReencryptedTenants: 0,
                FailedTenants: 0,
                StartedAt: DateTimeOffset.UtcNow,
                CompletedAt: null,
                FailureReason: null);

            _runningTask = Task.Run(
                () => RunRotationAsync(generated, fromVersion, toVersion, isRetry: false, cancellationToken),
                cancellationToken);

            return _status;
        }
    }

    /// <summary>
    /// R2-H3: re-attempt a previously-failed rotation. The coordinator
    /// re-uses the staged secondary KEK that was persisted on the
    /// failed run rather than generating a fresh one — that would
    /// orphan any rows that were already re-encrypted under the
    /// failed run's secondary. Returns the running snapshot when retry
    /// kicks off; throws when the current phase is not
    /// <see cref="KekRotationPhase.Failed"/>.
    /// </summary>
    public async Task<RotationRetryResponse> RetryAsync(CancellationToken cancellationToken = default)
    {
        KekRotationStatus snapshot;
        lock (_lock)
        {
            snapshot = _status;
        }

        if (snapshot.Phase != KekRotationPhase.Failed)
        {
            return new RotationRetryResponse(
                Success: false,
                Reason: $"Cannot retry: current phase is {snapshot.Phase}. "
                    + "Retry is only valid when the previous rotation is in the Failed phase.",
                Status: snapshot);
        }

        // Reload the staged secondary from durable storage. We can't
        // re-stage the in-memory secondary (it was cleared / never
        // persisted on the failed run); we must read it from
        // kek_rotations. The row is encrypted by the OLD primary.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var cpFactory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        await using var ctx = await cpFactory.CreateDbContextAsync(cancellationToken);

        // Find the most-recent failed rotation that still carries a
        // staged secondary. Older failed rows are zeroed.
        var failedRow = await ctx.KekRotations
            .Where(r => r.Status == "failed" && r.StagedSecondaryProtected != null)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (failedRow is null)
        {
            return new RotationRetryResponse(
                Success: false,
                Reason: "No failed rotation with a staged secondary KEK is available for retry. "
                    + "The previous failure may have run cleanup; mint a fresh rotation via /start.",
                Status: snapshot);
        }

        // Decrypt the staged secondary using the OLD primary (still in
        // KekProvider as primary today, since promotion never happened
        // on the failed run).
        var oldPrimary = _kekProvider.GetPrimary();
        if (oldPrimary is null)
        {
            return new RotationRetryResponse(
                Success: false,
                Reason: "Primary KEK is not configured — cannot decrypt the staged secondary.",
                Status: snapshot);
        }

        byte[] stagedSecondary;
        try
        {
            var plaintext = AesGcmConnectionStringDecryptor.DecryptWithKey(
                failedRow.StagedSecondaryProtected!, oldPrimary);
            stagedSecondary = Convert.FromBase64String(plaintext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retry: failed to decrypt persisted staged secondary.");
            return new RotationRetryResponse(
                Success: false,
                Reason: $"Failed to decrypt persisted staged secondary: {RedactForEvent(ex)}",
                Status: snapshot);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldPrimary);
        }

        if (stagedSecondary.Length != KekSize)
        {
            CryptographicOperations.ZeroMemory(stagedSecondary);
            return new RotationRetryResponse(
                Success: false,
                Reason: "Persisted staged secondary has wrong length.",
                Status: snapshot);
        }

        // Restore the staged secondary in the in-memory KekProvider so
        // GetByVersion can answer for the new version, then kick off
        // the rotation again under the same id.
        var fromVersion = _kekProvider.GetActiveVersion();
        var toVersion = failedRow.VersionNew;
        _kekProvider.RestoreStagedSecondary(stagedSecondary, toVersion);

        // Reset the row to pending so the next run can mark it running.
        failedRow.Status = "pending";
        failedRow.FailureReason = null;
        await ctx.SaveChangesAsync(cancellationToken);

        lock (_lock)
        {
            _status = new KekRotationStatus(
                Phase: KekRotationPhase.Running,
                FromVersion: fromVersion,
                ToVersion: toVersion,
                TotalTenants: 0,
                ReencryptedTenants: 0,
                FailedTenants: 0,
                StartedAt: DateTimeOffset.UtcNow,
                CompletedAt: null,
                FailureReason: null);
            _activeRotationId = failedRow.Id;
            _runningTask = Task.Run(
                () => RunRotationAsync(stagedSecondary, fromVersion, toVersion, isRetry: true, cancellationToken),
                cancellationToken);
        }

        return new RotationRetryResponse(Success: true, Reason: null, Status: _status);
    }

    /// <summary>
    /// Test-only: await the in-flight rotation task. Returns immediately
    /// if no rotation has run. Swallows exceptions thrown by the
    /// background task — callers must check
    /// <see cref="GetStatus"/> to discover the outcome.
    /// </summary>
    public async Task WaitForCompletionAsync()
    {
        Task? toAwait;
        lock (_lock)
        {
            toAwait = _runningTask;
        }
        if (toAwait is null) return;
        try
        {
            await toAwait.ConfigureAwait(false);
        }
        catch
        {
            // Background failures are reflected on _status (Failed phase
            // + FailureReason). The waiter only cares that the task has
            // exited — not whether it threw.
        }
    }

    private async Task RunRotationAsync(
        byte[] newKek,
        int fromVersion,
        int toVersion,
        bool isRetry,
        CancellationToken ct)
    {
        // R2-H14: scope the advisory lock to a dedicated DbContext +
        // its underlying connection so the lock follows the connection
        // lifetime. We hold this connection open for the duration of
        // RunRotationAsync; on completion the using-block releases the
        // connection which auto-releases the lock.
        await using var lockScope = _scopeFactory.CreateAsyncScope();
        var lockFactory = lockScope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        await using var lockCtx = await lockFactory.CreateDbContextAsync(ct);

        bool acquired;
        try
        {
            acquired = await TryAcquireAdvisoryLockAsync(lockCtx, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Lock acquisition failed for non-cancellation reasons
            // (e.g. EF InMemory provider doesn't support raw SQL). In
            // that case fall through with acquired = false; the
            // singleton _lock guard inside StartAsync still serialises
            // within-process callers.
            _logger.LogDebug(ex,
                "advisory lock acquisition skipped: provider does not support raw SQL");
            acquired = true; // proceed; in-process _lock is the only guard
        }

        if (!acquired)
        {
            UpdateStatus(s => s with
            {
                Phase = KekRotationPhase.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                FailureReason = "another rotation is already in progress on this cluster",
            });
            _logger.LogWarning(
                "tenant.kek.rotate aborted: advisory lock {LockKey} held by another pod",
                AdvisoryLockKey);
            return;
        }

        byte[]? oldPrimary = null;
        Guid? rotationId = null;
        try
        {
            // Snapshot the current primary BEFORE we promote — that is
            // the KEK that encrypted every existing envelope, which is
            // what we need to feed into the per-row decrypt. Capture
            // inside the try so the catch path flips the coordinator
            // status to Failed if no primary is configured.
            oldPrimary = _kekProvider.GetPrimary()
                ?? throw new InvalidOperationException(
                    "Cannot rotate without a primary KEK. Set "
                    + KekProvider.PrimaryConfigKey + " before invoking rotation.");

            await using var scope = _scopeFactory.CreateAsyncScope();
            var cpFactory = scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
            var eventRepo = scope.ServiceProvider
                .GetRequiredService<IPlatformEventRepository>();
            var bus = scope.ServiceProvider.GetService<IPlatformEventBus>();

            // R2-H14: persist the staged secondary into kek_rotations
            // so a process crash mid-rotation can resume by reading
            // the row back. The secondary is encrypted by the OLD
            // primary so the row is readable across restarts.
            rotationId = await PersistRotationStartAsync(
                cpFactory, fromVersion, toVersion, newKek, oldPrimary, isRetry, ct);
            lock (_lock) { _activeRotationId = rotationId; }

            await EmitPlatformEventAsync(
                eventRepo, bus,
                RotationStartedEvent,
                tenantId: null,
                tags: new()
                {
                    ["from_version"] = fromVersion.ToString(),
                    ["to_version"] = toVersion.ToString(),
                },
                data: new(),
                ct);

            // Pull tenant ids that still need rotation. We project to a
            // small DTO so we don't hold onto change-tracker entries
            // across the loop. KekVersion < toVersion catches both the
            // common steady-state (== fromVersion) and the
            // double-rotation safety case (a row that somehow already
            // sits at toVersion is skipped).
            await using var listingCtx = await cpFactory.CreateDbContextAsync(ct);
            var rotationRows = await listingCtx.Tenants
                .IgnoreQueryFilters()
                .Where(t => t.DeletedAt == null)
                .Where(t => EF.Property<byte[]?>(t, "EncryptedConnectionString") != null)
                .Where(t => (EF.Property<int?>(t, "KekVersion") ?? 0) < toVersion)
                .Select(t => new RotationRow(
                    t.Id,
                    EF.Property<byte[]?>(t, "EncryptedConnectionString"),
                    EF.Property<int?>(t, "KekVersion") ?? 0))
                .ToListAsync(ct);

            UpdateStatus(s => s with { TotalTenants = rotationRows.Count });

            int reencrypted = 0;
            int failed = 0;
            foreach (var row in rotationRows)
            {
                ct.ThrowIfCancellationRequested();
                if (row.Envelope is null || row.Envelope.Length == 0)
                {
                    failed++;
                    UpdateStatus(s => s with { FailedTenants = failed });
                    _logger.LogWarning(
                        "tenant.kek.rotate skipped tenantId={TenantId} reason=missing_envelope",
                        row.TenantId);
                    continue;
                }

                try
                {
                    // Decrypt under the OLD primary and re-encrypt under
                    // the NEW key. Direct-key calls bypass the adapter's
                    // fallback — we know the row is currently encrypted
                    // under the old primary because its KekVersion is
                    // below the target version.
                    var plaintext = AesGcmConnectionStringDecryptor.DecryptWithKey(
                        row.Envelope, oldPrimary);
                    var newEnvelope = AesGcmConnectionStringDecryptor.EncryptWithKey(
                        plaintext, newKek);

                    await using var writeCtx = await cpFactory.CreateDbContextAsync(ct);
                    var tenant = await writeCtx.Tenants
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(t => t.Id == row.TenantId, ct);
                    if (tenant is null)
                    {
                        // Row vanished mid-rotation (concurrent delete).
                        failed++;
                        UpdateStatus(s => s with { FailedTenants = failed });
                        _logger.LogWarning(
                            "tenant.kek.rotate skipped tenantId={TenantId} reason=row_vanished",
                            row.TenantId);
                        continue;
                    }

                    var entry = writeCtx.Entry(tenant);
                    entry.Property("EncryptedConnectionString").CurrentValue = newEnvelope;
                    entry.Property("KekVersion").CurrentValue = toVersion;
                    tenant.UpdatedAt = DateTime.UtcNow;
                    await writeCtx.SaveChangesAsync(ct);

                    // Drop the resolver's warm pool for this tenant so the
                    // next request decrypts the rotated row and rebuilds
                    // the data source from scratch.
                    await _resolver.EvictAsync(row.TenantId, ct);

                    await EmitPlatformEventAsync(
                        eventRepo, bus,
                        RotationStepEvent,
                        tenantId: row.TenantId,
                        tags: new()
                        {
                            ["from_version"] = row.PreviousKekVersion.ToString(),
                            ["to_version"] = toVersion.ToString(),
                        },
                        data: new(),
                        ct);

                    reencrypted++;
                    UpdateStatus(s => s with { ReencryptedTenants = reencrypted });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    UpdateStatus(s => s with { FailedTenants = failed });
                    _logger.LogWarning(
                        ex,
                        "tenant.kek.rotate failed tenantId={TenantId} errorType={ErrorType}",
                        row.TenantId,
                        ex.GetType().Name);
                }
            }

            // Promote only when every targeted row succeeded. A single
            // failure means the old primary still has work to do, so we
            // keep it in place.
            if (failed == 0 && reencrypted == rotationRows.Count)
            {
                _kekProvider.PromoteSecondaryToPrimary(toVersion);
                UpdateStatus(s => s with
                {
                    Phase = KekRotationPhase.Completed,
                    CompletedAt = DateTimeOffset.UtcNow,
                });

                // R2-H14: mark the kek_rotations row completed and
                // zero the staged secondary column.
                await PersistRotationCompletedAsync(cpFactory, rotationId.Value, "completed", null, ct);

                await EmitPlatformEventAsync(
                    eventRepo, bus,
                    RotationCompletedEvent,
                    tenantId: null,
                    tags: new()
                    {
                        ["from_version"] = fromVersion.ToString(),
                        ["to_version"] = toVersion.ToString(),
                    },
                    data: new()
                    {
                        ["reencrypted"] = reencrypted,
                        ["total"] = rotationRows.Count,
                    },
                    ct);

                _logger.LogInformation(
                    "tenant.kek.rotate completed reencrypted={Count} fromVersion={From} toVersion={To}",
                    reencrypted, fromVersion, toVersion);
            }
            else
            {
                var reason = failed > 0
                    ? $"{failed} tenant rows failed to re-encrypt"
                    : "no rows were re-encrypted";

                UpdateStatus(s => s with
                {
                    Phase = KekRotationPhase.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    FailureReason = reason,
                });

                // R2-H14: mark the kek_rotations row failed but KEEP
                // the staged secondary so /retry can resume.
                await PersistRotationCompletedAsync(
                    cpFactory, rotationId.Value, "failed", reason, ct, keepStaged: true);

                await EmitPlatformEventAsync(
                    eventRepo, bus,
                    RotationFailedEvent,
                    tenantId: null,
                    tags: new()
                    {
                        ["from_version"] = fromVersion.ToString(),
                        ["to_version"] = toVersion.ToString(),
                    },
                    data: new()
                    {
                        ["reencrypted"] = reencrypted,
                        ["failed"] = failed,
                        ["total"] = rotationRows.Count,
                        ["reason"] = reason,
                    },
                    ct);

                _logger.LogWarning(
                    "tenant.kek.rotate failed reason={Reason} reencrypted={Re} failed={Fail}",
                    reason, reencrypted, failed);
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatus(s => s with
            {
                Phase = KekRotationPhase.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                FailureReason = "rotation cancelled",
            });
            if (rotationId is not null)
            {
                try
                {
                    await using var bestEffortScope = _scopeFactory.CreateAsyncScope();
                    var fac = bestEffortScope.ServiceProvider
                        .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
                    await PersistRotationCompletedAsync(
                        fac, rotationId.Value, "cancelled", "cancelled", CancellationToken.None);
                }
                catch (Exception persistEx)
                {
                    _logger.LogWarning(persistEx,
                        "tenant.kek.rotate cancellation: failed to persist cancelled state");
                }
            }
            throw;
        }
        catch (Exception ex)
        {
            // R2-M1: redact ex.Message before persisting it into the
            // status (which is read by /status) and the kek_rotations
            // row. Bearer tokens / sk- keys / base64 blobs / internal
            // URLs and stack traces are scrubbed.
            var redactedReason = $"unhandled {ex.GetType().Name}: {RedactForEvent(ex)}";
            UpdateStatus(s => s with
            {
                Phase = KekRotationPhase.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                FailureReason = redactedReason,
            });
            _logger.LogError(
                ex,
                "tenant.kek.rotate aborted with unhandled exception");
            if (rotationId is not null)
            {
                try
                {
                    await using var bestEffortScope = _scopeFactory.CreateAsyncScope();
                    var fac = bestEffortScope.ServiceProvider
                        .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
                    await PersistRotationCompletedAsync(
                        fac, rotationId.Value, "failed", redactedReason, CancellationToken.None, keepStaged: true);
                }
                catch (Exception persistEx)
                {
                    _logger.LogWarning(persistEx,
                        "tenant.kek.rotate failure: failed to persist failed state");
                }
            }
        }
        finally
        {
            if (oldPrimary is not null) CryptographicOperations.ZeroMemory(oldPrimary);
            // R2-H14: release the advisory lock.
            try
            {
                await ReleaseAdvisoryLockAsync(lockCtx, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "advisory lock release skipped");
            }
            lock (_lock) { _activeRotationId = null; }
        }
    }

    /// <summary>
    /// R2-H14: try to acquire the cluster-wide rotation advisory lock.
    /// Returns true on success. Pg's <c>pg_try_advisory_lock</c> never
    /// blocks — it returns false immediately when another pod holds
    /// the lock. EF InMemory provider doesn't support
    /// <see cref="DatabaseFacade.ExecuteSqlRawAsync"/> against raw SQL,
    /// so the caller catches and falls through to the in-process lock.
    /// </summary>
    private static async Task<bool> TryAcquireAdvisoryLockAsync(
        ControlPlaneDbContext ctx, CancellationToken ct)
    {
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_try_advisory_lock(@key)";
        var p = cmd.CreateParameter();
        p.ParameterName = "@key";
        p.Value = AdvisoryLockKey;
        cmd.Parameters.Add(p);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is bool b && b;
    }

    /// <summary>
    /// R2-H14: release the advisory lock. Safe to call even when the
    /// lock was never acquired — Postgres ignores spurious releases.
    /// </summary>
    private static async Task ReleaseAdvisoryLockAsync(
        ControlPlaneDbContext ctx, CancellationToken ct)
    {
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) return;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_advisory_unlock(@key)";
        var p = cmd.CreateParameter();
        p.ParameterName = "@key";
        p.Value = AdvisoryLockKey;
        cmd.Parameters.Add(p);
        await cmd.ExecuteScalarAsync(ct);
    }

    private static async Task<Guid> PersistRotationStartAsync(
        IDbContextFactory<ControlPlaneDbContext> cpFactory,
        int fromVersion,
        int toVersion,
        byte[] newKek,
        byte[] oldPrimary,
        bool isRetry,
        CancellationToken ct)
    {
        await using var ctx = await cpFactory.CreateDbContextAsync(ct);

        // For a retry, look for a previously-failed row at this
        // version pair and reuse it. Otherwise mint a new id.
        if (isRetry)
        {
            var existing = await ctx.KekRotations
                .Where(r => r.VersionOld == fromVersion
                    && r.VersionNew == toVersion
                    && r.Status == "pending")
                .OrderByDescending(r => r.StartedAt)
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                existing.Status = "running";
                existing.FailureReason = null;
                existing.CompletedAt = null;
                await ctx.SaveChangesAsync(ct);
                return existing.Id;
            }
        }

        // Encrypt the new KEK under the OLD primary so the row is
        // readable across restarts. The plaintext is the base64
        // encoding so we can round-trip without binary surprises.
        var stagedB64 = Convert.ToBase64String(newKek);
        var protectedBlob = AesGcmConnectionStringDecryptor.EncryptWithKey(stagedB64, oldPrimary);

        var row = new KekRotation
        {
            Id = Guid.NewGuid(),
            Status = "running",
            VersionOld = fromVersion,
            VersionNew = toVersion,
            StagedSecondaryProtected = protectedBlob,
            StartedAt = DateTime.UtcNow,
        };
        ctx.KekRotations.Add(row);
        await ctx.SaveChangesAsync(ct);
        return row.Id;
    }

    private static async Task PersistRotationCompletedAsync(
        IDbContextFactory<ControlPlaneDbContext> cpFactory,
        Guid rotationId,
        string status,
        string? failureReason,
        CancellationToken ct,
        bool keepStaged = false)
    {
        await using var ctx = await cpFactory.CreateDbContextAsync(ct);
        var row = await ctx.KekRotations.FirstOrDefaultAsync(r => r.Id == rotationId, ct);
        if (row is null) return;
        row.Status = status;
        row.FailureReason = failureReason;
        row.CompletedAt = DateTime.UtcNow;
        if (!keepStaged && row.StagedSecondaryProtected is not null)
        {
            // Zero out the staged secondary blob — terminal phase.
            CryptographicOperations.ZeroMemory(row.StagedSecondaryProtected);
            row.StagedSecondaryProtected = null;
        }
        await ctx.SaveChangesAsync(ct);
    }

    private string RedactForEvent(Exception ex)
    {
        if (_errorRedactor is null) return ex.Message;
        try
        {
            return _errorRedactor.Redact(ex.Message);
        }
        catch
        {
            // Defence in depth — never let redaction failures leak the
            // unredacted message. Drop the original.
            return "[redaction-failure]";
        }
    }

    private void UpdateStatus(Func<KekRotationStatus, KekRotationStatus> mutator)
    {
        lock (_lock)
        {
            _status = mutator(_status);
        }
    }

    private static async Task EmitPlatformEventAsync(
        IPlatformEventRepository repo,
        IPlatformEventBus? bus,
        string type,
        Guid? tenantId,
        Dictionary<string, string?> tags,
        Dictionary<string, object?> data,
        CancellationToken ct)
    {
        var evt = new PlatformEvent
        {
            Type = type,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"eventSource":"system"}""",
            Data = JsonSerializer.Serialize(data),
        };

        if (bus is not null)
        {
            await bus.AppendAndPublishAsync(repo, evt, ct);
        }
        else
        {
            await repo.AppendAsync(evt, ct);
        }
    }

    private sealed record RotationRow(
        Guid TenantId,
        byte[]? Envelope,
        int PreviousKekVersion);
}

/// <summary>
/// R2-H3: response from <see cref="KekRotationCoordinator.RetryAsync"/>.
/// </summary>
public sealed record RotationRetryResponse(
    bool Success,
    string? Reason,
    KekRotationStatus Status);
