namespace Tamma.Activities.SecretsRotation.Contracts;

/// <summary>
/// Story 29-6 AC2 step 6 — thin port used by the activate step to
/// enqueue a "retire this version after the grace window" task.
/// Backed in production by <c>IPlatformQueuedTaskRepository</c>;
/// tests stub with an in-memory list.
///
/// <para>The grace window is represented as an absolute
/// <see cref="RunAfter"/> timestamp. The sweeper dequeues tasks whose
/// <see cref="RunAfter"/> is in the past and retires the referenced
/// version. Grace windows default to 15 minutes (Story 29-6 AC2); the
/// caller passes the concrete window so operator-adjusted windows
/// flow through.</para>
/// </summary>
public interface IRetireScheduler
{
    /// <summary>
    /// Enqueue a retirement task. Returns the task id (useful for tests
    /// asserting the row shape). Must be idempotent on
    /// <paramref name="rotationCorrelationId"/> so a replayed workflow
    /// doesn't double-enqueue.
    /// </summary>
    Task<Guid> ScheduleRetireAsync(
        Guid secretId,
        int versionNumber,
        Guid? tenantId,
        DateTimeOffset runAfter,
        string rotationCorrelationId,
        CancellationToken ct);

    /// <summary>
    /// Drain all due retire tasks (those with
    /// <c>RunAfter &lt;= DateTimeOffset.UtcNow</c>) and retire the
    /// referenced versions via the rotation gateway + optionally the
    /// handler's <c>RevokeOldAsync</c> hook. Returns the number of
    /// tasks processed. Idempotent — a version already in <c>Revoked</c>
    /// state is a no-op (the task still completes).
    /// </summary>
    Task<int> SweepDueRetireTasksAsync(CancellationToken ct);
}
