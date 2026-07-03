namespace Tamma.Core.Audit;

/// <summary>
/// Story 37-2 — identifies WHICH hash-chain a record belongs to. A record is a
/// member of exactly one chain; chains never cross-link (see the story Dev
/// Notes "Why two chains, not one").
///
/// <list type="bullet">
///   <item><description><see cref="AuditChainScopeKind.Platform"/> — the single
///     chain over the control-plane <c>audit_records</c> table. In SaaS this is
///     the platform-scope trail (tenant_id null); in single-user mode it is the
///     sole user's whole trail. Persisted in <c>ControlPlaneDbContext</c>.</description></item>
///   <item><description><see cref="AuditChainScopeKind.Tenant"/> — one chain per
///     tenant, persisted in that tenant's <c>t_&lt;hex&gt;</c> schema
///     (<c>TenantDbContext</c>). <see cref="TenantId"/> is set.</description></item>
/// </list>
/// </summary>
public enum AuditChainScopeKind
{
    /// <summary>The control-plane chain (platform-scope in SaaS; the sole user in single-user).</summary>
    Platform = 0,

    /// <summary>A per-tenant chain living in the tenant's own schema.</summary>
    Tenant = 1,
}

/// <summary>
/// Immutable value identifying an audit hash-chain. Use <see cref="Platform"/>
/// for the control-plane chain and <see cref="ForTenant"/> for a tenant chain.
/// </summary>
public readonly record struct AuditChainScope(AuditChainScopeKind Kind, Guid? TenantId)
{
    /// <summary>The single control-plane / single-user chain.</summary>
    public static readonly AuditChainScope Platform = new(AuditChainScopeKind.Platform, null);

    /// <summary>The chain for a specific tenant's schema-resident trail.</summary>
    public static AuditChainScope ForTenant(Guid tenantId) =>
        new(AuditChainScopeKind.Tenant, tenantId);

    /// <summary>
    /// The canonical scope discriminator string used in the checkpoint table
    /// (<c>scope</c> column) and mixed into the canonical record serialization.
    /// </summary>
    public string Discriminator => Kind == AuditChainScopeKind.Tenant ? "tenant" : "platform";

    /// <summary>
    /// A stable 64-bit key for the per-scope Postgres advisory lock. Platform =
    /// a fixed namespace constant; tenant = a deterministic fold of the tenant
    /// GUID so two tenants sharing a pooled database do not serialize behind one
    /// another (each schema has its own independent chain).
    /// </summary>
    public long AdvisoryLockKey()
    {
        // "TAUD" (Tamma AUDit chain) as the high-half namespace so pg_locks is
        // greppable; the low half is 0 for platform or a fold of the tenant id.
        const long ns = 0x5441_5544L << 32;
        if (Kind != AuditChainScopeKind.Tenant || TenantId is not Guid tid) return ns;
        Span<byte> b = stackalloc byte[16];
        tid.TryWriteBytes(b);
        long fold = 0;
        for (var i = 0; i < 16; i += 8)
        {
            fold ^= BitConverter.ToInt64(b.Slice(i, 8));
        }
        // Keep it inside the low 32 bits so the namespace half stays intact.
        return ns | (fold & 0xFFFF_FFFFL);
    }
}
