using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// at a time). The actual re-encrypt loop runs on a background
/// <see cref="Task"/> kicked off by
/// <see cref="StartAsync"/> so the API call returns 202 immediately.</para>
///
/// <para>Steps performed by the coordinator:</para>
/// <list type="number">
///   <item><description>Mint a fresh 32-byte KEK. Stage it as the
///     <see cref="KekProvider"/> secondary so concurrent decrypt
///     traffic can fall back to the previous primary.</description></item>
///   <item><description>List every <c>tenants</c> row that still has
///     <c>EncryptedConnectionString IS NOT NULL</c> and
///     <c>KekVersion &lt; targetVersion</c>.</description></item>
///   <item><description>Per row: decrypt with the OLD primary, re-encrypt
///     with the NEW key, persist the new envelope + bumped
///     <c>KekVersion</c>, evict the resolver pool cache (so the next
///     access decrypts fresh), publish a
///     <c>TENANT.CONNECTION_STRING_ROTATED.SUCCESS</c>
///     <see cref="PlatformEvent"/>.</description></item>
///   <item><description>Promote the staged secondary to primary via
///     <see cref="KekProvider.PromoteSecondaryToPrimary"/>. The previous
///     primary is now retired and zeroed.</description></item>
///   <item><description>Emit a final
///     <c>SECRETS.KEK.ROTATION.COMPLETED</c> platform event with the
///     row counts.</description></item>
/// </list>
///
/// <para>Cache invalidation: <see cref="ITenantConnectionResolver.EvictAsync"/>
/// is the documented seam for this. Story 28-4 noted that an
/// <see cref="IPlatformEventBus"/> subscriber would be cleaner, but
/// the bus didn't exist when 28-4 shipped. The 28-6 bus is now in
/// place; the coordinator publishes events via
/// <see cref="IPlatformEventBus.AppendAndPublishAsync"/> AND calls
/// <see cref="ITenantConnectionResolver.EvictAsync"/> directly. The
/// double channel is intentional — the bus subscriber for cross-pod
/// fanout is a Phase-3 follow-up; in-process eviction needs to happen
/// synchronously here so the next request on this pod reads the
/// rotated row, not the cached one.</para>
///
/// <para>Failure modes:</para>
/// <list type="bullet">
///   <item><description>A single row failing decrypt does NOT abort
///     the rotation — the row stays at the old <c>KekVersion</c> and
///     gets counted under <c>FailedTenants</c>. The operator inspects
///     the structured logs and either fixes the row by hand or
///     re-runs the rotation (idempotent: rows already at the new
///     <c>KekVersion</c> are skipped).</description></item>
///   <item><description>If every row failed (e.g. wrong primary KEK
///     deployed), the coordinator does NOT promote the secondary —
///     the old primary stays so live traffic continues working. The
///     operator pulls the bad secondary, fixes the deploy, re-runs.</description></item>
/// </list>
/// </summary>
public sealed class KekRotationCoordinator
{
    private const string RotationStartedEvent = "SECRETS.KEK.ROTATION.STARTED";
    private const string RotationStepEvent = "TENANT.CONNECTION_STRING_ROTATED.SUCCESS";
    private const string RotationCompletedEvent = "SECRETS.KEK.ROTATION.COMPLETED";
    private const string RotationFailedEvent = "SECRETS.KEK.ROTATION.FAILED";
    private const int KekSize = 32;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KekProvider _kekProvider;
    private readonly ITenantConnectionResolver _resolver;
    private readonly ILogger<KekRotationCoordinator> _logger;

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

    public KekRotationCoordinator(
        IServiceScopeFactory scopeFactory,
        KekProvider kekProvider,
        ITenantConnectionResolver resolver,
        ILogger<KekRotationCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(kekProvider);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _kekProvider = kekProvider;
        _resolver = resolver;
        _logger = logger;
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
                () => RunRotationAsync(generated, fromVersion, toVersion, actor, cancellationToken),
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
        RotationActor actor,
        CancellationToken ct)
    {
        byte[]? oldPrimary = null;
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
                actor: actor,
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
                        actor: actor,
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
            throw;
        }
        catch (Exception ex)
        {
            UpdateStatus(s => s with
            {
                Phase = KekRotationPhase.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                FailureReason = $"unhandled {ex.GetType().Name}: {ex.Message}",
            });
            _logger.LogError(
                ex,
                "tenant.kek.rotate aborted with unhandled exception");
        }
        finally
        {
            if (oldPrimary is not null) CryptographicOperations.ZeroMemory(oldPrimary);
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
