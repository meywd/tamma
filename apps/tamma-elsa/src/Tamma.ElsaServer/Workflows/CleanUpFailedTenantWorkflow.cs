using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.TenantLifecycle;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 28-5 AC7 — operator-triggered cleanup workflow for tenants in
/// a damaged state. Wraps the single
/// <see cref="CleanUpFailedTenantActivity"/> composite step.
///
/// <para>Triggered by <c>POST /api/admin/tenants/{id}/cleanup</c> which
/// publishes a <c>TENANT.CLEANUP.REQUESTED</c> platform event. The
/// workflow definition itself starts with an Elsa
/// <see cref="Event"/> trigger bound to
/// <see cref="CleanupRequestedEventName"/>; a small bridge
/// (<c>TenantCleanupRequestedTrigger</c> in <c>Tamma.Api</c>) subscribes
/// to <c>IPlatformEventBus</c> and forwards
/// <c>TENANT.CLEANUP.REQUESTED</c> via
/// <see cref="Elsa.Workflows.Runtime.IEventPublisher"/> so this
/// workflow's stored trigger fires.</para>
///
/// <para>Round-2 review M3: prior to this version the endpoint emitted
/// the platform event but no Elsa trigger consumed it — the workflow
/// could only be dispatched programmatically and the
/// <c>POST /cleanup</c> endpoint was effectively a no-op against the
/// activity. The <see cref="Event"/> at the root of the sequence
/// closes that integration cliff.</para>
///
/// <para>Unlike the regular delete workflow this is **not** triggered
/// by lifecycle events — only the explicit admin endpoint launches it.
/// This is deliberate: cleanup is a destructive recovery action that
/// should require human intent, not auto-fire on every
/// <c>TENANT.PROVISION.FAILED</c>.</para>
/// </summary>
public class CleanUpFailedTenantWorkflow : WorkflowBase
{
    /// <summary>
    /// Elsa event name the workflow listens for. The bridge in
    /// <c>Tamma.Api</c> publishes this name through
    /// <see cref="Elsa.Workflows.Runtime.IEventPublisher"/> when the
    /// platform event bus delivers <c>TENANT.CLEANUP.REQUESTED</c>.
    /// </summary>
    public const string CleanupRequestedEventName = "tenant-cleanup-requested";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Clean Up Failed Tenant";
        builder.DefinitionId = "clean-up-failed-tenant";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description =
            "Operator-triggered best-effort teardown for a tenant in a damaged state.";

        var tenantId = builder.WithVariable<Guid>("TenantId", Guid.Empty);
        var note = builder.WithVariable<string?>("Note", null);

        // Round-2 review M3: starter trigger bound to the event the
        // bridge re-publishes for every TENANT.CLEANUP.REQUESTED row
        // appended to platform_events. Elsa indexes this Event
        // activity as a stored trigger when the workflow is registered,
        // so subsequent IEventPublisher.PublishAsync(CleanupRequestedEventName)
        // calls dispatch a fresh workflow instance.
        var trigger = new Event(CleanupRequestedEventName)
        {
            Id = "OnCleanupRequested",
            Name = "On Cleanup Requested",
        };

        var initInputs = new SetVariable
        {
            Id = "InitInputs",
            Name = "Initialize Inputs",
            Variable = tenantId,
            Value = new Input<object?>(ctx =>
            {
                var raw = ctx.GetInput<object?>("tenantId");
                var parsed = raw switch
                {
                    Guid g => g,
                    string s when Guid.TryParse(s, out var p) => p,
                    _ => Guid.Empty,
                };
                if (parsed == Guid.Empty)
                    throw new InvalidOperationException(
                        "CleanUpFailedTenantWorkflow input 'tenantId' is required.");

                var noteIn = ctx.GetInput<string?>("note");
                note.Set(ctx, noteIn);
                return parsed;
            }),
        };

        var cleanup = new CleanUpFailedTenantActivity
        {
            Id = "CleanUpFailedTenant",
            Name = "Clean Up Failed Tenant",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Note = new Input<string?>(ctx => note.Get(ctx)),
        };

        builder.Root = new Sequence
        {
            Activities =
            {
                trigger,
                initInputs,
                cleanup,
            },
        };
    }
}
