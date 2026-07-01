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
            entity.Property(e => e.PayloadJson)
                .HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            // Reserved for Story 37-2 — nullable, never populated here.
            entity.Property(e => e.RecordHash).HasMaxLength(128);
            entity.Property(e => e.PrevRecordHash).HasMaxLength(128);

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

        // ── Analytics usage fact tables (Story 36-1) ──
        // Per-tenant dimensional usage/cost/performance store. Schema-only here
        // (Story 36-2 owns population). Mirrors ConfigurePlatformAnalyticsHourly
        // (Story 28-10) for defaults/precision; uses the prompt_overrides /
        // conventions NULLS NOT DISTINCT pattern for the idempotent business key.
        ConfigureAnalyticsUsageEntities(modelBuilder, fixedTenantId);

        // ── Curated audit records (Story 37-1) ──
        // Tenant-scope curated audit trail materialized from the tenant
        // domain_events stream. The SAME table shape is also configured on the
        // CP context for platform-scope rows — one physical schema, two homes.
        ConfigureAuditEntities(modelBuilder, fixedTenantId);

        // ── Agent role selections (Story 32-2) ──
        // SaaS tenant-keyed role→agent selections. The SAME table shape is also
        // configured on the CP context for single-user user-keyed rows — one
        // physical schema, two homes (mirrors audit_records / prompt_overrides).
        ConfigureAgentRoleSelections(modelBuilder, fixedTenantId);
    }

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
        // Dimensions.
        entity.Property("Provider").IsRequired().HasMaxLength(100);
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
