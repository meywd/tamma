using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Core.Billing;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Billing.Tasks;

/// <summary>
/// Story 35-1 (AC6) — retry seam for a tenant-create-time Stripe customer
/// failure. When the non-blocking hook in <c>OrgEndpoints</c>/<c>AuthEndpoints</c>
/// catches a Stripe error it enqueues a <c>billing.customer.create</c>
/// <see cref="PlatformQueuedTask"/>; this handler re-drives
/// <see cref="IBillingProvider.CreateCustomerAsync"/> (which fills
/// <c>StripeCustomerId</c> and emits <c>BILLING.CUSTOMER.CREATED</c>).
///
/// <para>Failure semantics: a malformed payload or unknown tenant →
/// <see cref="PlatformTaskTerminalException"/> (dead-letter, never succeeds); a
/// transient Stripe error rethrows so the worker retries per its budget.</para>
/// </summary>
public sealed class CreateBillingCustomerTaskHandler : IPlatformTaskHandler
{
    public const string TaskTypeName = "billing.customer.create";

    private readonly IBillingProvider _billing;
    private readonly ControlPlaneDbContext _db;
    private readonly ILogger<CreateBillingCustomerTaskHandler> _logger;

    public CreateBillingCustomerTaskHandler(
        IBillingProvider billing,
        ControlPlaneDbContext db,
        ILogger<CreateBillingCustomerTaskHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(billing);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);

        _billing = billing;
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public string TaskType => TaskTypeName;

    /// <inheritdoc />
    public async Task HandleAsync(PlatformQueuedTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        // Single-user safety: a disabled provider should never receive this
        // task, but if it does, dead-letter cleanly rather than throw SaaS-only.
        if (!_billing.IsEnabled)
        {
            throw new PlatformTaskTerminalException(
                "Billing provider is disabled (single-user mode); "
                + "billing.customer.create cannot run.");
        }

        CreateBillingCustomerTaskPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<CreateBillingCustomerTaskPayload>(task.Payload)
                ?? throw new PlatformTaskTerminalException(
                    "billing.customer.create payload deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new PlatformTaskTerminalException(
                "billing.customer.create payload is malformed JSON.", ex);
        }

        if (payload.TenantId == Guid.Empty)
        {
            throw new PlatformTaskTerminalException(
                "billing.customer.create payload has an empty tenant id.");
        }

        var tenant = await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == payload.TenantId, ct)
            .ConfigureAwait(false);
        if (tenant is null)
        {
            throw new PlatformTaskTerminalException(
                $"billing.customer.create: tenant {payload.TenantId} not found.");
        }

        string? ownerEmail = null;
        if (tenant.OwnerId is Guid ownerId)
        {
            ownerEmail = await _db.Users
                .AsNoTracking()
                .Where(u => u.Id == ownerId)
                .Select(u => u.Email)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        // BillingMode default: this story records PlatformProvided. (BYOK
        // detection is wired by a later story; the retry preserves the default.)
        // Transient Stripe failures propagate so the worker retries.
        await _billing.CreateCustomerAsync(
            tenant.Id,
            new CustomerDescriptor(
                tenant.Name, tenant.Slug, ownerEmail, BillingMode.PlatformProvided),
            ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "billing.customer.create retry succeeded for tenant {TenantId}.", tenant.Id);
    }
}
