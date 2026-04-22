namespace Tamma.Data.Entities;

/// <summary>
/// Cross-tenant lifecycle event store. Same shape as
/// <see cref="DomainEvent"/> but lives in the control plane so events that
/// fire before/after a tenant DB exists (or that touch multiple tenants) have
/// a durable home.
///
/// <para>Doc 01 §5.1–5.2: tenant-scoped events live in the tenant's
/// <c>domain_events</c>; cross-tenant events (<c>TENANT.*</c>,
/// <c>USER.REGISTERED</c>, <c>ORCHESTRATOR.TICK.*</c>,
/// <c>GITHUB.INSTALLATION.*</c> before tenant resolution,
/// <c>ADMIN.TENANT_ACCESSED</c>) write here.</para>
///
/// <para>Reuses the <see cref="DomainEvent"/> column shape to keep replayer
/// and analytics rollup code shared. The <c>TenantId</c> column is nullable
/// here too — null means "platform-only" (e.g. orchestrator tick); set means
/// "lifecycle event about this tenant".</para>
/// </summary>
public class PlatformEvent
{
    public Guid Id { get; set; }
    public string Type { get; set; } = null!;
    public Guid? TenantId { get; set; }
    public Guid? UserId { get; set; }
    public string Tags { get; set; } = "{}";
    public string Metadata { get; set; } = "{}";
    public string Data { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}
