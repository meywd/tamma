using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;

namespace Tamma.Core.Actions;

/// <summary>
/// The <c>automation:*</c> plane of the Action Catalog (Story 43-2 AC6): every
/// hosted background actor across both hosts. Re-derived from the tree on
/// 2026-07-28: 25 <c>AddHostedService</c> registrations (5 in
/// <c>Tamma.ElsaServer/Program.cs</c>, 8 in <c>Tamma.Api/Program.cs</c> including
/// one factory overload, 12 in <c>Tamma.Api/Extensions/*</c>) plus
/// <c>PlatformTaskWorker</c> (registered via a <c>TryAddEnumerable</c>
/// hosted-service descriptor in <c>AddPlatformTaskWorker</c>, so no literal
/// <c>AddHostedService&lt;&gt;</c> line exists for it) = <b>27</b> (the design's
/// figure of 25 plus the Epic 46 review-F1 settings-store primer plus Story
/// 41-30's <c>TenantScheduledTriggerService</c>). Wire rule:
/// kebab-case of the class name with a trailing
/// <c>HostedService</c>/<c>BackgroundService</c> suffix dropped.
///
/// <para>Bound to the real classes by the reflection sweep in
/// <c>Tamma.Activities.Tests/Actions/BackgroundActorCatalogSweepTests</c> —
/// descriptor <c>SiteKey</c>s carry the full type names the sweep matches on.</para>
///
/// <para>Every member is NON-ESCALATABLE (<c>EscalatableToHuman = false</c>):
/// a sweeper cannot suspend for a person, so Seam D (43-9) can only deny.</para>
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<BackgroundActor>))]
public enum BackgroundActor
{
    // ── Tamma.ElsaServer host ──

    /// <summary><c>Tamma.ElsaServer.Workflows.HourlyAnalyticsRollupScheduler</c>.</summary>
    [Wire("hourly-analytics-rollup-scheduler")] HourlyAnalyticsRollupScheduler,

    /// <summary><c>Tamma.ElsaServer.Workflows.TenantCleanupRequestedTrigger</c>.</summary>
    [Wire("tenant-cleanup-requested-trigger")] TenantCleanupRequestedTrigger,

    /// <summary><c>Tamma.ElsaServer.Workflows.TenantDeleteRequestedTrigger</c>.</summary>
    [Wire("tenant-delete-requested-trigger")] TenantDeleteRequestedTrigger,

    /// <summary><c>Tamma.ElsaServer.WorkflowSeeder</c>.</summary>
    [Wire("workflow-seeder")] WorkflowSeeder,

    /// <summary><c>Tamma.ElsaServer.AgentSeeder</c>.</summary>
    [Wire("agent-seeder")] AgentSeeder,

    /// <summary><c>Tamma.ElsaServer.Workflows.TenantScheduledTriggerService</c>
    /// — Story 41-30, the tenant-aware scheduled-trigger seam: dispatches any
    /// registered workflow definition per tenant per cron window, at most once
    /// across the fleet (tenant-scoped advisory lock + the
    /// <c>scheduled_trigger_fires</c> ledger). Ships <c>Enabled=false</c>.</summary>
    [Wire("tenant-scheduled-trigger-service")] TenantScheduledTriggerService,

    // ── Tamma.Api host — Program.cs registrations ──

    /// <summary><c>Tamma.Api.Services.PoolWarmupService</c>.</summary>
    [Wire("pool-warmup-service")] PoolWarmupService,

    /// <summary><c>Tamma.Api.Services.WorkflowSyncService</c>.</summary>
    [Wire("workflow-sync-service")] WorkflowSyncService,

    /// <summary><c>Tamma.Api.Services.Channels.ChannelOutboxSweeper</c>.</summary>
    [Wire("channel-outbox-sweeper")] ChannelOutboxSweeper,

