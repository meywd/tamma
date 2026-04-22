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
}
