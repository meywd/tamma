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

    public TenantDbContext(DbContextOptions<TenantDbContext> options, Guid tenantId = default)
        : base(options)
    {
        // Guid.Empty is permitted only for design-time and migration callers
        // (EF tooling, EfTenantDbMigrator.MigrateTenantAppAsync). Runtime
        // callers MUST route through ITenantDbContextFactory.CreateAsync
        // which always supplies a concrete tenant id. The ambient filter
        // wired by TammaModelConfiguration.ApplyTenantFilter still
        // short-circuits to permissive when TenantId is Guid.Empty so
        // the migration graph stays consistent with production.
        TenantId = tenantId;
    }

    // ── Tenant-scoped entities ──
    public DbSet<AgentConfig> AgentConfigs => Set<AgentConfig>();
    public DbSet<PromptOverride> PromptOverrides => Set<PromptOverride>();
    // Story 39-5 — tenant-resident acceptance-rules overrides (dual-scoped like
    // prompt_overrides: user_id in single-user mode, tenant_id in SaaS mode).
    public DbSet<AcceptanceRulesOverride> AcceptanceRulesOverrides => Set<AcceptanceRulesOverride>();
    // Story 32-2 — SaaS tenant-keyed role→agent selections (the single-user
    // user-keyed rows live on the CP context). Same table shape, two homes.
    public DbSet<AgentRoleSelection> AgentRoleSelections => Set<AgentRoleSelection>();
    public DbSet<Convention> Conventions => Set<Convention>();
    public DbSet<ProviderHealth> ProviderHealths => Set<ProviderHealth>();
    public DbSet<ProviderDiagnostic> ProviderDiagnostics => Set<ProviderDiagnostic>();
    public DbSet<SanitizationRule> SanitizationRules => Set<SanitizationRule>();
    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<WorkflowInstance> WorkflowInstances => Set<WorkflowInstance>();
    public DbSet<Entities.DomainEvent> DomainEvents => Set<Entities.DomainEvent>();
    public DbSet<QueuedTask> QueuedTasks => Set<QueuedTask>();
    public DbSet<EmailOutboxMessage> EmailOutbox => Set<EmailOutboxMessage>();
    public DbSet<ChannelOutboxMessage> ChannelOutbox => Set<ChannelOutboxMessage>();
    public DbSet<BudgetConfig> BudgetConfigs => Set<BudgetConfig>();

    // Story 36-1 — per-tenant dimensional analytics fact tables (hourly +
    // daily roll-up). Schema-only; Story 36-2 owns population.
    public DbSet<AnalyticsUsageHourly> AnalyticsUsageHourly => Set<AnalyticsUsageHourly>();
    public DbSet<AnalyticsUsageDaily> AnalyticsUsageDaily => Set<AnalyticsUsageDaily>();

    // Story 36-2 — per-tenant resumable cursor for the dimensional projection.
    public DbSet<AnalyticsProjectionCheckpoint> AnalyticsProjectionCheckpoints =>
        Set<AnalyticsProjectionCheckpoint>();

    // Story 37-1 — tenant-scope curated audit trail, materialized from this
    // tenant's domain_events stream by the AuditProjector. Platform-scope rows
    // live in the CP audit_records.
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    // Story 39-11 — tenant-resident document instances (the read-optimized
    // document product layer over this tenant's domain_events stream). Written
    // exclusively through IDocumentInstanceRepository; rebuildable from events.
    public DbSet<DocumentInstance> Documents => Set<DocumentInstance>();

    /// <summary>
    /// Tenant-scoped API keys (Story 28-7). The tenant DB api_keys table
    /// is locked to <c>Scope = 'tenant'</c> via a CHECK constraint; user /
    /// platform / installation keys stay on the CP <c>api_keys</c> table.
    /// </summary>
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    // Mentorship entities are tenant-scoped per Doc 01 §1.2 rows 27–30.
    public DbSet<MentorshipSession> MentorshipSessions => Set<MentorshipSession>();
    public DbSet<MentorshipEvent> MentorshipEvents => Set<MentorshipEvent>();
    public DbSet<JuniorDeveloper> JuniorDevelopers => Set<JuniorDeveloper>();
    public DbSet<Story> Stories => Set<Story>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Register the mentorship entities so configuration matches the
        // physical tables during the transition.
        TammaModelConfiguration.ConfigureMentorshipEntities(modelBuilder);
        // Tenant-scoped configuration — target architecture (Doc 01 §1.4)
        // has NO TenantId column on tenant-resident tables. When
        // fixedTenantId is non-null the configurator automatically
        // ignores CP entities + strips TenantId columns.
        TammaModelConfiguration.ConfigureTenantEntities(
            modelBuilder, fixedTenantId: TenantId);
        // Story 28-7 — tenant-scope api_keys with CHECK Scope='tenant'.
        TammaModelConfiguration.ConfigureTenantApiKeys(modelBuilder);
    }
}
