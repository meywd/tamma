namespace Tamma.Data.Abstractions;

/// <summary>
/// Story 44-1 follow-up (2026-07-30) — the operational wrapper around
/// <see cref="ITenantMigrationSweeper"/>. The sweeper knows how to migrate a
/// fleet; the RUNNER owns the three operational properties an HTTP-triggered
/// fleet-wide DDL primitive needs and the raw sweeper cannot provide:
///
/// <list type="number">
///   <item><b>Single-flight.</b> Two concurrent apply sweeps double-migrate
///   every tenant. The guard is CLUSTER-wide (a Postgres session-scoped
///   <c>pg_try_advisory_lock</c> on the control-plane connection, the
///   <c>HourlyAnalyticsRollupScheduler</c> / <c>ScheduleLockKey</c> idiom) —
///   a per-process lock is decoration on a multi-pod deploy, where the two
///   racing POSTs are exactly as likely to land on two different pods.</item>
///   <item><b>Prompt return.</b> An apply sweep over a large fleet outlives
///   any proxy/client timeout; the caller then sees a 504 while the sweep
///   keeps running and never learns the outcome. The run is started in the
///   background and identified by a <see cref="TenantMigrationSweepRun.RunId"/>
///   the caller polls — the 202-plus-status-poll shape the provisioning and
///   tenant-move admin endpoints already use.</item>
///   <item><b>Run bookkeeping.</b> A bounded in-memory ring of recent runs so
///   the poll has something to read after completion.</item>
/// </list>
///
/// <para><b>Scope of the guard:</b> only APPLY sweeps take the lock. A dry run
/// writes nothing — two concurrent dry runs are wasted metadata reads, never a
/// double-migration — and refusing "what would change?" while a long apply
/// runs would remove the one question an operator most wants answered mid-run.
/// Dry runs are NOT unbounded though: they are capped by a separate, much
/// looser admission gate (<c>TenantMigrationSweepRunner.MaxConcurrentDryRuns</c>)
/// because each one opens a pooled connection per tenant, N-way parallel, and a
/// repeated curl would otherwise amplify one request into arbitrarily many
/// concurrent fleet-wide connection walks. Over the cap the start is refused
/// with <see cref="TenantMigrationSweepConflict.ScopeDryRunCapacity"/> —
/// a capacity refusal, not a single-flight refusal.</para>
///
/// <para><b>Where run state lives:</b> in the process that accepted the POST.
/// A poll that lands on another pod cannot see the run; the runner exposes
/// <see cref="IsSweepRunningAsync"/> so that case is reported honestly
/// (<c>run_not_found_on_this_instance</c> + whether a sweep holds the
/// cluster lock somewhere) instead of being mistaken for "the run vanished".
/// Durable, cluster-visible run rows would need a control-plane table; that is
/// deliberately not built here — the lock already prevents the damage, and the
/// operator's fallback (poll again, or re-POST and read the 409) is honest.</para>
/// </summary>
public interface ITenantMigrationSweepRunner
{
    /// <summary>
    /// Start a sweep in the background. Returns immediately: either
    /// <see cref="TenantMigrationSweepStart.Accepted"/> with the new run, or a
    /// <see cref="TenantMigrationSweepStart.Conflict"/> describing the sweep
    /// that already holds the single-flight gate.
    /// </summary>
    Task<TenantMigrationSweepStart> StartAsync(
        bool dryRun,
        int maxConcurrency = TenantMigrationSweep.DefaultMaxConcurrency,
        CancellationToken ct = default);

    /// <summary>
    /// Snapshot of a run started by THIS process, or <c>null</c> if this
    /// process never saw that id (unknown id, evicted from the ring, or the
    /// run belongs to another pod).
    /// </summary>
    TenantMigrationSweepRun? TryGetRun(Guid runId);

    /// <summary>
    /// Best-effort cluster-wide probe: is an apply sweep holding the advisory
    /// lock right now (on any pod)? Read-only — it inspects <c>pg_locks</c>
    /// rather than acquiring, so probing can never steal the gate from a
    /// starting sweep. <c>false</c> on a non-Postgres provider.
    /// </summary>
    Task<bool> IsSweepRunningAsync(CancellationToken ct = default);
}

/// <summary>Lifecycle states of a sweep run.</summary>
public static class TenantMigrationSweepRunState
{
    /// <summary>The sweep is executing on this process.</summary>
    public const string Running = "running";

    /// <summary>The sweep finished; <see cref="TenantMigrationSweepRun.Result"/> is populated.</summary>
    public const string Completed = "completed";

    /// <summary>
    /// The sweep itself threw (not a per-tenant failure, which is a result row).
    /// <see cref="TenantMigrationSweepRun.Result"/> is still populated, with the
    /// PARTIAL set of tenants that completed before the throw
    /// (<see cref="TenantMigrationSweepRun.ResultIsPartial"/> is true) — see the
    /// note on that property for why a failed fleet-DDL run may not answer
    /// "which tenants got the DDL?" with silence.
    /// </summary>
    public const string Failed = "failed";
}

/// <summary>A background sweep run's observable state.</summary>
public sealed record TenantMigrationSweepRun(
    Guid RunId,
    string State,
    bool DryRun,
    int MaxConcurrency,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    TenantMigrationSweepResult? Result,
    /// <summary>
    /// True when <see cref="Result"/> holds only the tenants that finished
    /// before the sweep died, not the whole fleet. A fleet-DDL primitive that
    /// reports nothing after a partial failure leaves the operator unable to
    /// tell which tenants already carry the new schema — the single worst
    /// post-failure state this endpoint can be in — so the runner keeps every
    /// per-tenant row it observed and flags the set as incomplete rather than
    /// discarding it. Tenants absent from a partial result were either never
    /// attempted or were in flight when the sweep died.
    /// </summary>
    bool ResultIsPartial = false);

/// <summary>
/// Why a start was refused. <see cref="Scope"/> is <c>this-instance</c> when
/// this process owns the running apply sweep (then <see cref="RunId"/> and
/// <see cref="StartedAt"/> are exact), <c>another-instance</c> when the cluster
/// advisory lock is held elsewhere (this process cannot know the remote run's
/// id or start time, and says so rather than inventing one), or
/// <c>dry-run-capacity</c> when too many background dry runs are already in
/// flight on this instance.
/// </summary>
public sealed record TenantMigrationSweepConflict(
    string Scope,
    Guid? RunId,
    DateTimeOffset? StartedAt)
{
    public const string ScopeThisInstance = "this-instance";
    public const string ScopeAnotherInstance = "another-instance";

    /// <summary>
    /// Not a single-flight refusal — concurrent dry runs are legitimate and
    /// deliberately ungated by the apply lock. This is the admission cap that
    /// stops one repeated curl from amplifying into unbounded concurrent
    /// fleet-wide connection walks. Retryable the moment a slot frees, which
    /// is why the HTTP layer answers 429 rather than 409.
    /// </summary>
    public const string ScopeDryRunCapacity = "dry-run-capacity";
}

/// <summary>The outcome of <see cref="ITenantMigrationSweepRunner.StartAsync"/>.</summary>
public sealed record TenantMigrationSweepStart(
    bool Accepted,
    TenantMigrationSweepRun? Run,
    TenantMigrationSweepConflict? Conflict);
