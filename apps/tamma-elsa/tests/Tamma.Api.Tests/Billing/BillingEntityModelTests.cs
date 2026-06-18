using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-1 (AC1, AC2) — model-shape assertions for the billing foundation
/// entities. Pure design-time model metadata inspection (no Postgres
/// connection). Confirms the two tables map, the column defaults
/// (<c>BillingMode="PlatformProvided"</c>, <c>DefaultCurrency="usd"</c>,
/// <c>TaxStatus="none"</c>), the unique indexes (tenant + slug), the partial
/// unique on Stripe customer id, and the BillingMode CHECK constraint.
/// </summary>
[TestFixture]
public class BillingEntityModelTests
{
    private static ControlPlaneDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql("Host=localhost;Database=cp_test;Username=tamma;Password=tamma")
            .Options);

    [Test]
    public void Billing_Tables_Are_Mapped()
    {
        using var ctx = CreateContext();
        var tables = ctx.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .ToHashSet();

        tables.Should().Contain("billing_customers");
        tables.Should().Contain("billing_plan_prices");
    }

    [Test]
    public void BillingCustomer_Has_Column_Defaults()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(BillingCustomer))!;

        entity.FindProperty(nameof(BillingCustomer.BillingMode))!
            .GetDefaultValue().Should().Be("PlatformProvided");
        entity.FindProperty(nameof(BillingCustomer.DefaultCurrency))!
            .GetDefaultValue().Should().Be("usd");
        entity.FindProperty(nameof(BillingCustomer.TaxStatus))!
            .GetDefaultValue().Should().Be("none");
    }

    [Test]
    public void BillingCustomer_TenantId_Is_Unique()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(BillingCustomer))!;
        var idx = entity.GetIndexes()
            .Single(i => i.GetDatabaseName() == "UX_billing_customers_TenantId");

        idx.IsUnique.Should().BeTrue();
    }

    [Test]
    public void BillingCustomer_StripeCustomerId_Is_PartialUnique()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(BillingCustomer))!;
        var idx = entity.GetIndexes()
            .Single(i => i.GetDatabaseName() == "UX_billing_customers_StripeCustomerId");

        idx.IsUnique.Should().BeTrue();
        idx.GetFilter().Should().Contain("StripeCustomerId");
    }

    [Test]
    public void BillingCustomer_Has_BillingMode_Check_Constraint()
    {
        using var ctx = CreateContext();
        // Check constraints are only carried on the design-time model, not the
        // read-optimized runtime model.
        var model = ctx.GetService<IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(BillingCustomer))!;
        var checks = entity.GetCheckConstraints().Select(c => c.Name).ToList();

        checks.Should().Contain("ck_billing_customers_mode");
    }

    [Test]
    public void BillingPlanPrice_PlanSlug_Is_Unique()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(BillingPlanPrice))!;
        var idx = entity.GetIndexes()
            .Single(i => i.GetDatabaseName() == "UX_billing_plan_prices_PlanSlug");

        idx.IsUnique.Should().BeTrue();
    }
}
