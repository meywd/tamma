using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-2 model-shape tests for <see cref="ControlPlaneDbContext"/>.
/// Asserts the new context maps the 14 CP-resident tables onto a clean
/// model graph with no stray <c>TenantId</c> filters and the expected
/// shadow properties for the Epic 28 columns on <c>tenants</c>.
///
/// <para>Pure model assertions — no Postgres connection required. Uses
/// the relational metadata API to inspect the compiled model. The
/// connection string in <see cref="DbContextOptionsBuilder"/> is never
/// connected to.</para>
/// </summary>
[TestFixture]
public class ControlPlaneDbContextModelTests
{
    private static ControlPlaneDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql("Host=localhost;Database=cp_test;Username=tamma;Password=tamma")
            .Options;

        return new ControlPlaneDbContext(options);
    }

    [Test]
    public void Model_Has_ExpectedControlPlaneEntities()
    {
        using var ctx = CreateContext();

        var entityTypes = ctx.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(n => n is not null)
            .ToHashSet()!;

        // Story 28-1 PR D: enumerate every CP-resident table per Decision
        // #4. The base 14 (Doc 01 §1.2) + alerts (5) + analytics +
        // platform_api_key_index + kek_rotations + admin_impersonations +
        // platform_bootstrap. The 11 + 4 mentorship tenant-resident
        // entities have left CP entirely and now live exclusively on
        // TenantDbContext.
        entityTypes.Should().BeEquivalentTo(new[]
        {
            // Doc 01 §1.2 — foundational CP tables.
            "users",
            "refresh_tokens",
            "password_reset_tokens",
            "tenants",
            "tenant_memberships",
            "user_invites",
            "api_keys",
            "github_installations",
            "github_installation_repos",
            "github_webhook_deliveries",
            "plans",
            // Story 34-1 — typed price-book children of plans.
            "plan_features",
            "plan_entitlements",
            "plan_prices",
            "platform_events",
            "platform_queued_tasks",
            "platform_email_outbox",
            // Story 38-3 — control-plane Slack notification outbox (Class D
            // step-mediation). CP-resident so it delivers regardless of tenant-DB
            // routing (same rationale as platform_email_outbox).
            "slack_outbox",
            // Story 28-7 — bearer-token routing index.
            "platform_api_key_index",
            // Story 28-10 — hourly analytics fact table.
            "platform_analytics_hourly",
            // Story 5.6 + 1.5-37 (Wave C.1+C.2) — alert system.
            "alerts",
            "alert_channels",
            "alert_delivery_attempts",
            "alert_rules",
            "alert_evaluator_cursor",
            // R2-H14 — KEK rotation audit table.
            "kek_rotations",
            // Story 28-R2/B — platform-admin impersonation audit.
            "admin_impersonations",
            // PF-S9 — bootstrap superadmin sentinel (single-row).
            "platform_bootstrap",
            // Story 31-2 — generalised per-(tenant, platform_kind)
            // installation registry. Lives on CP because cross-tenant
            // routing (webhook arrives with no tenant context) needs
            // cross-tenant lookups.
            "tenant_platform_installations",
            // Story 31-7 — cross-platform webhook delivery idempotency
            // journal. Generalises github_webhook_deliveries; the
            // older table stays for the deprecation window but new
            // deliveries land here for every PlatformKind.
            "platform_webhook_deliveries",
            // Unified-tenancy Phase 0 (plan 2026-06-09) — registry of
            // shared-pool and dedicated DB instances. Each tenant row
            // references one of these via DatabaseId.
            "tenant_databases",
            // Story 32-1 — first-class agent entities (Epic 32 foundation).
            // CP-resident: public agents are shared cross-tenant and
            // visibility/identity is a control-plane concern. Definition-only
            // (no performance columns — those stay tenant-scoped).
            "agents",
            "agent_versions",
            // Story 32-2 — role→agent selections. Mapped on BOTH contexts (CP
            // build holds single-user user-keyed rows; tenant build holds SaaS
            // tenant-keyed rows). Same dual-resident pattern as audit_records.
            "agent_role_selections",
            // Story 32-16 — per-tenant agent/persona enablement (catalog
            // membership). CP-resident in BOTH modes (gates the CP public agent
            // catalog; keyed by tenant id / user id, not per t_<hex>) — so it is
            // mapped ONLY on the CP context, not the tenant context.
            "tenant_agent_enablements",
            // Story 35-1 — Epic 35 billing foundation. CP-resident: the
            // tenant→Stripe customer mapping (keyed by tenant) + the
            // slug→Stripe-ids catalog (platform-global). Definition/binding
            // only — usage/metering data is owned by later Epic 35 stories.
            "billing_customers",
            "billing_plan_prices",
            // Story 35-5 — Stripe webhook dedup + audit journal. CP-resident:
            // billing is a cross-cutting platform concern; the webhook arrives
            // with no tenant context (tenant resolved from the Stripe customer).
            "billing_webhook_events",
            // Story 35-4 — control-plane mirror of a tenant's Stripe subscription.
            // CP-resident (billing is cross-cutting, keyed by tenant); Story 35-6
            // reads PlanSlug + Seats as the single quota source. At most one
            // non-terminal row per tenant (partial-unique).
            "billing_subscriptions",
            // Story 37-1 — curated audit-record read-model + the per-(projector,
            // tenant) cursor. audit_records is mapped on BOTH contexts (the CP
            // build materializes platform-scope + single-user rows); the cursor
            // is CP-only (the projector resumes per-tenant domain streams from
            // CP-resident high-water marks).
            "audit_records",
            "audit_projector_cursor",
            // Story 34-11 — provider COST price-book. Platform-global (no
            // TenantId): cost is the provider's published rate, identical for
            // every tenant. Promotes the frozen ProviderPricingService rate
            // sheet behind the unchanged IProviderPricingService seam.
            "providers",
            "provider_model_prices",
            // Story 34-5 — cost→price markup policy (global/plan/provider
            // scope, versioned). Platform-owned (PlatformOwnerAccess); CP-resident
            // because margin is a platform-global pricing concern, not per-tenant.
            "margin_policies",
            // Story 34-4 — per-tenant, version-pinned plan assignments (source of
            // truth for "what plan version is this tenant on right now"). CP-resident
            // (keyed by tenant, alongside the plans catalog). One active row per
            // tenant (partial unique index).
            "tenant_plan_assignments",
        }, because: "Story 28-1 PR D (Decision #4) — enumerate every "
            + "CP-resident table; the 11 + 4 mentorship tenant-resident "
            + "entities have moved to TenantDbContext. Story 31-2 adds "
            + "tenant_platform_installations; Story 31-7 adds "
            + "platform_webhook_deliveries. Unified-tenancy Phase 0 adds "
            + "tenant_databases. Story 32-1 adds agents + agent_versions. "
            + "Story 32-2 adds agent_role_selections. "
            + "Story 32-16 adds tenant_agent_enablements. "
            + "Story 34-1 adds plan_features + plan_entitlements + plan_prices. "
            + "Story 35-1 adds billing_customers + billing_plan_prices. "
            + "Story 35-5 adds billing_webhook_events. "
            + "Story 35-4 adds billing_subscriptions. "
            + "Story 37-1 adds audit_records + audit_projector_cursor. "
            + "Story 34-11 adds providers + provider_model_prices. "
            + "Story 34-5 adds margin_policies. "
            + "Story 34-4 adds tenant_plan_assignments. "
            + "Story 38-3 adds slack_outbox.");
    }

    // ── Story 34-11 — provider cost price-book model shape ──

    [Test]
    public void Provider_And_ProviderModelPrice_Have_No_TenantOrUser_Column()
    {
        using var ctx = CreateContext();

        // AC3 — cost is the provider's published rate, GLOBAL in both modes.
        // Neither cost entity may carry a tenant/user scope column.
        var forbidden = new[] { "TenantId", "UserId", "OwnerTenantId", "OwnerUserId" };

        var providerProps = ctx.Model.FindEntityType(typeof(Provider))!
            .GetProperties().Select(p => p.Name).ToHashSet();
        var priceProps = ctx.Model.FindEntityType(typeof(ProviderModelPrice))!
            .GetProperties().Select(p => p.Name).ToHashSet();

        providerProps.Should().NotIntersectWith(forbidden,
            "Story 34-11 AC3 — the cost identity is platform-global, never tenant-scoped");
        priceProps.Should().NotIntersectWith(forbidden,
            "Story 34-11 AC3 — the cost rate is platform-global, never tenant-scoped");
    }

    [Test]
    public void Provider_Key_Is_Unique()
    {
        using var ctx = CreateContext();

        var provider = ctx.Model.FindEntityType(typeof(Provider))!;
        var keyIndex = provider.GetIndexes()
            .FirstOrDefault(i => i.Properties.Count == 1 && i.Properties[0].Name == "Key");

        keyIndex.Should().NotBeNull("the canonical provider key is the natural key");
        keyIndex!.IsUnique.Should().BeTrue();
    }

    [Test]
    public void Provider_Has_AuthModel_And_Status_CheckConstraints()
    {
        using var ctx = CreateContext();

        var designModel = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
            .GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>(ctx).Model;
        var provider = designModel.FindEntityType(typeof(Provider))!;
        var checks = provider.GetCheckConstraints().Select(c => c.Name).ToHashSet();

        checks.Should().Contain("ck_providers_auth_model");
        checks.Should().Contain("ck_providers_status");
    }

    [Test]
    public void ProviderModelPrice_Has_OneActivePerModel_PartialUniqueIndex()
    {
        using var ctx = CreateContext();

        var price = ctx.Model.FindEntityType(typeof(ProviderModelPrice))!;
        var oneActive = price.GetIndexes()
            .Single(i => i.GetDatabaseName() == "UX_provider_model_prices_OneActivePerModel");

        oneActive.IsUnique.Should().BeTrue("AC4 — exactly one active price per (ProviderKey, Model)");
        oneActive.GetFilter().Should().Be("\"Status\" = 'active'");
        oneActive.Properties.Select(p => p.Name).Should().Equal("ProviderKey", "Model");
    }

    [Test]
    public void ProviderModelPrice_Has_Window_Index_And_RestrictFk()
    {
        using var ctx = CreateContext();

        var price = ctx.Model.FindEntityType(typeof(ProviderModelPrice))!;

        var window = price.GetIndexes()
            .Single(i => i.GetDatabaseName() == "IX_provider_model_prices_Window");
        window.Properties.Select(p => p.Name)
            .Should().Equal(new[] { "ProviderKey", "Model", "EffectiveFrom" },
                because: "AC5 — EffectiveFrom-windowed resolution lookup");

        var fk = price.GetForeignKeys()
            .Single(k => k.PrincipalEntityType.ClrType == typeof(Provider));
        fk.DeleteBehavior.Should().Be(DeleteBehavior.Restrict,
            "a referenced cost identity must never be hard-deleted out from under its prices");
    }

    [Test]
    public void ProviderModelPrice_Has_Status_And_Source_CheckConstraints()
    {
        using var ctx = CreateContext();

        var designModel = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
            .GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>(ctx).Model;
        var price = designModel.FindEntityType(typeof(ProviderModelPrice))!;
        var checks = price.GetCheckConstraints().Select(c => c.Name).ToHashSet();

        checks.Should().Contain("ck_provider_model_prices_status");
        checks.Should().Contain("ck_provider_model_prices_source");
    }

    // ── Story 32-1 — agent entity model shape ──

    [Test]
    public void Agents_Has_VisibilityOwnership_CheckConstraint()
    {
        using var ctx = CreateContext();

        // CHECK constraints are stripped from the runtime read-optimized model;
        // they only live on the design-time model.
        var designModel = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
            .GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>(ctx).Model;
        var agent = designModel.FindEntityType(typeof(Agent))!;
        var check = agent.GetCheckConstraints()
            .FirstOrDefault(c => c.Name == "ck_agents_visibility_ownership");

        check.Should().NotBeNull(
            "Story 32-1 ties Visibility to the owner columns via a CHECK "
            + "(mirrors ck_prompt_overrides_principal_xor)");
        // Public = 0 ⇒ no owner; Private = 1 ⇒ exactly one owner.
        check!.Sql.Should().Contain("\"Visibility\" = 0");
        check.Sql.Should().Contain("\"Visibility\" = 1");
    }

    [Test]
    public void Agents_Has_PublicAndPrivate_PartialUniqueIndexes()
    {
        using var ctx = CreateContext();

        var agent = ctx.Model.FindEntityType(typeof(Agent))!;
        var indexes = agent.GetIndexes().ToList();

        // Story 32-15 — the public unique index drops Role: a persona is
        // cross-role, so public handles are globally unique on (Name) alone.
        var publicName = indexes.Single(i =>
            i.GetDatabaseName() == "IX_agents_public_name");
        publicName.IsUnique.Should().BeTrue();
        publicName.GetFilter().Should().Be("\"Visibility\" = 0");
        publicName.Properties.Select(p => p.Name)
            .Should().Equal("Name");

        // The old (Name, Role) public index is GONE (Story 32-15).
        indexes.Should().NotContain(i =>
            i.GetDatabaseName() == "IX_agents_public_name_role");

        var privateTenant = indexes.Single(i =>
            i.GetDatabaseName() == "IX_agents_private_tenant_name");
        privateTenant.IsUnique.Should().BeTrue();
        privateTenant.GetFilter().Should()
            .Be("\"Visibility\" = 1 AND \"OwnerTenantId\" IS NOT NULL");

        var privateUser = indexes.Single(i =>
            i.GetDatabaseName() == "IX_agents_private_user_name");
        privateUser.IsUnique.Should().BeTrue();
        privateUser.GetFilter().Should()
            .Be("\"Visibility\" = 1 AND \"OwnerUserId\" IS NOT NULL");
    }

    [Test]
    public void Agents_Role_Is_Nullable()
    {
        using var ctx = CreateContext();

        // Story 32-15 — Role becomes nullable: public personas are cross-role
        // (Role = NULL). The strict model contract must reflect the nullable
        // column so a regression to required is caught.
        var roleProp = ctx.Model.FindEntityType(typeof(Agent))!
            .FindProperty(nameof(Agent.Role))!;
        roleProp.IsNullable.Should().BeTrue(
            "Story 32-15 makes Agent.Role nullable for cross-role public personas");
        roleProp.GetMaxLength().Should().Be(64, "the HasMaxLength(64) cap is kept");
    }

    [Test]
    public void AgentVersions_Has_AgentVersion_UniqueIndex_And_RestrictFk()
    {
        using var ctx = CreateContext();

        var version = ctx.Model.FindEntityType(typeof(AgentVersion))!;

        var unique = version.GetIndexes()
            .Single(i => i.GetDatabaseName() == "IX_agent_versions_agent_version");
        unique.IsUnique.Should().BeTrue(
            "monotonic, non-duplicated versions per agent + the concurrency guard");
        unique.Properties.Select(p => p.Name).Should().Equal("AgentId", "Version");

        var fk = version.GetForeignKeys()
            .Single(k => k.PrincipalEntityType.ClrType == typeof(Agent));
        fk.DeleteBehavior.Should().Be(DeleteBehavior.Restrict,
            "versions are immutable audit history — archive, never cascade-delete");
    }

    [Test]
    public void Agents_And_AgentVersions_HaveNo_PerformanceColumns()
    {
        using var ctx = CreateContext();

        // Story 32-1 non-goal: performance/action data is ALWAYS tenant-scoped
        // (later Epic 32 stories). These CP entities are definition-only.
        var forbidden = new[]
        {
            "SuccessRate", "AvgIterations", "BugCount", "CostUsd", "Latency",
            "TokensIn", "TokensOut", "SuccessCount", "FailureCount",
        };

        var agentProps = ctx.Model.FindEntityType(typeof(Agent))!
            .GetProperties().Select(p => p.Name).ToHashSet();
        var versionProps = ctx.Model.FindEntityType(typeof(AgentVersion))!
            .GetProperties().Select(p => p.Name).ToHashSet();

        agentProps.Should().NotIntersectWith(forbidden);
        versionProps.Should().NotIntersectWith(forbidden);
    }

    // ── Story 36-1 — CP analytics table is untouched; the tenant-only
    //    analytics_usage_* fact tables never leak onto the control plane. ──

    [Test]
    public void Cp_Still_Maps_PlatformAnalyticsHourly_AndNot_AnalyticsUsageTables()
    {
        using var ctx = CreateContext();

        var tables = ctx.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(n => n is not null)
            .ToHashSet()!;

        // Story 28-10's owner-only fleet-wide fact table stays put.
        tables.Should().Contain("platform_analytics_hourly",
            "Story 36-1 leaves the control-plane analytics table entirely intact");

        // Story 36-1's per-tenant fact tables are tenant-resident only — they
        // must NOT appear on the control-plane model graph.
        tables.Should().NotContain("analytics_usage_hourly",
            "analytics_usage_* are per-tenant — they live only on TenantDbContext");
        tables.Should().NotContain("analytics_usage_daily",
            "analytics_usage_* are per-tenant — they live only on TenantDbContext");

        // And the CP context never even knows the CLR types.
        ctx.Model.FindEntityType(typeof(AnalyticsUsageHourly)).Should().BeNull();
        ctx.Model.FindEntityType(typeof(AnalyticsUsageDaily)).Should().BeNull();
    }

    [Test]
    public void Tenant_Carries_ShadowProperties_ForEpic28Columns()
    {
        using var ctx = CreateContext();

        var tenant = ctx.Model.FindEntityType(typeof(Tenant));
        tenant.Should().NotBeNull("the Tenant entity must be mapped on the CP context");

        var shadowNames = tenant!.GetProperties()
            .Where(p => p.IsShadowProperty())
            .Select(p => p.Name)
            .ToHashSet();

        shadowNames.Should().Contain("PlanId");
        shadowNames.Should().Contain("Status");
        shadowNames.Should().Contain("EncryptedConnectionString");
        shadowNames.Should().Contain("KekVersion");
        shadowNames.Should().Contain("FailureReason");
        shadowNames.Should().Contain("DeleteRequestedAt");
    }

    [Test]
    public void Tenant_Carries_V2ProviderShadowColumns_FromStory303()
    {
        using var ctx = CreateContext();

        var tenant = ctx.Model.FindEntityType(typeof(Tenant))!;
        var shadow = tenant.GetProperties()
            .Where(p => p.IsShadowProperty())
            .ToDictionary(p => p.Name, p => p);

        shadow.Should().ContainKey("ProviderKey",
            because: "Story 30-3 added tenants.provider_key for V2 dispatch routing");
        shadow.Should().ContainKey("ProviderResourceIds",
            because: "Story 30-3 added tenants.provider_resource_ids JSONB for V2 resource ids");

        shadow["ProviderKey"].IsNullable.Should().BeTrue(
            because: "shared-infra tenants have no provider; column must accept NULL");
        shadow["ProviderResourceIds"].IsNullable.Should().BeTrue(
            because: "JSONB stays NULL until the provider populates resource ids");

        shadow["ProviderResourceIds"].GetColumnType().Should().Be("jsonb");
    }

    [Test]
    public void ApiKey_Carries_RateLimitRpm_ShadowProperty()
    {
        using var ctx = CreateContext();

        var apiKey = ctx.Model.FindEntityType(typeof(ApiKey));
        apiKey.Should().NotBeNull();

        var rateLimit = apiKey!.GetProperties()
            .FirstOrDefault(p => p.Name == "RateLimitRpm");

        rateLimit.Should().NotBeNull("Story 28-7 requires a rate-limit shadow column on api_keys");
        rateLimit!.IsShadowProperty().Should().BeTrue();
    }

    [Test]
    public void Tenant_Has_Status_And_PlanId_Indexes()
    {
        using var ctx = CreateContext();

        var tenant = ctx.Model.FindEntityType(typeof(Tenant))!;
        var indexProps = tenant.GetIndexes()
            .Select(i => string.Join(",", i.Properties.Select(p => p.Name)))
            .ToHashSet();

        indexProps.Should().Contain("Status");
        indexProps.Should().Contain("PlanId");
    }

    [Test]
    public void ApiKey_Has_KeyPrefix_Index_For_Story28_7_Routing()
    {
        using var ctx = CreateContext();

        var apiKey = ctx.Model.FindEntityType(typeof(ApiKey))!;
        var hasKeyPrefixIndex = apiKey.GetIndexes()
            .Any(i => i.Properties.Count == 1 && i.Properties[0].Name == "KeyPrefix");

        hasKeyPrefixIndex.Should().BeTrue("Story 28-7 routes Bearer tokens by prefix and needs an index lookup");
    }

    [Test]
    public void ApiKey_Has_RevokedAt_PartialIndex_For_ActiveOnly_Lookups()
    {
        using var ctx = CreateContext();

        var apiKey = ctx.Model.FindEntityType(typeof(ApiKey))!;
        var revokedAtIndex = apiKey.GetIndexes()
            .FirstOrDefault(i => i.Properties.Count == 1 && i.Properties[0].Name == "RevokedAt");

        revokedAtIndex.Should().NotBeNull();
        revokedAtIndex!.GetFilter().Should().Be("\"RevokedAt\" IS NULL",
            because: "active-key lookups should hit a partial index, not scan revoked rows");
    }

    [Test]
    public void Tenants_Has_PlanId_FK_To_Plans_Restrict()
    {
        using var ctx = CreateContext();

        var tenant = ctx.Model.FindEntityType(typeof(Tenant))!;
        var fk = tenant.GetForeignKeys()
            .FirstOrDefault(k => k.PrincipalEntityType.ClrType == typeof(Plan));

        fk.Should().NotBeNull("a tenant must reference a plan via PlanId");
        fk!.DeleteBehavior.Should().Be(DeleteBehavior.Restrict,
            because: "deleting a plan that tenants still reference must fail");
    }

    [Test]
    public void Plans_Slug_Is_Unique()
    {
        using var ctx = CreateContext();

        var plan = ctx.Model.FindEntityType(typeof(Plan))!;
        var slugIndex = plan.GetIndexes()
            .FirstOrDefault(i => i.Properties.Count == 1 && i.Properties[0].Name == "Slug");

        slugIndex.Should().NotBeNull();
        slugIndex!.IsUnique.Should().BeTrue();
    }

    // Epic 30 Phase B (Task B3) — SHIPPED. The six dedicated Cranl columns
    // were dropped (migration DropCranlTenantColumns); their walk/resume state
    // moved into the tenants.provider_resource_ids JSONB (CranlResourceIds) and
    // the encrypted admin DB URL onto the tenant_databases pool row. B1 already
    // removed the LruPooledTenantConnectionResolver's dependency on
    // CranlDatabaseUrlEncrypted (every tenant now routes through the unified
    // EncryptedConnectionString envelope), so the stale rationale that kept this
    // test [Ignore]d no longer holds — it is re-enabled here.
    //
    // Deviation from the original Story 28-1 test: the three Provisioning*
    // assertions were DELETED. B3 KEEPS ProvisioningState/ProvisioningDetail/
    // ProvisioningUpdatedAt (the saga state machine) — only the six Cranl
    // columns were dropped, so asserting the Provisioning* columns are absent
    // would be wrong.
    [Test]
    public void Tenants_Cranl_Columns_Are_Dropped_From_Model()
    {
        using var ctx = CreateContext();

        var tenant = ctx.Model.FindEntityType(typeof(Tenant))!;
        var propertyNames = tenant.GetProperties().Select(p => p.Name).ToHashSet();

        // Dropped in Epic 30 Phase B (Task B3).
        propertyNames.Should().NotContain("CranlProjectId");
        propertyNames.Should().NotContain("CranlDatabaseId");
        propertyNames.Should().NotContain("CranlAppId");
        propertyNames.Should().NotContain("CranlAppUrl");
        propertyNames.Should().NotContain("CranlDatabaseUrlEncrypted");
        propertyNames.Should().NotContain("CranlRegion");

        // KEPT by B3 — the saga state machine stays on the tenants row.
        propertyNames.Should().Contain("ProvisioningState");
        propertyNames.Should().Contain("ProvisioningDetail");
        propertyNames.Should().Contain("ProvisioningUpdatedAt");
    }
}
