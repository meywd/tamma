using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Billing;
using Tamma.Api.Services.Billing.Handlers;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-5 (AC5, AC6, AC7, AC8, AC10, AC11, AC14) — the
/// <see cref="StripeWebhookProcessor"/> dedupe + tenant-resolve + dispatch + DCB
/// emit + fast-ack pipeline. EF InMemory CP context; <see cref="IEventRepository"/>
/// and <see cref="IPlatformQueuedTaskRepository"/> mocked; the four real default
/// handlers exercise the exact <c>BILLING.*</c> emission.
/// </summary>
[TestFixture]
public class StripeWebhookProcessorTests
{
    private const string Cus = "cus_tenantA";

    private static ControlPlaneDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(name).Options);

    private sealed class Harness
    {
        public required StripeWebhookProcessor Processor { get; init; }
        public required ControlPlaneDbContext Db { get; init; }
        public required Mock<IEventRepository> Events { get; init; }
        public required Mock<IPlatformQueuedTaskRepository> Tasks { get; init; }
        public required List<DomainEvent> Appended { get; init; }
    }

    private static Harness Build(
        string dbName, Guid tenantId,
        IEnumerable<IBillingEventHandler>? handlers = null,
        string customerId = Cus)
    {
        var db = NewDb(dbName);
        db.BillingCustomers.Add(new BillingCustomer
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StripeCustomerId = customerId,
            BillingMode = "PlatformProvided",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        var appended = new List<DomainEvent>();
        var events = new Mock<IEventRepository>();
        events.Setup(e => e.AppendAsync(It.IsAny<DomainEvent>()))
            .ReturnsAsync((DomainEvent d) => d)
            .Callback<DomainEvent>(appended.Add);

        var tasks = new Mock<IPlatformQueuedTaskRepository>();
        tasks.Setup(t => t.EnqueueAsync(It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlatformQueuedTask t, CancellationToken _) => t);

        var handlerList = (handlers ?? DefaultHandlers(events.Object)).ToList();
        var registry = new BillingEventHandlerRegistry(handlerList);
        var nullHandler = new NullBillingEventHandler(NullLogger<NullBillingEventHandler>.Instance);

        var processor = new StripeWebhookProcessor(
            db, events.Object, tasks.Object, registry, nullHandler,
            TimeProvider.System, NullLogger<StripeWebhookProcessor>.Instance);

        return new Harness
        {
            Processor = processor, Db = db, Events = events, Tasks = tasks, Appended = appended,
        };
    }

    private static IEnumerable<IBillingEventHandler> DefaultHandlers(IEventRepository events) =>
        new IBillingEventHandler[]
        {
            new SubscriptionWebhookHandler(events),
            new InvoiceWebhookHandler(events),
            new PaymentWebhookHandler(events),
            new DisputeWebhookHandler(events),
        };

    private static Stripe.Event Evt(string type, string id) => new() { Id = id, Type = type };

    private static string Payload(string objectId, string? customer)
    {
        var cust = customer is null ? string.Empty : ",\"customer\":\"" + customer + "\"";
        return "{\"data\":{\"object\":{\"id\":\"" + objectId + "\"" + cust + "}}}";
    }

    private static (string? tenantId, string? stripeEventId, string? eventType, string? stripeObjectId)
        ReadTags(DomainEvent e)
    {
        using var doc = JsonDocument.Parse(e.Tags);
        var r = doc.RootElement;
        string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
        return (S("tenantId"), S("stripeEventId"), S("eventType"), S("stripeObjectId"));
    }

    // ── AC5 — dedupe ──

    [Test]
    public async Task Duplicate_Delivery_Projects_Once()
    {
        var tid = Guid.NewGuid();
        var h = Build(nameof(Duplicate_Delivery_Projects_Once), tid);
        var evt = Evt(BillingWebhookEventTypes.SubscriptionCreated, "evt_dup");
        var payload = Payload("sub_1", Cus);

        var first = await h.Processor.ProcessAsync(evt, payload);
        var second = await h.Processor.ProcessAsync(evt, payload);

        first.Status.Should().Be(WebhookProcessResult.ProjectedStatus);
        second.Status.Should().Be(WebhookProcessResult.DuplicateStatus);
        (await h.Db.BillingWebhookEvents.CountAsync()).Should().Be(1, "one dedup row per event id");
        h.Events.Verify(e => e.AppendAsync(It.Is<DomainEvent>(
            d => d.Type == BillingWebhookEventTypes.DcbSubscriptionCreated)), Times.Once,
            "the second delivery does not re-project");
    }

    // ── AC6 — tenant resolution ──

    [Test]
    public async Task No_Customer_Match_Is_Skipped_200_No_Projection()
    {
        var tid = Guid.NewGuid();
        var h = Build(nameof(No_Customer_Match_Is_Skipped_200_No_Projection), tid);
        var evt = Evt(BillingWebhookEventTypes.InvoicePaid, "evt_nocust");

        var result = await h.Processor.ProcessAsync(evt, Payload("in_1", "cus_unknown"));

        result.Status.Should().Be(WebhookProcessResult.SkippedStatus);
        var row = await h.Db.BillingWebhookEvents.SingleAsync();
        row.Status.Should().Be("skipped");
        row.TenantId.Should().BeNull();
        h.Events.Verify(e => e.AppendAsync(It.IsAny<DomainEvent>()), Times.Never,
            "a no-customer-match emits no projection event");
    }

    [Test]
    public async Task Resolves_Tenant_And_Stamps_It_On_Row_And_Event()
    {
        var tid = Guid.NewGuid();
        var h = Build(nameof(Resolves_Tenant_And_Stamps_It_On_Row_And_Event), tid);

        await h.Processor.ProcessAsync(
            Evt(BillingWebhookEventTypes.SubscriptionUpdated, "evt_t"), Payload("sub_9", Cus));

        var row = await h.Db.BillingWebhookEvents.SingleAsync();
        row.TenantId.Should().Be(tid);
        row.StripeObjectId.Should().Be("sub_9");

        var (tenantTag, eventIdTag, typeTag, objTag) = ReadTags(h.Appended.Single());
        tenantTag.Should().Be(tid.ToString("D"));
        eventIdTag.Should().Be("evt_t");
        typeTag.Should().Be(BillingWebhookEventTypes.SubscriptionUpdated);
        objTag.Should().Be("sub_9");
    }

    // ── AC7 / AC8 — per default-handler DCB type ──

    [TestCase(BillingWebhookEventTypes.SubscriptionCreated, "sub_1", BillingWebhookEventTypes.DcbSubscriptionCreated)]
    [TestCase(BillingWebhookEventTypes.SubscriptionUpdated, "sub_1", BillingWebhookEventTypes.DcbSubscriptionUpdated)]
    [TestCase(BillingWebhookEventTypes.SubscriptionDeleted, "sub_1", BillingWebhookEventTypes.DcbSubscriptionDeleted)]
    [TestCase(BillingWebhookEventTypes.SubscriptionTrialWillEnd, "sub_1", BillingWebhookEventTypes.DcbSubscriptionTrialEnding)]
    [TestCase(BillingWebhookEventTypes.InvoiceCreated, "in_1", BillingWebhookEventTypes.DcbInvoiceCreated)]
    [TestCase(BillingWebhookEventTypes.InvoiceFinalized, "in_1", BillingWebhookEventTypes.DcbInvoiceFinalized)]
    [TestCase(BillingWebhookEventTypes.InvoicePaid, "in_1", BillingWebhookEventTypes.DcbInvoicePaid)]
    [TestCase(BillingWebhookEventTypes.PaymentIntentSucceeded, "pi_1", BillingWebhookEventTypes.DcbPaymentSucceeded)]
    [TestCase(BillingWebhookEventTypes.PaymentIntentPaymentFailed, "pi_1", BillingWebhookEventTypes.DcbPaymentFailed)]
    [TestCase(BillingWebhookEventTypes.ChargeDisputeCreated, "dp_1", BillingWebhookEventTypes.DcbDisputeOpened)]
    public async Task Default_Handler_Emits_Expected_Dcb_Type(
        string stripeType, string objectId, string expectedDcbType)
    {
        var tid = Guid.NewGuid();
        var h = Build($"perhandler_{stripeType}", tid);

        await h.Processor.ProcessAsync(Evt(stripeType, "evt_" + objectId), Payload(objectId, Cus));

        h.Appended.Should().ContainSingle()
            .Which.Type.Should().Be(expectedDcbType);
    }

    // ── AC11 — unknown type ──

    [Test]
    public async Task Unknown_Type_Is_Skipped_200_No_Projection()
    {
        var tid = Guid.NewGuid();
        var h = Build(nameof(Unknown_Type_Is_Skipped_200_No_Projection), tid);

        var result = await h.Processor.ProcessAsync(Evt("foo.bar.baz", "evt_unknown"), Payload("x_1", Cus));

        result.Status.Should().Be(WebhookProcessResult.SkippedStatus);
        (await h.Db.BillingWebhookEvents.SingleAsync()).Status.Should().Be("skipped");
        h.Events.Verify(e => e.AppendAsync(It.IsAny<DomainEvent>()), Times.Never);
    }

    // ── AC10 — fast-ack enqueue ──

    [Test]
    public async Task Invoice_Payment_Failed_Enqueues_Followup_Task()
    {
        var tid = Guid.NewGuid();
        var h = Build(nameof(Invoice_Payment_Failed_Enqueues_Followup_Task), tid);

        var result = await h.Processor.ProcessAsync(
            Evt(BillingWebhookEventTypes.InvoicePaymentFailed, "evt_pf"), Payload("in_pf", Cus));

        result.Status.Should().Be(WebhookProcessResult.EnqueuedStatus);
        (await h.Db.BillingWebhookEvents.SingleAsync()).Status.Should().Be("enqueued");
        h.Tasks.Verify(t => t.EnqueueAsync(
            It.Is<PlatformQueuedTask>(p =>
                p.Type == BillingWebhookEventTypes.FollowupTaskType && p.TenantId == tid),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Invoice_Paid_Does_Not_Enqueue_Followup()
    {
        var tid = Guid.NewGuid();
        var h = Build(nameof(Invoice_Paid_Does_Not_Enqueue_Followup), tid);

        var result = await h.Processor.ProcessAsync(
            Evt(BillingWebhookEventTypes.InvoicePaid, "evt_paid"), Payload("in_paid", Cus));

        result.Status.Should().Be(WebhookProcessResult.ProjectedStatus);
        h.Tasks.Verify(t => t.EnqueueAsync(It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Handler failure ──

    private sealed class ThrowingHandler : IBillingEventHandler
    {
        public IReadOnlyCollection<string> HandledEventTypes => new[] { "invoice.paid" };
        public Task<BillingFollowup?> HandleAsync(BillingWebhookContext ctx, CancellationToken ct)
            => throw new InvalidOperationException("boom Bearer sk_live_ABCDEF1234567890 leak");
    }

    [Test]
    public async Task Handler_Exception_Records_Failed_Scrubbed_And_Acks()
    {
        var tid = Guid.NewGuid();
        var h = Build(nameof(Handler_Exception_Records_Failed_Scrubbed_And_Acks), tid,
            handlers: new IBillingEventHandler[] { new ThrowingHandler() });

        var result = await h.Processor.ProcessAsync(
            Evt(BillingWebhookEventTypes.InvoicePaid, "evt_boom"), Payload("in_boom", Cus));

        result.Status.Should().Be(WebhookProcessResult.FailedStatus);
        var row = await h.Db.BillingWebhookEvents.SingleAsync();
        row.Status.Should().Be("failed");
        row.LastError.Should().NotBeNullOrEmpty();
        row.LastError.Should().NotContain("sk_live_", "the error must be credential-scrubbed");
        row.LastError.Should().Contain("[REDACTED]");
    }

    // ── AC14 — tenant isolation ──

    [Test]
    public async Task Interleaved_Deliveries_Tag_Correct_Tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var h = Build(nameof(Interleaved_Deliveries_Tag_Correct_Tenant), tenantA);
        // second customer → tenant B
        h.Db.BillingCustomers.Add(new BillingCustomer
        {
            Id = Guid.NewGuid(), TenantId = tenantB, StripeCustomerId = "cus_tenantB",
            BillingMode = "PlatformProvided", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await h.Db.SaveChangesAsync();

        await h.Processor.ProcessAsync(
            Evt(BillingWebhookEventTypes.SubscriptionCreated, "evt_A"), Payload("sub_A", Cus));
        await h.Processor.ProcessAsync(
            Evt(BillingWebhookEventTypes.SubscriptionCreated, "evt_B"), Payload("sub_B", "cus_tenantB"));

        var rowA = await h.Db.BillingWebhookEvents.SingleAsync(e => e.StripeEventId == "evt_A");
        var rowB = await h.Db.BillingWebhookEvents.SingleAsync(e => e.StripeEventId == "evt_B");
        rowA.TenantId.Should().Be(tenantA);
        rowB.TenantId.Should().Be(tenantB);

        h.Appended.Should().OnlyContain(e => e.TenantId == tenantA || e.TenantId == tenantB);
        h.Appended.Should().Contain(e => e.TenantId == tenantA);
        h.Appended.Should().Contain(e => e.TenantId == tenantB);
        // No event tagged tenant A carries tenant B's object and vice versa.
        var evA = h.Appended.Single(e => e.TenantId == tenantA);
        ReadTags(evA).stripeObjectId.Should().Be("sub_A");
    }

    // ── AC12 — replay idempotency ──

    [Test]
    public async Task Replay_Of_Projected_Row_Is_NoOp()
    {
        var tid = Guid.NewGuid();
        var h = Build(nameof(Replay_Of_Projected_Row_Is_NoOp), tid);
        await h.Processor.ProcessAsync(
            Evt(BillingWebhookEventTypes.SubscriptionCreated, "evt_rp"), Payload("sub_rp", Cus));
        var row = await h.Db.BillingWebhookEvents.SingleAsync();
        h.Appended.Clear();

        var result = await h.Processor.ReplayAsync(row.Id);

        result!.Status.Should().Be("projected");
        h.Appended.Should().BeEmpty("re-running a projected event emits no new DCB event");
    }

    [Test]
    public async Task Replay_Of_Missing_Row_Returns_Null()
    {
        var h = Build(nameof(Replay_Of_Missing_Row_Returns_Null), Guid.NewGuid());
        (await h.Processor.ReplayAsync(Guid.NewGuid())).Should().BeNull();
    }

    // ── Id extraction ──

    [Test]
    public void ExtractIds_Reads_Object_And_Customer()
    {
        var (obj, cust) = StripeWebhookProcessor.ExtractIds(Payload("sub_x", "cus_x"));
        obj.Should().Be("sub_x");
        cust.Should().Be("cus_x");
    }

    [Test]
    public void ExtractIds_Handles_Missing_Customer_And_Malformed()
    {
        StripeWebhookProcessor.ExtractIds(Payload("dp_1", null)).CustomerId.Should().BeNull();
        StripeWebhookProcessor.ExtractIds("not json").Should().Be((null, null));
    }
}
