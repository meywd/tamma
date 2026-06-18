using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-1 — model-shape assertions for the price-book entities. Pure model
/// metadata inspection (no Postgres connection). Confirms the three DbSets
/// resolve, the <c>MetricKey</c> is value-converted to text (never the ordinal),
/// the partial unique "one active per slug" index exists, and the CHECK
/// constraints are configured on the design-time model.
/// </summary>
[TestFixture]
public class PlanCatalogModelTests
{
    private static ControlPlaneDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql("Host=localhost;Database=cp_test;Username=tamma;Password=tamma")
            .Options);

    [Test]
    public void PriceBook_Tables_Are_Mapped()
    {
        using var ctx = CreateContext();
        var tables = ctx.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .ToHashSet();

        tables.Should().Contain("plan_features");
        tables.Should().Contain("plan_entitlements");
        tables.Should().Contain("plan_prices");
    }

    [Test]
    public void MetricKey_Is_Persisted_As_Text_Via_Converter()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(PlanEntitlement))!;
        var prop = entity.FindProperty(nameof(PlanEntitlement.MetricKey))!;

        prop.GetValueConverter().Should().NotBeNull(
            "MetricKey must round-trip through the snake_case value converter, "
            + "never the unstable numeric ordinal");
        prop.GetColumnType().Should().Be("text");
    }

    [Test]
    public void Plans_Has_OneActivePerSlug_PartialUniqueIndex()
    {
        using var ctx = CreateContext();
        var plan = ctx.Model.FindEntityType(typeof(Plan))!;
        var index = plan.GetIndexes()
            .Single(i => i.GetDatabaseName() == "UX_plans_OneActivePerSlug");

        index.IsUnique.Should().BeTrue();
        index.GetFilter().Should().Be("\"Status\" = 'active'");
    }

    [Test]
    public void Plans_Has_SlugVersion_UniqueIndex()
    {
        using var ctx = CreateContext();
        var plan = ctx.Model.FindEntityType(typeof(Plan))!;
        var index = plan.GetIndexes()
            .Single(i => i.GetDatabaseName() == "UX_plans_Slug_Version");

        index.IsUnique.Should().BeTrue();
        index.Properties.Select(p => p.Name).Should().Equal("Slug", "Version");
    }

    [Test]
    public void Plans_Has_Status_And_BillingInterval_CheckConstraints()
    {
        using var ctx = CreateContext();
        var designModel = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
            .GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>(ctx).Model;
        var plan = designModel.FindEntityType(typeof(Plan))!;
        var checks = plan.GetCheckConstraints().Select(c => c.Name).ToList();

        checks.Should().Contain("ck_plans_status");
        checks.Should().Contain("ck_plans_billing_interval");
    }

    [Test]
    public void Children_FK_To_Plans_Is_Restrict()
    {
        using var ctx = CreateContext();

        foreach (var type in new[] { typeof(PlanFeature), typeof(PlanEntitlement), typeof(PlanPrice) })
        {
            var entity = ctx.Model.FindEntityType(type)!;
            var fk = entity.GetForeignKeys()
                .Single(f => f.PrincipalEntityType.ClrType == typeof(Plan));
            fk.DeleteBehavior.Should().Be(DeleteBehavior.Restrict,
                $"{type.Name} → plans must RESTRICT (a referenced version can't be hard-deleted)");
        }
    }
}
