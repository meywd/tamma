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
/// Step A of the rebuilt <c>DeleteTenantWorkflow</c>. CONFIRMS the tenant is
/// still <c>deleting</c> before the destructive span begins, then emits a
/// <c>TENANT.DELETE.STARTED</c> marker.
///
/// <para><b>CRITICAL — cancellation race + self re-dispatch fixes.</b></para>
/// <list type="bullet">
///   <item><description><b>Does NOT resurrect an <c>active</c> tenant.</b>
///     The endpoint already flipped the row to <c>deleting</c> before the
///     trigger dispatched; this step does NOT flip <c>active→deleting</c>.
///     If the row is no longer <c>deleting</c> (an operator cancelled during
///     the cooling-off window, racing the dispatch), this step ABORTS the run
///     via <see cref="CleanupWorkflowState.MarkAborted(ActivityExecutionContext,string)"/>
///     — every subsequent destructive step then SKIPS and the terminal emits
///     <c>TENANT.DELETE.ABORTED</c>. The cancelled tenant is NEVER torn down.</description></item>
///   <item><description><b>Does NOT re-emit <c>TENANT.DELETE.REQUESTED</c>.</b>
///     That is the exact event <see cref="Tamma.Activities"/>' delete trigger
///     polls; re-emitting it is a self re-dispatch loop. The workflow signals
///     "started" with the distinct <see cref="TenantLifecycleEvents.DeleteStarted"/>
///     marker instead.</description></item>
/// </list>
///
/// <para>Continue-on-error like its siblings — a transient CP read failure
/// records into the accumulator and the run still reaches the terminal. It
/// overrides <see cref="SkipWhenAborted"/> to <c>false</c> because it is the
/// step that DETECTS cancellation; the destructive steps after it skip.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Mark Tenant Deleting (Delete)",
    "Confirm Status='deleting' (abort if cancelled) + emit TENANT.DELETE.STARTED; never throws.",
    Kind = ActivityKind.Task)]
public sealed class MarkTenantDeletingForDeleteActivity : CleanupStepActivity
{
    public override string StepName => CleanupSteps.MarkDeleting;

    // This step DETECTS cancellation and sets the abort flag, so it must run
    // even though the destructive steps after it skip on abort.
    protected override bool SkipWhenAborted => false;

    protected override async Task DoStepAsync(
        ActivityExecutionContext context,
        Guid tenantId)
    {
        var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        await using var db = await factory.CreateDbContextAsync(context.CancellationToken)
            .ConfigureAwait(false);

        var store = new ActivityContextStateStore(context);
        var stillDeleting = await EvaluateAsync(
            db, store, tenantId, Logger, context.CancellationToken).ConfigureAwait(false);
        if (!stillDeleting)
            return; // aborted — EvaluateAsync already set the abort flag.

        // Already 'deleting' (the endpoint set it). Emit the STARTED marker —
        // NOT TENANT.DELETE.REQUESTED (the trigger's own poll target — re-emitting
        // it would self re-dispatch). Idempotent: the step-dedup index swallows
        // a replay on the same attempt.
        var publisher = context.GetRequiredService<IPlatformEventPublisher>();
        await publisher.AppendAndPublishAsync(
            TenantLifecycleEvents.BuildEvent(
                TenantLifecycleEvents.DeleteStarted,
                tenantId,
                data: new Dictionary<string, object?>
                {
                    ["startedAt"] = DateTime.UtcNow,
                    ["source"] = "delete-workflow",
                }),
            context.CancellationToken).ConfigureAwait(false);

        Logger?.LogInformation(
            "tenant.delete.mark_deleting confirmed_deleting tenantId={TenantId}", tenantId);
    }

