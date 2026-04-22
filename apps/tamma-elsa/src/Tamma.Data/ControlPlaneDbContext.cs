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
    }
}
