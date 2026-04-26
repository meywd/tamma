using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Tamma.Activities.TenantLifecycle;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// H6 / Story 28-5 AC7 — operator-triggered cleanup workflow for
/// tenants in a damaged state.
///
/// <para><b>Round-2 review fix (H6)</b>: the previous shape was a single
/// 200-line <c>CleanUpFailedTenantActivity</c> with a hand-rolled
/// <c>RunStep</c> local function — a mini-orchestrator inside one Elsa
/// activity. That bypassed Elsa's per-step replay / cancel /
/// observability boundaries: a worker restart between, say, "drop
/// database" and "drop role" replayed the whole activity from scratch
/// (instead of resuming at the next step), and Elsa Studio saw the
/// workflow as a single opaque box with no per-step status.</para>
///
/// <para>This rewrite decomposes the cleanup into four sibling
/// continue-on-error activities + one terminal activity, all under a
/// regular <see cref="Sequence"/>:</para>
///
/// <list type="number">
///   <item><description><see cref="EvictTenantPoolForCleanupActivity"/> —
///     forget the LRU pool entry.</description></item>
///   <item><description><see cref="DropTenantDatabaseForCleanupActivity"/> —
///     <c>DROP DATABASE … WITH (FORCE)</c>.</description></item>
///   <item><description><see cref="DropTenantRoleForCleanupActivity"/> —
///     <c>DROP OWNED BY</c> + <c>DROP ROLE IF EXISTS</c>.</description></item>
///   <item><description><see cref="SoftDeleteTenantRowActivity"/> —
///     stamp the CP <c>tenants</c> row.</description></item>
///   <item><description><see cref="EmitCleanupTerminalEventActivity"/> —
///     read the accumulated step state, fire the SINGLE terminal event
///     (<c>TENANT.DELETED.SUCCESS</c> if all four prior steps
///     succeeded, <c>TENANT.DELETE.FAILED</c> with a
///     <c>failedSteps</c> array otherwise), and on partial failure flip
///     <c>tenants.ProvisioningState='requires_manual_cleanup'</c>.</description></item>
/// </list>
///
/// <para><b>Why no <c>TryCatch</c></b>: Elsa Workflows 3.5.x doesn't
/// ship a built-in <c>TryCatch</c> activity (it has the Incident model
/// instead — see Elsa docs <c>operate/incidents/strategies.md</c>).
/// The continue-on-error contract is implemented INSIDE each step
/// activity (<see cref="CleanupStepActivity"/>) — the activity catches
/// its own exception, redacts the message via
/// <see cref="Tamma.Activities.Security.IErrorRedactor"/>, records the
/// failure into a workflow variable, and returns normally. Combined
/// with <c>WorkflowOptions.IncidentStrategyType =
/// typeof(ContinueWithIncidentsStrategy)</c> as a defense-in-depth, the
/// <see cref="Sequence"/> reliably runs every sibling step regardless
/// of upstream failures.</para>
///
/// <para><b>Backwards compatibility</b>: workflow definition id
/// (<c>clean-up-failed-tenant</c>) and the
/// <c>POST /api/admin/tenants/{id}/cleanup</c> input contract are
/// unchanged. Anything that triggered the previous workflow continues
/// to trigger this one identically.</para>
///
/// <para><b>Single terminal event invariant</b>: Story 28-5's dashboard
/// timeline relies on exactly one terminal event per cleanup run. Only
/// <see cref="EmitCleanupTerminalEventActivity"/> emits a terminal
/// event; the per-step activities emit <c>TENANT.DELETE.STEP_*</c>
/// markers (which are step-scoped, not terminal).</para>
/// </summary>
public class CleanUpFailedTenantWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Clean Up Failed Tenant";
        builder.DefinitionId = "clean-up-failed-tenant";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description =
            "Operator-triggered best-effort teardown for a tenant in a damaged state. "
            + "Each step runs independently with continue-on-error semantics; a single "
            + "terminal event reports the overall outcome.";

        var tenantId = builder.WithVariable<Guid>("TenantId", Guid.Empty);
        var note = builder.WithVariable<string?>("Note", null);

        // ── Input binding ─────────────────────────────────────────────
        // Workflow inputs come in as 'tenantId' (string-or-Guid) and
        // 'note' (string, optional). SetVariable normalises them into
        // the typed workflow variables above.
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

        // ── Step 1: evict pool ────────────────────────────────────────
        var evictPool = new EvictTenantPoolForCleanupActivity
        {
            Id = "EvictTenantPoolForCleanup",
            Name = "Evict Tenant Pool",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
        };

        // ── Step 2: drop database (probe-before-drop) ────────────────
        var dropDb = new DropTenantDatabaseForCleanupActivity
        {
            Id = "DropTenantDatabaseForCleanup",
            Name = "Drop Tenant Database",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
        };

        // ── Step 3: drop role (DROP OWNED BY → DROP ROLE) ────────────
        var dropRole = new DropTenantRoleForCleanupActivity
        {
            Id = "DropTenantRoleForCleanup",
            Name = "Drop Tenant Role",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
        };

        // ── Step 4: soft-delete the CP row ───────────────────────────
        var softDelete = new SoftDeleteTenantRowActivity
        {
            Id = "SoftDeleteTenantRow",
            Name = "Soft Delete Tenant Row",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Note = new Input<string?>(ctx => note.Get(ctx)),
        };

        // ── Step 5: terminal event + ProvisioningState stamp ────────
        var terminal = new EmitCleanupTerminalEventActivity
        {
            Id = "EmitCleanupTerminalEvent",
            Name = "Emit Cleanup Terminal Event",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Note = new Input<string?>(ctx => note.Get(ctx)),
        };

        builder.Root = new Sequence
        {
            Activities =
            {
                initInputs,
                evictPool,
                dropDb,
                dropRole,
                softDelete,
                terminal,
            },
        };
    }
}