    /// <summary><c>Tamma.Api.Services.Secrets.Rotation.SecretAutoRotationScheduler</c>.</summary>
    [Wire("secret-auto-rotation-scheduler")] SecretAutoRotationScheduler,

    /// <summary><c>Tamma.Api.Services.Secrets.Rotation.RetireSweepHostedService</c>.</summary>
    [Wire("retire-sweep")] RetireSweep,

    /// <summary><c>Tamma.Api.Services.Engine.Lifecycle.EngineRegistryHeartbeatService</c>.</summary>
    [Wire("engine-registry-heartbeat-service")] EngineRegistryHeartbeatService,

    /// <summary><c>Tamma.Api.Services.TenantStatus.TenantStatusInvalidationListener</c>.
    /// FACTORY-REGISTERED (<c>AddHostedService(sp => …)</c> at
    /// <c>Tamma.Api/Program.cs</c> — the overload with a null
    /// <c>ImplementationType</c>): Story 43-8's registration-level sweep must
    /// special-case it; the type-level sweep here sees it normally.</summary>
    [Wire("tenant-status-invalidation-listener")] TenantStatusInvalidationListener,

    /// <summary><c>Tamma.Api.Services.Providers.ProviderSettingsStorePrimingService</c>
    /// — Epic 46 review F1: primes the provider-settings snapshot before the
    /// host serves traffic (fail-soft; the lazy TTL refresh is the fallback).</summary>
    [Wire("provider-settings-store-priming-service")] ProviderSettingsStorePrimingService,

    // ── Tamma.Api host — Extensions/* registrations ──

    /// <summary><c>Tamma.Api.Services.Pricing.EntitlementCacheInvalidationListener</c>.</summary>
    [Wire("entitlement-cache-invalidation-listener")] EntitlementCacheInvalidationListener,

    /// <summary><c>Tamma.Api.Services.Conventions.ConventionStoreSeeder</c>.</summary>
    [Wire("convention-store-seeder")] ConventionStoreSeeder,

    /// <summary><c>Tamma.Api.Services.Providers.ProviderSessionCleanupService</c>.</summary>
    [Wire("provider-session-cleanup-service")] ProviderSessionCleanupService,

    /// <summary><c>Tamma.Api.Services.TaskQueue.TaskQueueProcessor</c>.</summary>
    [Wire("task-queue-processor")] TaskQueueProcessor,

    /// <summary><c>Tamma.Api.Services.Notifications.OutboxSlackSender</c>.</summary>
    [Wire("outbox-slack-sender")] OutboxSlackSender,

    /// <summary><c>Tamma.Api.Services.Email.OutboxSmtpSender</c>.</summary>
    [Wire("outbox-smtp-sender")] OutboxSmtpSender,

    /// <summary><c>Tamma.Api.Services.Audit.AuditChainCheckpointScheduler</c>.</summary>
    [Wire("audit-chain-checkpoint-scheduler")] AuditChainCheckpointScheduler,

    /// <summary><c>Tamma.Api.Services.Secrets.Reveal.RevealTokenSweeper</c>.</summary>
    [Wire("reveal-token-sweeper")] RevealTokenSweeper,

    /// <summary><c>Tamma.Api.Services.Alerts.NotificationDispatcher</c>.</summary>
    [Wire("notification-dispatcher")] NotificationDispatcher,

    /// <summary><c>Tamma.Api.Services.Alerts.Rules.BuiltInAlertRuleSeeder</c>
    /// (plain <c>IHostedService</c>, not a <c>BackgroundService</c>).</summary>
    [Wire("built-in-alert-rule-seeder")] BuiltInAlertRuleSeeder,

    /// <summary><c>Tamma.Api.Services.Alerts.Rules.AlertRuleEvaluator</c>.</summary>
    [Wire("alert-rule-evaluator")] AlertRuleEvaluator,

    /// <summary><c>Tamma.Api.Services.Audit.AuditProjectorBackgroundService</c>.</summary>
    [Wire("audit-projector")] AuditProjector,

