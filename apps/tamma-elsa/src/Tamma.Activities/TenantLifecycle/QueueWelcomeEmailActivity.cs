using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Step 10 of <c>CreateTenantWorkflow</c> (Story 28-5 AC2 step-10 + AC5).
/// Idempotently enqueues the per-tenant welcome email into the
/// <b>control-plane</b> <c>platform_email_outbox</c> — per Epic 28 conflict
/// resolution #2 (Doc 03 §7.1 wins, Doc 01 §4.3 overridden), welcome mail
/// goes to the CP outbox, NOT the per-tenant outbox, so it can deliver
/// regardless of tenant-DB routing. Delivery is handled by the existing
/// <c>OutboxSmtpSender</c> unchanged.
///
/// <para>Exactly-once-per-tenant is enforced by
/// <see cref="IPlatformEmailOutboxRepository.EnqueueWelcomeOnceAsync"/>:
/// an in-code pre-check plus the partial unique index
/// <c>(TenantId, Template) WHERE Status &lt;&gt; 'failed'</c>. Re-running the
/// activity on workflow replay returns the existing row and inserts
/// nothing.</para>
///
/// <para>AC5 §3: enqueue is non-fatal. If the tenant owner can't be
/// resolved (no owner / no email) the step logs and returns rather than
/// failing the workflow — the tenant is already active; a missing welcome
/// email must not block provisioning. Transport failures are owned by the
/// outbox sender, not this activity.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Queue Welcome Email",
    "Idempotently enqueue the welcome email into the control-plane outbox.",
    Kind = ActivityKind.Task)]
public sealed class QueueWelcomeEmailActivity : TenantLifecycleActivity
{
    public override string StepName => "queue-welcome-email";

    protected override async Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt)
    {
        var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        await using var db = await factory.CreateDbContextAsync(context.CancellationToken);

        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .Include(t => t.Owner)
            .FirstOrDefaultAsync(t => t.Id == tenantId, context.CancellationToken)
            ?? throw new InvalidOperationException(
                $"QueueWelcomeEmail: tenant {tenantId} not found in control plane.");

        var ownerEmail = tenant.Owner?.Email;
        if (string.IsNullOrWhiteSpace(ownerEmail))
        {
            // AC5 §3: non-fatal. The tenant is already active; provisioning
            // must not fail because we can't address a welcome email.
            Logger?.LogWarning(
                "tenant.lifecycle.queue_welcome_email skipped tenantId={TenantId} "
                + "reason=no_owner_email",
                tenantId);
            return;
        }

        var config = context.GetService<IConfiguration>();
        var fromAddress = config?["Email:From"] ?? "noreply@tamma.dev";

        var repo = new PlatformEmailOutboxRepository(db);
        var row = await repo.EnqueueWelcomeOnceAsync(
            tenantId,
            ownerEmail,
            tenant.Name,
            fromAddress,
            context.CancellationToken);

        Logger?.LogInformation(
            "tenant.lifecycle.queue_welcome_email enqueued tenantId={TenantId} "
            + "outboxId={OutboxId}",
            tenantId,
            row.Id);
    }
}