    /// <summary>
    /// Pure-DI cancellation check — testable against an EF InMemory
    /// <see cref="ControlPlaneDbContext"/> + an in-memory
    /// <see cref="ICleanupStateStore"/>, with NO Elsa runtime. Reads the live
    /// <c>Status</c> and:
    /// <list type="bullet">
    ///   <item>returns <c>true</c> when the tenant is still <c>deleting</c> (the
    ///     destructive span may proceed);</item>
    ///   <item>returns <c>false</c> and calls
    ///     <see cref="CleanupWorkflowState.MarkAborted(ICleanupStateStore,string)"/>
    ///     when the tenant is NOT <c>deleting</c> — it does NOT flip the row, so
    ///     a cancelled (<c>active</c>) tenant is NEVER resurrected to
    ///     <c>deleting</c>.</item>
    /// </list>
    /// Throws only when the tenant row is missing (a genuinely broken input the
    /// continue-on-error base records as a step failure).
    /// </summary>
    public static async Task<bool> EvaluateAsync(
        ControlPlaneDbContext db,
        ICleanupStateStore store,
        Guid tenantId,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(store);

        var current = await db.Tenants
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .Select(t => new { Status = EF.Property<string?>(t, "Status"), Found = true })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (current is null)
            throw new InvalidOperationException(
                $"MarkDeleting: tenant {tenantId} not found in CP.");

        if (!string.Equals(current.Status, "deleting", StringComparison.OrdinalIgnoreCase))
        {
            // Cancellation race — the operator flipped the tenant out of
            // 'deleting' (typically back to 'active' via cancel-delete) after
            // the trigger dispatched. Do NOT re-flip to 'deleting' and do NOT
            // proceed: abort so the destructive steps skip and the terminal
            // emits ABORTED. The tenant stays exactly as the operator left it.
            var reason = $"tenant no longer 'deleting' (status={current.Status ?? "null"}) — delete cancelled before destructive span";
            CleanupWorkflowState.MarkAborted(store, reason);
            logger?.LogWarning(
                "tenant.delete.mark_deleting aborted_cancelled tenantId={TenantId} status={Status}",
                tenantId, current.Status);
            return false;
        }

        return true;
    }
}

/// <summary>
/// Cancellation guard — runs IMMEDIATELY before the destructive
/// <c>DropTenantSchema</c> step. Re-reads <c>tenants.Status</c> a final time;
/// if the tenant is no longer <c>deleting</c> (an operator cancelled during
/// the brief window after <see cref="MarkTenantDeletingForDeleteActivity"/>
/// confirmed but before the drop), it ABORTS the run so the drop + role-drop +
/// relationship-cleanup steps all skip and the terminal emits
/// <c>TENANT.DELETE.ABORTED</c>.
///
/// <para>This is the second (and last) cancellation checkpoint: the first is
/// the mark step at the top of the run; this one closes the window between
/// confirmation and the irreversible <c>DROP SCHEMA … CASCADE</c>. Together
/// they make a cancelled tenant un-droppable regardless of dispatch timing.
/// (The trigger's pre-dispatch re-read is the third, earliest, line of
/// defence.)</para>
///
/// <para>Like the mark step it overrides <see cref="SkipWhenAborted"/> to
/// <c>false</c> — it is itself a cancellation detector. A transient CP read
/// failure FAILS CLOSED: it aborts the run (does NOT proceed to the drop) so a
/// momentarily-unreadable status can never be assumed to be <c>deleting</c>.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Guard Tenant Deleting (Delete)",
    "Re-read Status before the destructive drop; abort the run if no longer 'deleting'. Fail-closed; never throws.",
    Kind = ActivityKind.Task)]
public sealed class GuardTenantDeletingActivity : CleanupStepActivity
{
    public override string StepName => CleanupSteps.GuardDeleting;

    // Itself a cancellation detector — must run on the non-aborted path.
    protected override bool SkipWhenAborted => false;

