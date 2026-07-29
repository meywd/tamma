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
            entity.ToTable("users", t =>
            {
                // Story 28-R2/C1 — model-level CHECK on platform_role. The
                // legacy raw-SQL constraint ('users_platform_role_check')
                // from the pre-collapse chain was dropped when the chain was
                // collapsed into InitialControlPlane (Phase 0); this model-
                // level constraint is now the only source of truth.
                t.HasCheckConstraint(
                    "ck_users_platform_role",
                    "\"platform_role\" IN ('user','platform_admin')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Email).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Role).IsRequired().HasMaxLength(20).HasDefaultValue("member");
            // Story 28-R2/C1 — separate platform-admin column. The DB-level
            // CHECK constraint is the model-level ck_users_platform_role
            // declared above; here we just declare the EF projection.
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
            entity.ToTable("refresh_tokens", t =>
            {
                // Story 28-9 AC3 — pin RevokedReason to the closed enum.
                // Adding a value requires widening the constraint via a new
                // migration; the entity-level constants in
                // RefreshTokenRevokedReasons mirror this list.
                t.HasCheckConstraint(
                    "CK_refresh_tokens_RevokedReason",
                    "\"RevokedReason\" IS NULL OR \"RevokedReason\" IN ("
                    + "'manual_logout','logout_all','rotation_consumed',"
                    + "'switch_org','reuse_detected','password_reset',"
                    + "'admin_force_logout')");

                // Story 28-9 AC3 — RevokedReason is set IFF RevokedAt is
                // set. The pair must move together so a SIEM query can
                // trust "WHERE RevokedReason='reuse_detected'" without a
                // null-RevokedAt fallback. New writes go through the
                // repository's Revoke* methods which set both atomically.
                t.HasCheckConstraint(
                    "CK_refresh_tokens_RevokedReason_NullParity",
                    "(\"RevokedAt\" IS NULL) = (\"RevokedReason\" IS NULL)");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.TokenHash).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            // Story 28-9 AC3 — tenant binding (nullable for rootless tokens
            // issued at login before a tenant is picked) + JtiChainHead
            // lineage pointer (nullable for pre-story rows). RevokedReason
            // mirrors the RevokedAt nullability via the CHECK constraint
            // above.
            entity.Property(e => e.TenantId);
            entity.Property(e => e.JtiChainHead);
            entity.Property(e => e.RevokedReason).HasMaxLength(32);

            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.UserId);

            // Story 28-9 AC3 — reuse-detection hot path. Given a presented
            // (revoked) refresh token we look up by hash, read its
            // JtiChainHead, then revoke every active sibling. Partial index
            // on JtiChainHead IS NOT NULL keeps it tight (only this story's
            // tokens carry the column).
            entity.HasIndex(e => e.JtiChainHead)
                .HasDatabaseName("IX_refresh_tokens_JtiChainHead")
                .HasFilter("\"JtiChainHead\" IS NOT NULL");

            // Story 28-9 AC3 — tenant-scoped queries (logout-all per
            // tenant, admin debugging). Partial keeps rootless tokens out
            // of the index.
            entity.HasIndex(e => new { e.UserId, e.TenantId })
                .HasDatabaseName("IX_refresh_tokens_UserId_TenantId")
                .HasFilter("\"TenantId\" IS NOT NULL");

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

            // Cranl per-tenant provisioning state machine. Epic 30 Phase B
            // (Task B3) dropped the six dedicated Cranl columns
            // (project/db/app ids, region, app url, encrypted DB URL) — that
            // walk/resume state now lives in provider_resource_ids JSONB and
            // the tenant_databases pool row. ProvisioningState stays here.
            entity.Property(e => e.ProvisioningState)
                .IsRequired().HasMaxLength(40).HasDefaultValue("none");

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
                // smallint NOT NULL DEFAULT 1 per spec (plan 2026-06-09 §2.2). CLR type
                // short — every EF.Property<T> read of this column must use short.
                entity.Property<short>("KekVersion").HasDefaultValue((short)1);
                entity.Property<string?>("FailureReason");
                entity.Property<DateTime?>("DeleteRequestedAt");

                // ── Epic 30 shadow columns (Story 30-3) ──
                //
                // ProviderKey + ProviderResourceIds back the v2
                // ITenantInfrastructureProvider contract. ProviderKey is a
                // backend LABEL: it records which provider (e.g. 'cranl')
                // minted hosting infrastructure for the tenant, and the
                // minted cloud-resource ids land in
                // tenants.provider_resource_ids JSONB. It is NOT a tenancy
                // mode — placement and schema lifecycle are owned by the
                // unified model (SchemaName + DatabaseId below). Both stay
                // nullable: NULL simply means no external backend minted
                // infrastructure for this tenant. The migration backfills
                // 'cranl' for any row already populated with the legacy
                // cranl_* identifiers.
                entity.Property<string?>("ProviderKey").HasMaxLength(40);
                entity.Property<string?>("ProviderResourceIds")
                    .HasColumnType("jsonb");

                // ── Unified-tenancy Phase 0 (plan 2026-06-09 §2.2) ──
                //
                // SchemaName = the tenant's schema (t_<hex>) inside its assigned DB;
                // DatabaseId = which tenant_databases row hosts that schema. Both stay
                // NULL until the unified creation path (Phase 3) mints them — Phase 0
                // is schema-only.
                entity.Property<string?>("SchemaName").HasMaxLength(63);
                entity.Property<Guid?>("DatabaseId");

                entity.HasIndex("SchemaName").IsUnique()
                    .HasFilter("\"SchemaName\" IS NOT NULL");
                entity.HasIndex("DatabaseId");

                entity.HasOne<TenantDatabase>()
                    .WithMany()
                    .HasForeignKey("DatabaseId")
                    .OnDelete(DeleteBehavior.Restrict);

                // CHECKs reference shadow columns, so they live inside this guard.
                // Conn-string CHECK: the spec invariant is "active tenants always have
                // a connection string" — enforced only for active/suspended.
                // provisioning/failed/deleted are exempt because today's flows
                // legitimately hold NULL there (mint happens mid-provisioning;
                // failure can precede mint; delete nulls the envelope).
                // deleting/delete_requested are exempt because force-delete enters
                // them from failed (or legacy NULL-status) rows that never got a
                // connection string minted — without the exemption the designed
                // cleanup path (AdminTenantsEndpoints.ForceDeleteTenant,
                // MarkTenantDeletingActivity) hits 23514.
                // Tighten to spec-exact (only pending_verification exempt) in Phase 3.
                entity.ToTable("tenants", t =>
                {
                    t.HasCheckConstraint(
                        "ck_tenants_status",
                        "\"Status\" IS NULL OR \"Status\" IN ('pending_verification'," +
                        "'provisioning','active','draining','delete_requested','deleting'," +
                        "'deleted','failed','suspended')");
                    t.HasCheckConstraint(
                        "ck_tenants_connection_string_present",
                        "\"Status\" IS NULL OR \"Status\" IN ('pending_verification'," +
                        "'provisioning','failed','deleted','deleting'," +
                        "'delete_requested') " +
                        "OR \"EncryptedConnectionString\" IS NOT NULL");
                });

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
            entity.ToTable("api_keys", t =>
            {
                // Phase 0 transitional enumeration (plan 2026-06-09 §2.4 deviation 1).
                // Spec target on CP is ('platform','user') — unreachable until
                // tenant-scoped keys physically move to tenant schemas (Phase 2+)
                // and the service/installation scopes are reconciled with the spec.
                t.HasCheckConstraint(
                    "ck_api_keys_scope",
                    "\"Scope\" IN ('platform','user','installation','service','tenant')");
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

        // ── Plan (Story 28-1; extended by Story 34-1 — versioned price-book) ──
        modelBuilder.Entity<Plan>(entity =>
        {
            entity.ToTable("plans", t =>
            {
                t.HasCheckConstraint(
                    "ck_plans_placement_policy",
                    "\"PlacementPolicy\" IN ('shared','dedicated')");
                // Story 34-1 — pin the version-lifecycle + billing cadence to
                // their closed enums so a buggy write path can't stash an
                // unknown value the catalog can't reason about.
                t.HasCheckConstraint(
                    "ck_plans_status",
                    "\"Status\" IN ('active','deprecated','draft')");
                t.HasCheckConstraint(
                    "ck_plans_billing_interval",
                    "\"BillingInterval\" IN ('monthly','annual')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Slug).IsRequired().HasMaxLength(64);
            entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.MonthlyPriceUsd).HasPrecision(18, 2);
            entity.Property(e => e.Quotas).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.PlacementPolicy)
                .IsRequired().HasMaxLength(20).HasDefaultValue("shared");

            // Story 34-1 — versioning columns. Defaults backfill the 3 seeded
            // rows on migrate: Version=1, Status='active'.
            entity.Property(e => e.Version).HasDefaultValue(1);
            entity.Property(e => e.Status)
                .IsRequired().HasMaxLength(20).HasDefaultValue("active");
            entity.Property(e => e.IsCustom).HasDefaultValue(false);
            entity.Property(e => e.BillingInterval)
                .IsRequired().HasMaxLength(20).HasDefaultValue("monthly");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // Story 34-1 — the legacy single-column UNIQUE(Slug) is replaced
            // by UNIQUE(Slug, Version) (a slug now has multiple version rows)
            // plus a partial unique index pinning exactly one 'active' version
            // per slug — the immutability invariant in SQL.
            entity.HasIndex(e => new { e.Slug, e.Version })
                .HasDatabaseName("UX_plans_Slug_Version").IsUnique();
            entity.HasIndex(e => e.Slug)
                .HasDatabaseName("UX_plans_OneActivePerSlug")
                .HasFilter("\"Status\" = 'active'").IsUnique();

            // Self-referencing version chain. RESTRICT — a superseded version
            // must not be hard-deleted out from under its successor's pointer.
            entity.HasOne<Plan>()
                .WithMany()
                .HasForeignKey(e => e.SupersedesPlanId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── PlanFeature / PlanEntitlement / PlanPrice (Story 34-1) ──
        //
        // The typed price-book children. Configured here (alongside Plan) so
        // the whole plan aggregate's mapping lives in one place — splitting a
        // single aggregate's config across files is the failure mode the
        // story explicitly forbids. All FK to plans(Id) with RESTRICT (a plan
        // version a tenant references must never be hard-deleted), each with a
        // natural-key unique index.
        modelBuilder.Entity<PlanFeature>(entity =>
        {
            entity.ToTable("plan_features");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.FeatureKey).IsRequired().HasMaxLength(128);
            entity.Property(e => e.StringValue).HasMaxLength(512);

            entity.HasIndex(e => new { e.PlanId, e.FeatureKey })
                .HasDatabaseName("UX_plan_features_PlanId_FeatureKey").IsUnique();

            entity.HasOne<Plan>()
                .WithMany(p => p.Features)
                .HasForeignKey(e => e.PlanId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlanEntitlement>(entity =>
        {
            entity.ToTable("plan_entitlements", t =>
            {
                t.HasCheckConstraint(
                    "ck_plan_entitlements_period",
                    "\"Period\" IN ('monthly','total')");
                t.HasCheckConstraint(
                    "ck_plan_entitlements_overage",
                    "\"OverageMode\" IN ('block','allow','meter')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

            // Persist the metric key as its snake_case text — never the
            // unstable ordinal (Story 34-1 AC5). The converter is the single
            // source of the wire/DB contract shared with metering/enforcement.
            entity.Property(e => e.MetricKey)
                .HasConversion(
                    k => k.ToMetricString(),
                    s => EntitlementMetricKeyExtensions.Parse(s))
                .HasColumnType("text")
                .IsRequired();
            entity.Property(e => e.Period)
                .IsRequired().HasMaxLength(20).HasDefaultValue("monthly");
            entity.Property(e => e.OverageMode)
                .IsRequired().HasMaxLength(20).HasDefaultValue("block");

            entity.HasIndex(e => new { e.PlanId, e.MetricKey })
                .HasDatabaseName("UX_plan_entitlements_PlanId_MetricKey").IsUnique();

            entity.HasOne<Plan>()
                .WithMany(p => p.Entitlements)
                .HasForeignKey(e => e.PlanId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlanPrice>(entity =>
        {
            entity.ToTable("plan_prices", t =>
            {
                t.HasCheckConstraint(
                    "ck_plan_prices_mode",
                    "\"PricingMode\" IN ('platform_provided','byok')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.PricingMode)
                .IsRequired().HasMaxLength(32).HasDefaultValue("platform_provided");
            entity.Property(e => e.RecurringUsd).HasPrecision(20, 4);
            entity.Property(e => e.SeatUsd).HasPrecision(20, 4);
            entity.Property(e => e.MeteredComponent)
                .HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");

            entity.HasIndex(e => new { e.PlanId, e.PricingMode })
                .HasDatabaseName("UX_plan_prices_PlanId_PricingMode").IsUnique();

            entity.HasOne<Plan>()
                .WithMany(p => p.Prices)
                .HasForeignKey(e => e.PlanId)
                .OnDelete(DeleteBehavior.Restrict);
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
            // Story 29-6 (review fix) — reservation visibility timestamp.
            // NULL = always visible (every existing producer); a future
            // value defers reservation until it elapses (RETIRE_SECRET_VERSION).
            entity.Property(e => e.VisibleAt);

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

            // Story 28-5 AC2 step-10 + AC5 — exactly-once-per-tenant welcome
            // email. Partial unique index on (TenantId, Template) excluding
            // terminally-failed rows so a failed welcome can be re-queued
            // while a pending/sending/sent one blocks duplicates. The
            // QueueWelcomeEmailActivity insert relies on this index for the
            // concurrent-run race; the pre-check covers the in-memory path.
            entity.HasIndex(e => new { e.TenantId, e.Template })
                .IsUnique()
                .HasFilter("\"Status\" <> 'failed' AND \"TenantId\" IS NOT NULL")
                .HasDatabaseName("UX_platform_email_outbox_tenant_template_active");
        });

        // ── SlackOutboxMessage (Story 38-3) ──
        // The fire-and-forget Slack analogue of platform_email_outbox. CP-resident
        // so it delivers regardless of tenant-DB routing; the body is already
        // formatted engine-side (token-free) and LastError is key-free.
        modelBuilder.Entity<SlackOutboxMessage>(entity =>
        {
            entity.ToTable("slack_outbox");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Channel).HasMaxLength(255);
            entity.Property(e => e.TargetUserId).HasMaxLength(255);
            entity.Property(e => e.MessageType).IsRequired().HasMaxLength(32).HasDefaultValue("Info");
            entity.Property(e => e.Body).IsRequired();
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue("pending");
            entity.Property(e => e.Attempts).HasDefaultValue(0);
            entity.Property(e => e.MaxAttempts).HasDefaultValue(5);
            entity.Property(e => e.NextAttemptAt).HasDefaultValueSql("now()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // Claim scan — the OutboxSlackSender polls pending rows due for delivery.
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
    /// Story 32-1 — configure the two CP-resident agent-entity tables
    /// (<c>agents</c> + <c>agent_versions</c>). Called ONLY from
    /// <see cref="ControlPlaneDbContext"/> (these are control-plane-resident;
    /// they are NOT added to <see cref="TenantDbContext"/> — no
    /// <c>omitTenantIdColumn</c>/<c>isTenantContext</c> branches).
    ///
    /// <para>The <c>ck_agents_visibility_ownership</c> CHECK mirrors
    /// <c>ck_prompt_overrides_principal_xor</c>: it ties the
    /// <see cref="Entities.AgentVisibility"/> discriminator (stored as int via
    /// <c>HasConversion&lt;int&gt;()</c>) to the owner columns —
    /// public (0) ⇒ no owner; private (1) ⇒ exactly one of tenant/user owner.
    /// The numeric literals <c>0</c>/<c>1</c> are load-bearing and match the
    /// enum ordinals.</para>
    /// </summary>
    public static void ConfigureAgentEntities(ModelBuilder modelBuilder)
    {
        // ── Agent (identity) ──
        modelBuilder.Entity<Agent>(entity =>
        {
            entity.ToTable("agents", t =>
            {
                // Visibility ⇄ ownership invariant (mirrors
                // ck_prompt_overrides_principal_xor). Visibility is stored as
                // int: 0 = Public, 1 = Private. A private agent never has both
                // owner columns set, and never both null; a public agent has
                // neither.
                t.HasCheckConstraint(
                    "ck_agents_visibility_ownership",
                    "(\"Visibility\" = 0 AND \"OwnerTenantId\" IS NULL AND \"OwnerUserId\" IS NULL) " +
                    "OR (\"Visibility\" = 1 AND \"OwnerTenantId\" IS NOT NULL AND \"OwnerUserId\" IS NULL) " +
                    "OR (\"Visibility\" = 1 AND \"OwnerUserId\" IS NOT NULL AND \"OwnerTenantId\" IS NULL)");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Name).IsRequired().HasMaxLength(128);
            // Story 32-15 — Role is NULLABLE (no .IsRequired()). Public personas
            // are cross-role (Role = NULL); private agents may still bind a role.
            entity.Property(e => e.Role).HasMaxLength(64);
            entity.Property(e => e.Visibility).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>().HasDefaultValue(AgentStatus.Active);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // Story 32-15 — public handles are globally unique on (Name) alone:
            // a persona is cross-role, so (Name, Role) is no longer the public
            // identity. Replaces the old IX_agents_public_name_role.
            entity.HasIndex(e => e.Name)
                .IsUnique()
                .HasFilter("\"Visibility\" = 0")
                .HasDatabaseName("IX_agents_public_name");

            // Private handles unique per owner — two tenants may each own a
            // private agent named "atlas" without colliding.
            entity.HasIndex(e => new { e.OwnerTenantId, e.Name })
                .IsUnique()
                .HasFilter("\"Visibility\" = 1 AND \"OwnerTenantId\" IS NOT NULL")
                .HasDatabaseName("IX_agents_private_tenant_name");
            entity.HasIndex(e => new { e.OwnerUserId, e.Name })
                .IsUnique()
                .HasFilter("\"Visibility\" = 1 AND \"OwnerUserId\" IS NOT NULL")
                .HasDatabaseName("IX_agents_private_user_name");
        });

        // ── AgentVersion (immutable config snapshot) ──
        modelBuilder.Entity<AgentVersion>(entity =>
        {
            entity.ToTable("agent_versions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.ConfigJson)
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            // Monotonic, non-duplicated versions per agent. Also the
            // concurrency guard: a double-publish loses the second INSERT.
            entity.HasIndex(e => new { e.AgentId, e.Version })
                .IsUnique()
                .HasDatabaseName("IX_agent_versions_agent_version");

            // Versions are immutable audit history — archive, never
            // cascade-delete.
            entity.HasOne(e => e.Agent)
                .WithMany(a => a.Versions)
                .HasForeignKey(e => e.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>
    /// Story 32-2 — configure the <c>agent_role_selections</c> table (which
    /// <see cref="Agent"/> serves a role for a principal). Dual-resident: this
    /// SAME table shape is configured on BOTH the CP context (single-user
    /// user-keyed rows) and every tenant context (SaaS tenant-keyed rows),
    /// mirroring <see cref="ConfigureAuditEntities"/> and the
    /// <c>prompt_overrides</c> dual-scoping model. <paramref name="fixedTenantId"/>
    /// is the tenant-context discriminator (NULL on the CP build); it only feeds
    /// the no-op <see cref="ApplyTenantFilter{T}"/> seam — isolation is the
    /// per-tenant connection, not a baked-in filter.
    ///
    /// <para>The <c>ck_agent_role_selections_principal_xor</c> CHECK ties exactly
    /// one of (<c>UserId</c>, <c>TenantId</c>) to non-null (mirrors
    /// <c>ck_prompt_overrides_principal_xor</c>). The unique index on
    /// <c>(TenantId, UserId, Role)</c> uses NULLS NOT DISTINCT so the
    /// (null, uid, role) and (tid, null, role) row spaces are disjoint and both
    /// halves dedupe on null — one selection per <c>(principal, role)</c>.</para>
    /// </summary>
    public static void ConfigureAgentRoleSelections(
        ModelBuilder modelBuilder, Guid? fixedTenantId = null)
    {
        modelBuilder.Entity<AgentRoleSelection>(entity =>
        {
            entity.ToTable("agent_role_selections", t =>
            {
                // Exactly one of user_id / tenant_id is non-null (mirrors
                // ck_prompt_overrides_principal_xor).
                t.HasCheckConstraint(
                    "ck_agent_role_selections_principal_xor",
                    "(\"UserId\" IS NOT NULL AND \"TenantId\" IS NULL) " +
                    "OR (\"UserId\" IS NULL AND \"TenantId\" IS NOT NULL)");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Role).IsRequired().HasMaxLength(64);
            entity.Property(e => e.AgentId).IsRequired();
            entity.Property(e => e.Visibility).IsRequired().HasMaxLength(32);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // One selection per (principal, role). NULLS NOT DISTINCT (PG15+;
            // production runs PG17) so a single (null, uid, role) or
            // (tid, null, role) row is unique across the repeated NULL halves —
            // same pattern as prompt_overrides / conventions.
            entity.HasIndex(e => new { e.TenantId, e.UserId, e.Role })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasDatabaseName("IX_agent_role_selections_TenantId_UserId_Role");

            ApplyTenantFilter(entity, fixedTenantId, e => e.TenantId);
        });
    }

    /// <summary>
    /// Story 32-16 — configure the <c>tenant_agent_enablements</c> table (which
    /// PUBLIC personas a principal exposes in its usable catalog). CP-resident in
    /// BOTH modes (it gates the CP-resident public <see cref="Agent"/> catalog and
    /// is keyed by tenant id / user id, never per <c>t_&lt;hex&gt;</c>), so —
    /// unlike <see cref="ConfigureAgentRoleSelections"/> — it is configured ONLY on
    /// the CP context (no tenant-context variant).
    ///
    /// <para>The <c>ck_tenant_agent_enablements_principal_xor</c> CHECK ties
    /// exactly one of (<c>UserId</c>, <c>TenantId</c>) to non-null (mirrors
    /// <c>ck_agent_role_selections_principal_xor</c>). The unique index on
    /// <c>(TenantId, UserId, AgentId)</c> uses NULLS NOT DISTINCT so the
    /// (null, uid, agent) and (tid, null, agent) row spaces are disjoint and both
    /// halves dedupe on null — one row per <c>(principal, agent)</c>.</para>
    /// </summary>
    public static void ConfigureTenantAgentEnablements(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantAgentEnablement>(entity =>
        {
            entity.ToTable("tenant_agent_enablements", t =>
            {
                // Exactly one of user_id / tenant_id is non-null (mirrors
                // ck_agent_role_selections_principal_xor).
                t.HasCheckConstraint(
                    "ck_tenant_agent_enablements_principal_xor",
                    "(\"UserId\" IS NOT NULL AND \"TenantId\" IS NULL) " +
                    "OR (\"UserId\" IS NULL AND \"TenantId\" IS NOT NULL)");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.AgentId).IsRequired();
            entity.Property(e => e.Enabled).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // One row per (principal, agent). NULLS NOT DISTINCT (PG15+;
            // production runs PG17) so a single (null, uid, agent) or
            // (tid, null, agent) row is unique across the repeated NULL halves —
            // same pattern as agent_role_selections / prompt_overrides.
            entity.HasIndex(e => new { e.TenantId, e.UserId, e.AgentId })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasDatabaseName("IX_tenant_agent_enablements_TenantId_UserId_AgentId");
        });
    }

    /// <summary>
    /// Story 34-4 — <c>tenant_plan_assignments</c> table. Version-pinned,
    /// audited plan assignments; CP-resident. Three CHECKs pin the closed
    /// <c>Status</c> set, the effective-window ordering, and a positive pinned
    /// version. The partial unique index
    /// <c>ux_tpa_one_active_per_tenant</c> on <c>(TenantId) WHERE Status='active'</c>
    /// guarantees at most one active assignment per tenant (mirrors
    /// <c>UX_plans_OneActivePerSlug</c>). A <c>(TenantId, Status)</c> index backs
    /// the "current assignment" lookup; a <c>PlanId</c> index backs the FK.
    /// FKs: TenantId → tenants (Cascade); PlanId → plans (Restrict).
    /// </summary>
    public static void ConfigureTenantPlanAssignments(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantPlanAssignment>(entity =>
        {
            entity.ToTable("tenant_plan_assignments", t =>
            {
                t.HasCheckConstraint(
                    "ck_tpa_status",
                    "\"Status\" IN ('active','scheduled','cancelled')");
                t.HasCheckConstraint(
                    "ck_tpa_effective_window",
                    "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" >= \"EffectiveFrom\"");
                t.HasCheckConstraint(
                    "ck_tpa_version_positive",
                    "\"PlanVersion\" >= 1");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Status).IsRequired().HasMaxLength(16);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // AC2 — at most one active assignment per tenant.
            entity.HasIndex(e => e.TenantId)
                .IsUnique()
                .HasFilter("\"Status\" = 'active'")
                .HasDatabaseName("ux_tpa_one_active_per_tenant");

            // "current assignment" lookup + FK support index.
            entity.HasIndex(e => new { e.TenantId, e.Status })
                .HasDatabaseName("IX_tenant_plan_assignments_TenantId_Status");
            entity.HasIndex(e => e.PlanId)
                .HasDatabaseName("IX_tenant_plan_assignments_PlanId");

            entity.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Plan>()
                .WithMany()
                .HasForeignKey(e => e.PlanId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>
    /// Story 34-3 — the authoritative per-<c>(tenant, provider)</c> billing-mode
    /// owner (<c>tenant_provider_billing</c>). CP-resident. The partial unique
    /// index enforces at most one <c>active</c> row per <c>(TenantId, ProviderKey)</c>;
    /// CHECKs pin <c>mode</c>/<c>status</c> to their closed domains and enforce the
    /// byok↔secret XOR (a byok row MUST carry a secret name; a platform row MUST
    /// NOT). FK to tenants cascades on tenant purge. Configured in the shared
    /// single source so the model graph and the additive migration stay aligned
    /// (same convention as <see cref="ConfigureTenantPlanAssignments"/>).
    /// </summary>
    public static void ConfigureTenantProviderBilling(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantProviderBilling>(entity =>
        {
            entity.ToTable("tenant_provider_billing", t =>
            {
                t.HasCheckConstraint("ck_tpb_mode", "\"Mode\" IN ('platform','byok')");
                t.HasCheckConstraint("ck_tpb_status", "\"Status\" IN ('active','disabled')");
                // A byok row MUST carry a secret name; a platform row MUST NOT.
                t.HasCheckConstraint(
                    "ck_tpb_secret_xor",
                    "(\"Mode\" = 'byok' AND \"SecretName\" IS NOT NULL) "
                    + "OR (\"Mode\" = 'platform' AND \"SecretName\" IS NULL)");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.ProviderKey).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Mode).IsRequired().HasMaxLength(16).HasDefaultValue("platform");
            entity.Property(e => e.SecretName).HasMaxLength(255);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(16).HasDefaultValue("active");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // AC1 / AC12 — one ACTIVE row per (tenant, provider). Partial index.
            entity.HasIndex(e => new { e.TenantId, e.ProviderKey })
                .IsUnique()
                .HasFilter("\"Status\" = 'active'")
                .HasDatabaseName("ux_tpb_active_provider");

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// Story 46-1 — configure the <c>provider_settings</c> table (persisted
    /// provider model selection + platform enable flag). CP-resident in BOTH
    /// modes (epic 46 D3a) — configured ONLY on <see cref="ControlPlaneDbContext"/>,
    /// never on the tenant context.
    ///
    /// <para>Three row kinds behind two CHECKs: <c>ck_provider_settings_scope</c>
    /// ties the <c>Scope</c> discriminator to the null pattern (platform = both
    /// principal columns null; principal = XOR — the
    /// <c>ck_prompt_overrides_principal_xor</c> pattern), and
    /// <c>ck_provider_settings_model</c> pins "a stored model is never an empty
    /// string" (a platform row may carry NULL when it only stores the enabled
    /// flag). The unique index uses NULLS NOT DISTINCT so the all-null platform
    /// principal dedupes like any other (same pattern as prompt_overrides /
    /// agent_role_selections).</para>
    ///
    /// <para><b>No FK to tenants/users</b> — this table is deliberately EXCLUDED
    /// from the Epic 19 destructive startup DROP list so UI model selections
    /// survive redeploys; an FK would cascade the rows away with the wiped
    /// principals (see <see cref="Entities.ProviderSetting"/>).</para>
    /// </summary>
    public static void ConfigureProviderSettings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProviderSetting>(entity =>
        {
            entity.ToTable("provider_settings", t =>
            {
                // Scope ⇄ null-pattern invariant: platform rows have neither
                // principal column; principal rows have exactly one.
                t.HasCheckConstraint(
                    "ck_provider_settings_scope",
                    "(\"Scope\" = 'platform' AND \"TenantId\" IS NULL AND \"UserId\" IS NULL) "
                    + "OR (\"Scope\" = 'principal' AND ("
                    + "(\"TenantId\" IS NOT NULL AND \"UserId\" IS NULL) "
                    + "OR (\"TenantId\" IS NULL AND \"UserId\" IS NOT NULL)))");
                // Strict XOR for principal rows (mirrors
                // ck_prompt_overrides_principal_xor; platform rows are exempt
                // by the all-null arm above — this CHECK forbids BOTH set).
                t.HasCheckConstraint(
                    "ck_provider_settings_principal_xor",
                    "NOT (\"TenantId\" IS NOT NULL AND \"UserId\" IS NOT NULL)");
                // A stored model is never the empty-string sentinel (config's
                // "" keeps meaning "no opinion"; DB rows must carry a real id
                // or NULL for a flag-only platform row).
                t.HasCheckConstraint(
                    "ck_provider_settings_model",
                    "\"DefaultModel\" IS NULL OR length(\"DefaultModel\") > 0");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Scope).IsRequired().HasMaxLength(16);
            entity.Property(e => e.ProviderKey).IsRequired().HasMaxLength(100);
            entity.Property(e => e.DefaultModel).HasMaxLength(256);
            entity.Property(e => e.Enabled).IsRequired().HasDefaultValue(true);
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // One row per (principal, provider). NULLS NOT DISTINCT (PG15+;
            // production runs PG17) so the all-null platform principal and the
            // half-null tenant/user principals each dedupe — same pattern as
            // prompt_overrides / agent_role_selections.
            entity.HasIndex(e => new { e.TenantId, e.UserId, e.ProviderKey })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasDatabaseName("IX_provider_settings_TenantId_UserId_ProviderKey");

            // Platform-row hot path (the snapshot rebuild filters by key).
            entity.HasIndex(e => e.ProviderKey)
                .HasDatabaseName("IX_provider_settings_ProviderKey");
        });
    }

    #region Story 41-30 — tenant-aware scheduled-trigger seam (control plane)

    /// <summary>
    /// Story 41-30 — the two control-plane tables of the tenant-aware
    /// scheduled-trigger seam: <c>scheduled_triggers</c> (the schedule
    /// registry, D1) and <c>scheduled_trigger_fires</c> (the durable
    /// at-most-once ledger, D2). CP-resident for the 43-5 reasons (the sweeper
    /// enumerates across tenants; tenant-schema migrations don't reach
    /// already-provisioned tenants). Both tables are deliberately EXCLUDED
    /// from the destructive startup DROP list (AC7) and therefore — mirroring
    /// <see cref="ConfigureProviderSettings"/> — carry NO FK to
    /// <c>tenants</c>: the tenants table IS wiped each deploy and a cascade
    /// would take the surviving schedule rows with it. Configured ONLY on
    /// <see cref="ControlPlaneDbContext"/>, never on the tenant context.
    /// </summary>
    public static void ConfigureScheduledTriggerEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Entities.ScheduledTrigger>(entity =>
        {
            entity.ToTable("scheduled_triggers", t =>
            {
                // Never an empty target / name / cron — the admin API rejects
                // them with a typed 400; these CHECKs make raw SQL fail too.
                t.HasCheckConstraint(
                    "ck_scheduled_triggers_definition_id",
                    "length(\"DefinitionId\") > 0");
                t.HasCheckConstraint(
                    "ck_scheduled_triggers_name",
                    "length(\"Name\") > 0");
                t.HasCheckConstraint(
                    "ck_scheduled_triggers_cron",
                    "length(\"CronExpression\") > 0");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.DefinitionId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.CronExpression).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Enabled).IsRequired().HasDefaultValue(true);
            entity.Property(e => e.InputJson)
                .IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.LastWindowKey).HasMaxLength(64);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // The natural key: one row per (tenant, definition, name). NULLS
            // NOT DISTINCT so at most ONE platform template per
            // (definition, name) — the prompt_overrides idiom for a
            // nullable-principal key (D1).
            entity.HasIndex(e => new { e.TenantId, e.DefinitionId, e.Name })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasDatabaseName("ux_scheduled_triggers_tenant_definition_name");

            // The tick's hot path: enabled rows ordered by due time.
            entity.HasIndex(e => new { e.Enabled, e.NextDueAt })
                .HasDatabaseName("IX_scheduled_triggers_Enabled_NextDueAt");
        });

        modelBuilder.Entity<Entities.ScheduledTriggerFire>(entity =>
        {
            entity.ToTable("scheduled_trigger_fires", t =>
            {
                t.HasCheckConstraint(
                    "ck_scheduled_trigger_fires_outcome",
                    "\"Outcome\" IN ('claimed','dispatched','failed')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.DefinitionId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.WindowKey).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Outcome)
                .IsRequired().HasMaxLength(16).HasDefaultValue("claimed");
            entity.Property(e => e.ClaimedAt).HasDefaultValueSql("now()");

            // THE at-most-once invariant (D2/Correction 3): the ON CONFLICT
            // DO NOTHING claim races against this index; Postgres arbitrates.
            entity.HasIndex(e => new { e.TriggerId, e.WindowKey })
                .IsUnique()
                .HasDatabaseName("ux_scheduled_trigger_fires_trigger_window");

            // Retention pruning scans by claim time.
            entity.HasIndex(e => e.ClaimedAt)
                .HasDatabaseName("IX_scheduled_trigger_fires_ClaimedAt");

            // Ledger dies with its trigger; intra-seam FK is safe because both
            // tables share the DROP-list exclusion (they survive together).
            entity.HasOne<Entities.ScheduledTrigger>()
                .WithMany()
                .HasForeignKey(e => e.TriggerId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    #endregion

    /// <summary>
    /// Story 35-1 — billing foundation entities. The tenant→Stripe customer
    /// mapping (<c>billing_customers</c>, unique <c>TenantId</c>) and the
    /// slug→Stripe-ids catalog (<c>billing_plan_prices</c>, unique
    /// <c>PlanSlug</c>). CP-resident; configured in the shared single source so
    /// the model graph and the additive migration stay aligned.
    /// </summary>
    public static void ConfigureBillingEntities(ModelBuilder modelBuilder)
    {
        // ── BillingCustomer (tenant → Stripe customer) ──
        modelBuilder.Entity<BillingCustomer>(entity =>
        {
            entity.ToTable("billing_customers", t =>
            {
                // Text domain for the persisted BillingMode member name.
                t.HasCheckConstraint(
                    "ck_billing_customers_mode",
                    "\"BillingMode\" IN ('PlatformProvided','Byok')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.BillingMode)
                .IsRequired().HasMaxLength(32).HasDefaultValue("PlatformProvided");
            entity.Property(e => e.DefaultCurrency)
                .IsRequired().HasMaxLength(3).HasDefaultValue("usd");
            entity.Property(e => e.TaxStatus)
                .IsRequired().HasMaxLength(32).HasDefaultValue("none");
            entity.Property(e => e.StripeCustomerId).HasMaxLength(255);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // Exactly one customer mapping per tenant (AC1 / AC12).
            entity.HasIndex(e => e.TenantId)
                .HasDatabaseName("UX_billing_customers_TenantId")
                .IsUnique();

            // Stripe customer ids are globally unique once assigned. Partial so
            // the null-until-acked retry rows don't collide on NULL.
            entity.HasIndex(e => e.StripeCustomerId)
                .HasDatabaseName("UX_billing_customers_StripeCustomerId")
                .HasFilter("\"StripeCustomerId\" IS NOT NULL")
                .IsUnique();

            // Tenant purge cascades the billing mapping.
            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── BillingPlanPrice (slug → Stripe ids catalog) ──
        modelBuilder.Entity<BillingPlanPrice>(entity =>
        {
            entity.ToTable("billing_plan_prices");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.PlanSlug).IsRequired().HasMaxLength(64);
            entity.Property(e => e.StripeProductId).HasMaxLength(255);
            entity.Property(e => e.StripePriceId).HasMaxLength(255);
            entity.Property(e => e.TokensInputMeterId).HasMaxLength(255);
            entity.Property(e => e.TokensInputPriceId).HasMaxLength(255);
            entity.Property(e => e.TokensOutputMeterId).HasMaxLength(255);
            entity.Property(e => e.TokensOutputPriceId).HasMaxLength(255);
            entity.Property(e => e.SeatsMeterId).HasMaxLength(255);
            entity.Property(e => e.SeatsPriceId).HasMaxLength(255);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // One catalog row per slug — the seed upserts on this key.
            entity.HasIndex(e => e.PlanSlug)
                .HasDatabaseName("UX_billing_plan_prices_PlanSlug")
                .IsUnique();
        });

        // ── BillingWebhookEvent (Story 35-5 — Stripe webhook dedup journal) ──
        modelBuilder.Entity<BillingWebhookEvent>(entity =>
        {
            entity.ToTable("billing_webhook_events", t =>
            {
                // Text domain for the processing status.
                t.HasCheckConstraint(
                    "ck_billing_webhook_events_status",
                    "\"Status\" IN ('received','processing','projected','enqueued','failed','skipped')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.StripeEventId).IsRequired().HasMaxLength(255);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(255);
            entity.Property(e => e.StripeObjectId).HasMaxLength(255);
            entity.Property(e => e.Status)
                .IsRequired().HasMaxLength(20).HasDefaultValue("received");
            entity.Property(e => e.Attempts).HasDefaultValue(0);
            entity.Property(e => e.Payload)
                .IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.ReceivedAt).HasDefaultValueSql("now()");

            // Dedup key — Stripe delivers at-least-once; the unique insert
            // collision is the authoritative idempotency guard (AC5).
            entity.HasIndex(e => e.StripeEventId)
                .HasDatabaseName("UX_billing_webhook_events_StripeEventId")
                .IsUnique();

            // Admin list — recent rows by status (AC12).
            entity.HasIndex(e => new { e.Status, e.ReceivedAt })
                .HasDatabaseName("IX_billing_webhook_events_Status_ReceivedAt")
                .IsDescending(false, true);

            // Tenant-scoped filter for the admin list (partial — most rows resolve).
            entity.HasIndex(e => e.TenantId)
                .HasDatabaseName("IX_billing_webhook_events_TenantId")
                .HasFilter("\"TenantId\" IS NOT NULL");
        });

        // ── BillingSubscription (Story 35-4 — control-plane subscription mirror) ──
        modelBuilder.Entity<BillingSubscription>(entity =>
        {
            entity.ToTable("billing_subscriptions", t =>
            {
                // Text domain for the Stripe-mirrored status.
                t.HasCheckConstraint(
                    "ck_billing_subscriptions_status",
                    "\"Status\" IN ('trialing','active','past_due','canceled',"
                    + "'incomplete','incomplete_expired','unpaid')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.StripeSubscriptionId).HasMaxLength(255);
            entity.Property(e => e.PlanSlug)
                .IsRequired().HasMaxLength(64).HasDefaultValue("free");
            entity.Property(e => e.Status)
                .IsRequired().HasMaxLength(32).HasDefaultValue("active");
            entity.Property(e => e.CancelAtPeriodEnd).HasDefaultValue(false);
            entity.Property(e => e.Seats).HasDefaultValue(1);
            entity.Property(e => e.ScheduledPlanSlug).HasMaxLength(64);
            entity.Property(e => e.StripeScheduleId).HasMaxLength(255);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // At most ONE non-terminal subscription per tenant (AC1 / AC12). A
            // tenant may keep many historical terminal rows; only the live one is
            // constrained. Partial-unique expresses that without a soft-delete col.
            entity.HasIndex(e => e.TenantId)
                .HasDatabaseName("UX_billing_subscriptions_TenantId_NonTerminal")
                .HasFilter("\"Status\" NOT IN ('canceled','incomplete_expired')")
                .IsUnique();

            // Stripe subscription ids are globally unique once assigned. Partial so
            // the null-until-acked rows don't collide on NULL.
            entity.HasIndex(e => e.StripeSubscriptionId)
                .HasDatabaseName("UX_billing_subscriptions_StripeSubscriptionId")
                .HasFilter("\"StripeSubscriptionId\" IS NOT NULL")
                .IsUnique();

            // Tenant purge cascades the subscription mirror.
            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// Story 37-1 — configure the curated <c>audit_records</c> read-model table.
    /// Called from BOTH <see cref="ControlPlaneDbContext"/> (platform-scope
    /// audit) and <see cref="TenantDbContext"/> (tenant-scope audit) so the
    /// single config source applies to both model builds — there is exactly one
    /// physical <c>audit_records</c> shape, materialized into either the CP or a
    /// tenant schema by the projector.
    ///
    /// <para>Mirrors <c>prompt_overrides</c>: the
    /// <c>ck_audit_records_principal_xor</c> CHECK ties exactly one of
    /// (<c>UserId</c>, <c>TenantId</c>) to non-null. The UNIQUE
    /// <c>SourceEventId</c> index is the idempotency key (one curated row per raw
    /// event — AC8). The <c>SourceSequenceNumber</c> index gives Story 37-2 a
    /// deterministic chain order. The reserved <c>RecordHash</c>/<c>PrevRecordHash</c>
    /// columns are nullable and left null by this story.</para>
    /// </summary>
    public static void ConfigureAuditEntities(
        ModelBuilder modelBuilder, Guid? fixedTenantId = null)
    {
        modelBuilder.Entity<AuditRecord>(entity =>
        {
            entity.ToTable("audit_records", t =>
            {
                // Per-mode ownership invariant (mirrors ck_prompt_overrides_principal_xor):
                // UserId and TenantId are mutually exclusive — never BOTH set.
                //   single-user → UserId set, TenantId null;
                //   SaaS tenant-scope → TenantId set, UserId null;
                //   SaaS platform-scope → BOTH null (a control-plane platform row,
                //     e.g. impersonation against the platform — AC11). This third
                //     case is why the CHECK is "not both" rather than strict XOR:
                //     a platform action has no tenant AND no single-user owner, yet
                //     AC11 mandates it land in the CP audit_records with tenant_id
                //     null. The "never a tenant's view" isolation (AC14) is then
                //     enforced by physical placement (CP schema) + the TenantId
                //     filter on tenant reads, not by the ownership columns.
                t.HasCheckConstraint(
                    "ck_audit_records_principal_xor",
                    "NOT (\"UserId\" IS NOT NULL AND \"TenantId\" IS NOT NULL)");
                // Outcome is a closed enum — a buggy projector can't stash junk.
                t.HasCheckConstraint(
                    "ck_audit_records_outcome",
                    "\"Outcome\" IN ('success','failure','denied')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.ActionCode).IsRequired().HasMaxLength(128);
            entity.Property(e => e.Category).IsRequired().HasMaxLength(32);
            entity.Property(e => e.Severity).IsRequired().HasMaxLength(16);
            entity.Property(e => e.ActorEmailSnapshot).HasMaxLength(320);
            entity.Property(e => e.TargetType).HasMaxLength(64);
            entity.Property(e => e.TargetId).HasMaxLength(255);
            entity.Property(e => e.Outcome)
                .IsRequired().HasMaxLength(16).HasDefaultValue("success");
            entity.Property(e => e.IpAddress).HasMaxLength(64);
            entity.Property(e => e.UserAgent).HasMaxLength(512);
            entity.Property(e => e.OccurredAt).HasColumnType("timestamp with time zone");
            // Story 37-2 (code-review fix) — PayloadJson is stored as `text`, NOT
            // `jsonb`. The hash-chain (AC2) is computed at insert over the in-memory
            // payload STRING, and verification recomputes it over the value read
            // back from this column. `jsonb` does not round-trip its input text —
            // Postgres reorders object keys, strips whitespace, and normalizes
            // numbers/unicode — so write-bytes != read-bytes and EVERY chain would
            // verify as TAMPERED. `text` preserves the exact bytes, making the
            // recompute deterministic. No code uses jsonb operators on this column
            // (the only jsonb-aware read is `"PayloadJson"::text ILIKE` in
            // AuditQueryService, and `text::text` is a no-op), so text is safe.
            entity.Property(e => e.PayloadJson)
                .HasColumnType("text").HasDefaultValueSql("'{}'");
            // Story 37-2 — tamper-evidence hash chain. The hash columns were
            // reserved by 37-1; 37-2 adds the per-scope monotonic sequence and a
            // UNIQUE index over it (one chain per physical table — the CP table
            // or a tenant schema's table). Nullable for pre-37-2 legacy rows
            // awaiting backfill; Postgres treats NULLs as distinct so the unique
            // index permits many un-backfilled rows.
            entity.Property(e => e.RecordHash).HasMaxLength(128);
            entity.Property(e => e.PrevRecordHash).HasMaxLength(128);
            entity.Property(e => e.ChainSequence);
            entity.HasIndex(e => e.ChainSequence)
                .IsUnique()
                .HasDatabaseName("UX_audit_records_ChainSequence");

            // Idempotency key — one curated row per raw event (AC8). The
            // projector inserts-if-absent; a re-scan after a crash is a no-op.
            entity.HasIndex(e => e.SourceEventId)
                .IsUnique()
                .HasDatabaseName("UX_audit_records_SourceEventId");
            // Replay / 37-2 chain order.
            entity.HasIndex(e => e.SourceSequenceNumber)
                .HasDatabaseName("IX_audit_records_SourceSequenceNumber");
            // Tenant query hot path (Story 37-10).
            entity.HasIndex(e => new { e.TenantId, e.OccurredAt })
                .HasDatabaseName("IX_audit_records_TenantId_OccurredAt");
            // Single-user query hot path.
            entity.HasIndex(e => new { e.UserId, e.OccurredAt })
                .HasDatabaseName("IX_audit_records_UserId_OccurredAt");
            // Category/severity compliance filter (Story 37-10).
            entity.HasIndex(e => new { e.Category, e.OccurredAt })
                .HasDatabaseName("IX_audit_records_Category_OccurredAt");

            // Tenant-context defence-in-depth: same no-op filter seam every
            // tenant-resident table uses. The per-tenant schema + connection is
            // the real isolation plane (Doc 01 §1.4); the explicit TenantId
            // predicate in the repository carries the transitional shared-DB
            // phase. AC14's isolation proof is the schema + the UserId/TenantId
            // routing, not a query filter.
            ApplyTenantFilter(entity, fixedTenantId, e => e.TenantId);
        });
    }

    /// <summary>
    /// Story 39-11 — configure the tenant-resident <c>document_instances</c> table
    /// (the read-optimized document product layer over the DCB stream). Called
    /// ONLY from <see cref="ConfigureTenantEntities"/> — the CP context never sees
    /// this table (document instances are tenant data). Copies the sectioned shape
    /// of <see cref="ConfigureAuditEntities"/>.
    ///
    /// <para>Every column carries an explicit snake_case <c>HasColumnName</c>
    /// (Design Decision D1, per AC1's column list). <c>id</c> is CLIENT-SET from the
    /// envelope's UUID v7 — NO <c>gen_random_uuid()</c> default. The status CHECK
    /// pins the 7-value store vocabulary (D3); the unique filtered index on
    /// <c>supersedes_document_id</c> keeps the supersession chain linear (D4).</para>
    /// </summary>
    public static void ConfigureDocumentEntities(
        ModelBuilder modelBuilder, Guid? fixedTenantId = null)
    {
        modelBuilder.Entity<DocumentInstance>(entity =>
        {
            entity.ToTable("document_instances", t =>
            {
                // Store status is a closed 7-value enum (D3) — a buggy writer can't
                // stash junk. Mirrors DocumentInstanceStatus's wire strings exactly.
                t.HasCheckConstraint(
                    "ck_document_instances_status",
                    "status IN ('draft','validated','in_review','accepted','rejected','superseded','escalated')");
            });
            entity.HasKey(e => e.Id);
            // Client-set id = the envelope's UUID v7 (NO gen_random_uuid() default);
            // the envelope id IS the row id (AC7 store↔stream linkage).
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.DocumentType).HasColumnName("document_type").IsRequired().HasMaxLength(64);
            entity.Property(e => e.IssueId).HasColumnName("issue_id").IsRequired();
            entity.Property(e => e.ProducedByRole).HasColumnName("produced_by_role").IsRequired().HasMaxLength(64);
            entity.Property(e => e.ProducedByAction).HasColumnName("produced_by_action").IsRequired().HasMaxLength(64);
            entity.Property(e => e.ProducedByWorkflow).HasColumnName("produced_by_workflow").HasMaxLength(128);
            entity.Property(e => e.SchemaVersion).HasColumnName("schema_version").IsRequired();
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id");
            entity.Property(e => e.Revision).HasColumnName("revision").IsRequired();
            entity.Property(e => e.Status).HasColumnName("status").IsRequired().HasMaxLength(16);
            entity.Property(e => e.SupersedesDocumentId).HasColumnName("supersedes_document_id");
            entity.Property(e => e.ParentDocumentId).HasColumnName("parent_document_id");
            entity.Property(e => e.CorrelatingEventId).HasColumnName("correlating_event_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.BodyJson)
                .HasColumnName("body").HasColumnType("jsonb")
                .IsRequired().HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.CreatedAt)
                .HasColumnName("created_at").HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt)
                .HasColumnName("updated_at").HasColumnType("timestamp with time zone");

            // Lineage render hot path (AC1): grouped-by-issue reads filtered by
            // type + status; and the first-produced ordering read.
            entity.HasIndex(e => new { e.IssueId, e.DocumentType, e.Status })
                .HasDatabaseName("IX_document_instances_issue_type_status");
            entity.HasIndex(e => new { e.IssueId, e.CreatedAt })
                .HasDatabaseName("IX_document_instances_issue_created");

            // Supersession self-reference (D4): the prior revision. Restrict so a
            // superseded row can't be deleted out from under its successor
            // (immutable history — there is no delete API anyway).
            entity.HasOne<DocumentInstance>()
                .WithMany()
                .HasForeignKey(e => e.SupersedesDocumentId)
                .OnDelete(DeleteBehavior.Restrict);

            // Chain linearity (D4): at most ONE row may supersede a given prior.
            // Filtered so the many revision-1 rows (supersedes IS NULL) don't
            // collide on a single NULL.
            entity.HasIndex(e => e.SupersedesDocumentId)
                .IsUnique()
                .HasFilter("supersedes_document_id IS NOT NULL")
                .HasDatabaseName("UX_document_instances_supersedes");

            // Tenant-context defence-in-depth: the established no-op filter seam
            // (schema + connection is the real isolation plane; the repository
            // carries an explicit TenantId predicate for the shared-DB phase).
            ApplyTenantFilter(entity, fixedTenantId, e => e.TenantId);
        });
    }

    /// <summary>
    /// Story 37-1 — configure the <c>audit_projector_cursor</c> table. Lives in
    /// the control plane only.
    ///
    /// <para><b>C1 fix — per-tenant domain cursor</b>: the key is the composite
    /// <c>(ProjectorId, TenantId)</c>. Each tenant's <c>domain_events</c> stream
    /// is an independent per-schema BIGSERIAL, so each tenant tracks its OWN
    /// <c>LastDomainSequenceNumber</c> on its own row. The global CP
    /// <c>platform_events</c> stream is tracked on the distinguished
    /// <see cref="AuditProjectorCursor.PlatformSentinel"/> row (all-zero
    /// <c>TenantId</c>), which also carries the single-user / shared-DB
    /// <c>cp.domain_events</c> fallback.</para>
    /// </summary>
    /// <summary>
    /// Story 37-2 (AC5) — configure the <c>audit_chain_checkpoints</c> table.
    /// CP-resident only (called from <see cref="ControlPlaneDbContext"/>). Holds
    /// signed anchors for BOTH platform and tenant chains; <c>tenant_id</c>
    /// discriminates. A CHECK ties <c>scope</c>↔<c>tenant_id</c> consistency.
    /// </summary>
    public static void ConfigureAuditChainCheckpoint(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditChainCheckpoint>(entity =>
        {
            entity.ToTable("audit_chain_checkpoints", t =>
            {
                t.HasCheckConstraint(
                    "ck_audit_chain_checkpoints_scope_tenant",
                    "(\"Scope\" = 'platform' AND \"TenantId\" IS NULL) "
                    + "OR (\"Scope\" = 'tenant' AND \"TenantId\" IS NOT NULL)");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Scope).IsRequired().HasMaxLength(16);
            entity.Property(e => e.HeadSequence).IsRequired();
            entity.Property(e => e.HeadHash).IsRequired().HasMaxLength(128);
            entity.Property(e => e.SignedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.Signature).IsRequired().HasColumnType("bytea");
            entity.Property(e => e.KeyVersion).IsRequired();
            entity.Property(e => e.CreatedAt)
                .HasColumnType("timestamp with time zone").HasDefaultValueSql("now()");

            // Latest-covering-checkpoint lookup: (scope, tenant_id, head_sequence DESC).
            entity.HasIndex(e => new { e.Scope, e.TenantId, e.HeadSequence })
                .HasDatabaseName("IX_audit_chain_checkpoints_scope_seq");
        });
    }

    public static void ConfigureAuditProjectorCursor(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditProjectorCursor>(entity =>
        {
            entity.ToTable("audit_projector_cursor");
            // Composite key: one domain high-water mark per tenant; the platform
            // stream lives on the all-zero sentinel row.
            entity.HasKey(e => new { e.ProjectorId, e.TenantId });
            entity.Property(e => e.ProjectorId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.TenantId).IsRequired();
            entity.Property(e => e.LastDomainSequenceNumber).HasDefaultValue(0L);
            entity.Property(e => e.LastPlatformSequenceNumber).HasDefaultValue(0L);
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
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
            // Story 34-1 — the typed price-book children are CP-resident; keep
            // them out of the tenant model graph (Plan's navigations would
            // otherwise pull them in via convention).
            modelBuilder.Ignore<PlanFeature>();
            modelBuilder.Ignore<PlanEntitlement>();
            modelBuilder.Ignore<PlanPrice>();
            modelBuilder.Ignore<PlatformEvent>();
            modelBuilder.Ignore<PlatformQueuedTask>();
            modelBuilder.Ignore<PlatformEmailOutboxMessage>();
            // Story 38-3 — slack_outbox is CP-resident; keep it out of the tenant
            // model graph (mirrors platform_email_outbox above).
            modelBuilder.Ignore<SlackOutboxMessage>();
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

            // Story 27-2 — unique on (UserId, TenantId, Scope, Role, Action)
            // with NULLS NOT DISTINCT so a single (null, tenantId, scope,
            // role, action) row is unique across all repeated NULLs in
            // UserId. NULLS NOT DISTINCT requires PG15+ (production runs
            // PG17 — see Tamma project tech stack).
            // Replaces the raw-SQL index from migration 20260429152530 — NULLS NOT
            // DISTINCT became model-expressible in EF 9. Name preserved.
            entity.HasIndex(e => new { e.UserId, e.TenantId, e.Scope, e.Role, e.Action })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasDatabaseName("IX_prompt_overrides_UserId_TenantId_Scope_Role_Action");

            if (omitTenantIdColumn) entity.Ignore(e => e.TenantId);
            ApplyTenantFilter(entity, fixedTenantId, e => e.TenantId);
        });

        // ── AcceptanceRulesOverride ──
        // Story 39-5: dual-scoping, mirrors PromptOverride exactly. Single-user
        // rows carry user_id (tenant_id IS NULL); SaaS rows carry tenant_id
        // (user_id IS NULL). The principal_xor CHECK enforces exactly-one.
        // DocumentTypeKey NULL = the principal BASE row (the deployment-wide
        // dial); a non-null key = a per-type override. The unique index covers
        // BOTH principal keys + the type key with NULLS NOT DISTINCT (PG15+;
        // production runs PG17) so the (null, tid, key) and (uid, null, key)
        // row spaces are disjoint and the (principal, NULL-key) base rows dedupe.
        modelBuilder.Entity<AcceptanceRulesOverride>(entity =>
        {
            entity.ToTable("acceptance_rules_overrides", t =>
            {
                t.HasCheckConstraint(
                    "ck_acceptance_rules_overrides_principal_xor",
                    "(\"UserId\" IS NOT NULL AND \"TenantId\" IS NULL) " +
                    "OR (\"UserId\" IS NULL AND \"TenantId\" IS NOT NULL)");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.DocumentTypeKey).HasMaxLength(64);
            entity.Property(e => e.RulesJson).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.Version).HasDefaultValue(1);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => new { e.UserId, e.TenantId, e.DocumentTypeKey })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasDatabaseName("IX_acceptance_rules_overrides_UserId_TenantId_DocumentTypeKey");

            if (omitTenantIdColumn) entity.Ignore(e => e.TenantId);
            ApplyTenantFilter(entity, fixedTenantId, e => e.TenantId);
        });

        // ── Convention ──
        // Story 27-8: two-tier convention store.
        //   tenant_id IS NULL  → system default (shipped by Tamma, seeded in 27-16)
        //   tenant_id NOT NULL → tenant override (tenant admin owns it)
        //
        // Unlike PromptOverride this table has only ONE principal key column
        // (tenant_id). There is no user_id column, no principal_xor CHECK, and
        // no per-user override layer — tenant admins own the whole team's
        // conventions; members cannot personalise them.
        //
        // omitTenantIdColumn is NOT applied here. TenantId is the two-tier
        // discriminator: omitting it in single-user mode would destroy the
        // ability to distinguish system defaults (null) from overrides
        // (non-null). Following BudgetConfig precedent which also retains the
        // nullable tenant_id column for system-vs-tenant discrimination.
        //
        // No FK to tenants table — per-tenant DB routing makes a hard FK
        // awkward (same precedent as PromptOverride). See Convention.cs
        // doc-comment.
        //
        // The UNIQUE(TenantId, Role, Action) index uses NULLS NOT DISTINCT
        // (declared on the model below — EF 9 expresses it natively) so
        // exactly one system-default row per (role, action) cell is permitted.
        // This index also serves as the resolution hot-path B-tree seek —
        // no separate non-unique index is needed (it would be fully covered
        // by the unique index and would be redundant).
        modelBuilder.Entity<Convention>(entity =>
        {
            entity.ToTable("conventions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Role).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Action).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Body).IsRequired();
            entity.Property(e => e.Version).HasDefaultValue(1);
            entity.Property(e => e.Enabled).HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // Unique on (TenantId, Role, Action) with NULLS NOT DISTINCT so
            // the single null-tenant system-default per (role, action) cell
            // is unique. NULLS NOT DISTINCT requires PG 15+ (production runs
            // PG17).
            // Replaces the raw-SQL index from migration 20260524143833. Name preserved.
            entity.HasIndex(e => new { e.TenantId, e.Role, e.Action })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasDatabaseName("IX_conventions_TenantId_Role_Action");

            // No omitTenantIdColumn branch — intentional. Unlike BudgetConfig,
            // where tenant_id is a simple ownership column that can be dropped
            // in a per-tenant DB, here tenant_id IS the two-tier discriminator:
            // NULL = system-default row, non-null = tenant override. Dropping
            // it would destroy the semantics of the entire table. This is
            // therefore NOT analogous to BudgetConfig's omitTenantIdColumn
            // pattern.
            //
            // Epic 28 per-tenant DB cutover (Story 28-13+): when
            // omitTenantIdColumn is set to true for a per-tenant DB, it is an
            // OPEN design decision how system-default convention rows are
            // provided in that DB (options include: replicate them during
            // provisioning, resolve them via the control-plane DB, or keep a
            // shared read-only conventions DB). Do NOT add a branch here until
            // that design is resolved.
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
            // Story 34-3 / 35-2 — per-call billing posture ("byok" | "platform").
            entity.Property(e => e.BillingMode).IsRequired().HasMaxLength(16).HasDefaultValue("platform");
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

        // ── ChannelOutboxMessage (Story 39-18, D4) ──
        // The workflow↔orchestrator / user↔orchestrator channel store-and-forward
        // outbox. Mirrors email_outbox (status transitions, lease semantics) minus
        // SMTP; PayloadJson is the whole ChannelEnvelope as jsonb for clean replay.
        modelBuilder.Entity<ChannelOutboxMessage>(entity =>
        {
            entity.ToTable("channel_outbox");
            entity.HasKey(e => e.Id);
            // Id IS the ChannelEnvelope message id (UUID v7) — client-set, NO
            // gen_random_uuid() default so ordering by Id is time-ordered replay.
            entity.Property(e => e.TenantId).IsRequired();
            entity.Property(e => e.Audience).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Kind).IsRequired().HasMaxLength(64);
            entity.Property(e => e.PayloadJson)
                .HasColumnName("PayloadJson").HasColumnType("jsonb")
                .IsRequired().HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue("pending");
            entity.Property(e => e.Attempts).HasDefaultValue(0);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            // Per-recipient unacked replay (connect-time + sweeper) and the
            // per-audience drain the sweeper walks.
            entity.HasIndex(e => new { e.TenantId, e.RecipientUserId, e.Status })
                .HasDatabaseName("IX_channel_outbox_tenant_recipient_status");
            entity.HasIndex(e => new { e.TenantId, e.Audience, e.Status })
                .HasDatabaseName("IX_channel_outbox_tenant_audience_status");

            if (omitTenantIdColumn)
            {
                entity.Ignore(e => e.TenantId);
            }

            // No query filter — the outbox is shared infra; tenant scoping is
            // explicit in the repository APIs (the per-tenant schema is the real
            // isolation plane, the repository carries a TenantId predicate).
            ApplyTenantFilter(entity, fixedTenantId, e => e.TenantId);
        });

        // ── Analytics usage fact tables (Story 36-1) ──
        // Per-tenant dimensional usage/cost/performance store. Schema-only here
        // (Story 36-2 owns population). Mirrors ConfigurePlatformAnalyticsHourly
        // (Story 28-10) for defaults/precision; uses the prompt_overrides /
        // conventions NULLS NOT DISTINCT pattern for the idempotent business key.
        ConfigureAnalyticsUsageEntities(modelBuilder, fixedTenantId);

        // ── Analytics projection checkpoint (Story 36-2) ──
        // Per-tenant resumable SequenceNumber cursor for the dimensional
        // projection. Schema is the isolation plane (no TenantId column).
        ConfigureAnalyticsProjectionCheckpoint(modelBuilder, fixedTenantId);

        // ── Curated audit records (Story 37-1) ──
        // Tenant-scope curated audit trail materialized from the tenant
        // domain_events stream. The SAME table shape is also configured on the
        // CP context for platform-scope rows — one physical schema, two homes.
        ConfigureAuditEntities(modelBuilder, fixedTenantId);

        // ── Document instances (Story 39-11) ──
        // Tenant-resident read-optimized document product layer over the DCB
        // stream. Tenant model ONLY — the CP context never carries this table
        // (document instances are tenant data).
        ConfigureDocumentEntities(modelBuilder, fixedTenantId);

        // ── Agent role selections (Story 32-2) ──
        // SaaS tenant-keyed role→agent selections. The SAME table shape is also
        // configured on the CP context for single-user user-keyed rows — one
        // physical schema, two homes (mirrors audit_records / prompt_overrides).
        ConfigureAgentRoleSelections(modelBuilder, fixedTenantId);

        // ── Native tracker (Story 44-1) ──
        // projects / work_items / work_item_relations / iterations /
        // tracker_preferences. Tenant model ONLY — the CP context never
        // carries these tables (work-tracking rows are tenant data, epic D5).
        ConfigureTrackerEntities(modelBuilder, fixedTenantId);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // ── Story 44-1 — Native tracker entity configuration (Epic 44) ──
    //
    // ONE contiguous region; do not interleave other stories' configs here.
    // Five tenant-schema tables. Vocabulary columns store 44-0's wire strings
    // and every CHECK below mirrors the corresponding Tamma.Core.Tracking
    // enum's [Wire] set EXACTLY — TrackerMigrationTests asserts set equality
    // by reflection, so a member added there without an amendment here fails
    // loudly in CI (the ck_document_instances_status posture).
    //
    // The two rank columns carry UseCollation("C") IN THE MODEL: the base-62
    // rank alphabet (Rank.cs) agrees with Postgres ORDER BY only under the C
    // collation — under en_US.UTF-8 case interleaves ('a' before 'B') and the
    // board order silently diverges from API order. Keeping the collation in
    // the model (not a migration hand-edit) means a regenerated migration or
    // snapshot cannot silently drop it.
    // ═════════════════════════════════════════════════════════════════════════
    public static void ConfigureTrackerEntities(
        ModelBuilder modelBuilder, Guid? fixedTenantId = null)
    {
        // ── ProjectEntity ──
        modelBuilder.Entity<ProjectEntity>(entity =>
        {
            entity.ToTable("projects", t =>
            {
                // Mirrors EstimateScale's 5 wire strings (44-0 AC13).
                t.HasCheckConstraint(
                    "ck_projects_estimate_scale",
                    "\"EstimateScale\" IN ('not_used','linear','fibonacci','exponential','t_shirt')");
                // The mint counter can never fall below the first mintable number.
                t.HasCheckConstraint("ck_projects_next_number", "\"NextNumber\" >= 1");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            // ^[A-Z][A-Z0-9]{1,9}$ — 10 chars max (WorkItemRef.IsValidProjectKey
            // is the validation boundary; the width just pins the invariant).
            entity.Property(e => e.Key).IsRequired().HasMaxLength(10);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.EstimateScale)
                .IsRequired().HasMaxLength(16).HasDefaultValue("not_used");
            entity.Property(e => e.NextNumber).HasDefaultValue(1);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.Version).HasDefaultValue(1);
            // NOTE: RepositoryId is a bare Guid? — deliberately NO FK (story
            // AC10): 39-20's repositories table is control-plane resident and
            // a cross-plane FK is not expressible; no second repo registry.

            entity.HasIndex(e => e.Key).IsUnique()
                .HasDatabaseName("UX_projects_key");
        });

        // ── WorkItemEntity ──
        modelBuilder.Entity<WorkItemEntity>(entity =>
        {
            entity.ToTable("work_items", t =>
            {
                // Mirrors WorkItemStatus's EIGHT wire strings (triage included
                // from day one — 44-0 AC2; adding a member later is a
                // fleet-wide migration through the 44-1 sweep).
                t.HasCheckConstraint(
                    "ck_work_items_status",
                    "\"Status\" IN ('triage','backlog','ready','in_progress','in_review','blocked','done','cancelled')");
                // Mirrors WorkItemKind's FOUR wire strings — no bug, no chore;
                // those live on the IssueType axis (44-0 AC1).
                t.HasCheckConstraint(
                    "ck_work_items_kind",
                    "\"Kind\" IN ('epic','story','task','spike')");
                // Mirrors TriagePriority's wires; NULL = unprioritised (44-0 AC11).
                t.HasCheckConstraint(
                    "ck_work_items_priority",
                    "\"Priority\" IS NULL OR \"Priority\" IN ('urgent','high','normal','low')");
                // Mirrors TriageIssueType's wires; NULL = not yet classified.
                t.HasCheckConstraint(
                    "ck_work_items_issue_type",
                    "\"IssueType\" IS NULL OR \"IssueType\" IN ('bug','feature','chore','question','security','docs')");
                t.HasCheckConstraint("ck_work_items_number", "\"Number\" >= 1");
            });
            entity.HasKey(e => e.Id);
            // Client-set UUIDv7 (UuidV7.NewGuid() in the repository) — NO DB
            // default: the id is minted before the row exists so events/docs
            // can reference it (the DocumentInstance client-set-id posture).
            entity.Property(e => e.Key).IsRequired();
            entity.Property(e => e.PreviousKeys)
                .IsRequired().HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]");
            entity.Property(e => e.Kind).IsRequired().HasMaxLength(16);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(16);
            entity.Property(e => e.Priority).HasMaxLength(16);
            entity.Property(e => e.IssueType).HasMaxLength(16);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(500);
            // THE collation obligation (Rank.cs; 44-0 D7 / this story AC4):
            // both rank axes, ordinal byte order, in the model so a
            // regenerate cannot drop it.
            entity.Property(e => e.Rank).IsRequired().UseCollation("C");
            entity.Property(e => e.SiblingRank).IsRequired().UseCollation("C");
            entity.Property(e => e.Estimate).HasColumnType("numeric");
            entity.Property(e => e.ExternalRefJson).HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.Version).HasDefaultValue(1);

            entity.HasOne<ProjectEntity>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            // Parent delete is RESTRICT, not CASCADE: silently deleting an
            // epic's subtree is unrecoverable; 44-2 returns 409 (reparent or
            // delete children first).
            entity.HasOne<WorkItemEntity>()
                .WithMany()
                .HasForeignKey(e => e.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<IterationEntity>()
                .WithMany()
                .HasForeignKey(e => e.IterationId)
                .OnDelete(DeleteBehavior.SetNull);

            // Key identity: unique wire key per tenant; (ProjectId, Number)
            // unique is the mint's belt-and-braces (story AC5).
            entity.HasIndex(e => e.Key).IsUnique()
                .HasDatabaseName("UX_work_items_key");
            entity.HasIndex(e => new { e.ProjectId, e.Number }).IsUnique()
                .HasDatabaseName("UX_work_items_project_number");
            // Board/backlog hot path: project rank order filtered by status.
            entity.HasIndex(e => new { e.ProjectId, e.Status, e.Rank })
                .HasDatabaseName("IX_work_items_project_status_rank");
            // Sibling ordering under a parent (44-0 AC10's second axis).
            entity.HasIndex(e => new { e.ProjectId, e.ParentId, e.SiblingRank })
                .HasDatabaseName("IX_work_items_project_parent_sibling_rank");
            entity.HasIndex(e => new { e.AssigneeUserId, e.Status })
                .HasDatabaseName("IX_work_items_assignee_status");
            entity.HasIndex(e => e.IterationId)
                .HasDatabaseName("IX_work_items_iteration");
            // Current-or-previous key lookup (WorkItemKeyHistory.Matches in
            // SQL: PreviousKeys @> ARRAY[key]) — GIN serves the containment.
            entity.HasIndex(e => e.PreviousKeys)
                .HasMethod("gin")
                .HasDatabaseName("IX_work_items_previous_keys");
            // The 44-8 already-linked skip index on ExternalRefJson keys is an
            // expression index the EF model cannot express — raw SQL in the
            // AddTrackerCore migration (AddDomainEventsUserIdIndex pattern).

            // No principal plane on work items (epic D6) — the no-op filter
            // seam documents the posture; schema is the isolation plane.
            ApplyTenantFilter<WorkItemEntity>(entity, fixedTenantId, _ => null);
        });

        // ── WorkItemRelation ──
        modelBuilder.Entity<WorkItemRelation>(entity =>
        {
            entity.ToTable("work_item_relations", t =>
            {
                // Mirrors WorkItemRelationKind's THREE wire strings (44-0 AC14).
                t.HasCheckConstraint(
                    "ck_work_item_relations_kind",
                    "\"Kind\" IN ('blocks','duplicate','related')");
                // Backs Canonicalize's TRACKER.SELF_RELATION at the DB layer.
                t.HasCheckConstraint(
                    "ck_work_item_relations_no_self",
                    "\"SourceId\" <> \"TargetId\"");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Kind).IsRequired().HasMaxLength(16);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            // An item's edges die with it — relations are annotations, not
            // history (the DCB stream is the history).
            entity.HasOne<WorkItemEntity>()
                .WithMany()
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<WorkItemEntity>()
                .WithMany()
                .HasForeignKey(e => e.TargetId)
                .OnDelete(DeleteBehavior.Cascade);

            // ASSUMES canonical form (WorkItemRelationKind.Canonicalize:
            // symmetric kinds lower-id-first) — a mirror duplicate of a
            // symmetric edge maps onto the same stored triple and collides
            // here. The repository is the only writer and always canonicalizes.
            entity.HasIndex(e => new { e.SourceId, e.TargetId, e.Kind }).IsUnique()
                .HasDatabaseName("UX_work_item_relations_source_target_kind");
            entity.HasIndex(e => e.TargetId)
                .HasDatabaseName("IX_work_item_relations_target");
        });

        // ── IterationEntity ──
        modelBuilder.Entity<IterationEntity>(entity =>
        {
            entity.ToTable("iterations", t =>
            {
                t.HasCheckConstraint(
                    "ck_iterations_status",
                    "\"Status\" IN ('planned','active','closed')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Status)
                .IsRequired().HasMaxLength(16).HasDefaultValue("planned");
            entity.Property(e => e.CapacityPoints).HasColumnType("numeric");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.Version).HasDefaultValue(1);

            entity.HasOne<ProjectEntity>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.ProjectId, e.Name }).IsUnique()
                .HasDatabaseName("UX_iterations_project_name");
        });

        // ── TrackerPreference ──
        // The ONE tracker table with the dual-scoped principal pattern:
        // STRONG XOR (acceptance_rules_overrides form — both-NULL rejected,
        // NOT the weak audit_records form) + NULLS NOT DISTINCT unique index
        // so each plane's null half dedupes (PG15+; production PG17).
        modelBuilder.Entity<TrackerPreference>(entity =>
        {
            entity.ToTable("tracker_preferences", t =>
            {
                t.HasCheckConstraint(
                    "ck_tracker_preferences_principal_xor",
                    "(\"UserId\" IS NOT NULL AND \"TenantId\" IS NULL) " +
                    "OR (\"UserId\" IS NULL AND \"TenantId\" IS NOT NULL)");
                t.HasCheckConstraint(
                    "ck_tracker_preferences_default_kind",
                    "\"DefaultKind\" IS NULL OR \"DefaultKind\" IN ('epic','story','task','spike')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.DefaultKind).HasMaxLength(16);
            entity.Property(e => e.BoardGroupBy).HasMaxLength(32);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.Version).HasDefaultValue(1);

            entity.HasIndex(e => new { e.UserId, e.TenantId })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasDatabaseName("UX_tracker_preferences_principal");

            ApplyTenantFilter(entity, fixedTenantId, e => e.TenantId);
        });
    }
    // ═══════════════════ end Story 44-1 tracker region ═══════════════════════

    /// <summary>
    /// Story 36-1 — maps the two per-tenant dimensional analytics fact tables
    /// (<c>analytics_usage_hourly</c> + <c>analytics_usage_daily</c>). The two
    /// share an identical dimension + measure contract; only the time bucket
    /// column differs (<c>Hour</c> vs <c>Day</c>), so a single shared inner
    /// configurator is applied to both — no drift.
    ///
    /// <para>Defaults/precision mirror <c>ConfigurePlatformAnalyticsHourly</c>
    /// (Story 28-10): <c>gen_random_uuid()</c> PK, <c>timestamp with time
    /// zone</c> bucket, <c>HasDefaultValue(0L)</c> counters,
    /// <c>HasPrecision(20,4).HasDefaultValue(0m)</c> costs, <c>now()</c> write
    /// timestamp. <see cref="CostBasis"/> is persisted as lowercase text
    /// (<c>byok</c>/<c>platform</c>). The breakdown index and the NULLS NOT
    /// DISTINCT unique business-key index follow the prompt_overrides /
    /// conventions precedent.</para>
    ///
    /// <para>No <c>TenantId</c> column — tenancy is the schema (Doc 01 §1.4);
    /// <see cref="ApplyTenantFilter{T}"/> is the deliberate no-op (the accessor
    /// lambda returns <c>null</c> because there is nothing to filter on).</para>
    /// </summary>
    private static void ConfigureAnalyticsUsageEntities(
        ModelBuilder modelBuilder, Guid? fixedTenantId)
    {
        // Lowercase byok/platform text — keeps the discriminator uniform in
        // ad-hoc SQL and round-trips identically on InMemory and Npgsql.
        var costBasisConverter = new ValueConverter<CostBasis, string>(
            v => v.ToString().ToLowerInvariant(),
            s => Enum.Parse<CostBasis>(s, true));

        modelBuilder.Entity<AnalyticsUsageHourly>(entity =>
        {
            entity.ToTable("analytics_usage_hourly");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Hour).HasColumnType("timestamp with time zone");
            ConfigureAnalyticsUsageShared(entity, costBasisConverter);

            // Breakdown index (AC6) — "by provider / agent / workflow / cost-basis".
            entity.HasIndex(e => new
                { e.Hour, e.Provider, e.AgentId, e.WorkflowDefinitionId, e.CostBasis })
                .HasDatabaseName("IX_analytics_usage_hourly_breakdown");

            // Idempotent business key (AC7) — full dimension tuple,
            // NULLS NOT DISTINCT so NULL AgentId/WorkflowDefinitionId/RepoId
            // dedupe to one row per bucket (same pattern as prompt_overrides /
            // conventions; PG15+, production PG17).
            entity.HasIndex(e => new
                { e.Hour, e.Provider, e.AgentId, e.WorkflowDefinitionId, e.RepoId, e.CostBasis })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasDatabaseName("UX_analytics_usage_hourly_dims");

            // No TenantId column — schema is the isolation plane. The filter is
            // the established no-op; the accessor returns null (nothing to filter).
            ApplyTenantFilter<AnalyticsUsageHourly>(entity, fixedTenantId, _ => null);
        });

        modelBuilder.Entity<AnalyticsUsageDaily>(entity =>
        {
            entity.ToTable("analytics_usage_daily");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Day).HasColumnType("timestamp with time zone");
            ConfigureAnalyticsUsageShared(entity, costBasisConverter);

            entity.HasIndex(e => new
                { e.Day, e.Provider, e.AgentId, e.WorkflowDefinitionId, e.CostBasis })
                .HasDatabaseName("IX_analytics_usage_daily_breakdown");

            entity.HasIndex(e => new
                { e.Day, e.Provider, e.AgentId, e.WorkflowDefinitionId, e.RepoId, e.CostBasis })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasDatabaseName("UX_analytics_usage_daily_dims");

            ApplyTenantFilter<AnalyticsUsageDaily>(entity, fixedTenantId, _ => null);
        });
    }

    /// <summary>
    /// Story 36-1 — the dimension + measure mapping shared verbatim by
    /// <c>analytics_usage_hourly</c> and <c>analytics_usage_daily</c>. The
    /// bucket column (<c>Hour</c>/<c>Day</c>) and the indexes are configured by
    /// each caller; everything else (dimensions, measures, defaults, CostBasis
    /// conversion) lives here so the two tables can never drift.
    /// </summary>
    private static void ConfigureAnalyticsUsageShared<T>(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> entity,
        ValueConverter<CostBasis, string> costBasisConverter)
        where T : class
    {
        // Dimensions. Provider is nullable (Story 36-2): workflow-lifecycle and
        // agent-dispatch counts carry no provider and bucket under NULL — the
        // UX_*_dims NULLS-NOT-DISTINCT index dedupes those NULL-provider rows.
        entity.Property("Provider").IsRequired(false).HasMaxLength(100);
        entity.Property("AgentId").HasMaxLength(200);
        entity.Property("RepoId").HasMaxLength(400);
        entity.Property(typeof(CostBasis), "CostBasis")
            .HasConversion(costBasisConverter)
            .IsRequired()
            .HasMaxLength(20);

        // Measures — counters default 0L, costs decimal(20,4) default 0m.
        entity.Property("TokensIn").HasDefaultValue(0L);
        entity.Property("TokensOut").HasDefaultValue(0L);
        entity.Property("WorkflowsStarted").HasDefaultValue(0L);
        entity.Property("WorkflowsCompleted").HasDefaultValue(0L);
        entity.Property("WorkflowsFailed").HasDefaultValue(0L);
        entity.Property("AgentDispatches").HasDefaultValue(0L);
        entity.Property("CostUsd").HasPrecision(20, 4).HasDefaultValue(0m);
        entity.Property("PlatformBilledUsd").HasPrecision(20, 4).HasDefaultValue(0m);
        entity.Property("ComputedAt").HasDefaultValueSql("now()");
    }

    /// <summary>
    /// Story 36-2 — maps the per-tenant <c>analytics_projection_checkpoint</c>
    /// resumable cursor. One row per projection <c>Stream</c> (unique). No
    /// <c>TenantId</c> column — the tenant schema is the isolation plane
    /// (Doc 01 §1.4), so <see cref="ApplyTenantFilter{T}"/> is the deliberate
    /// no-op accessor (returns <c>null</c>; nothing to filter).
    /// </summary>
    private static void ConfigureAnalyticsProjectionCheckpoint(
        ModelBuilder modelBuilder, Guid? fixedTenantId)
    {
        modelBuilder.Entity<AnalyticsProjectionCheckpoint>(entity =>
        {
            entity.ToTable("analytics_projection_checkpoint");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Stream).IsRequired().HasMaxLength(50);
            entity.Property(e => e.LastSequenceNumber).HasDefaultValue(0L);
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // One cursor per stream — the upsert target for the atomic advance.
            entity.HasIndex(e => e.Stream)
                .IsUnique()
                .HasDatabaseName("UX_analytics_projection_checkpoint_stream");

            ApplyTenantFilter<AnalyticsProjectionCheckpoint>(entity, fixedTenantId, _ => null);
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
            // gen_random_uuid (pg_catalog builtin, PG13+) instead of uuid-ossp's
            // uuid_generate_v4: extension functions don't resolve under a
            // per-tenant "Search Path=t_<hex>" and the extension dependency is
            // pointless since PG13. Unified-tenancy Phase 1.
            entity.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
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
            entity.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
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
