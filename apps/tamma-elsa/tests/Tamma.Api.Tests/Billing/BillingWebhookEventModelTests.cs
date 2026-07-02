using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-5 (AC4) — model-shape assertions for <see cref="BillingWebhookEvent"/>.
/// Pure design-time metadata inspection (no Postgres). Confirms the table maps,
/// the dedup unique index on <c>StripeEventId</c>, the status CHECK constraint,
/// and the column defaults.
/// </summary>
[TestFixture]
public class BillingWebhookEventModelTests
{
    private static ControlPlaneDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql("Host=localhost;Database=cp_test;Username=tamma;Password=tamma")
            .Options);

    [Test]
    public void Table_Is_Mapped()
    {
        using var ctx = CreateContext();
        ctx.Model.GetEntityTypes().Select(t => t.GetTableName())
            .Should().Contain("billing_webhook_events");
    }

    [Test]
    public void StripeEventId_Is_Unique_Dedup_Index()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(BillingWebhookEvent))!;
        var idx = entity.GetIndexes()
            .Single(i => i.GetDatabaseName() == "UX_billing_webhook_events_StripeEventId");

        idx.IsUnique.Should().BeTrue("at-least-once redelivery is deduped on the unique index (AC5)");
        idx.Properties.Select(p => p.Name).Should().Equal(nameof(BillingWebhookEvent.StripeEventId));
    }

    [Test]
    public void Has_Status_Check_Constraint()
    {
        using var ctx = CreateContext();
        var model = ctx.GetService<IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(BillingWebhookEvent))!;
        entity.GetCheckConstraints().Select(c => c.Name)
            .Should().Contain("ck_billing_webhook_events_status");
    }

    [Test]
    public void Has_Column_Defaults()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(BillingWebhookEvent))!;

        entity.FindProperty(nameof(BillingWebhookEvent.Status))!
            .GetDefaultValue().Should().Be("received");
        entity.FindProperty(nameof(BillingWebhookEvent.Attempts))!
            .GetDefaultValue().Should().Be(0);
    }
}
