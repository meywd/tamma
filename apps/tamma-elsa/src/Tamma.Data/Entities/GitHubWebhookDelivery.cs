namespace Tamma.Data.Entities;

/// <summary>
/// Idempotency journal for inbound GitHub webhook deliveries. The
/// <see cref="DeliveryId"/> is GitHub's <c>X-GitHub-Delivery</c> header
/// (UUID, stable across retry attempts of the same logical delivery).
/// A row's existence means "we've accepted (and dispatched) this delivery".
///
/// <para>Audit findings 003 + 019 — TS never tracked deliveries; the C#
/// port-gap audit elevated this to P2 because the durable Postgres-backed
/// task queue makes duplicate dispatch more damaging (tasks survive
/// restart; downstream handlers are not idempotent by default).</para>
/// </summary>
public class GitHubWebhookDelivery
{
    /// <summary>GitHub's delivery UUID — the natural primary key.</summary>
    public Guid DeliveryId { get; set; }

    public DateTime ReceivedAt { get; set; }
    public string EventType { get; set; } = null!;
    public string? Action { get; set; }
    public long? InstallationId { get; set; }
}
