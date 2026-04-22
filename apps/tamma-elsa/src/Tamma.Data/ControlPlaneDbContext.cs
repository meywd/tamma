using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data;

/// <summary>
/// Control-plane <see cref="DbContext"/> introduced by Epic 28
/// (Database-per-Tenant). Owns the 14 control-plane-resident tables —
/// <c>users</c>, <c>tenants</c>, <c>tenant_memberships</c>,
/// <c>refresh_tokens</c>, <c>password_reset_tokens</c>,
/// <c>user_invites</c>, <c>api_keys</c>, <c>github_installations</c>,
/// <c>github_installation_repos</c>, <c>github_webhook_deliveries</c>,
/// <c>plans</c>, <c>platform_events</c>, <c>platform_queued_tasks</c>,
/// <c>platform_email_outbox</c>.
///
/// <para>Connection: <c>ConnectionStrings:ControlPlane</c> →
/// <c>tamma_control</c> Postgres database. No tenant query filters
/// (tables are not tenant-scoped — the tenant directory itself lives
/// here, plus cross-tenant lifecycle data).</para>
///
/// <para>Story status:
/// <list type="bullet">
///   <item><description>28-1 (this commit) — context + entity configs +
///     migrations only. Not yet wired into any handler.</description></item>
///   <item><description>28-2 — DI registration of this context alongside
///     legacy <see cref="TammaDbContext"/>; endpoint migration is
///     follow-up work tracked by Story 19-6 / Epic 28-9.</description></item>
///   <item><description>28-3 — split the remaining tenant-scoped tables
///     onto <see cref="TenantDbContext"/>.</description></item>
/// </list></para>
///
/// <para>The Epic-28 columns on <c>tenants</c> (PlanId, Status,
/// EncryptedConnectionString, KekVersion, FailureReason,
/// DeleteRequestedAt) are declared as EF shadow properties so the shared
/// <see cref="Tenant"/> POCO stays untouched; downstream stories access
/// them via <c>EF.Property&lt;T&gt;(entity, "PlanId")</c>. Promoting them
/// to first-class POCO properties is deferred until the legacy
/// <see cref="TammaDbContext"/> is removed.</para>
/// </summary>
public class ControlPlaneDbContext : DbContext
{
    public ControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options)
        : base(options)
    {
    }

    // ── 14 CP-resident DbSets ──
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
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<PlatformEvent> PlatformEvents => Set<PlatformEvent>();
    public DbSet<PlatformQueuedTask> PlatformQueuedTasks => Set<PlatformQueuedTask>();
    public DbSet<PlatformEmailOutboxMessage> PlatformEmailOutbox => Set<PlatformEmailOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureUsers(modelBuilder);
        ConfigureRefreshTokens(modelBuilder);
        ConfigurePasswordResetTokens(modelBuilder);
        ConfigurePlans(modelBuilder);
        ConfigureTenants(modelBuilder);
        ConfigureTenantMemberships(modelBuilder);
        ConfigureUserInvites(modelBuilder);
        ConfigureApiKeys(modelBuilder);
        ConfigureGitHubInstallations(modelBuilder);
        ConfigureGitHubInstallationRepos(modelBuilder);
        ConfigureGitHubWebhookDeliveries(modelBuilder);
        ConfigurePlatformEvents(modelBuilder);
        ConfigurePlatformQueuedTasks(modelBuilder);
        ConfigurePlatformEmailOutbox(modelBuilder);
    }

    private static void ConfigureUsers(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Email).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Role).IsRequired().HasMaxLength(20).HasDefaultValue("member");
            entity.Property(e => e.AuthMethod).IsRequired().HasMaxLength(20).HasDefaultValue("email");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.GitHubId).HasColumnType("bigint");
            entity.Property(e => e.Settings).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => e.Email).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
            entity.HasIndex(e => e.GitHubId).IsUnique().HasFilter("\"GitHubId\" IS NOT NULL AND \"DeletedAt\" IS NULL");

            // No tenant query filter — CP context is not tenant-scoped.
            // Soft-delete filter retained.
            entity.HasQueryFilter(e => e.DeletedAt == null);

            // TenantId column on User stays nullable for back-compat with the
            // legacy single-DB schema. New auth flows derive the active tenant
            // from TenantMembership rather than this column.
            entity.Property(e => e.TenantId);
        });
    }

    private static void ConfigureRefreshTokens(ModelBuilder modelBuilder)
    {
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
    }

    private static void ConfigurePasswordResetTokens(ModelBuilder modelBuilder)
    {
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
    }

    private static void ConfigurePlans(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Plan>(entity =>
        {
            entity.ToTable("plans");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Slug).IsRequired().HasMaxLength(64);
            entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.MonthlyPriceUsd).HasPrecision(18, 2);
            entity.Property(e => e.Quotas).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => e.Slug).IsUnique();
        });
    }

    private static void ConfigureTenants(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.ToTable("tenants");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Slug).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Type).IsRequired().HasMaxLength(20).HasDefaultValue("personal");
            entity.Property(e => e.Settings).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // Legacy plan string column retained for back-compat.
            entity.Property(e => e.Plan).IsRequired().HasMaxLength(50).HasDefaultValue("free");

            // ── Epic 28 DB-per-Tenant columns (EF shadow properties) ──
            //
            // Declared on the new CP context only; the shared
            // <see cref="Entities.Tenant"/> POCO stays untouched. Future
            // stories access these via <c>EF.Property&lt;T&gt;(entity, "...")</c>:
            //   - PlanId (FK to Plans)
            //   - Status (state machine — pending_verification → ... → deleted)
            //   - EncryptedConnectionString (Doc 01 §8.1 envelope)
            //   - KekVersion (rotation slot)
            //   - FailureReason (provisioning terminal failure detail)
            //   - DeleteRequestedAt (cooling-off timer source)
            entity.Property<Guid?>("PlanId");
            entity.Property<string?>("Status").HasMaxLength(40);
            entity.Property<byte[]?>("EncryptedConnectionString").HasColumnType("bytea");
            entity.Property<int?>("KekVersion");
            entity.Property<string?>("FailureReason");
            entity.Property<DateTime?>("DeleteRequestedAt");

            // Cranl provisioning columns are part of the legacy single-DB
            // topology and not part of the database-per-tenant model.
            entity.Ignore(e => e.ProvisioningState);
            entity.Ignore(e => e.ProvisioningDetail);
            entity.Ignore(e => e.ProvisioningUpdatedAt);
            entity.Ignore(e => e.CranlProjectId);
            entity.Ignore(e => e.CranlDatabaseId);
            entity.Ignore(e => e.CranlAppId);
            entity.Ignore(e => e.CranlRegion);
            entity.Ignore(e => e.CranlAppUrl);
            entity.Ignore(e => e.CranlDatabaseUrlEncrypted);

            entity.HasIndex(e => e.Slug).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
            entity.HasIndex(e => e.ExternalId).IsUnique().HasFilter("\"ExternalId\" IS NOT NULL AND \"DeletedAt\" IS NULL");
            entity.HasIndex("Status").HasFilter("\"Status\" IS NOT NULL");
            entity.HasIndex("PlanId").HasFilter("\"PlanId\" IS NOT NULL");

            entity.HasQueryFilter(e => e.DeletedAt == null);

            entity.HasOne(e => e.Owner)
                .WithMany()
                .HasForeignKey(e => e.OwnerId)
                .OnDelete(DeleteBehavior.SetNull);

            // FK from tenants.PlanId → plans.Id (via shadow property). Restrict
            // delete: a plan in active use cannot be removed without first
            // moving every tenant off it.
            entity.HasOne<Plan>()
                .WithMany()
                .HasForeignKey("PlanId")
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureTenantMemberships(ModelBuilder modelBuilder)
    {
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
    }

    private static void ConfigureUserInvites(ModelBuilder modelBuilder)
    {
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
    }

    private static void ConfigureApiKeys(ModelBuilder modelBuilder)
    {
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
            // Story 28-7 routes Bearer tokens by KeyPrefix — index drives the lookup.
            entity.HasIndex(e => e.KeyPrefix);
            // Active-key filter — partial index keeps it cheap on revoke-heavy tenants.
            entity.HasIndex(e => e.RevokedAt).HasFilter("\"RevokedAt\" IS NULL");
            entity.HasIndex(e => new { e.Scope, e.OwnerId });
            entity.HasIndex(e => e.TenantId);

            // Rate-limit shadow column referenced by Story 28-7.
            entity.Property<int?>("RateLimitRpm");

            // CP context holds platform/user-scoped API keys (Doc 01 §1.2 row
            // 7). Tenant-scoped keys (Scope='tenant') live on the tenant DB
            // (TenantDbContext) and are dropped with the tenant.
        });
    }

    private static void ConfigureGitHubInstallations(ModelBuilder modelBuilder)
    {
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
    }

    private static void ConfigureGitHubInstallationRepos(ModelBuilder modelBuilder)
    {
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
    }

    private static void ConfigureGitHubWebhookDeliveries(ModelBuilder modelBuilder)
    {
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

    private static void ConfigurePlatformEvents(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlatformEvent>(entity =>
        {
            entity.ToTable("platform_events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Type).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Tags).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.Metadata).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.Data).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => new { e.Type, e.CreatedAt });
            entity.HasIndex(e => e.TenantId).HasFilter("\"TenantId\" IS NOT NULL");
            entity.HasIndex(e => e.UserId).HasFilter("\"UserId\" IS NOT NULL");
            entity.HasIndex(e => e.CreatedAt);
        });
    }

    private static void ConfigurePlatformQueuedTasks(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlatformQueuedTask>(entity =>
        {
            entity.ToTable("platform_queued_tasks");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Type).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Payload).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue("pending");
            entity.Property(e => e.RetryCount).HasDefaultValue(0);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => new { e.Status, e.CreatedAt });
            entity.HasIndex(e => e.InstallationId).HasFilter("\"InstallationId\" IS NOT NULL");
        });
    }

    private static void ConfigurePlatformEmailOutbox(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlatformEmailOutboxMessage>(entity =>
        {
            entity.ToTable("platform_email_outbox");
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
        });
    }
}
