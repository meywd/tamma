using Tamma.Api.Services.PlatformTasks;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-5 (AC10) — processes the fast-ack <c>billing.webhook.followup</c>
/// tasks the processor enqueues (dunning escalation, dispute response, email).
/// This is a thin v1: it logs the follow-up and completes so the queue drains
/// without dead-lettering. Story 35-8 (dunning) replaces the body with the real
/// escalation logic; unknown follow-up subtypes are logged and acked rather than
/// thrown so a future subtype never dead-letters here.
/// </summary>
public sealed class BillingWebhookFollowupTaskHandler : IPlatformTaskHandler
{
    private readonly ILogger<BillingWebhookFollowupTaskHandler> _logger;

    public BillingWebhookFollowupTaskHandler(ILogger<BillingWebhookFollowupTaskHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public string TaskType => BillingWebhookEventTypes.FollowupTaskType;

    public Task HandleAsync(PlatformQueuedTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        _logger.LogInformation(
            "Processing billing.webhook.followup task {TaskId} for tenant {TenantId} "
            + "(v1 no-op sink; Story 35-8 dunning replaces this).",
            task.Id, task.TenantId);
        return Task.CompletedTask;
    }
}
