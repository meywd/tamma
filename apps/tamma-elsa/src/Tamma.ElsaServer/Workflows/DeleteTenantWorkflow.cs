using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Tamma.Activities.TenantLifecycle;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 28-5 — global Elsa workflow that tears down a tenant. Triggered
/// by <c>TENANT.DELETE_REQUESTED</c> emitted by the admin endpoint
/// <c>DELETE /api/admin/tenants/{id}</c>. The workflow flips the tenant
/// to <c>deleting</c>, optionally honours a cooling-off delay (held by
/// the trigger / queue, not by this definition), evicts the LRU pool
/// entry, drops the database, drops the role, and emits the terminal
/// <c>TENANT.DELETED.SUCCESS</c> event.
///
/// <para>Wall-clock O(1) on tenant data volume — the cost is
/// <c>DROP DATABASE</c>, not a row-by-row purge — matching epic-28
/// success metric #3 (a tenant with 10 events and a tenant with 10M
/// events both finish in &lt; 30s).</para>
///
/// <para>Idempotency: every activity probes its target before performing
/// the destructive work. A workflow restart between Step C and Step D
/// (database dropped, role still present) re-runs both steps; the role
/// drop short-circuits if the role is already gone.</para>
/// </summary>
public class DeleteTenantWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Delete Tenant";
        builder.DefinitionId = "delete-tenant";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description =
            "Tear down a tenant: mark deleting, evict pool, drop DB + role, emit terminal event.";

        var tenantId = builder.WithVariable<Guid>("TenantId", Guid.Empty);
        var attempt = builder.WithVariable<int>("Attempt", 1);

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
        var markDeleting = new MarkTenantDeletingActivity
        {
            Id = "MarkTenantDeleting",
            Name = "Mark Tenant Deleting",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
        };

        // ── Step B: evict pool ──────────────────────────────────────────
        var evictPool = new EvictTenantPoolActivity
        {
            Id = "EvictTenantPool",
            Name = "Evict Tenant Pool",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
        };

        // ── Step B2: optional pg_dump backup (gated by Backup:DeletionBackup;
        //    no-op when off). Must run AFTER evictPool (pool released) and
        //    BEFORE dropDatabase (snapshot the data before it's gone). ────
        var backupDatabase = new BackupTenantDatabaseActivity
        {
            Id = "BackupTenantDatabase",
            Name = "Backup Tenant Database",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
        };

        // ── Step C: DROP DATABASE WITH (FORCE) ──────────────────────────
        var dropDatabase = new DropTenantDatabaseActivity
        {
            Id = "DropTenantDatabase",
            Name = "Drop Tenant Database",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
        };

        // ── Step D: DROP OWNED BY + DROP ROLE ──────────────────────────
        var dropRole = new DropTenantRoleActivity
        {
            Id = "DropTenantRole",
            Name = "Drop Tenant Role",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
        };

        // ── Step E: soft-delete the CP row + emit terminal event ───────
        var emitDeleted = new EmitDeletedSuccessActivity
        {
            Id = "EmitDeletedSuccess",
            Name = "Emit Deleted Success",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
        };

        builder.Root = new Sequence
        {
            Activities =
            {
                initInputs,
                markDeleting,
                evictPool,
                backupDatabase,
                dropDatabase,
                dropRole,
                emitDeleted,
            },
        };
    }
}
