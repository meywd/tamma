using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Billing.Tasks;
using Tamma.Core.Billing;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-1 (AC6) — the shared, non-blocking tenant-create billing hook,
/// invoked by both <c>OrgEndpoints.CreateOrg</c> and the registration path in
/// <c>AuthEndpoints</c>. Centralising it keeps the two call sites identical and
/// the failure policy in one place.
///
/// <para>Behaviour:</para>
/// <list type="bullet">
///   <item>Single-user (<c>IBillingProvider.IsEnabled == false</c>): complete
///     no-op — no row, no event, no Stripe call.</item>
///   <item>SaaS happy path: creates the Stripe customer + <c>BillingCustomer</c>
///     row and emits <c>BILLING.CUSTOMER.CREATED</c> (done inside the
///     provider).</item>
///   <item>SaaS Stripe failure: tenant creation is NEVER blocked — a
///     <c>billing.customer.create</c> <see cref="PlatformQueuedTask"/> is
///     enqueued for retry and a WARN is logged. The customer row is filled in by
///     the retry handler on a later attempt.</item>
/// </list>
/// </summary>
public static class BillingTenantCreateHook
{
    /// <summary>
    /// Try to create the Stripe customer for a freshly-created tenant. Never
    /// throws back to the caller on a Stripe failure — enqueues a retry instead.
    /// </summary>
    public static async Task RunAsync(
        IBillingProvider billing,
        IPlatformQueuedTaskRepository platformTasks,
        ILoggerFactory loggerFactory,
        Tenant tenant,
        string? ownerEmail,
        BillingMode mode = BillingMode.PlatformProvided,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(billing);
        ArgumentNullException.ThrowIfNull(platformTasks);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(tenant);

        if (!billing.IsEnabled)
        {
            // Single-user — billing is SaaS-only; nothing to do.
            return;
        }

        var logger = loggerFactory.CreateLogger(typeof(BillingTenantCreateHook).FullName!);
        try
        {
            await billing.CreateCustomerAsync(
                tenant.Id,
                new CustomerDescriptor(tenant.Name, tenant.Slug, ownerEmail, mode),
                ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Stripe unreachable / rate-limited / transient. DO NOT block tenant
            // creation — enqueue a retry. Never log the exception's full detail
            // at this site (could carry config); the error class is enough.
            await platformTasks.EnqueueAsync(new PlatformQueuedTask
            {
                Type = CreateBillingCustomerTaskHandler.TaskTypeName,
                TenantId = tenant.Id,
                Payload = JsonSerializer.Serialize(
                    new CreateBillingCustomerTaskPayload(tenant.Id)),
            }, ct)
                .ConfigureAwait(false);

            logger.LogWarning(
                "Stripe customer create failed for tenant {TenantId} ({ErrorClass}); "
                + "enqueued billing.customer.create retry — tenant creation not blocked.",
                tenant.Id, ex.GetType().Name);
        }
    }
}
