using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Billing;
using Tamma.Api.Services.Billing.Tasks;
using Tamma.Core.Billing;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-1 (AC6, AC9, AC14) — the non-blocking tenant-create billing hook
/// (shared by <c>OrgEndpoints.CreateOrg</c> and <c>AuthEndpoints.Register</c>):
/// happy path creates the customer; a Stripe failure enqueues a retry task and
/// does NOT throw (tenant creation is never blocked); single-user is a complete
/// no-op (no Stripe call, no enqueue).
/// </summary>
[TestFixture]
public class BillingTenantCreateHookTests
{
    private static readonly ILoggerFactory LoggerFactory = NullLoggerFactory.Instance;

    private static Tenant NewTenant() =>
        new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };

    [Test]
    public async Task SingleUser_Is_Complete_NoOp()
    {
        var billing = new NullBillingProvider(); // IsEnabled == false
        var tasks = new Mock<IPlatformQueuedTaskRepository>(MockBehavior.Strict);

        await BillingTenantCreateHook.RunAsync(
            billing, tasks.Object, LoggerFactory, NewTenant(), "owner@example.com");

        // No enqueue, no Stripe — strict mock would throw on any call.
        tasks.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Saas_HappyPath_Creates_Customer_No_Enqueue()
    {
        var tenant = NewTenant();
        var billing = new Mock<IBillingProvider>();
        billing.SetupGet(b => b.IsEnabled).Returns(true);
        billing.Setup(b => b.CreateCustomerAsync(
                tenant.Id, It.IsAny<CustomerDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingCustomer { TenantId = tenant.Id, StripeCustomerId = "cus_ok" });

        var tasks = new Mock<IPlatformQueuedTaskRepository>();

        await BillingTenantCreateHook.RunAsync(
            billing.Object, tasks.Object, LoggerFactory, tenant, "owner@example.com");

        billing.Verify(b => b.CreateCustomerAsync(
            tenant.Id, It.IsAny<CustomerDescriptor>(), It.IsAny<CancellationToken>()), Times.Once);
        tasks.Verify(t => t.EnqueueAsync(
            It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()), Times.Never,
            "the happy path enqueues no retry");
    }

    [Test]
    public async Task Saas_StripeFailure_Enqueues_Retry_Does_Not_Throw()
    {
        var tenant = NewTenant();
        var billing = new Mock<IBillingProvider>();
        billing.SetupGet(b => b.IsEnabled).Returns(true);
        billing.Setup(b => b.CreateCustomerAsync(
                tenant.Id, It.IsAny<CustomerDescriptor>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("stripe down"));

        PlatformQueuedTask? enqueued = null;
        var tasks = new Mock<IPlatformQueuedTaskRepository>();
        tasks.Setup(t => t.EnqueueAsync(
                It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()))
            .Callback<PlatformQueuedTask, CancellationToken>((task, _) => enqueued = task)
            .ReturnsAsync((PlatformQueuedTask task, CancellationToken _) => task);

        // Must NOT throw — tenant creation is never blocked.
        var act = async () => await BillingTenantCreateHook.RunAsync(
            billing.Object, tasks.Object, LoggerFactory, tenant, "owner@example.com");
        await act.Should().NotThrowAsync();

        enqueued.Should().NotBeNull();
        enqueued!.Type.Should().Be(CreateBillingCustomerTaskHandler.TaskTypeName);
        enqueued.TenantId.Should().Be(tenant.Id);
    }
}

/// <summary>
/// Story 35-1 (AC12, AC14) — tenant isolation: each tenant gets exactly one
/// <see cref="BillingCustomer"/> row, and a second create for the same tenant
/// never inserts a duplicate.
/// </summary>
[TestFixture]
public class BillingTenantIsolationTests
{
    private static ControlPlaneDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    [Test]
    public async Task Two_Tenants_Get_Two_Distinct_Rows_One_Per_Tenant()
    {
        var dbName = nameof(Two_Tenants_Get_Two_Distinct_Rows_One_Per_Tenant);
        await using var db = NewDb(dbName);

        var customers = new Mock<Stripe.CustomerService>();
        var seq = 0;
        customers.Setup(c => c.CreateAsync(
                It.IsAny<Stripe.CustomerCreateOptions>(), It.IsAny<Stripe.RequestOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Stripe.Customer { Id = $"cus_{seq++}" });

        var bundle = new Mock<IStripeServices>();
        bundle.SetupGet(s => s.Customers).Returns(customers.Object);
        var factory = new Mock<IStripeServicesFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(bundle.Object);

        var provider = new StripeBillingProvider(
            factory.Object, db, Mock.Of<IEventRepository>(),
            Microsoft.Extensions.Options.Options.Create(new BillingOptions()),
            NullLogger<StripeBillingProvider>.Instance);

        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        await provider.CreateCustomerAsync(t1, new CustomerDescriptor("A", "a", null, BillingMode.PlatformProvided));
        await provider.CreateCustomerAsync(t2, new CustomerDescriptor("B", "b", null, BillingMode.PlatformProvided));
        // Re-create t1 — must NOT insert a duplicate.
        await provider.CreateCustomerAsync(t1, new CustomerDescriptor("A", "a", null, BillingMode.PlatformProvided));

        var rows = await db.BillingCustomers.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2, "exactly one BillingCustomer per tenant");
        rows.Select(r => r.TenantId).Should().BeEquivalentTo(new[] { t1, t2 });
    }
}
