namespace Tamma.Api.Services.Secrets.Rotation;

/// <summary>
/// Story 29-6 AC8 — JSON payload shape for a <c>RETIRE_SECRET_VERSION</c>
/// <c>platform_queued_tasks</c> row. Enqueued by
/// <see cref="RetireScheduler.ScheduleRetireAsync"/> at the end of a
/// successful rotation and drained either by the per-task
/// <c>RetireSecretVersionTaskHandler</c> (the AC8-specified
/// <c>PlatformTaskWorker</c> route) or by
/// <see cref="RetireScheduler.SweepDueRetireTasksAsync"/> (periodic
/// fallback). <c>PlatformQueuedTask</c> does not model a first-class
/// <c>RunAfter</c> column, so the grace-window deadline travels in the
/// payload and the drainer enforces it.
/// </summary>
public sealed class RetireTaskPayload
{
    /// <summary>Secret whose old version is being retired.</summary>
    public Guid SecretId { get; set; }

    /// <summary>Monotonic version number to flip <c>RetiredGrace → Revoked</c>.</summary>
    public int VersionNumber { get; set; }

    /// <summary>
    /// Absolute UTC deadline — the drainer refuses to retire before
    /// this instant (re-queues as retryable) so the grace window is
    /// honoured even though the queue has no run-after column.
    /// </summary>
    public DateTimeOffset RunAfter { get; set; }

    /// <summary>Correlation id of the rotation saga that scheduled this.</summary>
    public string RotationCorrelationId { get; set; } = string.Empty;
}
