using Microsoft.EntityFrameworkCore;
using Tamma.Core.Entities;
using Tamma.Data.Entities;

namespace Tamma.Data;

/// <summary>
/// Per-tenant <see cref="DbContext"/>. One instance is constructed per
/// request + per tenant via <see cref="ITenantDbContextFactory"/>. Owns the
/// per-tenant business data: agent configs, prompt overrides, provider
/// health/diagnostics, sanitization rules, workflow defs + instances,
/// domain events, queued tasks, email outbox, budget configs, mentorship
/// sessions.
///
/// <para>Epic 28 isolation model: tenancy is implicit in the connection
/// string. The context carries no <c>HasQueryFilter</c> clauses and, in
/// the target architecture, no <c>TenantId</c> column — each tenant's
/// physical database holds only that tenant's rows. During the transition
/// (shared central DB) the factory still binds the correct tenant to
/// <c>ITenantContext</c> so the inherited filters continue to scope
/// correctly.</para>
///
/// <para>Supersedes the obsolete <c>TammaAppDbContext</c> + RLS scaffold:
/// RLS on a shared DB is no longer the isolation plane; per-tenant
/// database routing via <see cref="ITenantDbContextFactory"/> is.</para>
/// </summary>
public class TenantDbContext : DbContext
{
    /// <summary>
    /// The tenant this context is bound to. Populated at factory
    /// construction — every <see cref="TenantDbContext"/> instance is
    /// tied to exactly one tenant. Exposed for query-filter evaluation
    /// during the transitional period when the underlying DB is still
    /// shared; becomes redundant once each tenant has its own DB.
    /// </summary>
    public Guid TenantId { get; }

    public TenantDbContext(DbContextOptions<TenantDbContext> options, Guid tenantId)
        : base(options)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException(
                "TenantDbContext requires a non-empty tenant id. Use ControlPlaneDbContext for CP data.",
                nameof(tenantId));
        TenantId = tenantId;
    }

    // ── Tenant-scoped entities ──
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

    // Mentorship entities are CP-hosted today but tenant-scoped in the
    // domain model. The factory path uses these DbSets for tenant-scoped
    // mentorship queries; CP housekeeping uses ControlPlaneDbContext.
    public DbSet<MentorshipSession> MentorshipSessions => Set<MentorshipSession>();
    public DbSet<MentorshipEvent> MentorshipEvents => Set<MentorshipEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Register the mentorship entities so configuration matches the
        // physical tables during the transition.
        TammaModelConfiguration.ConfigureMentorshipEntities(modelBuilder);
        // Tenant-scoped configuration — fail-closed per-tenant filter that
        // reads TenantId from this context instance (not ITenantContext).
        TammaModelConfiguration.ConfigureTenantEntities(
            modelBuilder, fixedTenantId: TenantId);
    }
}
