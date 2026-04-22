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

    // Existing mentorship entities (global registry — shared across tenants,
    // lives on CP until mentorship becomes a per-tenant feature).
    public DbSet<MentorshipSession> MentorshipSessions => Set<MentorshipSession>();
    public DbSet<MentorshipEvent> MentorshipEvents => Set<MentorshipEvent>();
    public DbSet<JuniorDeveloper> JuniorDevelopers => Set<JuniorDeveloper>();
    public DbSet<Story> Stories => Set<Story>();

    // ── Control-plane entities ──
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

    // ── Legacy-shared tables (DEPRECATED) ──
    //
    // These DbSets cover tenant-scoped entities that still live on the same
    // physical database during the Epic 28 transition. They are exposed here
    // solely so migrations (which run against ControlPlaneDbContext as the
    // owning context) still see the complete schema graph. Application code
    // MUST NOT read/write these through ControlPlaneDbContext — use
    // ITenantDbContextFactory instead. The DbSet surface will move to
    // TenantDbContext exclusively once Story 28-1 physical-DB split lands.
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        TammaModelConfiguration.ConfigureMentorshipEntities(modelBuilder);
        TammaModelConfiguration.ConfigureControlPlaneEntities(modelBuilder);
        TammaModelConfiguration.ConfigureTenantEntities(modelBuilder);
    }
}
