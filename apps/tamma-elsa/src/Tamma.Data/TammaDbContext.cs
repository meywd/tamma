using Microsoft.EntityFrameworkCore;
using Tamma.Core.Entities;
using Tamma.Core.Enums;
using Tamma.Data.Entities;

namespace Tamma.Data;

public class TammaDbContext : DbContext
{
    private readonly ITenantContext? _tenantContext;

    public TammaDbContext(DbContextOptions<TammaDbContext> options)
        : base(options)
    {
    }

    public TammaDbContext(DbContextOptions<TammaDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    /// <summary>
    /// Constructor used by subclasses that want to project their own typed
    /// <see cref="DbContextOptions{T}"/> onto this base. EF Core dispatches
    /// by runtime context type, so the subclass carries its own model
    /// cache and OnModelCreating can behave differently (notably: enable
    /// fail-closed tenant query filters).
    /// </summary>
    protected TammaDbContext(DbContextOptions options)
        : base(options)
    {
    }

    protected TammaDbContext(DbContextOptions options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    /// <summary>
    /// When <c>true</c>, tenant-scoped <c>HasQueryFilter</c> calls emit
    /// <c>TenantId == _tenantContext.TenantId</c> (fail-closed: a null
    /// tenant context returns zero rows — the correct TS-parity
    /// behavior). When <c>false</c>, filters use the legacy permissive
    /// form (<c>tenantId == null || TenantId == tenantId</c>) so admin
    /// paths that deliberately read cross-tenant (migrations, task queue,
    /// outbox, workflow sync) continue to work without touching every
    /// repository call site.
    ///
    /// <para>The base class (admin) returns <c>false</c>. The
    /// <see cref="TammaAppDbContext"/> subclass overrides to <c>true</c>.
    /// This closes finding orgs/002 by making the per-request runtime
    /// path fail-closed while preserving admin escape hatches.</para>
    /// </summary>
    protected virtual bool EnforceTenantFilter => false;

    // Existing mentorship entities
    public DbSet<MentorshipSession> MentorshipSessions => Set<MentorshipSession>();
    public DbSet<MentorshipEvent> MentorshipEvents => Set<MentorshipEvent>();
    public DbSet<JuniorDeveloper> JuniorDevelopers => Set<JuniorDeveloper>();
    public DbSet<Story> Stories => Set<Story>();

    // New multi-tenant entities
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureExistingMentorshipEntities(modelBuilder);
        ConfigureNewEntities(modelBuilder);
    }

    private void ConfigureNewEntities(ModelBuilder modelBuilder)
    {
        // ── User ──
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Email).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Role).IsRequired().HasMaxLength(20).HasDefaultValue("member");
            entity.Property(e => e.AuthMethod).IsRequired().HasMaxLength(20).HasDefaultValue("email");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            // GitHubId widened to bigint (long) — see entity comment.
            entity.Property(e => e.GitHubId).HasColumnType("bigint");
            // Per-user provider settings (jsonb). Restored from TS migration 004.
            entity.Property(e => e.Settings).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // Case-sensitive unique kept for back-compat; the case-insensitive
            // partial unique on LOWER(email) is added by the Phase-1 hardening
            // migration and is the canonical lookup path.
            entity.HasIndex(e => e.Email).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
            entity.HasIndex(e => e.GitHubId).IsUnique().HasFilter("\"GitHubId\" IS NOT NULL AND \"DeletedAt\" IS NULL");
            entity.HasIndex(e => e.TenantId);

