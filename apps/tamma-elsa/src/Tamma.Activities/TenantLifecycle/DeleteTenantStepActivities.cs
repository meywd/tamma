using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamma.Activities.AgentDispatch;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Pooling;

namespace Tamma.Activities.TenantLifecycle;

// ═══════════════════════════════════════════════════════════════════════
// Story 28-5 item #3 — DeleteTenantWorkflow continue-on-error decomposition
//
// The destructive teardown used to be a flat Sequence of throwing
// TenantLifecycleActivity steps: a mid-sequence throw aborted the run and
// left the tenant stuck in 'deleting' with NO terminal event (violating
// "no silent-failure" + "exactly one terminal event"). This file mirrors
// the sibling cleanup decomposition (CleanupStepActivity + the
// *ForCleanupActivity variants) so the delete workflow runs every step
// regardless of upstream failures and concludes with exactly one terminal
// event (EmitDeleteTerminalEventActivity).
//
// These delete-specific steps inherit CleanupStepActivity for the
// continue-on-error contract (catch → record into CleanupWorkflowState →
// emit TENANT.DELETE.STEP_* → return). The reusable destructive steps
// (evict pool, drop schema, drop role) already have continue-on-error
// variants in the cleanup file; the delete workflow reuses those directly.
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Step A of the rebuilt <c>DeleteTenantWorkflow</c> — continue-on-error
/// variant of <see cref="MarkTenantDeletingActivity"/>. Flips
/// <c>tenants.Status='deleting'</c> (idempotent) and emits the
/// <c>TENANT.DELETE.REQUESTED</c> marker.
///
/// <para>Unlike the throwing <see cref="MarkTenantDeletingActivity"/>, a
/// failure here records into the per-step accumulator and lets the
/// Sequence continue so the run still reaches the single terminal event.
/// The endpoint already flipped the row to <c>deleting</c> before the
/// trigger dispatched, so this is normally a fast no-op.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Mark Tenant Deleting (Delete)",
    "Continue-on-error variant — set Status='deleting' + emit TENANT.DELETE.REQUESTED; never throws.",
    Kind = ActivityKind.Task)]
public sealed class MarkTenantDeletingForDeleteActivity : CleanupStepActivity
{
    public override string StepName => CleanupSteps.MarkDeleting;

    protected override async Task DoStepAsync(
        ActivityExecutionContext context,
        Guid tenantId)
    {
        var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        await using var db = await factory.CreateDbContextAsync(context.CancellationToken)
            .ConfigureAwait(false);

        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId, context.CancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"MarkDeleting: tenant {tenantId} not found in CP.");

        var current = (string?)db.Entry(tenant).Property("Status").CurrentValue;
        if (!string.Equals(current, "deleting", StringComparison.OrdinalIgnoreCase))
        {
            db.Entry(tenant).Property("Status").CurrentValue = "deleting";
            if (db.Entry(tenant).Property("DeleteRequestedAt").CurrentValue is null)
                db.Entry(tenant).Property("DeleteRequestedAt").CurrentValue = DateTime.UtcNow;
            tenant.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);
        }

        var publisher = context.GetRequiredService<IPlatformEventPublisher>();
        await publisher.AppendAndPublishAsync(
            TenantLifecycleEvents.BuildEvent(
                TenantLifecycleEvents.DeleteRequested,
                tenantId,
                data: new Dictionary<string, object?>
                {
                    ["requestedAt"] = DateTime.UtcNow,
                    ["source"] = "delete-workflow",
                }),
            context.CancellationToken).ConfigureAwait(false);

        Logger?.LogInformation(
            "tenant.delete.mark_deleting completed tenantId={TenantId}", tenantId);
    }
}