    /// <summary><c>Tamma.Api.Services.Actions.ActionCatalogStartupValidator</c> —
    /// Story 43-4's fail-loud boot check that the tool vocabularies agree with
    /// this catalog. Read-only by construction: its only "action" is refusing
    /// to start the Tamma.Api host. Catalogued because the hosted-service sweep
    /// binds EVERY <c>IHostedService</c> class, deliberately including the
    /// governance machinery itself.</summary>
    [Wire("action-catalog-startup-validator")] ActionCatalogStartupValidator,

    /// <summary><c>Tamma.Api.Services.Actions.GovernancePolicySnapshotPrimingService</c>
    /// — Story 43-5's cold-start primer for the action-assignments policy
    /// snapshot (the ProviderSettingsStorePrimingService review-F1 posture):
    /// awaits one control-plane read before the host serves traffic, fail-soft,
    /// so persisted autonomy policy applies from the first request. Read-only —
    /// it writes nothing anywhere. Catalogued because the hosted-service sweep
    /// binds EVERY <c>IHostedService</c> class, the governance machinery
    /// included.</summary>
    [Wire("governance-policy-snapshot-priming-service")] GovernancePolicySnapshotPrimingService,

    /// <summary><c>Tamma.Api.Services.PlatformTasks.PlatformTaskWorker</c> — the
    /// generic platform-task drain loop (one task at a time per process;
    /// <c>RunOnStartup</c> ships <c>false</c>). Registered via a
    /// <c>TryAddEnumerable</c> hosted-service descriptor, not an
    /// <c>AddHostedService&lt;&gt;</c> line — catalogued explicitly so it is not
    /// invisible to a registration grep.</summary>
    [Wire("platform-task-worker")] PlatformTaskWorker,

    // ── Epic 31 P2 — the platform-plane subscribers/sweeps ──

    /// <summary><c>Tamma.Api.Services.Platforms.PlatformDriverCacheInvalidator</c>
    /// — the Story 31-2-designed cache-invalidation subscriber (built in Epic
    /// 31 P2): CREDENTIAL_ROTATED / DISCONNECTED / SWITCH_ORG platform events
    /// evict the tenant's cached drivers immediately. Process-local, in-memory
    /// eviction only — it touches no external system.</summary>
    [Wire("platform-driver-cache-invalidator")] PlatformDriverCacheInvalidator,

    /// <summary><c>Tamma.Api.Services.Platforms.GitHubInstallationBridgeBackfillService</c>
    /// — Epic 31 P2 (seam 14) one-shot startup backfill: every tenant-linked
    /// <c>github_installations</c> row is idempotently bridged into
    /// <c>tenant_platform_installations</c> so App-installed tenants are
    /// visible to the driver plane. Re-runs are no-ops by construction.</summary>
    [Wire("github-installation-bridge-backfill")] GitHubInstallationBridgeBackfill,

    // ── Epic 31 P3 — the CI completion vehicle (DG-5) ──

    /// <summary><c>Tamma.Api.Services.Ci.CiCompletionPollerService</c> —
    /// Epic 31 P3 (DG-5): the durable CI completion poller. Enumerates the
    /// engine's suspended CI-result waits, polls each run's status through
    /// the tenant's resolved platform driver (<c>driver.Actions</c>), and
    /// resumes the bookmark with the terminal result — before it, only the
    /// 30-minute timeout ended a CI wait, on every platform. Mutating: it
    /// advances suspended workflow instances (idempotent against the timeout
    /// race — a burned bookmark 404s, never double-advances).</summary>
    [Wire("ci-completion-poller")] CiCompletionPoller,
}

/// <summary><see cref="BackgroundActor"/> wire helper.</summary>
public static class BackgroundActorExtensions
{
    /// <summary>The canonical wire string for <paramref name="actor"/>.</summary>
    public static string ToWire(this BackgroundActor actor) => EnumWire<BackgroundActor>.ToWire(actor);
}
