using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Secrets.Stopgap;

/// <summary>
/// Default <see cref="IStopgapSecretMigrator"/>. Walks
/// <see cref="StopgapSecretMap.Platform"/>, inserts a parent
/// <see cref="SecretRow"/> + first <see cref="SecretVersionRow"/> per
/// missing entry via the existing
/// <see cref="IDbContextFactory{SecretsDbContext}"/> +
/// <see cref="ISecretStoreBackend"/> plumbing established by Story 29-2.
///
/// <para>The migrator is deliberately thin — it does not own its own
/// crypto path (that's <see cref="ISecretStoreBackend"/>'s job) and
/// does not emit domain events outside the secrets audit pipe (that's
/// <see cref="ISecretAccessAuditor"/>'s job). Its sole concern is
/// correctness + idempotency of the import semantics.</para>
///
/// <para>On any per-row failure the migrator logs + emits
/// <see cref="SecretAuditEventTypes.MigratedFailed"/> but continues to
/// the next entry so a transient backend hiccup on a single key does
/// not block the rest of the import.</para>
/// </summary>
public sealed class StopgapSecretMigrator : IStopgapSecretMigrator
{
    private readonly IDbContextFactory<SecretsDbContext> _secretsFactory;
    private readonly ISecretStoreBackend _backend;
    private readonly ISecretAccessAuditor _auditor;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StopgapSecretMigrator> _logger;

