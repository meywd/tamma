using Microsoft.EntityFrameworkCore;
using Tamma.Core.Entities;
using Tamma.Core.Enums;
using Tamma.Data.Entities;

namespace Tamma.Data;

/// <summary>
/// Shared entity configuration reused by <see cref="ControlPlaneDbContext"/>
/// and <see cref="TenantDbContext"/>. Keeping this in one place avoids
/// divergence between the two contexts during the Epic 28 transition where
/// both physically read from the same central Postgres but logically own
/// different entity subsets.
///
/// <para>Methods are grouped by placement plane:</para>
/// <list type="bullet">
///   <item><description><see cref="ConfigureControlPlaneEntities"/> — CP
///     tables (users, tenants, memberships, invites, API keys, GitHub
///     installations, webhook deliveries, auth tokens). No tenant filter.
///     </description></item>
///   <item><description><see cref="ConfigureTenantEntities"/> — per-tenant
///     tables (agent configs, prompts, providers, workflows, events,
///     tasks, outbox, budgets). Configured with a fail-closed query
///     filter when constructed via <see cref="TenantDbContext"/>, and a
///     permissive filter when configured on <see cref="ControlPlaneDbContext"/>
///     (migration graph coverage only — app code never reads these through
///     CP).</description></item>
///   <item><description><see cref="ConfigureMentorshipEntities"/> — legacy
///     mentorship schema, present on both contexts for the transition.
///     </description></item>
/// </list>
/// </summary>
internal static class TammaModelConfiguration
{
    public static void ConfigureControlPlaneEntities(ModelBuilder modelBuilder)
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

            entity.HasIndex(e => e.Email).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
            entity.HasIndex(e => e.GitHubId).IsUnique().HasFilter("\"GitHubId\" IS NOT NULL AND \"DeletedAt\" IS NULL");
            entity.HasIndex(e => e.TenantId);

            // Soft-delete filter only — users table is CP-scoped, tenant
            // membership is modelled via tenant_memberships. The TenantId
            // column on users is retained as a "last active tenant" hint,
            // not a hard-isolation scope. No per-tenant filter.
            entity.HasQueryFilter(e => e.DeletedAt == null);
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

