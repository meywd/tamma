using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Billing;
using Tamma.Api.Services.Billing.Tasks;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Core.Billing;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-1 (AC6) — the <c>billing.customer.create</c> retry handler re-drives
/// <see cref="IBillingProvider.CreateCustomerAsync"/> on a queued task; a
/// malformed payload / unknown tenant → <see cref="PlatformTaskTerminalException"/>
/// (dead-letter); a transient Stripe error rethrows (retry).
/// </summary>
[TestFixture]
public class CreateBillingCustomerTaskHandlerTests
{
    private static ControlPlaneDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    private static CreateBillingCustomerTaskHandler Build(
        ControlPlaneDbContext db, IBillingProvider billing) =>
        new(billing, db, NullLogger<CreateBillingCustomerTaskHandler>.Instance);

    [Test]
    public async Task HandleAsync_ReDrives_Create_For_Valid_Tenant()
    {
        await using var db = NewDb(nameof(HandleAsync_ReDrives_Create_For_Valid_Tenant));
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme", OwnerId = null };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var billing = new Mock<IBillingProvider>();
        billing.SetupGet(b => b.IsEnabled).Returns(true);
        billing.Setup(b => b.CreateCustomerAsync(
                tenant.Id, It.IsAny<CustomerDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingCustomer { TenantId = tenant.Id, StripeCustomerId = "cus_x" });

        var task = new PlatformQueuedTask
        {
            Type = CreateBillingCustomerTaskHandler.TaskTypeName,
            TenantId = tenant.Id,
            Payload = System.Text.Json.JsonSerializer.Serialize(
                new CreateBillingCustomerTaskPayload(tenant.Id)),
        };

        await Build(db, billing.Object).HandleAsync(task, CancellationToken.None);

        billing.Verify(b => b.CreateCustomerAsync(
            tenant.Id, It.IsAny<CustomerDescriptor>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task HandleAsync_Malformed_Payload_Is_Terminal()
    {
        await using var db = NewDb(nameof(HandleAsync_Malformed_Payload_Is_Terminal));
        var billing = new Mock<IBillingProvider>();
        billing.SetupGet(b => b.IsEnabled).Returns(true);

        var task = new PlatformQueuedTask
        {
            Type = CreateBillingCustomerTaskHandler.TaskTypeName,
            Payload = "{ not json",
        };

        var act = async () => await Build(db, billing.Object).HandleAsync(task, CancellationToken.None);
        await act.Should().ThrowAsync<PlatformTaskTerminalException>();
    }

    [Test]
    public async Task HandleAsync_Unknown_Tenant_Is_Terminal()
    {
        await using var db = NewDb(nameof(HandleAsync_Unknown_Tenant_Is_Terminal));
        var billing = new Mock<IBillingProvider>();
        billing.SetupGet(b => b.IsEnabled).Returns(true);

        var task = new PlatformQueuedTask
        {
            Type = CreateBillingCustomerTaskHandler.TaskTypeName,
            Payload = System.Text.Json.JsonSerializer.Serialize(
                new CreateBillingCustomerTaskPayload(Guid.NewGuid())),
        };

        var act = async () => await Build(db, billing.Object).HandleAsync(task, CancellationToken.None);
        await act.Should().ThrowAsync<PlatformTaskTerminalException>();
    }

    [Test]
    public async Task HandleAsync_Transient_Stripe_Error_Rethrows_For_Retry()
    {
        await using var db = NewDb(nameof(HandleAsync_Transient_Stripe_Error_Rethrows_For_Retry));
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var billing = new Mock<IBillingProvider>();
        billing.SetupGet(b => b.IsEnabled).Returns(true);
        billing.Setup(b => b.CreateCustomerAsync(
                tenant.Id, It.IsAny<CustomerDescriptor>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("stripe unreachable"));

        var task = new PlatformQueuedTask
        {
            Type = CreateBillingCustomerTaskHandler.TaskTypeName,
            TenantId = tenant.Id,
            Payload = System.Text.Json.JsonSerializer.Serialize(
                new CreateBillingCustomerTaskPayload(tenant.Id)),
        };

        var act = async () => await Build(db, billing.Object).HandleAsync(task, CancellationToken.None);
        // A NON-terminal exception so the worker retries per its budget.
        var ex = await act.Should().ThrowAsync<Exception>();
        ex.And.Should().NotBeOfType<PlatformTaskTerminalException>();
    }

    [Test]
    public async Task HandleAsync_Disabled_Provider_Is_Terminal()
    {
        await using var db = NewDb(nameof(HandleAsync_Disabled_Provider_Is_Terminal));
        var billing = new Mock<IBillingProvider>();
        billing.SetupGet(b => b.IsEnabled).Returns(false);

        var task = new PlatformQueuedTask
        {
            Type = CreateBillingCustomerTaskHandler.TaskTypeName,
            Payload = System.Text.Json.JsonSerializer.Serialize(
                new CreateBillingCustomerTaskPayload(Guid.NewGuid())),
        };

        var act = async () => await Build(db, billing.Object).HandleAsync(task, CancellationToken.None);
        await act.Should().ThrowAsync<PlatformTaskTerminalException>();
    }

    [Test]
    public void TaskType_Is_Stable()
    {
        new CreateBillingCustomerTaskHandler(
            Mock.Of<IBillingProvider>(),
            NewDb(nameof(TaskType_Is_Stable)),
            NullLogger<CreateBillingCustomerTaskHandler>.Instance)
            .TaskType.Should().Be("billing.customer.create");
    }
}
