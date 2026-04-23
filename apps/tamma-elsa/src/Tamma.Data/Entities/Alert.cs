namespace Tamma.Data.Entities;

/// <summary>
/// Story 5.6 (Wave C.1) — one row per raised alert on the
/// control-plane <c>alerts</c> table. Alerts cross every tenant /
/// platform boundary; the <see cref="TenantId"/> column is nullable so
/// a platform-scoped alert (no tenant) stays visible on the admin
/// feed while a tenant-scoped alert is routed into the tenant
/// dashboard via <see cref="TenantId"/>-filtered reads.
///
/// <para><b>Lifecycle</b>:
/// <list type="bullet">
///   <item><description><c>active</c> — freshly raised, awaiting ack.</description></item>
///   <item><description><c>acknowledged</c> — seen by a human; not yet resolved.</description></item>
///   <item><description><c>resolved</c> — closed out; <see cref="Resolution"/> captures the post-mortem blurb.</description></item>
/// </list>
/// Every transition emits an <c>ALERT.*</c> DCB event via
/// <see cref="Tamma.Data.Repositories.IEventRepository"/>.
/// </para>
///
/// <para><see cref="RuleId"/> is a forward-reference to Wave C.2's
/// <c>alert_rules</c> table and is nullable today — alerts raised
/// directly through <c>IAlertSink.RaiseAsync</c> without a rule
/// attached carry <c>rule_id = null</c> and skip the per-rule rate
/// limiter. Once Wave C.2 lands the rule engine always stamps this
/// column before <c>IAlertSink</c> is invoked.</para>
/// </summary>
public class Alert
{
    public Guid Id { get; set; }

    /// <summary>
    /// Forward-reference to Wave C.2's <c>alert_rules</c>. Nullable
    /// until that stream ships. When null, the rate limiter is
    /// bypassed (there's no rule key to bucket against).
    /// </summary>
    public Guid? RuleId { get; set; }

    /// <summary>
    /// One of <c>critical</c>, <c>warning</c>, <c>info</c>. Enforced
    /// by a CHECK constraint in the migration.
    /// </summary>
    public string Severity { get; set; } = null!;

    public string Title { get; set; } = null!;
    public string Description { get; set; } = null!;

    /// <summary>
    /// Free-form correlation id threading a set of related alerts
    /// (e.g. all alerts for a single workflow retry storm). Indexed
    /// via <see cref="Tamma.Data.ControlPlaneDbContext"/> for fast
    /// sibling-alert lookup.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Tenant owning the alert, or <c>null</c> for platform-scoped
    /// alerts (e.g. KEK rotation failure). Indexed partial WHERE
    /// <c>TenantId IS NOT NULL</c> so the tenant-admin feed is O(log n).
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// Opaque JSON blob attached to the payload. Stored as
    /// <c>jsonb</c> in Postgres for flexible filter queries in the
    /// admin UI.
    /// </summary>
    public string Metadata { get; set; } = "{}";

    /// <summary>
    /// One of <c>active</c>, <c>acknowledged</c>, <c>resolved</c>.
    /// Defaults to <c>active</c>. Enforced by a CHECK constraint.
    /// </summary>
    public string Status { get; set; } = "active";

    public Guid? AcknowledgedBy { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public Guid? ResolvedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? Resolution { get; set; }
    public DateTime CreatedAt { get; set; }
}
