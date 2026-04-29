using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Tamma.Data.Entities;

/// <summary>
/// Story 31-7 — cross-platform webhook delivery idempotency journal.
/// Generalises <see cref="GitHubWebhookDelivery"/> across every
/// <c>PlatformKind</c> the receiver accepts. The natural primary key is
/// <c>(<see cref="PlatformKind"/>, <see cref="DeliveryId"/>)</c> — a
/// platform's delivery id is opaque and may collide with another
/// platform's id space.
///
/// <para>A row's existence means "we've accepted (and dispatched) this
/// delivery, do not re-process". The receiver consults the table BEFORE
/// dispatch so a re-delivery of the same logical event from a platform
/// that retries on transient errors does not double-fire handlers.</para>
///
/// <para>The previous GitHub-specific table (<c>github_webhook_deliveries</c>)
/// stays in place for the deprecation window — Story 31-7 ships a
/// backfill that copies its rows into this generalised table.</para>
/// </summary>
[Table("platform_webhook_deliveries")]
public class PlatformWebhookDelivery
{
    /// <summary>Internal id — surrogate, the natural key is (PlatformKind, DeliveryId).</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Lower-snake string form of <c>PlatformKind</c>. Persisted as a
    /// short string with a CHECK constraint matching
    /// <see cref="TenantPlatformInstallation.PlatformKind"/>.
    /// </summary>
    [Required]
    [MaxLength(32)]
    public string PlatformKind { get; set; } = string.Empty;

    /// <summary>
    /// Platform-supplied delivery identifier. GitHub:
    /// <c>X-GitHub-Delivery</c> (UUID). Gitea/Forgejo:
    /// <c>X-Gitea-Delivery</c>. GitLab: <c>X-Gitlab-Event-UUID</c>. The
    /// receiver passes this through as a string — drivers may have
    /// different shapes (UUID vs ULID-ish vs free-form).
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string DeliveryId { get; set; } = string.Empty;

    /// <summary>
    /// Platform-native event type (e.g. GitHub <c>installation</c>,
    /// Gitea <c>push</c>). Logged for diagnostics; not used for dedup.
    /// </summary>
    [MaxLength(100)]
    public string? EventType { get; set; }

    /// <summary>
    /// Installation external id at the time of delivery — useful for
    /// audit (which install fired this) and for the cleanup pruner
    /// (drop rows older than N days for any installation that's been
    /// disconnected).
    /// </summary>
    [MaxLength(255)]
    public string? InstallationExternalId { get; set; }

    /// <summary>UTC timestamp the receiver accepted the delivery.</summary>
    public DateTime ReceivedAt { get; set; }
}
