using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Billing;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-5 (AC7) — <see cref="BillingEventHandlerRegistry"/> resolves a
/// registered handler by event type, returns null for an unclaimed type, and
/// throws at construction when two handlers claim the same type (mirrors
/// <c>PlatformTaskHandlerRegistry</c>). <see cref="NullBillingEventHandler"/> is
/// excluded from the registered set.
/// </summary>
[TestFixture]
public class BillingEventHandlerRegistryTests
{
    private sealed class FakeHandler : IBillingEventHandler
    {
        public FakeHandler(params string[] types) => HandledEventTypes = types;
        public IReadOnlyCollection<string> HandledEventTypes { get; }
        public Task<BillingFollowup?> HandleAsync(BillingWebhookContext ctx, CancellationToken ct)
            => Task.FromResult<BillingFollowup?>(null);
    }

    [Test]
    public void Resolves_Registered_Handler_By_EventType()
    {
        var handler = new FakeHandler("invoice.paid", "invoice.created");
        var registry = new BillingEventHandlerRegistry(new[] { handler });

        registry.Resolve("invoice.paid").Should().BeSameAs(handler);
        registry.Resolve("invoice.created").Should().BeSameAs(handler);
        registry.RegisteredEventTypes.Should().BeEquivalentTo("invoice.paid", "invoice.created");
    }

    [Test]
    public void Returns_Null_For_Unclaimed_Type()
    {
        var registry = new BillingEventHandlerRegistry(new[] { new FakeHandler("invoice.paid") });
        registry.Resolve("foo.bar.baz").Should().BeNull();
    }

    [Test]
    public void Throws_On_Duplicate_EventType_Claim()
    {
        var a = new FakeHandler("invoice.paid");
        var b = new FakeHandler("invoice.paid");

        var act = () => new BillingEventHandlerRegistry(new IBillingEventHandler[] { a, b });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate*invoice.paid*");
    }

    [Test]
    public void Excludes_NullBillingEventHandler_From_Registered_Set()
    {
        var nullHandler = new NullBillingEventHandler(NullLogger<NullBillingEventHandler>.Instance);
        var real = new FakeHandler("invoice.paid");

        var registry = new BillingEventHandlerRegistry(
            new IBillingEventHandler[] { nullHandler, real });

        registry.Resolve("invoice.paid").Should().BeSameAs(real);
        registry.RegisteredEventTypes.Should().BeEquivalentTo("invoice.paid");
    }
}