/// <summary>
/// Step B2 of the rebuilt <c>DeleteTenantWorkflow</c> — continue-on-error
/// variant of <see cref="BackupTenantDatabaseActivity"/>. Optional
/// <c>pg_dump</c> taken before the schema drop, gated by
/// <c>Backup:DeletionBackup</c> (a pure no-op when off). Reuses the
/// pure-DI static entry points so the dump logic stays single-sourced.
///
/// <para>A backup failure records into the accumulator and continues —
/// but because the backup is taken BEFORE the destructive drop, the
/// terminal will report <c>TENANT.DELETE.FAILED</c> and quarantine the
/// tenant so an operator decides whether to proceed without a snapshot.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Backup Tenant Database (Delete)",
    "Continue-on-error variant — pg_dump before drop (gated by Backup:DeletionBackup); never throws.",
    Kind = ActivityKind.Task)]
public sealed class BackupTenantDatabaseForDeleteActivity : CleanupStepActivity
{
    public override string StepName => CleanupSteps.BackupDatabase;

    protected override async Task DoStepAsync(
        ActivityExecutionContext context,
        Guid tenantId)
    {
        var options = context.GetService<IOptions<TenantBackupOptions>>()?.Value
                      ?? new TenantBackupOptions();

        if (!options.DeletionBackup)
        {
            Logger?.LogInformation(
                "tenant.delete.backup_database disabled_skip tenantId={TenantId}", tenantId);
            return;
        }

        var runner = context.GetRequiredService<IProcessRunner>();
        var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        var placement = await TenantPlacementShadow.LoadAsync(
            factory, tenantId, context.CancellationToken).ConfigureAwait(false);

        if (placement.DatabaseId is not null && placement.SchemaName is not null)
        {
            var pool = context.GetRequiredService<ITenantDatabasePool>();
            await BackupTenantDatabaseActivity.BackupSchemaAsync(
                options, pool, runner, tenantId,
                placement.DatabaseId.Value, placement.SchemaName,
                DateTime.UtcNow, Logger, context.CancellationToken).ConfigureAwait(false);
            return;
        }

        var admin = context.GetRequiredService<ITenantAdminConnection>();
        await BackupTenantDatabaseActivity.BackupAsync(
            options, admin, runner, tenantId, DateTime.UtcNow, Logger,
            context.CancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Story 28-5 item #4 / AC4 Step I — CP-side relationship cleanup. Runs
/// between <see cref="DropTenantRoleForCleanupActivity"/> and the terminal
/// step. Removes the control-plane rows that key off the deleted tenant so
/// no foreign-key dangle survives the soft-delete tombstone.
///
/// <para><b>Disposition policy (decided per table):</b></para>
/// <list type="bullet">
///   <item><description><b>Delete</b> — operational CP rows with no audit
///     value once the tenant is gone: <c>tenant_memberships</c>,
///     <c>user_invites</c>, pending <c>platform_queued_tasks</c>,
///     <c>tenant_agent_enablements</c>, <c>alert_channels</c>,
///     <c>api_keys</c> (revoked — the credentials must stop working
///     immediately).</description></item>
///   <item><description><b>Null the FK</b> — <c>github_installations</c>:
///     a GitHub App installation is owned by the GitHub org, not the
///     tenant; nulling <c>TenantId</c> releases it for re-binding without
///     destroying the installation record.</description></item>
///   <item><description><b>Keep for audit</b> — <c>billing_customers</c>
///     (financial record / Stripe linkage), <c>audit_records</c>,
///     <c>platform_events</c>: retained intentionally; the soft-deleted
///     tenant row + these immutable trails are what compliance reads back.
///     NOT touched here.</description></item>
///   <item><description><b>Out of scope (tenant-schema, not CP)</b> —
///     <c>prompt_overrides</c> and other per-tenant rows live inside the
///     tenant's own <c>t_&lt;hex&gt;</c> schema (excluded from the
///     <see cref="ControlPlaneDbContext"/> model) and are destroyed by the
///     upstream <c>DROP SCHEMA … CASCADE</c> — this CP step must not, and
///     cannot, reach them.</description></item>
/// </list>
///
/// <para>Idempotent + single <c>SaveChanges</c>: the whole disposition is
/// one unit of work, so a replay after a partial commit re-runs cleanly
/// (already-deleted rows match nothing; already-nulled FKs are no-ops). A
/// failure records into the per-step accumulator → the terminal emits
/// <c>TENANT.DELETE.FAILED</c> + quarantine.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Cleanup Tenant Relationships",
    "Delete/null CP rows keyed off the tenant (memberships, invites, installations, queued tasks, ...). Continue-on-error.",
    Kind = ActivityKind.Task)]
public sealed class CleanupTenantRelationshipsActivity : CleanupStepActivity
{
    public override string StepName => CleanupSteps.CleanupRelationships;

