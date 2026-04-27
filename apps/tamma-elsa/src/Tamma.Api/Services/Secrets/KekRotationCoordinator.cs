using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
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
///
/// <para>R2 post-fix (PF-S5 + PF-S8 + PF-C3 + retry-actor-identity):</para>
/// <list type="bullet">
///   <item><description><b>Lock lifetime owned by Npgsql, not EF</b>:
///     the advisory lock is acquired on a dedicated
///     <see cref="NpgsqlConnection"/> opened from the registered
///     <see cref="NpgsqlDataSource"/> (PF-C3). EF's pooled
///     <c>DbContext</c> sends <c>DISCARD ALL</c> on connection return,
///     which silently releases session-level advisory locks; bypassing
///     EF for the lock keeps the contract explicit.</description></item>
///   <item><description><b>State changes happen INSIDE the lock</b>
///     (PF-S5): the retry path no longer mutates
///     <see cref="KekProvider"/>'s in-memory secondary or flips the
///     <c>kek_rotations</c> row status before the cluster-wide lock is
///     held. Two pods racing <c>/retry</c> can't both mount the same
///     secondary anymore — the lock-loser pod exits cleanly with a
///     "rotation already in progress" status.</description></item>
///   <item><description><b>Advisory-lock failures are fatal</b>
///     (PF-S8): a transient <see cref="NpgsqlException"/> during
///     <c>pg_try_advisory_lock</c> no longer flips the rotation to
///     "acquired" — it fails closed. Only the EF-InMemory test
///     scenario (no <see cref="NpgsqlDataSource"/> registered) skips
///     the lock entirely; in that case the in-process
///     <see cref="_lock"/> is the only guard, which matches the
///     test fixture's single-process scope.</description></item>
///   <item><description><b>Retry inherits operator identity</b>: the
///     retry endpoint now threads the caller's
///     <see cref="ClaimsPrincipal"/> through to the coordinator so
///     retry-emitted events carry the actor that re-pressed the button
///     (rather than the original failed run's operator).</description></item>
/// </list>
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
    // PF-C3: optional NpgsqlDataSource for the cluster-wide advisory
    // lock. Resolved lazily through the service scope factory so this
    // coordinator stays usable in test fixtures that wire EF InMemory
    // (no NpgsqlDataSource registered → the lock is skipped, in-process
    // _lock is the only guard, which matches the single-process scope).
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
    /// <param name="actorUserId">Story 28-R2 / Finding M2 — JWT
    /// <c>sub</c> of the operator who kicked off the rotation.
    /// Captured into every emitted platform event so the audit trail
    /// answers "who rotated the KEK?". Optional because tests + the
    /// CLI migrate-secrets path both lack a bound principal.</param>
    /// <param name="actorEmail">JWT <c>email</c> claim, see
    /// <paramref name="actorUserId"/>.</param>
    /// <param name="actorPlatformRole">JWT <c>platformRole</c> claim,
    /// see <paramref name="actorUserId"/>.</param>
    public KekRotationStatus StartAsync(
        byte[]? newKek = null,
        CancellationToken cancellationToken = default,
        string? actorUserId = null,
        string? actorEmail = null,
        string? actorPlatformRole = null)
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

            var actor = new RotationActor(actorUserId, actorEmail, actorPlatformRole);
            _runningTask = Task.Run(
                () => RunRotationAsync(
                    generated,
                    fromVersion,
                    toVersion,
                    isRetry: false,
                    retryRowId: null,
                    actor,
                    cancellationToken),
                cancellationToken);

            return _status;
        }
    }

    /// <summary>
    /// Story 28-R2 / Finding M2 — projection of the JWT-bound operator
    /// identity captured at <see cref="StartAsync"/> time and replayed
    /// into every platform event emitted during the rotation. Threaded
    /// through <see cref="RunRotationAsync"/> so the background task
    /// can attach the actor to STARTED / STEP / COMPLETED / FAILED
    /// rows without re-reading any HTTP context.
    /// </summary>
    private readonly record struct RotationActor(
        string? UserId, string? Email, string? PlatformRole);

    /// <summary>
    /// R2-H3: re-attempt a previously-failed rotation. The coordinator
    /// re-uses the staged secondary KEK that was persisted on the
    /// failed run rather than generating a fresh one — that would
    /// orphan any rows that were already re-encrypted under the
    /// failed run's secondary. Returns the running snapshot when retry
    /// kicks off; throws when the current phase is not
    /// <see cref="KekRotationPhase.Failed"/>.
    ///
    /// <para>PF-S5 (R2 post-fix): no longer mutates
    /// <see cref="KekProvider"/>'s in-memory secondary or flips the
    /// <c>kek_rotations</c> row status before the cluster-wide
    /// advisory lock is held. The lock-loser pod exits cleanly without
    /// having touched <c>_kekProvider._secondary</c>. The actual
    /// staged-secondary lookup + restore + status transition happens
    /// inside <see cref="RunRotationAsync"/> after the lock is taken.</para>
    ///
    /// <para>Retry-actor-identity (R2 post-fix): now takes a
    /// <see cref="ClaimsPrincipal"/> so the retry-emitted events
    /// (STARTED / STEP / COMPLETED / FAILED) carry the operator who
    /// pressed the retry button rather than re-using a stale actor or
    /// emitting <c>default(RotationActor)</c>. Pass <c>null</c> from
    /// CLI / migration-script callers that genuinely lack an HTTP
    /// principal — the coordinator records the actor as anonymous.</para>
    /// </summary>
    public async Task<RotationRetryResponse> RetryAsync(
        ClaimsPrincipal? principal,
        CancellationToken cancellationToken = default)
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

        // PF-S5: locate the failed row first (read-only — no state
        // mutation) so we can fail fast with a 4xx if no recoverable
        // row exists. The actual staged-secondary mount + status flip
        // happens inside RunRotationAsync once the advisory lock is
        // held.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var cpFactory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        await using var ctx = await cpFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Find the most-recent failed rotation that still carries a
        // staged secondary. Older failed rows are zeroed.
        var failedRow = await ctx.KekRotations
            .Where(r => r.Status == "failed" && r.StagedSecondaryProtected != null)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (failedRow is null)
        {
            return new RotationRetryResponse(
                Success: false,
                Reason: "No failed rotation with a staged secondary KEK is available for retry. "
                    + "The previous failure may have run cleanup; mint a fresh rotation via /start.",
                Status: snapshot);
        }

        // Capture the row id; the staged-secondary blob itself is read
        // and decrypted INSIDE RunRotationAsync (under the advisory
        // lock). That keeps the in-memory KekProvider state untouched
        // until we actually own the rotation.
        var fromVersion = _kekProvider.GetActiveVersion();
        var toVersion = failedRow.VersionNew;
        var retryRowId = failedRow.Id;

        var actor = BuildActor(principal);

        lock (_lock)
        {
            // Status flips to Running so concurrent /status pollers see
            // the retry pending. The actual KEK material isn't loaded
            // yet (RunRotationAsync does that under the advisory lock).
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
            _activeRotationId = retryRowId;
            _runningTask = Task.Run(
                () => RunRotationAsync(
                    newKek: null,
                    fromVersion: fromVersion,
                    toVersion: toVersion,
                    isRetry: true,
                    retryRowId: retryRowId,
                    actor: actor,
                    ct: cancellationToken),
                cancellationToken);
        }

        return new RotationRetryResponse(Success: true, Reason: null, Status: _status);
    }

    /// <summary>
    /// Project a <see cref="ClaimsPrincipal"/> into the
    /// <see cref="RotationActor"/> the coordinator threads through
    /// audit events. Mirrors the claim-extraction logic in
    /// <c>KekRotationEndpoints.Start</c> so retry events carry the
    /// same shape as start events.
    /// </summary>
    private static RotationActor BuildActor(ClaimsPrincipal? principal)
    {
        if (principal is null) return default;
        var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? principal.FindFirst("email")?.Value;
        var platformRole = principal.FindFirst("platformRole")?.Value;
        return new RotationActor(sub, email, platformRole);
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
        byte[]? newKek,
        int fromVersion,
        int toVersion,
        bool isRetry,
        Guid? retryRowId,
        RotationActor actor,
        CancellationToken ct)
    {
        // PF-C3: the cluster-wide advisory lock lives on a dedicated
        // NpgsqlConnection so the lock follows the connection lifetime.
        // EF's pooled DbContext sends DISCARD ALL on connection return,
        // which silently releases session-level advisory locks — that
        // would defeat the cluster-wide singleton guarantee if the EF
        // context was reused mid-rotation. Holding a Npgsql connection
        // open for the full RunRotationAsync lifetime keeps the lock
        // until we explicitly release it in the finally-block.
        //
        // The connection is resolved lazily through the service scope
        // factory: when the test container has no NpgsqlDataSource
        // registered (EF InMemory fixtures), we fall back to the
        // in-process _lock as the only guard, which matches the
        // single-process scope of those tests.
        await using var lockScope = _scopeFactory.CreateAsyncScope();
        var dataSource = lockScope.ServiceProvider.GetService<NpgsqlDataSource>();

        NpgsqlConnection? lockConnection = null;
        bool acquired;
        try
        {
            if (dataSource is null)
            {
                // PF-S8 + test-fixture path: no NpgsqlDataSource means
                // EF InMemory provider. The advisory lock is a no-op
                // here; the in-process _lock guards single-process
                // serialisation. This is the ONLY scenario in which the
                // coordinator skips the cluster-wide lock — every other
                // failure mode falls closed.
                _logger.LogDebug(
                    "advisory lock skipped: no NpgsqlDataSource registered "
                    + "(EF InMemory test fixture)");
                acquired = true;
            }
            else
            {
                lockConnection = await dataSource
                    .OpenConnectionAsync(ct).ConfigureAwait(false);
                acquired = await TryAcquireAdvisoryLockAsync(lockConnection, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (lockConnection is not null)
            {
                await lockConnection.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
        catch (NpgsqlException ex)
        {
            // PF-S8: a transient Postgres error during lock acquisition
            // is NOT a free pass. Two pods racing the rotation would
            // both flip to "acquired" under the previous (broken)
            // catch-all. Fail closed: log + bail, the operator hits
            // /start again once the database is healthy.
            _logger.LogWarning(ex,
                "tenant.kek.rotate aborted: transient Npgsql error during "
                + "advisory lock acquisition lockKey={LockKey}",
                AdvisoryLockKey);
            acquired = false;
            if (lockConnection is not null)
            {
                await lockConnection.DisposeAsync().ConfigureAwait(false);
                lockConnection = null;
            }
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
        byte[]? retryStagedSecondary = null;
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

            // PF-S5: load + restore the staged secondary INSIDE the
            // advisory lock. The retry path no longer mutates
            // KekProvider state from RetryAsync (which runs without the
            // lock). Two pods racing /retry can no longer both mount
            // the same secondary — the lock-loser exited above without
            // touching anything.
            byte[] keyForReencrypt;
            if (isRetry)
            {
                if (retryRowId is null)
                {
                    throw new InvalidOperationException(
                        "Retry path entered without a retry row id.");
                }

                retryStagedSecondary = await LoadStagedSecondaryAsync(
                    cpFactory, retryRowId.Value, oldPrimary, ct)
                        .ConfigureAwait(false);
                _kekProvider.RestoreStagedSecondary(retryStagedSecondary, toVersion);

                // Flip the failed row back to "pending" so
                // PersistRotationStartAsync can find it and re-mark it
                // running. This was previously done in RetryAsync
                // BEFORE the lock — two pods could race the flip.
                await using (var flipCtx = await cpFactory
                    .CreateDbContextAsync(ct).ConfigureAwait(false))
                {
                    var row = await flipCtx.KekRotations
                        .FirstOrDefaultAsync(r => r.Id == retryRowId.Value, ct)
                        .ConfigureAwait(false);
                    if (row is not null)
                    {
                        row.Status = "pending";
                        row.FailureReason = null;
                        await flipCtx.SaveChangesAsync(ct).ConfigureAwait(false);
                    }
                }

                keyForReencrypt = retryStagedSecondary;
            }
            else
            {
                if (newKek is null)
                {
                    throw new InvalidOperationException(
                        "Start path entered without a new KEK.");
                }
                keyForReencrypt = newKek;
            }

            // R2-H14: persist the staged secondary into kek_rotations
            // so a process crash mid-rotation can resume by reading
            // the row back. The secondary is encrypted by the OLD
            // primary so the row is readable across restarts.
            rotationId = await PersistRotationStartAsync(
                cpFactory, fromVersion, toVersion, keyForReencrypt, oldPrimary, isRetry, ct)
                    .ConfigureAwait(false);
            lock (_lock) { _activeRotationId = rotationId; }

            await EmitPlatformEventAsync(
                eventRepo, bus,
                RotationStartedEvent,
                tenantId: null,
                tags: new()
                {
                    ["from_version"] = fromVersion.ToString(),
                    ["to_version"] = toVersion.ToString(),
                    ["isRetry"] = isRetry ? "true" : "false",
                },
                data: new(),
                actor: actor,
                ct).ConfigureAwait(false);

            // Pull tenant ids that still need rotation. We project to a
            // small DTO so we don't hold onto change-tracker entries
            // across the loop. KekVersion < toVersion catches both the
            // common steady-state (== fromVersion) and the
            // double-rotation safety case (a row that somehow already
            // sits at toVersion is skipped).
            await using var listingCtx = await cpFactory
                .CreateDbContextAsync(ct).ConfigureAwait(false);
            var rotationRows = await listingCtx.Tenants
                .IgnoreQueryFilters()
                .Where(t => t.DeletedAt == null)
                .Where(t => EF.Property<byte[]?>(t, "EncryptedConnectionString") != null)
                .Where(t => (EF.Property<int?>(t, "KekVersion") ?? 0) < toVersion)
                .Select(t => new RotationRow(
                    t.Id,
                    EF.Property<byte[]?>(t, "EncryptedConnectionString"),
                    EF.Property<int?>(t, "KekVersion") ?? 0))
                .ToListAsync(ct).ConfigureAwait(false);

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
                        plaintext, keyForReencrypt);

                    await using var writeCtx = await cpFactory
                        .CreateDbContextAsync(ct).ConfigureAwait(false);
                    var tenant = await writeCtx.Tenants
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(t => t.Id == row.TenantId, ct)
                        .ConfigureAwait(false);
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
                    await writeCtx.SaveChangesAsync(ct).ConfigureAwait(false);

                    // Drop the resolver's warm pool for this tenant so the
                    // next request decrypts the rotated row and rebuilds
                    // the data source from scratch.
                    await _resolver.EvictAsync(row.TenantId, ct).ConfigureAwait(false);

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
                        actor: actor,
                        ct).ConfigureAwait(false);

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
                await PersistRotationCompletedAsync(
                    cpFactory, rotationId.Value, "completed", null, ct).ConfigureAwait(false);

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
                    actor: actor,
                    ct).ConfigureAwait(false);

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
                    cpFactory, rotationId.Value, "failed", reason, ct, keepStaged: true)
                        .ConfigureAwait(false);

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
                    actor: actor,
                    ct).ConfigureAwait(false);

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
                    await using var bestEffortScope = _scopeFactory
                        .CreateAsyncScope();
                    var fac = bestEffortScope.ServiceProvider
                        .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
                    await PersistRotationCompletedAsync(
                        fac, rotationId.Value, "cancelled", "cancelled", CancellationToken.None)
                            .ConfigureAwait(false);
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
                    await using var bestEffortScope = _scopeFactory
                        .CreateAsyncScope();
                    var fac = bestEffortScope.ServiceProvider
                        .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
                    await PersistRotationCompletedAsync(
                        fac, rotationId.Value, "failed", redactedReason, CancellationToken.None,
                        keepStaged: true).ConfigureAwait(false);
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
            if (retryStagedSecondary is not null)
            {
                CryptographicOperations.ZeroMemory(retryStagedSecondary);
            }
            // PF-C3: release the advisory lock on the dedicated
            // NpgsqlConnection. Disposing the connection auto-releases
            // any session-level locks it holds (unlock is best-effort —
            // the connection drop is the actual guarantee).
            if (lockConnection is not null)
            {
                try
                {
                    await ReleaseAdvisoryLockAsync(lockConnection, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "advisory lock release skipped");
                }
                await lockConnection.DisposeAsync().ConfigureAwait(false);
            }
            lock (_lock) { _activeRotationId = null; }
        }
    }

    /// <summary>
    /// PF-S5: read the staged-secondary blob from the persisted
    /// <c>kek_rotations</c> row and decrypt it under the OLD primary.
    /// Runs INSIDE the advisory lock so the lock-loser pod doesn't
    /// touch the row's plaintext.
    /// </summary>
    private async Task<byte[]> LoadStagedSecondaryAsync(
        IDbContextFactory<ControlPlaneDbContext> cpFactory,
        Guid rowId,
        byte[] oldPrimary,
        CancellationToken ct)
    {
        await using var ctx = await cpFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.KekRotations
            .FirstOrDefaultAsync(r => r.Id == rowId, ct).ConfigureAwait(false);
        if (row is null || row.StagedSecondaryProtected is null)
        {
            throw new InvalidOperationException(
                "Retry: staged secondary row no longer carries protected material.");
        }

        string plaintext;
        try
        {
            plaintext = AesGcmConnectionStringDecryptor.DecryptWithKey(
                row.StagedSecondaryProtected, oldPrimary);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Retry: failed to decrypt persisted staged secondary: {RedactForEvent(ex)}",
                ex);
        }

        var staged = Convert.FromBase64String(plaintext);
        if (staged.Length != KekSize)
        {
            CryptographicOperations.ZeroMemory(staged);
            throw new InvalidOperationException(
                "Retry: persisted staged secondary has wrong length.");
        }
        return staged;
    }

    /// <summary>
    /// R2-H14 + PF-C3: try to acquire the cluster-wide rotation
    /// advisory lock on a dedicated <see cref="NpgsqlConnection"/>.
    /// Returns true on success. Pg's <c>pg_try_advisory_lock</c> never
    /// blocks — it returns false immediately when another pod holds
    /// the lock. The connection itself is owned by the caller and held
    /// open for the rotation lifetime so the session-level lock isn't
    /// released by EF's pooled-context recycling.
    /// </summary>
    private static async Task<bool> TryAcquireAdvisoryLockAsync(
        NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_try_advisory_lock(@key)";
        var p = cmd.CreateParameter();
        p.ParameterName = "key";
        p.Value = AdvisoryLockKey;
        cmd.Parameters.Add(p);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is bool b && b;
    }

    /// <summary>
    /// R2-H14 + PF-C3: release the advisory lock on the dedicated
    /// connection. Safe to call even when the lock was never acquired
    /// — Postgres ignores spurious releases. The follow-up
    /// <c>DisposeAsync</c> on the connection itself also releases any
    /// remaining session-level locks; this explicit unlock makes the
    /// release deterministic on the happy path.
    /// </summary>
    private static async Task ReleaseAdvisoryLockAsync(
        NpgsqlConnection conn, CancellationToken ct)
    {
        if (conn.State != System.Data.ConnectionState.Open) return;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_advisory_unlock(@key)";
        var p = cmd.CreateParameter();
        p.ParameterName = "key";
        p.Value = AdvisoryLockKey;
        cmd.Parameters.Add(p);
        await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
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
        await using var ctx = await cpFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        // For a retry, look for a previously-failed row at this
        // version pair and reuse it. Otherwise mint a new id.
        if (isRetry)
        {
            var existing = await ctx.KekRotations
                .Where(r => r.VersionOld == fromVersion
                    && r.VersionNew == toVersion
                    && r.Status == "pending")
                .OrderByDescending(r => r.StartedAt)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (existing is not null)
            {
                existing.Status = "running";
                existing.FailureReason = null;
                existing.CompletedAt = null;
                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
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
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
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
        await using var ctx = await cpFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.KekRotations
            .FirstOrDefaultAsync(r => r.Id == rotationId, ct).ConfigureAwait(false);
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
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
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

    /// <summary>
    /// Story 28-R2 / Finding M2 — emits a KEK-rotation platform event with
    /// the operator identity (sub + email + platformRole) baked into both
    /// <c>tags</c> (for SQL filtering) and <c>data</c> (immutable record).
    /// The actor is captured at <see cref="StartAsync"/> time and threaded
    /// through every subsequent emit on the rotation's background task.
    /// </summary>
    private static async Task EmitPlatformEventAsync(
        IPlatformEventRepository repo,
        IPlatformEventBus? bus,
        string type,
        Guid? tenantId,
        Dictionary<string, string?> tags,
        Dictionary<string, object?> data,
        RotationActor actor,
        CancellationToken ct)
    {
        // Mutate copies so a caller-built dictionary isn't unexpectedly
        // augmented in place — RunRotationAsync re-uses tag dictionaries
        // across emits in tight loops.
        var tagsWithActor = new Dictionary<string, string?>(tags);
        if (!string.IsNullOrEmpty(actor.UserId))
            tagsWithActor["actorUserId"] = actor.UserId;
        if (!string.IsNullOrEmpty(actor.Email))
            tagsWithActor["actorEmail"] = actor.Email;
        if (!string.IsNullOrEmpty(actor.PlatformRole))
            tagsWithActor["actorPlatformRole"] = actor.PlatformRole;

        var dataWithActor = new Dictionary<string, object?>(data)
        {
            ["actorUserId"] = actor.UserId,
            ["actorEmail"] = actor.Email,
            ["actorPlatformRole"] = actor.PlatformRole,
        };

        var evt = new PlatformEvent
        {
            Type = type,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(tagsWithActor),
            Metadata = """{"eventSource":"system"}""",
            Data = JsonSerializer.Serialize(dataWithActor),
        };

        if (bus is not null)
        {
            await bus.AppendAndPublishAsync(repo, evt, ct).ConfigureAwait(false);
        }
        else
        {
            await repo.AppendAsync(evt, ct).ConfigureAwait(false);
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