            // Soft delete + tenant isolation filter.
            //
            // Fail-closed (app-role context): null tenant returns zero rows.
            // Permissive (admin/base context): null tenant returns ALL rows
            // so background services + migrations keep working without
            // manual .IgnoreQueryFilters() at every call site.
            var tenantId = _tenantContext?.TenantId;
            if (EnforceTenantFilter)
            {
                entity.HasQueryFilter(e => e.DeletedAt == null && e.TenantId == tenantId);
            }
            else
            {
                entity.HasQueryFilter(e => e.DeletedAt == null && (tenantId == null || e.TenantId == tenantId));
            }
        });

        // ── RefreshToken ──
        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("refresh_tokens");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.TokenHash).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.UserId);

            entity.HasOne(e => e.User)
                .WithMany(u => u.RefreshTokens)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── PasswordResetToken ──
        modelBuilder.Entity<PasswordResetToken>(entity =>
        {
            entity.ToTable("password_reset_tokens");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.TokenHash).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.UserId);

            entity.HasOne(e => e.User)
                .WithMany(u => u.PasswordResetTokens)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Tenant ──
        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.ToTable("tenants");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Slug).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Type).IsRequired().HasMaxLength(20).HasDefaultValue("personal");
            entity.Property(e => e.Plan).IsRequired().HasMaxLength(50).HasDefaultValue("free");
            entity.Property(e => e.Settings).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => e.Slug).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
            entity.HasIndex(e => e.ExternalId).IsUnique().HasFilter("\"ExternalId\" IS NOT NULL AND \"DeletedAt\" IS NULL");

            entity.HasQueryFilter(e => e.DeletedAt == null);

            entity.HasOne(e => e.Owner)
                .WithMany()
                .HasForeignKey(e => e.OwnerId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ── TenantMembership ──
        modelBuilder.Entity<TenantMembership>(entity =>
        {
            entity.ToTable("tenant_memberships");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Role).IsRequired().HasMaxLength(20).HasDefaultValue("member");
            entity.Property(e => e.JoinedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => new { e.TenantId, e.UserId }).IsUnique();

            entity.HasOne(e => e.Tenant)
                .WithMany(t => t.Memberships)
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany(u => u.Memberships)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── UserInvite ──
        modelBuilder.Entity<UserInvite>(entity =>
        {
            entity.ToTable("user_invites");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.InviteTokenHash).IsRequired();
            entity.Property(e => e.Role).IsRequired().HasMaxLength(20).HasDefaultValue("member");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => e.InviteTokenHash).IsUnique();
            entity.HasIndex(e => e.TenantId);

            entity.HasOne(e => e.Tenant)
                .WithMany(t => t.Invites)
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── ApiKey ──
        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.ToTable("api_keys");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Scope).IsRequired().HasMaxLength(50);
            entity.Property(e => e.OwnerId).IsRequired();
            entity.Property(e => e.KeyHash).IsRequired();
            entity.Property(e => e.KeyPrefix).IsRequired().HasMaxLength(16);
            entity.Property(e => e.Label).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Permissions).HasColumnType("text[]");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => e.KeyHash).IsUnique();
            entity.HasIndex(e => new { e.Scope, e.OwnerId });
            entity.HasIndex(e => e.TenantId);
        });

        // ── GitHubInstallation ──
        modelBuilder.Entity<GitHubInstallation>(entity =>
        {
            entity.ToTable("github_installations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.AccountLogin).IsRequired().HasMaxLength(255);
            entity.Property(e => e.AccountType).IsRequired().HasMaxLength(50);
            // AppId widened to bigint to match the GitHub API.
            entity.Property(e => e.AppId).HasColumnType("bigint");
            entity.Property(e => e.Permissions).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => e.InstallationId).IsUnique();
            // Lookup-by-account-login (webhook → UI flow). Restored from TS.
            entity.HasIndex(e => e.AccountLogin);
            // Tenant-scoped listings.
            entity.HasIndex(e => e.TenantId);
        });

        // ── GitHubInstallationRepo ──
        modelBuilder.Entity<GitHubInstallationRepo>(entity =>
        {
            entity.ToTable("github_installation_repos");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Owner).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.RepoFullName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => new { e.InstallationEntityId, e.RepoId }).IsUnique();
            entity.HasIndex(e => e.RepoFullName);

            entity.HasOne(e => e.Installation)
                .WithMany(i => i.Repos)
                .HasForeignKey(e => e.InstallationEntityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── GitHubWebhookDelivery (audit findings 003 + 019) ──
        modelBuilder.Entity<GitHubWebhookDelivery>(entity =>
        {
            entity.ToTable("github_webhook_deliveries");
            entity.HasKey(e => e.DeliveryId);
            entity.Property(e => e.DeliveryId).HasColumnType("uuid");
            entity.Property(e => e.ReceivedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Action).HasMaxLength(100);
            entity.Property(e => e.InstallationId).HasColumnType("bigint");

            entity.HasIndex(e => e.ReceivedAt);
            entity.HasIndex(e => new { e.InstallationId, e.ReceivedAt });
        });

        // ── AgentConfig ──
        modelBuilder.Entity<AgentConfig>(entity =>
        {
            entity.ToTable("agent_configs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Config).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.Version).HasDefaultValue(1);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // Plain unique on a nullable column allows multiple NULL rows in
            // Postgres, which would permit several "system default" rows to
            // coexist. The Phase-1 hardening migration replaces this with
            // two partial unique indexes (NULL vs NOT NULL).
            entity.HasIndex(e => e.TenantId).IsUnique().HasFilter("\"TenantId\" IS NOT NULL");

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            var tenantId = _tenantContext?.TenantId;
            if (EnforceTenantFilter)
            {
                entity.HasQueryFilter(e => e.TenantId == tenantId);
            }
            else
            {
                entity.HasQueryFilter(e => tenantId == null || e.TenantId == tenantId);
            }
        });

        // ── PromptOverride ──
        modelBuilder.Entity<PromptOverride>(entity =>
        {
            entity.ToTable("prompt_overrides");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Scope).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Template).IsRequired();
            entity.Property(e => e.Variables).HasColumnType("text[]");
            entity.Property(e => e.MaxTokens).HasDefaultValue(4096);
            entity.Property(e => e.Version).HasDefaultValue(1);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => new { e.UserId, e.Scope, e.Role, e.Action }).IsUnique();

            var tenantId = _tenantContext?.TenantId;
            if (EnforceTenantFilter)
            {
                entity.HasQueryFilter(e => e.TenantId == tenantId);
            }
            else
            {
                entity.HasQueryFilter(e => tenantId == null || e.TenantId == tenantId);
            }
        });

        // ── ProviderHealth ──
        modelBuilder.Entity<ProviderHealth>(entity =>
        {
            entity.ToTable("provider_health");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.ProviderKey).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue("unknown");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // Plain unique on a nullable-TenantId tuple permits multiple
            // global rows per provider key in Postgres. The Phase-1
            // hardening migration replaces this with split partial uniques.
            entity.HasIndex(e => new { e.ProviderKey, e.TenantId }).IsUnique()
                .HasFilter("\"TenantId\" IS NOT NULL");

            var tenantId = _tenantContext?.TenantId;
            if (EnforceTenantFilter)
            {
                entity.HasQueryFilter(e => e.TenantId == tenantId);
            }
            else
            {
                entity.HasQueryFilter(e => tenantId == null || e.TenantId == tenantId);
            }
        });

        // ── ProviderDiagnostic ──
        modelBuilder.Entity<ProviderDiagnostic>(entity =>
        {
            entity.ToTable("provider_diagnostics");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.ProviderKey).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Cost).HasPrecision(18, 6);
            entity.Property(e => e.Success).HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => new { e.ProviderKey, e.CreatedAt });
            // Per-tenant billing reports (TS migration 014).
            entity.HasIndex(e => new { e.TenantId, e.CreatedAt });
            // Per-engine usage breakdown.
            entity.HasIndex(e => new { e.EngineId, e.CreatedAt });
            // Per-model usage breakdown.
            entity.HasIndex(e => new { e.Model, e.CreatedAt });
            // Per-event-type breakdown.
            entity.HasIndex(e => new { e.RequestType, e.CreatedAt });
            // Cross-step trace stitching.
            entity.HasIndex(e => e.CorrelationId).HasFilter("\"CorrelationId\" IS NOT NULL");

            // No FK on TenantId — see migration note for finding 032.
            // Diagnostics is a write-once event sink; the tenant row may not
            // exist yet at ingest time. Tenant isolation is enforced at the
            // query-filter layer.

            var tenantId = _tenantContext?.TenantId;
            if (EnforceTenantFilter)
            {
                entity.HasQueryFilter(e => e.TenantId == tenantId);
            }
            else
            {
                entity.HasQueryFilter(e => tenantId == null || e.TenantId == tenantId);
            }
        });

        // ── SanitizationRule ──
        modelBuilder.Entity<SanitizationRule>(entity =>
        {
            entity.ToTable("sanitization_rules");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Rules).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // One row per tenant. Partial unique allows the system-default row
            // (TenantId IS NULL) to coexist with per-tenant overrides.
            entity.HasIndex(e => e.TenantId).IsUnique().HasFilter("\"TenantId\" IS NOT NULL");

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            var tenantId = _tenantContext?.TenantId;
            if (EnforceTenantFilter)
            {
                entity.HasQueryFilter(e => e.TenantId == tenantId);
            }
            else
            {
                entity.HasQueryFilter(e => tenantId == null || e.TenantId == tenantId);
            }
        });

        // ── WorkflowDefinition ──
        modelBuilder.Entity<WorkflowDefinition>(entity =>
        {
            entity.ToTable("workflow_definitions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Steps).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            entity.Property(e => e.Version).HasDefaultValue(1);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.SyncedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => e.TenantId);

            var tenantId = _tenantContext?.TenantId;
            if (EnforceTenantFilter)
            {
                entity.HasQueryFilter(e => e.TenantId == tenantId);
            }
            else
            {
                entity.HasQueryFilter(e => tenantId == null || e.TenantId == tenantId);
            }
        });

        // ── WorkflowInstance ──
        modelBuilder.Entity<WorkflowInstance>(entity =>
        {
            entity.ToTable("workflow_instances");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50).HasDefaultValue("pending");
            entity.Property(e => e.Variables).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.Result).HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => new { e.DefinitionId, e.Status });
            entity.HasIndex(e => e.TenantId);
            // Per-tenant-per-definition listings (TS migration 011).
            entity.HasIndex(e => new { e.TenantId, e.DefinitionId });
            entity.HasIndex(e => new { e.TenantId, e.Status });

            entity.HasOne(e => e.Definition)
                .WithMany(d => d.Instances)
                .HasForeignKey(e => e.DefinitionId)
                .OnDelete(DeleteBehavior.Cascade);

            var tenantId = _tenantContext?.TenantId;
            if (EnforceTenantFilter)
            {
                entity.HasQueryFilter(e => e.TenantId == tenantId);
            }
            else
            {
                entity.HasQueryFilter(e => tenantId == null || e.TenantId == tenantId);
            }
        });

        // ── QueuedTask ──
        // Multi-tenant task queue. Webhook dispatcher enqueues here so the
        // handler returns fast; TaskQueueProcessor polls for pending rows.
        modelBuilder.Entity<QueuedTask>(entity =>
        {
            entity.ToTable("queued_tasks");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Type).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Payload).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue("pending");
            entity.Property(e => e.RetryCount).HasDefaultValue(0);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // Index drives the processor's "next pending task" query.
            entity.HasIndex(e => new { e.Status, e.CreatedAt });
            // Index for tenant-scoped listings.
            entity.HasIndex(e => new { e.TenantId, e.Status });

            // No query filter: the task queue is shared infrastructure; tenant
            // scoping is explicit via repository APIs. This mirrors the TS
            // InMemoryTaskQueue, which never bound to a tenant context.
        });

        // ── DomainEvent ──
        modelBuilder.Entity<Entities.DomainEvent>(entity =>
        {
            entity.ToTable("domain_events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Type).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Tags).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.Metadata).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.Data).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => new { e.Type, e.CreatedAt });
            entity.HasIndex(e => e.TenantId);
            // Per-issue replay (dominant query on the engine replay path).
            // Partial — most events have no issue number.
            entity.HasIndex(e => new { e.TenantId, e.IssueNumber })
                .HasFilter("\"IssueNumber\" IS NOT NULL");

            var tenantId = _tenantContext?.TenantId;
            if (EnforceTenantFilter)
            {
                entity.HasQueryFilter(e => e.TenantId == tenantId);
            }
            else
            {
                entity.HasQueryFilter(e => tenantId == null || e.TenantId == tenantId);
            }
        });

        // ── EmailOutboxMessage ──
        // Store-and-forward outbox for the SMTP sender. The SMTP IEmailService
        // enqueues here; OutboxSmtpSender polls, claims, delivers, and records
        // the outcome. Resend provider does NOT use this table — it is an
        // HTTP-synchronous path that writes straight to the event store.
        //
        // No query filter: the outbox is shared infrastructure. Tenant scoping
        // is explicit via repository APIs, matching the QueuedTask pattern.
        modelBuilder.Entity<EmailOutboxMessage>(entity =>
        {
            entity.ToTable("email_outbox");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Template).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ToAddress).IsRequired().HasMaxLength(320);
            entity.Property(e => e.Subject).IsRequired().HasMaxLength(512);
            entity.Property(e => e.HtmlBody).IsRequired();
            entity.Property(e => e.TextBody).IsRequired();
            entity.Property(e => e.FromAddress).IsRequired().HasMaxLength(320);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue("pending");
            entity.Property(e => e.Attempts).HasDefaultValue(0);
            entity.Property(e => e.MaxAttempts).HasDefaultValue(5);
            entity.Property(e => e.NextAttemptAt).HasDefaultValueSql("now()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // Drives the sender's claim query: "next pending row due for send".
            entity.HasIndex(e => new { e.Status, e.NextAttemptAt });
            // Tenant-scoped reporting.
            entity.HasIndex(e => e.TenantId);
        });
    }

    private static void ConfigureExistingMentorshipEntities(ModelBuilder modelBuilder)
    {
        // MentorshipSession configuration
        modelBuilder.Entity<MentorshipSession>(entity =>
        {
            entity.ToTable("mentorship_sessions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("uuid_generate_v4()");
            entity.Property(e => e.StoryId).HasColumnName("story_id").IsRequired();
            entity.Property(e => e.JuniorId).HasColumnName("junior_id").IsRequired();
            entity.Property(e => e.CurrentState).HasColumnName("current_state").HasConversion<string>().IsRequired();
            entity.Property(e => e.PreviousState).HasColumnName("previous_state").HasConversion<string>();
            entity.Property(e => e.Context).HasColumnName("context").HasColumnType("jsonb");
            entity.Property(e => e.Variables).HasColumnName("variables").HasColumnType("jsonb");
            entity.Property(e => e.WorkflowInstanceId).HasColumnName("workflow_instance_id");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            entity.Property(e => e.Status).HasColumnName("status").HasConversion<string>().HasDefaultValue(SessionStatus.Active);
            entity.Property(e => e.RowVersion).HasColumnName("row_version").IsRowVersion();
            entity.HasIndex(e => e.JuniorId);
            entity.HasIndex(e => e.StoryId);
            entity.HasIndex(e => e.CurrentState);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.WorkflowInstanceId);
            entity.HasOne(e => e.Junior).WithMany(j => j.Sessions).HasForeignKey(e => e.JuniorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Story).WithMany(s => s.Sessions).HasForeignKey(e => e.StoryId).OnDelete(DeleteBehavior.Restrict);
        });

        // MentorshipEvent configuration
        modelBuilder.Entity<MentorshipEvent>(entity =>
        {
            entity.ToTable("mentorship_events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("uuid_generate_v4()");
            entity.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
            entity.Property(e => e.EventType).HasColumnName("event_type").IsRequired();
            entity.Property(e => e.EventData).HasColumnName("event_data").HasColumnType("jsonb");
            entity.Property(e => e.StateFrom).HasColumnName("state_from").HasConversion<string>();
            entity.Property(e => e.StateTo).HasColumnName("state_to").HasConversion<string>();
            entity.Property(e => e.Trigger).HasColumnName("trigger");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            entity.HasIndex(e => e.SessionId);
            entity.HasIndex(e => e.EventType);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasOne(e => e.Session).WithMany(s => s.Events).HasForeignKey(e => e.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        // JuniorDeveloper configuration
        modelBuilder.Entity<JuniorDeveloper>(entity =>
        {
            entity.ToTable("junior_developers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Name).HasColumnName("name").IsRequired();
            entity.Property(e => e.Email).HasColumnName("email");
            entity.Property(e => e.SlackId).HasColumnName("slack_id");
            entity.Property(e => e.GitHubUsername).HasColumnName("github_username");
            entity.Property(e => e.SkillLevel).HasColumnName("skill_level").HasDefaultValue(1);
            entity.Property(e => e.Preferences).HasColumnName("preferences").HasColumnType("jsonb");
            entity.Property(e => e.LearningPatterns).HasColumnName("learning_patterns").HasColumnType("jsonb");
            entity.Property(e => e.TotalSessions).HasColumnName("total_sessions").HasDefaultValue(0);
            entity.Property(e => e.SuccessfulSessions).HasColumnName("successful_sessions").HasDefaultValue(0);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
            entity.HasIndex(e => e.Email);
            entity.HasIndex(e => e.GitHubUsername);
            entity.HasIndex(e => e.SkillLevel);
        });

        // Story configuration
        modelBuilder.Entity<Story>(entity =>
        {
            entity.ToTable("stories");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Title).HasColumnName("title").IsRequired();
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.AcceptanceCriteria).HasColumnName("acceptance_criteria").HasColumnType("jsonb");
            entity.Property(e => e.TechnicalRequirements).HasColumnName("technical_requirements").HasColumnType("jsonb");
            entity.Property(e => e.Priority).HasColumnName("priority").HasDefaultValue(3);
            entity.Property(e => e.Complexity).HasColumnName("complexity").HasDefaultValue(3);
            entity.Property(e => e.EstimatedHours).HasColumnName("estimated_hours");
            entity.Property(e => e.Tags).HasColumnName("tags");
            entity.Property(e => e.RepositoryUrl).HasColumnName("repository_url");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
            entity.HasIndex(e => e.Priority);
            entity.HasIndex(e => e.Complexity);
        });
    }
}
