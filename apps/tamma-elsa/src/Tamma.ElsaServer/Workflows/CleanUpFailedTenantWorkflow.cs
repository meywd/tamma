using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Tamma.Activities.TenantLifecycle;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 28-5 AC7 — operator-triggered cleanup workflow for tenants in
/// a damaged state. Wraps the single
/// <see cref="CleanUpFailedTenantActivity"/> composite step.
///
/// <para>Triggered by <c>POST /api/admin/tenants/{id}/cleanup</c> which
/// publishes a <c>TENANT.CLEANUP.REQUESTED</c> platform event. The
/// workflow definition itself is one input-binding step plus the
/// composite cleanup activity — most of the logic lives in the activity
/// (so the cleanup is unit-testable without an Elsa runtime).</para>
///
/// <para>Unlike the regular delete workflow this is **not** triggered
/// by lifecycle events — only the explicit admin endpoint launches it.
/// This is deliberate: cleanup is a destructive recovery action that
/// should require human intent, not auto-fire on every
/// <c>TENANT.PROVISION.FAILED</c>.</para>
/// </summary>
public class CleanUpFailedTenantWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Clean Up Failed Tenant";
        builder.DefinitionId = "clean-up-failed-tenant";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description =
            "Operator-triggered best-effort teardown for a tenant in a damaged state.";

        var tenantId = builder.WithVariable<Guid>("TenantId", Guid.Empty);
        var note = builder.WithVariable<string?>("Note", null);

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
                initInputs,
                cleanup,
            },
        };
    }
}
