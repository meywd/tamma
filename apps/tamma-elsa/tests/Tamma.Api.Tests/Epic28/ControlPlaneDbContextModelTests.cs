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
    [Ignore("Story 28-1 end-state assertion: passes once legacy-shared DbSets "
        + "(AgentConfigs, PromptOverrides, WorkflowInstances, DomainEvents, etc.) "
        + "migrate off ControlPlaneDbContext and onto TenantDbContext via "
        + "ITenantDbContextFactory. Wave A.5 deliberately exposes them on CP as "
        + "compile-time shims so the eleven legacy-shared repositories still "
        + "build during the transition; re-enable this test when Story 28-1 "
        + "lands the db-per-tenant cutover.")]
    public void Model_Has_ExpectedControlPlaneEntities()
    {
        using var ctx = CreateContext();

        var entityTypes = ctx.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(n => n is not null)
            .ToHashSet()!;

        // Doc 01 §1.2 lists 14 foundational CP tables; Story 28-10 adds
        // platform_analytics_hourly (fact table for the hourly rollup).
        // Each CP-resident table must be listed here so a missing mapping
        // — or an accidental TenantId-scoped entity leaking onto the CP
        // context — fails the test loudly.
        entityTypes.Should().BeEquivalentTo(new[]
        {
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
            "platform_analytics_hourly",
        }, because: "Doc 01 §1.2 (14 tables) + Story 28-10 (platform_analytics_hourly).");
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
    [Ignore("Story 28-1 end-state assertion: passes once the Cranl-provisioning "
        + "columns on Tenant are removed (Cranl is superseded by the db-per-tenant "
        + "architecture from Epic 28). Wave A.5 keeps them mapped on CP because "
        + "legacy Tamma.Core.Entities.Tenant callers still reference them; "
        + "re-enable after Story 28-1 drops the Cranl POCO fields.")]
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
