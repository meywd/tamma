using Microsoft.EntityFrameworkCore;
using Tamma.Core.Entities;
using Tamma.Core.Enums;
using Tamma.Data.Entities;

namespace Tamma.Data;

/// <summary>
/// Control-plane <see cref="DbContext"/> that owns the tenant-agnostic
/// tables: users, tenants (registry), tenant memberships, invites, API keys
/// (CP-scoped), GitHub installations, webhook deliveries, and auth tokens.
///
/// <para>Epic 28 isolation model: control-plane data never carries a
/// <c>TenantId</c> filter — the rows themselves are either global (users,
/// plans) or organisational (tenants, memberships, invites). Per-tenant
/// business data (workflows, prompts, events, queued tasks, budgets,
/// diagnostics, etc.) lives on <see cref="TenantDbContext"/>, constructed
/// via <see cref="ITenantDbContextFactory"/>.</para>
///
/// <para>Supersedes the Epic 17 dual-context split (<c>TammaDbContext</c>
/// + <c>TammaAppDbContext</c> + RLS on shared DB). The db-per-tenant
/// architecture replaces RLS entirely — each tenant eventually gets its
/// own physical database with no tenant column and no row-level filter.</para>
/// </summary>
public class ControlPlaneDbContext : DbContext
{
    public ControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options)
        : base(options)
    {
    }

    // ── Control-plane entities (Doc 01 §1.2 — exactly 14 tables) ──
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<UserInvite> UserInvites => Set<UserInvite>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<GitHubInstallation> GitHubInstallations => Set<GitHubInstallation>();
    public DbSet<GitHubInstallationRepo> GitHubInstallationRepos => Set<GitHubInstallationRepo>();
    public DbSet<GitHubWebhookDelivery> GitHubWebhookDeliveries => Set<GitHubWebhookDelivery>();

    // ── Control-plane platform tables (Story 28-6 + 28-10) ──
    //
    // These three tables (platform_events, platform_queued_tasks,
    // platform_email_outbox) own cross-tenant / pre-tenant-resolution work.
    // They never live on a TenantDbContext — they are the control plane's
    // durable scratchpad for lifecycle events, installation-routing tasks,
    // and system-scope mail that must flow before or after a tenant DB
    // exists. The Plans table owns the subscription-plan catalogue.
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<PlatformEvent> PlatformEvents => Set<PlatformEvent>();
    public DbSet<PlatformQueuedTask> PlatformQueuedTasks => Set<PlatformQueuedTask>();
    public DbSet<PlatformEmailOutboxMessage> PlatformEmailOutbox => Set<PlatformEmailOutboxMessage>();

    /// <summary>
    /// Story 28-10 fact table — one row per <c>(Hour, TenantId)</c> tuple,
    /// populated hourly by <c>HourlyAnalyticsRollupWorkflow</c>. Platform-wide
    /// rows carry <c>TenantId = null</c>. See
    /// <see cref="Entities.PlatformAnalyticsHourly"/> for the column catalogue.
    /// </summary>
    public DbSet<PlatformAnalyticsHourly> PlatformAnalyticsHourly => Set<PlatformAnalyticsHourly>();

    /// <summary>
    /// Story 28-7 deferred-item routing table: O(1) prefix → tenant+apiKey
    /// lookups for <c>ApiKeyAuthHandler</c>. See
    /// <see cref="PlatformApiKeyIndex"/> for row semantics.
    /// </summary>
    public DbSet<PlatformApiKeyIndex> PlatformApiKeyIndex => Set<PlatformApiKeyIndex>();

    /// <summary>
    /// R2-H14: durable record of in-flight + completed KEK rotations.
    /// The <c>StagedSecondaryProtected</c> column lets a coordinator
    /// recover from a process crash mid-rotation without losing the
    /// new KEK material.
    /// </summary>
    public DbSet<KekRotation> KekRotations => Set<KekRotation>();

    // ── Story 5.6 + 1.5-37 (Wave C.1) — alert system ──
    //
    // The three alert tables are CP-resident: alert rules + channels
    // fan out across tenants, and platform-scoped alerts (TenantId=null)
    // must live somewhere every tenant dashboard can't read directly.
    // Tenant-scoped alerts still carry TenantId and are routed into the
    // tenant-facing UI (Wave C.3) via explicit TenantId filters.

    /// <summary>
    /// Story 5.6 — raised alerts. One row per lifecycle (active →
    /// acknowledged → resolved).
    /// </summary>
    public DbSet<Alert> Alerts => Set<Alert>();

    /// <summary>
    /// Story 1.5-37 — configured delivery targets. Credentials live
    /// in the secret store via <c>CredentialsSecretId</c>, never in
    /// the <c>Config</c> column.
    /// </summary>
    public DbSet<AlertChannel> AlertChannels => Set<AlertChannel>();

    /// <summary>
    /// Story 1.5-37 — audit log of every delivery attempt.
    /// <c>NotificationDispatcher</c> drains <c>pending</c>/<c>failed</c>
    /// rows; <c>dropped_rate_limit</c> rows are audited-only.
    /// </summary>
    public DbSet<AlertDeliveryAttempt> AlertDeliveryAttempts =>
        Set<AlertDeliveryAttempt>();

    // ── Story 5.6 (Wave C.2) — alert rule engine ──
    //
    // Rules + evaluator cursor both live on the control plane: rules
    // fan out across tenants, and the cursor is the single control-
    // plane-wide progress marker for the <c>AlertRuleEvaluator</c>.

    /// <summary>Story 5.6 (Wave C.2) — alert rules.</summary>
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();

    /// <summary>Story 5.6 (Wave C.2) — evaluator progress cursor.</summary>
    public DbSet<AlertEvaluatorCursor> AlertEvaluatorCursors =>
        Set<AlertEvaluatorCursor>();

    // ── Legacy-shared tables (DEPRECATED — transitional-topology scratchpad) ──
    //
    // These DbSets cover per-tenant business data that still lives on the
    // shared central Postgres until Story 28-1's db-per-tenant rollout
    // completes. They are exposed here ONLY so the eleven legacy-shared
    // repositories (AgentConfigRepository, PromptRepository, etc.) that
    // take <see cref="ControlPlaneDbContext"/> can still compile during
    // the transition — they are scoped to the <c>TenantId IS NULL</c>
    // platform-default row family (Doc 01 §1.4 — "system defaults"
    // carry no tenant scope; tenant-scoped data goes through
    // <see cref="ITenantDbContextFactory"/>).
    //
    // <para><b>Mapping:</b> The entity types are NOT included in the CP
    // model (<see cref="OnModelCreating"/> does not call
    // <c>ConfigureTenantEntities</c>). Query attempts against these
    // DbSets will throw — by design. Model-shape tests
    // (<c>ControlPlaneDbContextModelTests.Model_Has14_ControlPlaneEntities</c>)
    // enforce the 14-entity invariant. The DbSet surface is retained as a
    // compile-time shim only; repositories must migrate fully onto
    // <see cref="ITenantDbContextFactory"/> or onto the platform-plane
    // tables before Story 28-1 ships.</para>
    public DbSet<AgentConfig> AgentConfigs => Set<AgentConfig>();
    public DbSet<PromptOverride> PromptOverrides => Set<PromptOverride>();
    public DbSet<ProviderHealth> ProviderHealths => Set<ProviderHealth>();
    public DbSet<ProviderDiagnostic> ProviderDiagnostics => Set<ProviderDiagnostic>();
    public DbSet<SanitizationRule> SanitizationRules => Set<SanitizationRule>();
    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<WorkflowInstance> WorkflowInstances => Set<WorkflowInstance>();
    public DbSet<Entities.DomainEvent> DomainEvents => Set<Entities.DomainEvent>();
    public DbSet<QueuedTask> QueuedTasks => Set<QueuedTask>();
    public DbSet<EmailOutboxMessage> EmailOutbox => Set<EmailOutboxMessage>();
    public DbSet<BudgetConfig> BudgetConfigs => Set<BudgetConfig>();
    public DbSet<MentorshipSession> MentorshipSessions => Set<MentorshipSession>();
    public DbSet<MentorshipEvent> MentorshipEvents => Set<MentorshipEvent>();
    public DbSet<JuniorDeveloper> JuniorDevelopers => Set<JuniorDeveloper>();
    public DbSet<Story> Stories => Set<Story>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        TammaModelConfiguration.ConfigureMentorshipEntities(modelBuilder);
        TammaModelConfiguration.ConfigureControlPlaneEntities(
            modelBuilder, includeTenantShadowColumns: true);
        // Legacy tables still live on the shared central DB during the
        // Epic 28 transition; repos access them through this context
        // with explicit tenant predicates. Once Story 28-1's db-per-tenant
        // rollout ships, these configurations move to TenantDbContext.
        TammaModelConfiguration.ConfigureTenantEntities(modelBuilder);

        ConfigurePlatformAnalyticsHourly(modelBuilder);
        ConfigurePlatformApiKeyIndex(modelBuilder);
        ConfigureAlerts(modelBuilder);
        ConfigureAlertChannels(modelBuilder);
        ConfigureAlertDeliveryAttempts(modelBuilder);
        ConfigureAlertRules(modelBuilder);
        ConfigureAlertEvaluatorCursor(modelBuilder);
        ConfigureKekRotations(modelBuilder);
    }

    /// <summary>
    /// R2-H14 — <c>kek_rotations</c> table. One row per rotation; the
    /// active row is identified by a partial unique index on
    /// <c>Status IN ('pending', 'running')</c>. The
    /// <c>StagedSecondaryProtected</c> column is encrypted by the OLD
    /// primary KEK at write time so the row is always readable after a
    /// restart.
    /// </summary>
    private static void ConfigureKekRotations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KekRotation>(entity =>
        {
            entity.ToTable("kek_rotations", t =>
            {
                t.HasCheckConstraint(
                    "CK_kek_rotations_status",
                    "\"Status\" IN ('pending','running','completed','failed','cancelled')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Status).IsRequired().HasMaxLength(32);
            entity.Property(e => e.VersionOld).IsRequired();
            entity.Property(e => e.VersionNew).IsRequired();
            entity.Property(e => e.StagedSecondaryProtected).HasColumnType("bytea");
            entity.Property(e => e.FailureReason).HasMaxLength(2000);
            entity.Property(e => e.StartedAt);

            // Hot read: status transitions on the in-flight row. Partial
            // index keeps the index tight (most rows are completed/failed).
            entity.HasIndex(e => e.Status)
                .HasDatabaseName("IX_kek_rotations_Status")
                .HasFilter("\"Status\" IN ('pending','running')");

            // Reverse-chronological list for the operator dashboard.
            entity.HasIndex(e => e.StartedAt)
                .HasDatabaseName("IX_kek_rotations_StartedAt")
                .IsDescending(true);
        });
    }

    /// <summary>
    /// Story 5.6 (Wave C.2) — <c>alert_rules</c> table. CHECK
    /// constraint pins severity to its enum; unique index on the
    /// human-readable <c>Name</c>; partial unique index on
    /// <c>BuiltInKey</c> so seeder upserts are idempotent without
    /// colliding on admin-created rules (which carry NULL
    /// <c>BuiltInKey</c>).
    /// </summary>
    private static void ConfigureAlertRules(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AlertRule>(entity =>
        {
            entity.ToTable("alert_rules", t =>
            {
                t.HasCheckConstraint(
                    "CK_alert_rules_severity",
                    "\"Severity\" IN ('critical','warning','info')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Description).IsRequired();
            entity.Property(e => e.IsEnabled).HasDefaultValue(true);
            entity.Property(e => e.Severity).IsRequired().HasMaxLength(20);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Predicate)
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.ThrottleSeconds).HasDefaultValue(0);
            entity.Property(e => e.ChannelIds)
                .HasColumnType("uuid[]")
                .HasDefaultValueSql("ARRAY[]::uuid[]");
            entity.Property(e => e.IsBuiltIn).HasDefaultValue(false);
            entity.Property(e => e.BuiltInKey).HasMaxLength(64);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // Hot read: the evaluator fetches by (EventType, IsEnabled).
            entity.HasIndex(e => new { e.EventType, e.IsEnabled })
                .HasDatabaseName("IX_alert_rules_EventType_IsEnabled");

            // Idempotent seeder upserts key off BuiltInKey. Partial
            // unique keeps admin-created NULL-key rules from colliding.
            entity.HasIndex(e => e.BuiltInKey)
                .HasDatabaseName("UX_alert_rules_BuiltInKey")
                .HasFilter("\"BuiltInKey\" IS NOT NULL")
                .IsUnique();

            // Human-readable uniqueness — admin UI rejects a duplicate
            // name at write time via the 409 handler; the DB-level
            // index is defence in depth.
            entity.HasIndex(e => e.Name)
                .HasDatabaseName("UX_alert_rules_Name")
                .IsUnique();
        });
    }

    /// <summary>
    /// Story 5.6 (Wave C.2) — evaluator cursor. Composite key on
    /// <see cref="AlertEvaluatorCursor.EvaluatorId"/> so each logical
    /// evaluator owns one row; multi-process deployments share the
    /// single <c>"default"</c> evaluator id today.
    /// </summary>
    private static void ConfigureAlertEvaluatorCursor(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AlertEvaluatorCursor>(entity =>
        {
            entity.ToTable("alert_evaluator_cursor");
            entity.HasKey(e => e.EvaluatorId);
            entity.Property(e => e.EvaluatorId)
                .IsRequired()
                .HasMaxLength(64);
            // Per-stream BIGINT cursors. Default 0 means "fetch from
            // the start" — a freshly-inserted row resumes at the head
            // of each stream.
            entity.Property(e => e.LastDomainSequenceNumber)
                .HasDefaultValue(0L);
            entity.Property(e => e.LastPlatformSequenceNumber)
                .HasDefaultValue(0L);
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        });
    }

    /// <summary>
    /// Story 5.6 (Wave C.1) — <c>alerts</c> table configuration.
    /// CHECK constraints pin severity + status to their enums so a
    /// buggy write path can't stash an unknown value and confuse the
    /// admin feed. Indexes cover the two hot reads: the admin feed
    /// (by status + recency) and the tenant feed (tenant-filtered +
    /// recency).
    /// </summary>
    private static void ConfigureAlerts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Alert>(entity =>
        {
            entity.ToTable("alerts", t =>
            {
                t.HasCheckConstraint(
                    "CK_alerts_severity",
                    "\"Severity\" IN ('critical','warning','info')");
                t.HasCheckConstraint(
                    "CK_alerts_status",
                    "\"Status\" IN ('active','acknowledged','resolved')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Severity).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(512);
            entity.Property(e => e.Description).IsRequired();
            entity.Property(e => e.CorrelationId).HasMaxLength(255);
            entity.Property(e => e.Metadata)
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .HasDefaultValue("active");
            entity.Property(e => e.Resolution).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            // Admin feed — "show me the last 50 active alerts" hits
            // this index. Descending on CreatedAt avoids an in-memory
            // reverse sort.
            entity.HasIndex(e => new { e.Status, e.CreatedAt })
                .HasDatabaseName("IX_alerts_Status_CreatedAt")
                .IsDescending(false, true);

            // Tenant feed — partial index keeps the tenant-scoped
            // rows tight; platform-wide alerts (TenantId=null) don't
            // waste space here because they're served by the admin
            // feed index above.
            entity.HasIndex(e => new { e.TenantId, e.CreatedAt })
                .HasDatabaseName("IX_alerts_TenantId_CreatedAt")
                .HasFilter("\"TenantId\" IS NOT NULL")
                .IsDescending(false, true);

            // Severity-first dashboards (e.g. "all criticals this
            // week") get their own ordering.
            entity.HasIndex(e => new { e.Severity, e.CreatedAt })
                .HasDatabaseName("IX_alerts_Severity_CreatedAt")
                .IsDescending(false, true);

            // CorrelationId lookup — "show me every alert tied to
            // this workflow retry storm". Partial so the null
            // majority doesn't bloat the index.
            entity.HasIndex(e => e.CorrelationId)
                .HasDatabaseName("IX_alerts_CorrelationId")
                .HasFilter("\"CorrelationId\" IS NOT NULL");
        });
    }

    /// <summary>
    /// Story 1.5-37 (Wave C.1) — <c>alert_channels</c> table.
    /// </summary>
    private static void ConfigureAlertChannels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AlertChannel>(entity =>
        {
            entity.ToTable("alert_channels", t =>
            {
                t.HasCheckConstraint(
                    "CK_alert_channels_channel_type",
                    "channel_type IN ('email','slack','pagerduty','webhook')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.ChannelType)
                .IsRequired()
                .HasMaxLength(32)
                .HasColumnName("channel_type");
            entity.Property(e => e.IsEnabled)
                .HasDefaultValue(true);
            entity.Property(e => e.Config)
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => new { e.TenantId, e.IsEnabled })
                .HasDatabaseName("IX_alert_channels_TenantId_IsEnabled");

            entity.HasIndex(e => new { e.ChannelType, e.IsEnabled })
                .HasDatabaseName("IX_alert_channels_ChannelType_IsEnabled");
        });
    }

    /// <summary>
    /// Story 1.5-37 (Wave C.1) — <c>alert_delivery_attempts</c>
    /// table. The partial index on <c>(Status, CreatedAt)</c>
    /// covers the dispatcher poll query
    /// (<c>WHERE status IN ('pending','failed')</c>) — Postgres can
    /// scan the index top-down with no table hits until it has the
    /// batch size.
    /// </summary>
    private static void ConfigureAlertDeliveryAttempts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AlertDeliveryAttempt>(entity =>
        {
            entity.ToTable("alert_delivery_attempts", t =>
            {
                t.HasCheckConstraint(
                    "CK_alert_delivery_attempts_status",
                    "\"Status\" IN ('pending','success','failed','dropped_rate_limit')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.AttemptNumber).IsRequired();
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(32)
                .HasDefaultValue("pending");
            entity.Property(e => e.Error).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => e.AlertId)
                .HasDatabaseName("IX_alert_delivery_attempts_AlertId");

            // Dispatcher hot path — filter on status keeps the
            // successful attempts (the vast majority after steady
            // state) out of the index.
            entity.HasIndex(e => new { e.Status, e.CreatedAt })
                .HasDatabaseName("IX_alert_delivery_attempts_Status_CreatedAt")
                .HasFilter("\"Status\" IN ('pending','failed')")
                .IsDescending(false, true);

            // FK cascade from alerts keeps delivery-attempt rows
            // honest when an alert is hard-deleted (tenant purge).
            entity.HasOne<Alert>()
                .WithMany()
                .HasForeignKey(e => e.AlertId)
                .OnDelete(DeleteBehavior.Cascade);

            // Preserve delivery history even when a channel is
            // soft-deleted — the admin can still audit who received
            // what. Enforced by RESTRICT; soft-delete is the
            // contracted deprovisioning path.
            entity.HasOne<AlertChannel>()
                .WithMany()
                .HasForeignKey(e => e.ChannelId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigurePlatformAnalyticsHourly(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlatformAnalyticsHourly>(entity =>
        {
            entity.ToTable("platform_analytics_hourly");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Hour).HasColumnType("timestamp with time zone");
            entity.Property(e => e.TenantId);
            entity.Property(e => e.WorkflowsStarted).HasDefaultValue(0L);
            entity.Property(e => e.WorkflowsCompleted).HasDefaultValue(0L);
            entity.Property(e => e.WorkflowsFailed).HasDefaultValue(0L);
            entity.Property(e => e.AgentDispatches).HasDefaultValue(0L);
            entity.Property(e => e.TokensIn).HasDefaultValue(0L);
            entity.Property(e => e.TokensOut).HasDefaultValue(0L);
            entity.Property(e => e.CostUsd).HasPrecision(20, 4).HasDefaultValue(0m);
            entity.Property(e => e.ActiveTenantsAtHourEnd).HasDefaultValue(0);
            entity.Property(e => e.ComputedAt).HasDefaultValueSql("now()");

            // Per-tenant time-series lookup — "show me tenant X's last
            // 30d of workflows" on the admin dashboard. The filter keeps
            // the index tight (one platform-wide row per hour is routed
            // through UX_*_PlatformWide below instead). Descending on the
            // Hour leg matches "most-recent-first" scans.
            entity.HasIndex(e => new { e.TenantId, e.Hour })
                .HasDatabaseName("IX_platform_analytics_hourly_TenantId_Hour")
                .HasFilter("\"TenantId\" IS NOT NULL")
                .IsDescending(false, true);

            // Idempotency key — the rollup fan-out writes at most one row
            // per (Hour, TenantId). Two partial unique indexes cover both
            // the tenant-scoped path and the NULL-TenantId platform-wide
            // slot without needing a COALESCE expression that some older
            // Postgres servers reject on unique indexes. Combined, they
            // also serve descending "last N hours" scans because Postgres
            // can use a partial index for an `ORDER BY Hour DESC` with a
            // matching predicate.
            entity.HasIndex(e => new { e.Hour, e.TenantId })
                .HasDatabaseName("UX_platform_analytics_hourly_Hour_TenantId")
                .HasFilter("\"TenantId\" IS NOT NULL")
                .IsDescending(true, false)
                .IsUnique();

            entity.HasIndex(e => e.Hour)
                .HasDatabaseName("UX_platform_analytics_hourly_Hour_PlatformWide")
                .HasFilter("\"TenantId\" IS NULL")
                .IsDescending(true)
                .IsUnique();
        });
    }

    /// <summary>
    /// Story 28-7 deferred-item routing table: maps a key prefix to its
    /// tenant + ApiKey for O(1) bearer-token authentication. The
    /// <see cref="PlatformApiKeyIndex.HashedSuffix"/> is SHA-256 of the
    /// remainder so the table never stores plaintext material.
    /// </summary>
    private static void ConfigurePlatformApiKeyIndex(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlatformApiKeyIndex>(entity =>
        {
            entity.ToTable("platform_api_key_index");
            entity.HasKey(e => e.KeyPrefix);
            entity.Property(e => e.KeyPrefix).IsRequired().HasMaxLength(16);
            entity.Property(e => e.HashedSuffix).IsRequired().HasMaxLength(64);
            entity.Property(e => e.ApiKeyId).IsRequired();
            entity.Property(e => e.Scope).IsRequired().HasMaxLength(50);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            // Look-ups during auth:
            // 1. Fast path — by composite (KeyPrefix, HashedSuffix); PK gives
            //    us KeyPrefix already, suffix filter is a secondary equality.
            //    A dedicated index keeps SELECT ... WHERE KeyPrefix = ? AND
            //    HashedSuffix = ? off a full table scan on large CPs.
            entity.HasIndex(e => new { e.KeyPrefix, e.HashedSuffix });
            // 2. Reverse lookup by ApiKeyId for revoke cascades.
            entity.HasIndex(e => e.ApiKeyId);
            // 3. Tenant filter for bulk-revoke on tenant delete (cascade).
            entity.HasIndex(e => e.TenantId).HasFilter("\"TenantId\" IS NOT NULL");
        });
    }
}