    protected override async Task DoStepAsync(
        ActivityExecutionContext context,
        Guid tenantId)
    {
        var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        await using var db = await factory.CreateDbContextAsync(context.CancellationToken)
            .ConfigureAwait(false);

        var removed = await CleanupRelationshipsAsync(db, tenantId, context.CancellationToken)
            .ConfigureAwait(false);

        Logger?.LogInformation(
            "tenant.delete.cleanup_relationships completed tenantId={TenantId} "
            + "memberships={Memberships} invites={Invites} installationsReleased={Installations} "
            + "queuedTasks={QueuedTasks} enablements={Enablements} "
            + "alertChannels={AlertChannels} apiKeys={ApiKeys}",
            tenantId,
            removed.Memberships, removed.Invites, removed.InstallationsReleased,
            removed.QueuedTasks, removed.Enablements,
            removed.AlertChannels, removed.ApiKeys);
    }

    /// <summary>
    /// Pure-DI entry point — testable against an EF InMemory
    /// <see cref="ControlPlaneDbContext"/> without a live Elsa runtime.
    /// Deletes/nulls every tenant-keyed CP row per the disposition policy
    /// in a single <c>SaveChanges</c>. Audit-retained tables
    /// (billing_customers, audit_records, platform_events) are left
    /// untouched. Returns per-table counts for the structured log.
    /// </summary>
    public static async Task<RelationshipCleanupResult> CleanupRelationshipsAsync(
        ControlPlaneDbContext db,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        // ── Delete: operational rows with no post-deletion audit value ──
        var memberships = await db.TenantMemberships
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        db.TenantMemberships.RemoveRange(memberships);

        var invites = await db.UserInvites
            .IgnoreQueryFilters()
            .Where(i => i.TenantId == tenantId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        db.UserInvites.RemoveRange(invites);

        // Pending platform_queued_tasks only — completed/dead_letter rows
        // stay as their own audit trail; a pending row pointed at a gone
        // tenant would dead-letter forever otherwise.
        var queuedTasks = await db.PlatformQueuedTasks
            .Where(q => q.TenantId == tenantId && q.Status == "pending")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        db.PlatformQueuedTasks.RemoveRange(queuedTasks);

        var enablements = await db.TenantAgentEnablements
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        db.TenantAgentEnablements.RemoveRange(enablements);

        var alertChannels = await db.AlertChannels
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        db.AlertChannels.RemoveRange(alertChannels);

        // api_keys revoked outright — the credentials must stop working the
        // instant the tenant is deleted, and a dangling key against a
        // soft-deleted tenant is a standing security liability.
        var apiKeys = await db.ApiKeys
            .IgnoreQueryFilters()
            .Where(k => k.TenantId == tenantId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        db.ApiKeys.RemoveRange(apiKeys);

        // ── Null the FK: github_installations is org-owned, not
        //    tenant-owned. Release it for re-binding; keep the record. ──
        var installations = await db.GitHubInstallations
            .IgnoreQueryFilters()
            .Where(g => g.TenantId == tenantId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var installation in installations)
            installation.TenantId = null;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new RelationshipCleanupResult(
            Memberships: memberships.Count,
            Invites: invites.Count,
            QueuedTasks: queuedTasks.Count,
            Enablements: enablements.Count,
            AlertChannels: alertChannels.Count,
            ApiKeys: apiKeys.Count,
            InstallationsReleased: installations.Count);
    }

    /// <summary>Per-table disposition counts returned by
    /// <see cref="CleanupRelationshipsAsync"/> for the structured log +
    /// test assertions.</summary>
    public readonly record struct RelationshipCleanupResult(
        int Memberships,
        int Invites,
        int QueuedTasks,
        int Enablements,
        int AlertChannels,
        int ApiKeys,
        int InstallationsReleased);
}
