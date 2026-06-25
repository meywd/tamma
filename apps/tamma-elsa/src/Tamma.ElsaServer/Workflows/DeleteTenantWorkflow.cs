using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.TenantLifecycle;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 28-5 — global Elsa workflow that tears down a tenant. Triggered by
/// <c>TENANT.DELETE.REQUESTED</c> emitted by the admin endpoint
/// <c>POST /api/admin/tenants/{id}/actions/delete</c> and bridged to Elsa by
/// <see cref="TenantDeleteRequestedTrigger"/> (which also enforces the
/// cooling-off window + the operator-cancel check before dispatch).
///
/// <para><b>Item #3 rebuild — continue-on-error + single terminal event.</b>
/// The previous shape was a flat <see cref="Sequence"/> of throwing
/// <see cref="TenantLifecycleActivity"/> steps: a mid-sequence throw aborted
/// the run and left the tenant stuck in <c>deleting</c> with NO terminal
/// event. This rebuild mirrors the sibling
/// <see cref="CleanUpFailedTenantWorkflow"/>: every destructive step is a
/// continue-on-error <see cref="CleanupStepActivity"/> (catch → record into
/// the workflow accumulator → emit <c>TENANT.DELETE.STEP_*</c> → return),
/// and a single terminal step (<see cref="EmitDeleteTerminalEventActivity"/>)
/// reads the accumulated state and emits exactly one of
/// <c>TENANT.DELETED.SUCCESS</c> (soft-delete + placement release folded in)
/// or <c>TENANT.DELETE.FAILED</c> (+ <c>ProvisioningState=
/// 'requires_manual_cleanup'</c>).</para>
///
/// <para>Order: trigger → init → mark-deleting → evict pool → backup
/// (gated) → cancellation guard → drop schema → drop role → CP relationship
/// cleanup → terminal. Pool eviction precedes <c>DROP SCHEMA … CASCADE</c> so
/// the resolver releases its cached <c>NpgsqlDataSource</c> first; the backup
/// precedes the drop; the cancellation guard re-reads <c>Status</c> as the
/// LAST act before the irreversible drop (closing the
/// dispatch→cancel→drop race); the relationship cleanup precedes the terminal
/// so a dangling-FK failure is attributed before the soft-delete decision.</para>
///
/// <para><b>Cancellation safety.</b> The mark step (top of run) and the
/// cancellation guard (immediately before the drop) both re-read
/// <c>tenants.Status</c> and ABORT the run (via the workflow accumulator's
/// abort flag) if the tenant is no longer <c>deleting</c> — an operator
/// cancelled during the cooling-off window. On abort every destructive step
/// SKIPS and the terminal emits <c>TENANT.DELETE.ABORTED</c> (non-destructive:
/// no schema drop, no soft-delete). The mark step does NOT re-flip an
/// <c>active</c> tenant back to <c>deleting</c>, and does NOT re-emit
/// <c>TENANT.DELETE.REQUESTED</c> (the trigger's own poll target — which would
/// self re-dispatch).</para>
///
/// <para>Idempotency: every step probes its target (or uses <c>IF EXISTS</c>)
/// before destructive work, so an Elsa restart between any two steps is
/// safe.</para>
/// </summary>
public class DeleteTenantWorkflow : WorkflowBase
{
    /// <summary>
    /// Elsa event name the workflow listens for.
    /// <see cref="TenantDeleteRequestedTrigger"/> publishes this name through
    /// <see cref="Elsa.Workflows.Runtime.IEventPublisher"/> when the platform
    /// event log shows a <c>TENANT.DELETE.REQUESTED</c> row whose cooling-off
    /// window has elapsed and whose tenant is still <c>deleting</c>.
    /// </summary>
    public const string DeleteRequestedEventName = "tenant-delete-requested";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Delete Tenant";
        builder.DefinitionId = "delete-tenant";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description =
            "Tear down a tenant: mark deleting, evict pool, backup, drop schema + role, "
            + "clean up CP relationships. Each step runs continue-on-error; a single "
            + "terminal event reports the overall outcome.";

        var tenantId = builder.WithVariable<Guid>("TenantId", Guid.Empty);
        var attempt = builder.WithVariable<int>("Attempt", 1);

        // ── Starter trigger — bridged from TENANT.DELETE.REQUESTED ──────
        var trigger = new Event(DeleteRequestedEventName)
        {
            Id = "OnDeleteRequested",
            Name = "On Delete Requested",
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
                        "DeleteTenantWorkflow input 'tenantId' is required and must be a non-empty Guid.");

                var attemptIn = ctx.GetInput<int?>("attempt") ?? 1;
                attempt.Set(ctx, attemptIn <= 0 ? 1 : attemptIn);
                return parsed;
            }),
        };

        // ── Step A: mark tenant deleting + emit TENANT.DELETE.REQUESTED ──
        var markDeleting = new MarkTenantDeletingForDeleteActivity
        {
            Id = "MarkTenantDeleting",
            Name = "Mark Tenant Deleting",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
        };

        // ── Step B: evict pool (before DROP SCHEMA … CASCADE) ───────────
        var evictPool = new EvictTenantPoolForCleanupActivity
        {
            Id = "EvictTenantPool",
            Name = "Evict Tenant Pool",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
        };

        // ── Step B2: optional pg_dump backup (gated; before the drop) ───
        var backupDatabase = new BackupTenantDatabaseForDeleteActivity
        {
            Id = "BackupTenantDatabase",
            Name = "Backup Tenant Database",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
        };

        // ── Step B3: cancellation guard — LAST check before the
        //    irreversible DROP SCHEMA. Re-reads Status; aborts the run
        //    (so the drop + role-drop + relationship cleanup all skip and
        //    the terminal emits TENANT.DELETE.ABORTED) if the operator
        //    cancelled. Closes the dispatch→cancel→drop race. ────────────
        var guardDeleting = new GuardTenantDeletingActivity
        {
            Id = "GuardTenantDeleting",
            Name = "Guard Tenant Deleting",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
        };

        // ── Step C: DROP SCHEMA IF EXISTS t_<hex> CASCADE ───────────────
        var dropSchema = new DropTenantSchemaForCleanupActivity
        {
            Id = "DropTenantSchema",
            Name = "Drop Tenant Schema",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
        };

        // ── Step D: DROP OWNED BY + DROP ROLE ───────────────────────────
        var dropRole = new DropTenantRoleForCleanupActivity
        {
            Id = "DropTenantRole",
            Name = "Drop Tenant Role",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
        };

        // ── Step I: CP-side relationship cleanup (item #4) ──────────────
        var cleanupRelationships = new CleanupTenantRelationshipsActivity
        {
            Id = "CleanupTenantRelationships",
            Name = "Cleanup Tenant Relationships",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
        };

        // ── Terminal: single terminal event (soft-delete folded in) ─────
        var terminal = new EmitDeleteTerminalEventActivity
        {
            Id = "EmitDeleteTerminalEvent",
            Name = "Emit Delete Terminal Event",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
        };

        builder.Root = new Sequence
        {
            Activities =
            {
                trigger,
                initInputs,
                markDeleting,
                evictPool,
                backupDatabase,
                guardDeleting,
                dropSchema,
                dropRole,
                cleanupRelationships,
                terminal,
            },
        };
    }
}
