namespace Tamma.Data.Entities;

/// <summary>
/// Multi-tenant task queue row. Ported from the deleted TypeScript
/// <c>packages/api/src/services/task-queue.ts</c> (<see cref="Status"/> + tenant
/// scoping preserved).
///
/// <para>
/// The GitHub webhook dispatcher enqueues <c>push</c>/<c>issues</c>/<c>pull_request</c>
/// events here so the webhook handler returns immediately; a background
/// <c>TaskQueueProcessor</c> picks them up, invokes the registered
/// <c>ITaskHandler</c>, and transitions the row through the status states.
/// </para>
/// </summary>
public class QueuedTask
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Task type (e.g. <c>github.push.unknown</c>, <c>github.issues.opened</c>).
    /// Used by the processor to pick the right <c>ITaskHandler</c>.
    /// </summary>
    public string Type { get; set; } = null!;

    /// <summary>
    /// Owning tenant; <c>null</c> for self-hosted/system-scope tasks.
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// GitHub App installation ID that produced this task. Preserved for
    /// backward compatibility with the TypeScript queue. May be null when the
    /// event came from a source other than the GitHub App.
    /// </summary>
    public long? InstallationId { get; set; }

    /// <summary>
    /// Opaque JSON payload (the raw webhook body, or arbitrary task args).
    /// Stored as <c>jsonb</c> on Postgres.
    /// </summary>
    public string Payload { get; set; } = "{}";

    /// <summary>
    /// One of <c>pending | processing | completed | failed</c>.
    /// </summary>
    public string Status { get; set; } = "pending";

    /// <summary>Error message set by the processor when <see cref="Status"/> is <c>failed</c>.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// Number of processing attempts so far. The processor increments on each
    /// failure and flips the row to <c>failed</c> when this hits the configured
    /// ceiling (default 3).
    /// </summary>
    public int RetryCount { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
