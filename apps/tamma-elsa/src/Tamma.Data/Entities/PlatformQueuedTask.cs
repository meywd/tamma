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
}
