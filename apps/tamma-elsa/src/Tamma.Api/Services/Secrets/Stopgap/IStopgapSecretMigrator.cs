namespace Tamma.Api.Services.Secrets.Stopgap;

/// <summary>
/// Story 29-9 one-shot migrator. Walks
/// <see cref="StopgapSecretMap.Platform"/>, imports every non-empty
/// stopgap value into the cabinet, and emits
/// <see cref="SecretAuditEventTypes.MigratedSuccess"/> /
/// <see cref="SecretAuditEventTypes.MigratedFailed"/> /
/// <see cref="SecretAuditEventTypes.MigratedSkipped"/> events per
/// entry.
///
/// <para><b>Idempotency</b>: re-running the migrator on an
/// already-populated cabinet is a no-op for every already-imported
/// row — the migrator probes the cabinet by <c>(scope, name)</c> and
/// skips any row that already exists. New rows added to
/// <see cref="StopgapSecretMap.Platform"/> in a later release get
/// picked up by the next run.</para>
/// </summary>
public interface IStopgapSecretMigrator
{
    /// <summary>
    /// Execute the migration. Returns a
    /// <see cref="StopgapMigrationReport"/> summarising which entries
    /// were imported, skipped, or failed. Does not throw on per-row
    /// failure — the caller inspects the report to decide.
    /// </summary>
    Task<StopgapMigrationReport> RunAsync(
        Guid actorUserId, CancellationToken ct = default);
}

/// <summary>
/// Outcome of a single stopgap entry in the migration run.
/// </summary>
public enum StopgapMigrationOutcome
{
    /// <summary>Row imported into the cabinet for the first time.</summary>
    Imported,

    /// <summary>Cabinet row already existed — nothing to do.</summary>
    Skipped,

    /// <summary>Stopgap had no source value (config/env empty).</summary>
    NoSourceValue,

    /// <summary>Backend write failed mid-import — caller should retry.</summary>
    Failed,
}

/// <summary>
/// Single row in the migration report.
/// </summary>
public sealed record StopgapMigrationResult(
    string CabinetName,
    StopgapMigrationOutcome Outcome,
    string PreviousLocation,
    string? Detail);

/// <summary>
/// Aggregate report returned from
/// <see cref="IStopgapSecretMigrator.RunAsync"/>.
/// </summary>
public sealed record StopgapMigrationReport(
    IReadOnlyList<StopgapMigrationResult> Results,
    DateTimeOffset RanAt)
{
    public int ImportedCount =>
        Results.Count(r => r.Outcome == StopgapMigrationOutcome.Imported);
    public int SkippedCount =>
        Results.Count(r => r.Outcome == StopgapMigrationOutcome.Skipped);
    public int NoSourceCount =>
        Results.Count(r => r.Outcome == StopgapMigrationOutcome.NoSourceValue);
    public int FailedCount =>
        Results.Count(r => r.Outcome == StopgapMigrationOutcome.Failed);
}