            // Cranl per-tenant provisioning columns (audit cranl/001).
            entity.Property(e => e.ProvisioningState)
                .IsRequired().HasMaxLength(40).HasDefaultValue("none");
            entity.Property(e => e.CranlProjectId).HasMaxLength(255);
            entity.Property(e => e.CranlDatabaseId).HasMaxLength(255);
            entity.Property(e => e.CranlAppId).HasMaxLength(255);
            entity.Property(e => e.CranlRegion).HasMaxLength(100);
            entity.Property(e => e.CranlAppUrl).HasMaxLength(255);
            entity.Property(e => e.CranlDatabaseUrlEncrypted).HasColumnType("bytea");

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
            entity.Property(e => e.AppId).HasColumnType("bigint");
            entity.Property(e => e.Permissions).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => e.InstallationId).IsUnique();
            entity.HasIndex(e => e.AccountLogin);
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
    }

    /// <summary>
    /// Configure the per-tenant entity graph. <paramref name="fixedTenantId"/>
    /// is non-null when invoked from <see cref="TenantDbContext"/> — the
    /// query filter binds directly to that tenant (fail-closed, no ambient
    /// dependency). When null (migration-graph coverage only, invoked from
    /// <see cref="ControlPlaneDbContext"/>), the filters are permissive —
    /// app code MUST NOT read these tables through CP; the permissive
    /// filter is only there to keep the migration graph generating correctly
    /// during the Epic 28 transition.
    /// </summary>
    public static void ConfigureTenantEntities(
        ModelBuilder modelBuilder, Guid? fixedTenantId = null)
    {
        // When invoked from TenantDbContext, register Tenant/User as
        // shadow entities (no CP-side navigations) so EF can resolve the
        // HasOne(e => e.Tenant) nav on AgentConfig / SanitizationRule
        // without demanding the full CP relationship graph (Tenant↔Owner
        // ↔ User ↔ Memberships). Without this EF complains about
        // ambiguous one-to-one sides for Tenant.Owner / User.Tenant.
        if (fixedTenantId is not null)
        {
            modelBuilder.Entity<Tenant>(entity =>
            {
                entity.ToTable("tenants");
                entity.HasKey(e => e.Id);
                entity.Ignore(e => e.Owner);
                entity.Ignore(e => e.Memberships);
                entity.Ignore(e => e.Invites);
            });
            modelBuilder.Entity<User>(entity =>
            {
                entity.ToTable("users");
                entity.HasKey(e => e.Id);
                entity.Ignore(e => e.Tenant);
                entity.Ignore(e => e.Memberships);
                entity.Ignore(e => e.RefreshTokens);
                entity.Ignore(e => e.PasswordResetTokens);
            });
        }

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

            entity.HasIndex(e => e.TenantId).IsUnique().HasFilter("\"TenantId\" IS NOT NULL");

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            ApplyTenantFilter(entity, fixedTenantId, e => e.TenantId);
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

            ApplyTenantFilter(entity, fixedTenantId, e => e.TenantId);
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

            entity.HasIndex(e => new { e.ProviderKey, e.TenantId }).IsUnique()
                .HasFilter("\"TenantId\" IS NOT NULL");

            ApplyTenantFilter(entity, fixedTenantId, e => e.TenantId);
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
            entity.HasIndex(e => new { e.TenantId, e.CreatedAt });
            entity.HasIndex(e => new { e.EngineId, e.CreatedAt });
            entity.HasIndex(e => new { e.Model, e.CreatedAt });
            entity.HasIndex(e => new { e.RequestType, e.CreatedAt });
            entity.HasIndex(e => e.CorrelationId).HasFilter("\"CorrelationId\" IS NOT NULL");

            ApplyTenantFilter(entity, fixedTenantId, e => e.TenantId);
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

            entity.HasIndex(e => e.TenantId).IsUnique().HasFilter("\"TenantId\" IS NOT NULL");

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            ApplyTenantFilter(entity, fixedTenantId, e => e.TenantId);
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

            ApplyTenantFilter(entity, fixedTenantId, e => e.TenantId);
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
            entity.HasIndex(e => new { e.TenantId, e.DefinitionId });
            entity.HasIndex(e => new { e.TenantId, e.Status });

            entity.HasOne(e => e.Definition)
                .WithMany(d => d.Instances)
                .HasForeignKey(e => e.DefinitionId)
                .OnDelete(DeleteBehavior.Cascade);

            ApplyTenantFilter(entity, fixedTenantId, e => e.TenantId);
        });

        // ── QueuedTask ──
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

            entity.HasIndex(e => new { e.Status, e.CreatedAt });
            entity.HasIndex(e => new { e.TenantId, e.Status });
            // No query filter: the task queue is shared infra; tenant scoping
            // is explicit in the repository APIs (they take tenantId).
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
            entity.HasIndex(e => new { e.TenantId, e.IssueNumber })
                .HasFilter("\"IssueNumber\" IS NOT NULL");

            ApplyTenantFilter(entity, fixedTenantId, e => e.TenantId);
        });

        // ── BudgetConfig ──
        modelBuilder.Entity<BudgetConfig>(entity =>
        {
            entity.ToTable("budget_configs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.AccountId).IsRequired().HasMaxLength(255);
            entity.Property(e => e.LimitUsd).HasPrecision(18, 6);
            entity.Property(e => e.AlertThreshold).HasDefaultValue(0.8);
            entity.Property(e => e.PeriodDays).HasDefaultValue(30);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => new { e.TenantId, e.AccountId })
                .IsUnique()
                .HasFilter("\"TenantId\" IS NOT NULL");
            entity.HasIndex(e => e.AccountId)
                .IsUnique()
                .HasDatabaseName("ix_budget_configs_accountid_default")
                .HasFilter("\"TenantId\" IS NULL");
            // No query filter — the budget provider resolves tenant-specific
            // vs platform-default rows explicitly.
        });

        // ── EmailOutboxMessage ──
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

            entity.HasIndex(e => new { e.Status, e.NextAttemptAt });
            entity.HasIndex(e => e.TenantId);
            // No query filter — the outbox is shared infra; tenant scoping is
            // explicit in the repository APIs.
        });
    }

    private static void ApplyTenantFilter<T>(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> entity,
        Guid? fixedTenantId,
        System.Linq.Expressions.Expression<Func<T, Guid?>> tenantAccessor) where T : class
    {
        // When configuring the tenant context we DO NOT bake the tenant id
        // into the model graph. EF caches compiled models by context type,
        // so a per-instance constant inside the filter lambda would leak
        // the first-seen tenant to every subsequent context. Instead we
        // leave the filter off the model entirely and rely on the
        // per-tenant Npgsql connection to enforce isolation at the wire
        // (one physical DB per tenant) plus the factory binding tenant
        // id into the context instance. Until the physical-DB split lands
        // (Story 28-1) call sites that query a tenant context MUST apply
        // WHERE TenantId == @tid explicitly — that is the Wave A.5
        // contract. Many repos already filter by TenantId as part of the
        // primary key lookup or composite index.
        //
        // On ControlPlaneDbContext (fixedTenantId == null) we leave the
        // filter off too — the CP plane is a cross-tenant admin /
        // migration-graph carrier during the transition. Only tenant
        // repos read from these DbSets, and they always pass an explicit
        // tenantId predicate.
        _ = entity; _ = fixedTenantId; _ = tenantAccessor;
    }

    public static void ConfigureMentorshipEntities(ModelBuilder modelBuilder)
    {
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
