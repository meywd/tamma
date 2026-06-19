namespace Tamma.Api.Services.Billing.Tasks;

/// <summary>
/// Story 35-1 — payload for the <c>billing.customer.create</c> retry task. The
/// tenant id is the only thing needed; the handler re-reads the tenant's name /
/// slug / owner email from the control plane so the retry sees fresh data.
/// </summary>
public sealed record CreateBillingCustomerTaskPayload(Guid TenantId);
