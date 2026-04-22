namespace Tamma.Data;

public class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }

    public void SetTenantId(Guid tenantId) => TenantId = tenantId;
    public void ClearTenantId() => TenantId = null;
}