    protected override async Task DoStepAsync(
        ActivityExecutionContext context,
        Guid tenantId)
    {
        var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        var store = new ActivityContextStateStore(context);
        await EvaluateAsync(factory, store, tenantId, Logger, context.CancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Pure-DI cancellation guard — testable against an EF InMemory
    /// <see cref="ControlPlaneDbContext"/> factory + an in-memory
    /// <see cref="ICleanupStateStore"/>. Re-reads the live <c>Status</c>; if it
    /// is not <c>deleting</c> it ABORTS the run (so the drop is skipped). A read
    /// failure FAILS CLOSED — it also aborts, so a momentarily-unreadable status
    /// can never be mistaken for "still deleting" right before the irreversible
    /// drop. Returns <c>true</c> only when the tenant is confirmed still
    /// <c>deleting</c>.
    /// </summary>
    public static async Task<bool> EvaluateAsync(
        IDbContextFactory<ControlPlaneDbContext> factory,
        ICleanupStateStore store,
        Guid tenantId,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(store);

        string? current;
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            current = await db.Tenants
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(t => t.Id == tenantId)
                .Select(t => EF.Property<string?>(t, "Status"))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Fail-closed: a read failure right before DROP SCHEMA must NOT be
            // treated as "still deleting". Abort so the destructive span skips;
            // the trigger re-dispatches on the next tick once CP is readable.
            CleanupWorkflowState.MarkAborted(
                store, "cancellation guard could not re-read status before drop (fail-closed)");
            logger?.LogWarning(
                ex, "tenant.delete.guard read_failed_abort tenantId={TenantId}", tenantId);
            return false;
        }

        if (!string.Equals(current, "deleting", StringComparison.OrdinalIgnoreCase))
        {
            var reason = $"tenant no longer 'deleting' (status={current ?? "null"}) — delete cancelled before DROP SCHEMA";
            CleanupWorkflowState.MarkAborted(store, reason);
            logger?.LogWarning(
                "tenant.delete.guard aborted_cancelled tenantId={TenantId} status={Status}",
                tenantId, current);
            return false;
        }

        logger?.LogInformation(
            "tenant.delete.guard still_deleting tenantId={TenantId}", tenantId);
        return true;
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
/// <para><b>Disposition policy (decided per table; every live CP table with
/// a tenant key has an explicit verdict — no silent FK dangle).</b></para>
/// <list type="bullet">
///   <item><description><b>Delete</b> — operational CP rows with no audit
///     value once the tenant is gone: <c>tenant_memberships</c>,
///     <c>user_invites</c>, pending <c>platform_queued_tasks</c>,
///     <c>tenant_agent_enablements</c>, <c>alert_channels</c>,
///     <c>api_keys</c> (revoked — the credentials must stop working
///     immediately), <c>platform_api_key_index</c> (the auth routing rows
///     for those keys; its model was DESIGNED with a <c>TenantId</c> index
///     "for bulk-revoke on tenant delete (cascade)" — leaving them dangles
///     an index pointing at deleted <c>api_keys</c>),
///     <c>tenant_platform_installations</c> (NON-nullable <c>TenantId</c> FK
///     — a git-platform binding for the gone tenant; its credentials live in
///     the tenant-scoped secret store and are dropped with the schema, so
///     the row has no standalone value and would dangle if kept),
///     <c>agent_role_selections</c> (tenant-keyed selections) and the
///     tenant's PRIVATE <c>agents</c> (+ their <c>agent_versions</c>) —
///     tenant-owned data, deleted with the tenant. Public/system agents
///     (<c>OwnerTenantId IS NULL</c>) are platform-global and untouched.</description></item>
///   <item><description><b>Null the FK</b> — <c>github_installations</c>:
///     a GitHub App installation is owned by the GitHub org, not the
///     tenant; nulling <c>TenantId</c> releases it for re-binding without
///     destroying the installation record.</description></item>
///   <item><description><b>Keep for audit</b> — <c>billing_customers</c>
///     (financial record / Stripe linkage), <c>audit_records</c>,
///     <c>platform_events</c>, <c>alerts</c> (operational + compliance
///     history; <c>TenantId</c> is NULLABLE so no FK dangle, and the alert
///     feed is read back for incident review), <c>platform_analytics_hourly</c>
///     (immutable cross-tenant analytics fact table; <c>TenantId</c> NULLABLE,
///     the platform-wide rollup rows carry <c>TenantId=null</c> — purging a
///     gone tenant's hourly rows would corrupt historical platform totals).
///     All retained intentionally; NOT touched here.</description></item>
///   <item><description><b>Out of scope (tenant-schema, not CP)</b> —
///     <c>prompt_overrides</c>, <c>secrets</c> (tenant KEK-encrypted),
///     <c>analytics_usage_daily</c>/<c>analytics_usage_hourly</c>, and the
///     SaaS tenant-keyed <c>agent_role_selections</c> rows all live inside the
///     tenant's own <c>t_&lt;hex&gt;</c> schema (configured on
///     <see cref="TenantDbContext"/>, NOT the
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
            + "alertChannels={AlertChannels} apiKeys={ApiKeys} apiKeyIndex={ApiKeyIndex} "
            + "platformInstallations={PlatformInstallations} agentSelections={AgentSelections} "
            + "privateAgents={PrivateAgents} agentVersions={AgentVersions}",
            tenantId,
            removed.Memberships, removed.Invites, removed.InstallationsReleased,
            removed.QueuedTasks, removed.Enablements,
            removed.AlertChannels, removed.ApiKeys, removed.ApiKeyIndexRows,
            removed.PlatformInstallations, removed.AgentRoleSelections,
            removed.PrivateAgents, removed.AgentVersions);
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

        // platform_api_key_index — the auth routing rows for those keys. The
        // entity was DESIGNED with a TenantId index "for bulk-revoke on tenant
        // delete (cascade)"; with api_keys hard-deleted above, leaving these
        // dangles an index pointing at gone api_keys rows. Purge them.
        var apiKeyIndex = await db.PlatformApiKeyIndex
            .Where(i => i.TenantId == tenantId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        db.PlatformApiKeyIndex.RemoveRange(apiKeyIndex);

        // tenant_platform_installations — git-platform bindings keyed by a
        // NON-nullable TenantId FK. The binding credentials live in the
        // tenant-scoped secret store (dropped with the schema), so the row has
        // no standalone value and would FK-dangle if kept. Delete.
        var platformInstallations = await db.TenantPlatformInstallations
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        db.TenantPlatformInstallations.RemoveRange(platformInstallations);

        // agent_role_selections — CP-resident tenant-keyed selections (SaaS
        // tenant-keyed rows that ALSO live in the tenant schema are dropped by
        // CASCADE; this purges any CP-side rows carrying this TenantId).
        var agentSelections = await db.AgentRoleSelections
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        db.AgentRoleSelections.RemoveRange(agentSelections);

        // Private (tenant-owned) agents + their immutable version snapshots.
        // Public/system agents (OwnerTenantId IS NULL) are platform-global and
        // untouched. agent_versions has OnDelete(Restrict), so the versions
        // must be removed BEFORE the parent agents in the same unit of work.
        var privateAgents = await db.Agents
            .IgnoreQueryFilters()
            .Where(a => a.OwnerTenantId == tenantId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var privateAgentIds = privateAgents.Select(a => a.Id).ToList();
        var agentVersions = await db.AgentVersions
            .Where(v => privateAgentIds.Contains(v.AgentId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        db.AgentVersions.RemoveRange(agentVersions);
        db.Agents.RemoveRange(privateAgents);

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
            InstallationsReleased: installations.Count,
            ApiKeyIndexRows: apiKeyIndex.Count,
            PlatformInstallations: platformInstallations.Count,
            AgentRoleSelections: agentSelections.Count,
            PrivateAgents: privateAgents.Count,
            AgentVersions: agentVersions.Count);
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
        int InstallationsReleased,
        int ApiKeyIndexRows = 0,
        int PlatformInstallations = 0,
        int AgentRoleSelections = 0,
        int PrivateAgents = 0,
        int AgentVersions = 0);
}
