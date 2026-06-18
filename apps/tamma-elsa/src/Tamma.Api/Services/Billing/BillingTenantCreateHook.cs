using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Stripe;
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
///
/// <para>Error scoping (deliberate — this hook is copied into later billing
/// stories, so it must not launder errors):</para>
/// <list type="bullet">
///   <item><see cref="OperationCanceledException"/> (request aborted / shutdown)
///     is rethrown untouched — it is NOT a Stripe failure and must not become a
///     misleading WARN or a spurious retry.</item>
///   <item>Expected/transient Stripe failures
///     (<see cref="StripeException"/>, <see cref="TimeoutException"/>,
///     <see cref="HttpRequestException"/>) → enqueue retry + WARN.</item>
///   <item>Any other unexpected exception (e.g. a real bug in the row-persist)
///     still enqueues the retry so tenant-create is never blocked, but is logged
///     at ERROR with a DISTINCT message so the defect is surfaced rather than
///     buried under a Stripe-shaped WARN.</item>
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
        catch (OperationCanceledException)
        {
            // Request aborted / shutdown — NOT a Stripe failure. Never launder a
            // cancellation into a "Stripe failed" WARN + retry: it would bury the
            // real cause and enqueue a spurious task. Propagate it untouched so the
            // caller's cancellation contract holds. (Stripe's SDK rethrows
            // OperationCanceledException raw rather than wrapping it in a
            // StripeException, so it never reaches the catch blocks below.)
            throw;
        }
        catch (Exception ex) when (IsExpectedStripeFailure(ex))
        {
            // Expected Stripe failure: unreachable / rate-limited / transient
            // network. DO NOT block tenant creation — enqueue a retry. Never log
            // the exception's full detail at this site (could carry config); the
            // error class is enough.
            await EnqueueRetryAsync(platformTasks, tenant.Id, ct).ConfigureAwait(false);

            logger.LogWarning(
                "Stripe customer create failed for tenant {TenantId} ({ErrorClass}); "
                + "enqueued billing.customer.create retry — tenant creation not blocked.",
                tenant.Id, ex.GetType().Name);
        }
        catch (Exception ex)
        {
            // Unexpected (non-Stripe) error — e.g. a NullReferenceException or a
            // bug in the row-persist. The "tenant-create is never blocked"
            // guarantee still holds (we enqueue the retry), but we log at ERROR
            // with a DISTINCT message so a real defect is surfaced, not buried
            // under a Stripe-shaped WARN. This is the silent-failure trap this
            // hook (which later billing stories copy) must avoid.
            await EnqueueRetryAsync(platformTasks, tenant.Id, ct).ConfigureAwait(false);

            logger.LogError(
                ex,
                "Unexpected (non-Stripe) error in billing customer hook for tenant "
                + "{TenantId} ({ErrorClass}); enqueued billing.customer.create retry so "
                + "tenant creation is not blocked, but THIS IS LIKELY A DEFECT — investigate.",
                tenant.Id, ex.GetType().Name);
        }
    }

    /// <summary>
    /// True for failures that are an expected, transient Stripe/network problem
    /// (the customer create can simply be retried later): a Stripe API error, a
    /// timeout, or a raw transport failure. Anything else is treated as an
    /// unexpected defect by the caller.
    /// </summary>
    private static bool IsExpectedStripeFailure(Exception ex) =>
        ex is StripeException or TimeoutException or HttpRequestException;

    private static async Task EnqueueRetryAsync(
        IPlatformQueuedTaskRepository platformTasks, Guid tenantId, CancellationToken ct)
    {
        await platformTasks.EnqueueAsync(new PlatformQueuedTask
        {
            Type = CreateBillingCustomerTaskHandler.TaskTypeName,
            TenantId = tenantId,
            Payload = JsonSerializer.Serialize(
                new CreateBillingCustomerTaskPayload(tenantId)),
        }, ct)
            .ConfigureAwait(false);
    }
}
