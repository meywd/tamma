namespace Tamma.Data;

public interface ITenantContext
{
    Guid? TenantId { get; }
    void SetTenantId(Guid tenantId);
    void ClearTenantId();
}
