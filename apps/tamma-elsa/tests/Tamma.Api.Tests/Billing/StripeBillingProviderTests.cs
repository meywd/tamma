using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Stripe;
using Tamma.Api.Services.Billing;
using Tamma.Core.Billing;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-1 (AC6, AC12, AC13) — <see cref="StripeBillingProvider"/> creates a
/// Stripe customer with the deterministic idempotency key, persists the
/// <see cref="BillingCustomer"/> row, emits <c>BILLING.CUSTOMER.CREATED</c>, and
/// short-circuits a duplicate-tenant create (no second Stripe call). Stripe is
/// mocked at the service-interface boundary — no live Stripe.
/// </summary>
[TestFixture]
public class StripeBillingProviderTests
{
    private static ControlPlaneDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    private static (StripeBillingProvider Provider, Mock<CustomerService> Customers,
        Mock<IEventRepository> Events, ControlPlaneDbContext Db) Build(string dbName)
    {
        var db = NewDb(dbName);

        var customers = new Mock<CustomerService>(MockBehavior.Strict);
        var stripeServices = new Mock<IStripeServices>();
        stripeServices.SetupGet(s => s.Customers).Returns(customers.Object);

        var factory = new Mock<IStripeServicesFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(stripeServices.Object);

        var events = new Mock<IEventRepository>();
        events.Setup(e => e.AppendAsync(It.IsAny<DomainEvent>()))
            .ReturnsAsync((DomainEvent d) => d);

        var provider = new StripeBillingProvider(
            factory.Object, db, events.Object,
            Options.Create(new BillingOptions()),
            NullLogger<StripeBillingProvider>.Instance);

        return (provider, customers, events, db);
    }

    [Test]
    public async Task CreateCustomerAsync_Creates_Stripe_Customer_With_Deterministic_Key()
    {
        var (provider, customers, events, db) =
            Build(nameof(CreateCustomerAsync_Creates_Stripe_Customer_With_Deterministic_Key));
        var tenantId = Guid.NewGuid();
        var expectedKey = StripeBillingProvider.CustomerIdempotencyKey(tenantId);

        RequestOptions? capturedOptions = null;
        customers.Setup(c => c.CreateAsync(
                It.IsAny<CustomerCreateOptions>(),
                It.IsAny<RequestOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<CustomerCreateOptions, RequestOptions, CancellationToken>(
                (_, ro, _) => capturedOptions = ro)
            .ReturnsAsync(new Customer { Id = "cus_test_123" });

        var row = await provider.CreateCustomerAsync(
            tenantId,
            new CustomerDescriptor("Acme", "acme", "owner@example.com", BillingMode.PlatformProvided));

        capturedOptions.Should().NotBeNull();
        capturedOptions!.IdempotencyKey.Should().Be(expectedKey);
        row.StripeCustomerId.Should().Be("cus_test_123");
        row.TenantId.Should().Be(tenantId);
        row.BillingMode.Should().Be("PlatformProvided");

        var persisted = await db.BillingCustomers.SingleAsync();
        persisted.StripeCustomerId.Should().Be("cus_test_123");

        events.Verify(e => e.AppendAsync(
            It.Is<DomainEvent>(d => d.Type == BillingEvents.CustomerCreatedType
                                    && d.TenantId == tenantId)),
            Times.Once);
    }

    [Test]
    public async Task CreateCustomerAsync_Records_Byok_Mode()
    {
        var (provider, customers, _, db) =
            Build(nameof(CreateCustomerAsync_Records_Byok_Mode));
        customers.Setup(c => c.CreateAsync(
                It.IsAny<CustomerCreateOptions>(), It.IsAny<RequestOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Customer { Id = "cus_byok" });

        var row = await provider.CreateCustomerAsync(
            Guid.NewGuid(),
            new CustomerDescriptor("Beta", "beta", null, BillingMode.Byok));

        row.BillingMode.Should().Be("Byok");
        (await db.BillingCustomers.SingleAsync()).BillingMode.Should().Be("Byok");
    }

    [Test]
    public async Task CreateCustomerAsync_Is_Idempotent_For_Same_Tenant()
    {
        var (provider, customers, _, db) =
            Build(nameof(CreateCustomerAsync_Is_Idempotent_For_Same_Tenant));
        var tenantId = Guid.NewGuid();
        customers.Setup(c => c.CreateAsync(
                It.IsAny<CustomerCreateOptions>(), It.IsAny<RequestOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Customer { Id = "cus_once" });

        var first = await provider.CreateCustomerAsync(
            tenantId, new CustomerDescriptor("Acme", "acme", null, BillingMode.PlatformProvided));
        var second = await provider.CreateCustomerAsync(
            tenantId, new CustomerDescriptor("Acme", "acme", null, BillingMode.PlatformProvided));

        first.Id.Should().Be(second.Id, "the same tenant resolves the existing row");
        (await db.BillingCustomers.CountAsync()).Should().Be(1, "one BillingCustomer per tenant");
        customers.Verify(c => c.CreateAsync(
                It.IsAny<CustomerCreateOptions>(), It.IsAny<RequestOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Once, "the second create makes NO Stripe call");
    }
}
