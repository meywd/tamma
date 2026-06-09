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
            "platform_events",
            "platform_queued_tasks",
            "platform_email_outbox",
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
        }, because: "Story 28-1 PR D (Decision #4) — enumerate every "
            + "CP-resident table; the 11 + 4 mentorship tenant-resident "
            + "entities have moved to TenantDbContext. Story 31-2 adds "
            + "tenant_platform_installations; Story 31-7 adds "
            + "platform_webhook_deliveries. Unified-tenancy Phase 0 adds "
            + "tenant_databases.");
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

    [Test]
    [Ignore("Story 28-1 PR D — Decision #3: keep this test ignored until "
        + "Epic 30 ships pluggable infra backends and an alternative "
        + "routing column. Today the Cranl columns are load-bearing "
        + "(Story 29-10 stopgap) — LruPooledTenantConnectionResolver "
        + "reads tenants.CranlDatabaseUrlEncrypted to route per-request "
        + "DB connections in production. Re-enable when Epic 30 lands "
        + "the alternative routing column.")]
    public void Tenants_Cranl_Columns_Are_Ignored_On_NewContext()
    {
        using var ctx = CreateContext();

        var tenant = ctx.Model.FindEntityType(typeof(Tenant))!;
        var propertyNames = tenant.GetProperties().Select(p => p.Name).ToHashSet();

        // Cranl columns belong to the legacy single-DB topology.
        propertyNames.Should().NotContain("CranlProjectId");
        propertyNames.Should().NotContain("CranlDatabaseId");
        propertyNames.Should().NotContain("CranlAppId");
        propertyNames.Should().NotContain("CranlAppUrl");
        propertyNames.Should().NotContain("CranlDatabaseUrlEncrypted");
        propertyNames.Should().NotContain("CranlRegion");
        propertyNames.Should().NotContain("ProvisioningState");
        propertyNames.Should().NotContain("ProvisioningDetail");
        propertyNames.Should().NotContain("ProvisioningUpdatedAt");
    }
}