    public StopgapSecretMigrator(
        IDbContextFactory<SecretsDbContext> secretsFactory,
        ISecretStoreBackend backend,
        ISecretAccessAuditor auditor,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<StopgapSecretMigrator> logger)
    {
        ArgumentNullException.ThrowIfNull(secretsFactory);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(auditor);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _secretsFactory = secretsFactory;
        _backend = backend;
        _auditor = auditor;
        _configuration = configuration;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<StopgapMigrationReport> RunAsync(
        Guid actorUserId, CancellationToken ct = default)
    {
        var results = new List<StopgapMigrationResult>();
        var now = _timeProvider.GetUtcNow();

        foreach (var descriptor in StopgapSecretMap.Platform)
        {
            ct.ThrowIfCancellationRequested();
            var result = await MigrateOneAsync(
                descriptor, actorUserId, now, ct).ConfigureAwait(false);
            results.Add(result);
        }

        var report = new StopgapMigrationReport(results, now);
        _logger.LogInformation(
            "Stopgap secret migration complete: imported={Imported} " +
            "skipped={Skipped} no_source={NoSource} failed={Failed}",
            report.ImportedCount, report.SkippedCount,
            report.NoSourceCount, report.FailedCount);
        return report;
    }

    private async Task<StopgapMigrationResult> MigrateOneAsync(
        StopgapSecretDescriptor descriptor,
        Guid actorUserId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var reference = SecretRef.ForPlatform(descriptor.CabinetName);

        // Idempotency gate: cabinet row already exists → skip.
        if (await CabinetRowExistsAsync(descriptor.CabinetName, ct)
            .ConfigureAwait(false))
        {
            await EmitAuditAsync(
                SecretAuditEventTypes.MigratedSkipped,
                reference, actorUserId, versionNumber: null,
                SecretAuditOutcome.Success,
                detail: $"already_present; previousLocation={descriptor.PreviousLocation}",
                now, ct).ConfigureAwait(false);
            return new StopgapMigrationResult(
                descriptor.CabinetName,
                StopgapMigrationOutcome.Skipped,
                descriptor.PreviousLocation,
                Detail: "cabinet_row_already_present");
        }

        // Resolve source value. Missing value → emit MigratedFailed
        // with a clear detail; caller decides whether to escalate.
        var plaintext = descriptor.ResolveFromConfig(_configuration);
        if (string.IsNullOrEmpty(plaintext))
        {
            await EmitAuditAsync(
                SecretAuditEventTypes.MigratedFailed,
                reference, actorUserId, versionNumber: null,
                SecretAuditOutcome.Failure,
                detail: $"no_source_value; previousLocation={descriptor.PreviousLocation}",
                now, ct).ConfigureAwait(false);
            return new StopgapMigrationResult(
                descriptor.CabinetName,
                StopgapMigrationOutcome.NoSourceValue,
                descriptor.PreviousLocation,
                Detail: "no_source_value");
        }

        try
        {
            var metadata = SecretMetadataFactory.Create(
                descriptor.CabinetName,
                SecretScope.Platform,
                tenantId: null,
                descriptor.Purpose,
                new[] { descriptor.Consumer },
                actorUserId == Guid.Empty
                    ? DeterministicSystemActor
                    : actorUserId,
                descriptor.BuildSchedule(),
                now);

            await PersistParentRowAsync(metadata, now, ct).ConfigureAwait(false);

            try
            {
                await _backend.PutVersionAsync(
                    metadata.Id, versionNumber: 1, plaintext, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Backend PutVersion failed for stopgap {CabinetName}",
                    descriptor.CabinetName);
                await DeleteParentRowAsync(metadata.Id, ct).ConfigureAwait(false);
                await EmitAuditAsync(
                    SecretAuditEventTypes.MigratedFailed,
                    reference, actorUserId, versionNumber: 1,
                    SecretAuditOutcome.Failure,
                    detail: $"backend_putversion_failed; previousLocation={descriptor.PreviousLocation}",
                    now, ct).ConfigureAwait(false);
                return new StopgapMigrationResult(
                    descriptor.CabinetName,
                    StopgapMigrationOutcome.Failed,
                    descriptor.PreviousLocation,
                    Detail: "backend_putversion_failed");
            }

            await ActivateFirstVersionAsync(
                metadata.Id, actorUserId, now, ct).ConfigureAwait(false);

            await EmitAuditAsync(
                SecretAuditEventTypes.MigratedSuccess,
                reference, actorUserId, versionNumber: 1,
                SecretAuditOutcome.Success,
                detail: $"source=config; previousLocation={descriptor.PreviousLocation}",
                now, ct).ConfigureAwait(false);

            return new StopgapMigrationResult(
                descriptor.CabinetName,
                StopgapMigrationOutcome.Imported,
                descriptor.PreviousLocation,
                Detail: $"imported_from={descriptor.PreviousLocation}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error migrating stopgap {CabinetName}",
                descriptor.CabinetName);
            await EmitAuditAsync(
                SecretAuditEventTypes.MigratedFailed,
                reference, actorUserId, versionNumber: null,
                SecretAuditOutcome.Failure,
                detail: $"unexpected:{Truncate(ex.Message, 160)}",
                now, ct).ConfigureAwait(false);
            return new StopgapMigrationResult(
                descriptor.CabinetName,
                StopgapMigrationOutcome.Failed,
                descriptor.PreviousLocation,
                Detail: Truncate(ex.Message, 200));
        }
    }

    // Deterministic placeholder for system-initiated imports. The
    // cabinet requires a non-empty owner GUID; we use a fixed one so
    // migrations emitted by the CLI (no authenticated user) produce
    // the same owner row across runs.
    private static readonly Guid DeterministicSystemActor =
        Guid.Parse("00000000-0000-0000-0000-000000000029");

    private async Task<bool> CabinetRowExistsAsync(
        string cabinetName, CancellationToken ct)
    {
        await using var ctx = await _secretsFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ctx.Secrets
            .AsNoTracking()
            .AnyAsync(
                s => s.Name == cabinetName && s.Scope == "platform",
                ct)
            .ConfigureAwait(false);
    }

    private async Task PersistParentRowAsync(
        SecretMetadata metadata, DateTimeOffset now, CancellationToken ct)
    {
        await using var ctx = await _secretsFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        var row = new SecretRow
        {
            Id = metadata.Id,
            Name = metadata.Name,
            Scope = metadata.Scope.ToString().ToLowerInvariant(),
            TenantId = metadata.TenantId,
            Purpose = metadata.Purpose.ToString(),
            OwnerUserId = metadata.OwnerUserId,
            ActiveVersionNumber = 0,
            LastRotatedAt = null,
            NextRotationDueAt = metadata.NextRotationDueAt?.UtcDateTime,
            CreatedAt = metadata.CreatedAt.UtcDateTime,
            UpdatedAt = metadata.UpdatedAt.UtcDateTime,
            ConsumerRefsJson = SerializeConsumers(metadata.ConsumerRefs),
            RotationScheduleJson = SerializeSchedule(metadata.RotationSchedule),
        };
        ctx.Secrets.Add(row);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task DeleteParentRowAsync(Guid secretId, CancellationToken ct)
    {
        try
        {
            await using var ctx = await _secretsFactory
                .CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await ctx.Secrets
                .FirstOrDefaultAsync(s => s.Id == secretId, ct)
                .ConfigureAwait(false);
            if (row is not null)
            {
                ctx.Secrets.Remove(row);
                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Swallow — the enclosing failure has already been audited
            // and the caller will surface a Failed result. Best-effort
            // rollback only.
            _logger.LogWarning(ex,
                "Rollback of parent row for {SecretId} failed; " +
                "next migrate-secrets run will re-probe via idempotent " +
                "presence check.", secretId);
        }
    }

    private async Task ActivateFirstVersionAsync(
        Guid secretId, Guid actorUserId, DateTimeOffset now, CancellationToken ct)
    {
        await using var ctx = await _secretsFactory
            .CreateDbContextAsync(ct).ConfigureAwait(false);

        var versionRow = await ctx.SecretVersions
            .FirstOrDefaultAsync(
                v => v.SecretId == secretId && v.VersionNumber == 1, ct)
            .ConfigureAwait(false);
        if (versionRow is not null)
        {
            versionRow.Status = "active";
            versionRow.ActivatedAt = now.UtcDateTime;
            versionRow.CreatedByUserId = actorUserId == Guid.Empty
                ? DeterministicSystemActor
                : actorUserId;
        }

        var secretRow = await ctx.Secrets
            .FirstOrDefaultAsync(s => s.Id == secretId, ct)
            .ConfigureAwait(false);
        if (secretRow is not null)
        {
            secretRow.ActiveVersionNumber = 1;
            // Per Story 29-9 AC9: LastRotatedAt stamped at import time
            // so rotation overdue calculations start from NOW.
            secretRow.LastRotatedAt = now.UtcDateTime;
            secretRow.UpdatedAt = now.UtcDateTime;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task EmitAuditAsync(
        string eventType,
        SecretRef reference,
        Guid actorUserId,
        int? versionNumber,
        SecretAuditOutcome outcome,
        string detail,
        DateTimeOffset now,
        CancellationToken ct)
    {
        try
        {
            await _auditor.EmitAsync(
                new SecretAuditEvent(
                    EventType: eventType,
                    Reference: reference,
                    ActorUserId: actorUserId,
                    VersionNumber: versionNumber,
                    Outcome: outcome,
                    Detail: detail,
                    OccurredAt: now),
                ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Auditor contract forbids throwing on persistence failure —
            // log and continue so a broken audit pipe does not break the
            // migration flow.
            _logger.LogWarning(ex,
                "Audit emit failed for {EventType} on {CabinetName}; continuing.",
                eventType, reference.Name);
        }
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

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max
            ? value
            : value.Substring(0, max);
}
