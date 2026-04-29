using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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
    /// <summary>
    /// Configure the 14 control-plane tables (Doc 01 §1.2). When
    /// <paramref name="includeTenantShadowColumns"/> is <c>true</c> the
    /// Epic 28 shadow columns (<c>Status</c>, <c>PlanId</c>,
    /// <c>EncryptedConnectionString</c>, <c>KekVersion</c>,
    /// <c>FailureReason</c>, <c>DeleteRequestedAt</c>) are wired up on
    /// the Tenant entity — those columns are CP-plane only and shouldn't
    /// ride along when this configurator is called against a
    /// <c>TenantDbContext</c>.
    /// </summary>
    public static void ConfigureControlPlaneEntities(
        ModelBuilder modelBuilder,
        bool includeTenantShadowColumns = false)
    {
        // ── User ──
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Email).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Role).IsRequired().HasMaxLength(20).HasDefaultValue("member");
            // Story 28-R2/C1 — separate platform-admin column. The DB-level
            // CHECK constraint is installed by the AddUsersPlatformRole
            // migration; here we just declare the EF projection.
            entity.Property(e => e.PlatformRole)
                .HasColumnName("platform_role")
                .IsRequired()
                .HasMaxLength(20)
                .HasDefaultValue("user");
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

            if (includeTenantShadowColumns)
            {
                // ── Epic 28 shadow columns (Story 28-1, 28-4, 28-11, 28-12) ──
                //
                // These six fields land on the `tenants` table but are NOT
                // modelled on the Tenant POCO — they belong to the
                // db-per-tenant rollout and are accessed through
                // EF.Property<T> by LruPooledTenantConnectionResolver (28-4),
                // KekRotationCoordinator (28-12), AdminTenantsEndpoints
                // (28-11), and PlatformAnalyticsService (28-10).
                entity.Property<string?>("Status").HasMaxLength(32);
                entity.Property<Guid?>("PlanId");
                entity.Property<byte[]?>("EncryptedConnectionString").HasColumnType("bytea");
                entity.Property<int?>("KekVersion");
                entity.Property<string?>("FailureReason");
                entity.Property<DateTime?>("DeleteRequestedAt");

                // ── Epic 30 shadow columns (Story 30-3) ──
                //
                // ProviderKey + ProviderResourceIds back the v2
                // ITenantInfrastructureProvider contract: the dispatch
                // workflow (30-2) selects a provider by tenants.provider_key
                // and writes minted cloud-resource ids into
                // tenants.provider_resource_ids JSONB. Both stay nullable so
                // tenants that haven't been routed to a v2 backend yet (the
                // shared-infra default) continue to work; the migration
                // backfills 'cranl' for any row already populated with the
                // legacy cranl_* identifiers.
                entity.Property<string?>("ProviderKey").HasMaxLength(40);
                entity.Property<string?>("ProviderResourceIds")
                    .HasColumnType("jsonb");

                // Epic 28 shadow-column indexes used by Admin tenant
                // filtering + plan FK joins.
                entity.HasIndex("Status");
                entity.HasIndex("PlanId");

                // FK to plans, Restrict so deleting a referenced plan
                // fails loudly instead of silently orphaning tenants.
                entity.HasOne<Plan>()
                    .WithMany()
                    .HasForeignKey("PlanId")
                    .OnDelete(DeleteBehavior.Restrict);
            }

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

            // Story 28-7 shadow column — per-key rate-limit override, carried
            // on the row so the API gateway can pick the tighter of the
            // per-key and per-plan ceilings without an extra table join.
            entity.Property<int?>("RateLimitRpm");

            entity.HasIndex(e => e.KeyHash).IsUnique();
            entity.HasIndex(e => new { e.Scope, e.OwnerId });
            entity.HasIndex(e => e.TenantId);
            // Story 28-7 — route Bearer tokens by their 8-char prefix.
            entity.HasIndex(e => e.KeyPrefix);
            // Story 28-7 — partial index for active-key lookups only (filter
            // out revoked rows to keep the b-tree dense).
            entity.HasIndex(e => e.RevokedAt).HasFilter("\"RevokedAt\" IS NULL");
            // Story 28-7 deferred-item — per-key rate limit shadow column.
            entity.Property<int?>("RateLimitRpm");
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

        // ── Plan (Story 28-1) ──
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

        // ── PlatformEvent (Story 28-6) ──
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
            // BIGSERIAL identity — Postgres assigns the next value on
            // INSERT; EF reads it back. Used by AlertRuleEvaluator as
            // a monotonic cursor that never collides on equal CreatedAt.
            // The HasValueGenerator bridges the InMemory test provider
            // (which doesn't honour UseSerialColumn for non-PK columns)
            // to the same monotonic semantics; on Postgres the
            // server-side RETURNING value overwrites it.
            entity.Property(e => e.SequenceNumber)
                .ValueGeneratedOnAdd()
                .UseSerialColumn()
                .HasValueGenerator<Internal.EventSequenceValueGenerator>();

            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.TenantId).HasFilter("\"TenantId\" IS NOT NULL");
            entity.HasIndex(e => e.UserId).HasFilter("\"UserId\" IS NOT NULL");
            entity.HasIndex(e => new { e.Type, e.CreatedAt });
            // Cursor scan by AlertRuleEvaluator — single-column index
            // keeps the WHERE SequenceNumber > ? ORDER BY SequenceNumber
            // path on a covering index.
            entity.HasIndex(e => e.SequenceNumber)
                .IsUnique()
                .HasDatabaseName("UX_platform_events_SequenceNumber");
        });

        // ── PlatformQueuedTask (Story 28-6 + Round-2 M8/H8) ──
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
            // Round-2 M8 — record which worker holds the row's lease.
            entity.Property(e => e.ClaimedBy).HasMaxLength(128);
            // Round-2 H8 — last time the worker observed "no handler".
            // Nullable; non-null only on rows currently parked waiting
            // for a handler to be deployed.
            entity.Property(e => e.UnprocessableAt);

            entity.HasIndex(e => new { e.Status, e.CreatedAt });
            entity.HasIndex(e => e.TenantId).HasFilter("\"TenantId\" IS NOT NULL");
            entity.HasIndex(e => e.InstallationId).HasFilter("\"InstallationId\" IS NOT NULL");
        });

        // ── PlatformEmailOutboxMessage (Story 28-6) ──
        modelBuilder.Entity<PlatformEmailOutboxMessage>(entity =>
        {
            entity.ToTable("platform_email_outbox");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Template).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ToAddress).IsRequired().HasMaxLength(320);
            entity.Property(e => e.FromAddress).IsRequired().HasMaxLength(320);
            entity.Property(e => e.Subject).IsRequired().HasMaxLength(512);
            entity.Property(e => e.HtmlBody).IsRequired();
            entity.Property(e => e.TextBody).IsRequired();
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue("pending");
            entity.Property(e => e.Attempts).HasDefaultValue(0);
            entity.Property(e => e.MaxAttempts).HasDefaultValue(5);
            entity.Property(e => e.NextAttemptAt).HasDefaultValueSql("now()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => new { e.Status, e.NextAttemptAt });
        });

        // ── AdminImpersonation (Story 28-R2 follow-up B) ──
        //
        // SOC2 / ISO 27001 audit row: each platform-admin impersonation
        // session writes one immutable INSERT here at start, and a single
        // UPDATE setting (EndedAt, EndedReason) at session-end. The
        // matching IMPERSONATION.STARTED / IMPERSONATION.ENDED platform
        // events carry the same identity in their data/tags channels for
        // defence-in-depth (Story 28-R2 / M2 pattern).
        modelBuilder.Entity<AdminImpersonation>(entity =>
        {
            entity.ToTable("admin_impersonations", t =>
            {
                // Charset whitelist — same gate as X-Admin-Note (M17).
                // Length 1..500 (NOT NULL + REQUIRED) keeps the table
                // honest about every row carrying an SOC2 reason string.
                t.HasCheckConstraint(
                    "chk_impersonation_reason_charset",
                    "\"Reason\" ~ '^[A-Za-z0-9 .,;:_!@#$%&()\\-]{1,500}$'");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.ImpersonatorEmail)
                .IsRequired()
                .HasMaxLength(320);
            entity.Property(e => e.Reason)
                .IsRequired()
                .HasMaxLength(500);
            entity.Property(e => e.EndedReason).HasMaxLength(64);
            entity.Property(e => e.IpAddress).HasMaxLength(64);
            entity.Property(e => e.UserAgent).HasMaxLength(512);
            entity.Property(e => e.StartedAt)
                .HasColumnType("timestamp with time zone");
            entity.Property(e => e.EndedAt)
                .HasColumnType("timestamp with time zone");

            // Hot reads:
            //   1. "what has Alice impersonated?" — by impersonator
            //   2. "who has impersonated Acme Corp?" — by target tenant
            //   3. "who is currently active?" — partial on EndedAt IS NULL
            //      (the active set is small; the full table will grow over
            //      time but the partial index keeps incident-response
            //      lookups O(active-count)).
            entity.HasIndex(e => e.ImpersonatorUserId)
                .HasDatabaseName("idx_admin_impersonations_impersonator");
            entity.HasIndex(e => e.TargetTenantId)
                .HasDatabaseName("idx_admin_impersonations_target_tenant");
            entity.HasIndex(e => e.EndedAt)
                .HasDatabaseName("idx_admin_impersonations_active")
                .HasFilter("\"EndedAt\" IS NULL");

            // FK to users(id) — cascade nothing on user delete; we want
            // the audit row to outlive the actor (SOC2 requirement: a
            // departed admin's actions remain auditable). EF default
            // is RESTRICT for required FKs, which is exactly right.
            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(e => e.ImpersonatorUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(e => e.TargetTenantId)
                .OnDelete(DeleteBehavior.Restrict);

            // Optional FK — TargetUserId may be null (full-tenant
            // impersonation). When set, RESTRICT for the same reason
            // (audit row outlives a deleted target user).
            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(e => e.TargetUserId)
                .OnDelete(DeleteBehavior.Restrict);
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
        ModelBuilder modelBuilder,
        Guid? fixedTenantId = null,
        bool omitTenantIdColumn = false)
    {
        // When invoked from TenantDbContext (fixedTenantId != null) the
        // tenant DB must NOT carry any CP entities (users, tenants,
        // plans, platform_*, etc.) — tenancy is implicit in the
        // connection string (Doc 01 §1.4). Ignore the POCOs entirely so
        // EF doesn't pick them up through navigation properties like
        // AgentConfig.Tenant / SanitizationRule.Tenant. The TenantId
        // columns on tenant-resident tables are retained during the
        // transitional shared-DB phase — tenant repos still filter by
        // TenantId explicitly until Story 28-1's db-per-tenant split
        // ships and that filter becomes redundant.
        var isTenantContext = fixedTenantId is not null;
        if (isTenantContext)
        {
            modelBuilder.Ignore<Tenant>();
            modelBuilder.Ignore<User>();
            modelBuilder.Ignore<TenantMembership>();
            modelBuilder.Ignore<UserInvite>();
            modelBuilder.Ignore<RefreshToken>();
            modelBuilder.Ignore<PasswordResetToken>();
            modelBuilder.Ignore<GitHubInstallation>();
            modelBuilder.Ignore<GitHubInstallationRepo>();
            modelBuilder.Ignore<GitHubWebhookDelivery>();
            modelBuilder.Ignore<Plan>();
            modelBuilder.Ignore<PlatformEvent>();
            modelBuilder.Ignore<PlatformQueuedTask>();
            modelBuilder.Ignore<PlatformEmailOutboxMessage>();
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

            if (omitTenantIdColumn)
            {
                entity.Ignore(e => e.TenantId);
                entity.Ignore(e => e.Tenant);
            }
            else if (isTenantContext)
            {
                // Story 28-1 PR D: Tenant entity is CP-resident. The
                // navigation property stays on the POCO for code-shape
                // compatibility, but on the tenant DB context we must
                // not pull Tenant into the model. Keep the TenantId
                // column (predicate-as-isolation during the shared-DB
                // transition) but break the navigation.
                entity.Ignore(e => e.Tenant);
                entity.HasIndex(e => e.TenantId).IsUnique().HasFilter("\"TenantId\" IS NOT NULL");
            }
            else
            {
                entity.HasIndex(e => e.TenantId).IsUnique().HasFilter("\"TenantId\" IS NOT NULL");
                entity.HasOne(e => e.Tenant)
                    .WithMany()
                    .HasForeignKey(e => e.TenantId)
                    .OnDelete(DeleteBehavior.Cascade);
            }

            ApplyTenantFilter(entity, fixedTenantId, e => e.TenantId);
        });

        // ── PromptOverride ──
        // Story 27-2: dual-scoping. Single-user-mode rows have user_id set
        // (tenant_id IS NULL); SaaS-mode rows have tenant_id set
        // (user_id IS NULL). The principal_xor CHECK constraint (added in
        // migration 27-2) enforces exactly-one. Unique index covers BOTH
        // keys with NULLS NOT DISTINCT semantics so the (null, tid, ...)
        // and (uid, null, ...) row spaces are disjoint and both keys
        // dedupe on null.
        modelBuilder.Entity<PromptOverride>(entity =>
        {
            entity.ToTable("prompt_overrides", t =>
            {
                // Story 27-2 — exactly one of user_id / tenant_id is non-null.
                t.HasCheckConstraint(
                    "ck_prompt_overrides_principal_xor",
                    "(\"UserId\" IS NOT NULL AND \"TenantId\" IS NULL) " +
                    "OR (\"UserId\" IS NULL AND \"TenantId\" IS NOT NULL)");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Scope).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Template).IsRequired();
            entity.Property(e => e.Variables).HasColumnType("text[]");
            entity.Property(e => e.MaxTokens).HasDefaultValue(4096);
            entity.Property(e => e.Version).HasDefaultValue(1);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // Story 27-2 — unique on (UserId, TenantId, Scope, Role, Action).
            // The Postgres-side index uses NULLS NOT DISTINCT so a single
            // (null, tenantId, scope, role, action) row is unique across
            // all repeated NULLs in UserId. This is enforced through raw
            // SQL in the migration; the EF HasIndex below is purely a
            // model-graph hint so the migration generator + InMemory
            // tests see the same shape. NULLS NOT DISTINCT requires PG15+
            // (production runs PG17 — see Tamma project tech stack).
            entity.HasIndex(e => new { e.UserId, e.TenantId, e.Scope, e.Role, e.Action })
                .IsUnique();

            if (omitTenantIdColumn) entity.Ignore(e => e.TenantId);
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

            if (omitTenantIdColumn)
            {
                entity.Ignore(e => e.TenantId);
                entity.HasIndex(e => e.ProviderKey).IsUnique();
            }
            else
            {
                entity.HasIndex(e => new { e.ProviderKey, e.TenantId }).IsUnique()
                    .HasFilter("\"TenantId\" IS NOT NULL");
            }

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
            entity.HasIndex(e => new { e.EngineId, e.CreatedAt });
            entity.HasIndex(e => new { e.Model, e.CreatedAt });
            entity.HasIndex(e => new { e.RequestType, e.CreatedAt });
            entity.HasIndex(e => e.CorrelationId).HasFilter("\"CorrelationId\" IS NOT NULL");

            if (omitTenantIdColumn)
            {
                entity.Ignore(e => e.TenantId);
            }
            else
            {
                entity.HasIndex(e => new { e.TenantId, e.CreatedAt });
            }

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

            if (omitTenantIdColumn)
            {
                entity.Ignore(e => e.TenantId);
                entity.Ignore(e => e.Tenant);
            }
            else if (isTenantContext)
            {
                // Story 28-1 PR D — see AgentConfig comment above.
                entity.Ignore(e => e.Tenant);
                entity.HasIndex(e => e.TenantId).IsUnique().HasFilter("\"TenantId\" IS NOT NULL");
            }
            else
            {
                entity.HasIndex(e => e.TenantId).IsUnique().HasFilter("\"TenantId\" IS NOT NULL");
                entity.HasOne(e => e.Tenant)
                    .WithMany()
                    .HasForeignKey(e => e.TenantId)
                    .OnDelete(DeleteBehavior.Cascade);
            }

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

            if (omitTenantIdColumn)
            {
                entity.Ignore(e => e.TenantId);
            }
            else
            {
                entity.HasIndex(e => e.TenantId);
            }

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

            entity.HasOne(e => e.Definition)
                .WithMany(d => d.Instances)
                .HasForeignKey(e => e.DefinitionId)
                .OnDelete(DeleteBehavior.Cascade);

            if (omitTenantIdColumn)
            {
                entity.Ignore(e => e.TenantId);
            }
            else
            {
                entity.HasIndex(e => e.TenantId);
                entity.HasIndex(e => new { e.TenantId, e.DefinitionId });
                entity.HasIndex(e => new { e.TenantId, e.Status });
            }

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
            if (omitTenantIdColumn)
            {
                entity.Ignore(e => e.TenantId);
            }
            else
            {
                entity.HasIndex(e => new { e.TenantId, e.Status });
            }
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
            // BIGSERIAL identity — see PlatformEvent above for rationale.
            entity.Property(e => e.SequenceNumber)
                .ValueGeneratedOnAdd()
                .UseSerialColumn()
                .HasValueGenerator<Internal.EventSequenceValueGenerator>();

            entity.HasIndex(e => new { e.Type, e.CreatedAt });
            // Cursor scan by AlertRuleEvaluator — covering index on
            // the monotonic stream key.
            entity.HasIndex(e => e.SequenceNumber)
                .IsUnique()
                .HasDatabaseName("UX_domain_events_SequenceNumber");

            if (omitTenantIdColumn)
            {
                entity.Ignore(e => e.TenantId);
            }
            else
            {
                entity.HasIndex(e => e.TenantId);
                entity.HasIndex(e => new { e.TenantId, e.IssueNumber })
                    .HasFilter("\"IssueNumber\" IS NOT NULL");
            }

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

            if (omitTenantIdColumn)
            {
                entity.Ignore(e => e.TenantId);
                entity.HasIndex(e => e.AccountId).IsUnique();
            }
            else
            {
                entity.HasIndex(e => new { e.TenantId, e.AccountId })
                    .IsUnique()
                    .HasFilter("\"TenantId\" IS NOT NULL");
                entity.HasIndex(e => e.AccountId)
                    .IsUnique()
                    .HasDatabaseName("ix_budget_configs_accountid_default")
                    .HasFilter("\"TenantId\" IS NULL");
            }
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
            if (omitTenantIdColumn)
            {
                entity.Ignore(e => e.TenantId);
            }
            else
            {
                entity.HasIndex(e => e.TenantId);
            }
            // No query filter — the outbox is shared infra; tenant scoping is
            // explicit in the repository APIs.
        });
    }

    /// <summary>
    /// Configure the tenant-DB-only <c>api_keys</c> table (Story 28-7).
    /// The tenant DB locks the api_keys scope to <c>tenant</c> via a
    /// CHECK constraint — user / installation / service keys live on the
    /// CP api_keys table. KeyPrefix + RevokedAt indexes mirror the CP
    /// side so Bearer-token routing works identically on either DB.
    /// </summary>
    public static void ConfigureTenantApiKeys(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.ToTable("api_keys", t =>
            {
                // Doc 01 §1.4 — only tenant-scope keys allowed on this DB.
                t.HasCheckConstraint(
                    "ck_api_keys_tenant_scope",
                    "\"Scope\" = 'tenant'");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Scope).IsRequired().HasMaxLength(50);
            entity.Property(e => e.OwnerId).IsRequired();
            entity.Property(e => e.KeyHash).IsRequired();
            entity.Property(e => e.KeyPrefix).IsRequired().HasMaxLength(16);
            entity.Property(e => e.Label).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Permissions).HasColumnType("text[]");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            // Tenant DB has no TenantId column — tenancy is implicit.
            entity.Ignore(e => e.TenantId);

            entity.HasIndex(e => e.KeyHash).IsUnique();
            entity.HasIndex(e => new { e.Scope, e.OwnerId });
            entity.HasIndex(e => e.KeyPrefix);
            entity.HasIndex(e => e.RevokedAt).HasFilter("\"RevokedAt\" IS NULL");
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

    /// <summary>
    /// Converts <see cref="JsonDocument"/> properties to/from a JSON string
    /// so that non-relational providers (notably <c>Microsoft.EntityFrameworkCore.InMemory</c>
    /// used by Epic 28 tests) can materialise mentorship entities without
    /// blowing up on the native jsonb type. On Postgres the column is still
    /// declared as <c>jsonb</c> via <c>HasColumnType("jsonb")</c>; the
    /// provider simply rewrites the conversion at the storage layer.
    /// </summary>
    private static readonly ValueConverter<JsonDocument?, string?> JsonDocumentConverter =
        new(
            v => v == null ? null : v.RootElement.GetRawText(),
            v => string.IsNullOrWhiteSpace(v) ? null : JsonDocument.Parse(v, default));

    /// <summary>
    /// Explicitly excludes the 11 legacy-shared + 4 mentorship entities
    /// from a <see cref="ModelBuilder"/>. Used by
    /// <see cref="ControlPlaneDbContext"/> so that the DbSet declarations
    /// (which exist for compile-time shim reasons) do NOT auto-register
    /// the entity types with EF's convention-based discovery. The CP
    /// model should contain exactly the 14 Doc 01 §1.2 tables.
    /// </summary>
    public static void IgnoreLegacyAndMentorshipEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Ignore<AgentConfig>();
        modelBuilder.Ignore<PromptOverride>();
        modelBuilder.Ignore<ProviderHealth>();
        modelBuilder.Ignore<ProviderDiagnostic>();
        modelBuilder.Ignore<SanitizationRule>();
        modelBuilder.Ignore<WorkflowDefinition>();
        modelBuilder.Ignore<WorkflowInstance>();
        modelBuilder.Ignore<Entities.DomainEvent>();
        modelBuilder.Ignore<QueuedTask>();
        modelBuilder.Ignore<EmailOutboxMessage>();
        modelBuilder.Ignore<BudgetConfig>();
        modelBuilder.Ignore<MentorshipSession>();
        modelBuilder.Ignore<MentorshipEvent>();
        modelBuilder.Ignore<JuniorDeveloper>();
        modelBuilder.Ignore<Story>();
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
            entity.Property(e => e.Context).HasColumnName("context").HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter);
            entity.Property(e => e.Variables).HasColumnName("variables").HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter);
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
            entity.Property(e => e.EventData).HasColumnName("event_data").HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter);
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
            entity.Property(e => e.Preferences).HasColumnName("preferences").HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter);
            entity.Property(e => e.LearningPatterns).HasColumnName("learning_patterns").HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter);
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
            entity.Property(e => e.AcceptanceCriteria).HasColumnName("acceptance_criteria").HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter);
            entity.Property(e => e.TechnicalRequirements).HasColumnName("technical_requirements").HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter);
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
