namespace Tamma.Data.Entities;

/// <summary>
/// Pre-routing task queue on the control plane — same shape as
/// <see cref="QueuedTask"/> but for work items that exist before the
/// tenant has been resolved (e.g. raw GitHub webhook delivery the
/// installation router needs to inspect to pick a tenant DB).
///
/// <para>Doc 01 §1.2 row 24: queued tasks with no tenant context yet
/// (installation routing, admin-level tasks) live in CP. Once the router
/// resolves a tenant, the task is re-enqueued to the tenant DB's
/// <c>queued_tasks</c> table and the CP row is marked <c>completed</c>.</para>
/// </summary>
public class PlatformQueuedTask
{
    public Guid Id { get; set; }
    public string Type { get; set; } = null!;
    public Guid? TenantId { get; set; }
    public long? InstallationId { get; set; }
    public string Payload { get; set; } = "{}";
    public string Status { get; set; } = "pending";
    public string? Error { get; set; }
    public int RetryCount { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Round-2 M8 — id of the worker that currently holds the row's
    /// reservation. Set by <c>ReserveNextAsync(workerId, ...)</c>;
    /// cleared when the row returns to <c>pending</c> (failure retry,
    /// reaper recovery). NULL on rows that have never been claimed.
    /// </summary>
    public string? ClaimedBy { get; set; }

    /// <summary>
    /// Round-2 H8 — set when a poll observes the row but no
    /// <c>IPlatformTaskHandler</c> is registered for its
    /// <see cref="Type"/>. Increments the retry budget without flipping
    /// to <c>dead_letter</c>; the next deploy that registers the
    /// handler can pick the row up. Falls through to <c>dead_letter</c>
    /// only after <c>RetryCount</c> reaches the configured ceiling.
    /// </summary>
    public DateTime? UnprocessableAt { get; set; }

    /// <summary>
    /// Story 29-6 (review fix) — reservation visibility timestamp.
    /// <c>ReserveNextAsync</c> only claims a row when <c>VisibleAt</c> is
    /// <c>NULL</c> OR has elapsed, so a deferred ("not yet due") task is
    /// simply not reserved until its window opens instead of being
    /// re-delivered every poll and dead-lettered before it is due.
    ///
    /// <para><b>Backward-compatible</b>: existing producers (MoveTenant,
    /// ProvisionTenantV2, CreateBillingCustomer, installation routing)
    /// leave this <c>NULL</c> ⇒ always visible ⇒ their reservation is
    /// UNCHANGED. Only <c>RETIRE_SECRET_VERSION</c> rows set it (to the
    /// payload's <c>runAfter</c>) so the grace window is enforced by the
    /// queue itself, not by a per-task throw that burns the retry budget.</para>
    /// </summary>
    public DateTime? VisibleAt { get; set; }
}
