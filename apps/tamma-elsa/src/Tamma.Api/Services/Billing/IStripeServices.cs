using Stripe;
using Stripe.Billing;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-1 — the narrow Stripe service surface this story touches, bundled
/// behind one seam so the <see cref="StripeBillingProvider"/> and
/// <c>BillingSeeder</c> are testable without a live Stripe account. Every Stripe
/// service class (<see cref="Stripe.CustomerService"/>,
/// <see cref="Stripe.ProductService"/>, <see cref="Stripe.PriceService"/>,
/// <see cref="Stripe.Billing.MeterService"/>) is non-sealed with <c>virtual</c>
/// <c>*Async</c> methods, so tests mock these properties directly with Moq.
///
/// <para>The concrete bundle (<see cref="StripeServices"/>) is built lazily from
/// the cabinet-resolved secret key by <see cref="IStripeServicesFactory"/> — the
/// key is read at most once per process and never logged.</para>
/// </summary>
public interface IStripeServices
{
    CustomerService Customers { get; }
    ProductService Products { get; }
    PriceService Prices { get; }
    MeterService Meters { get; }

    // ── Story 35-4 — subscription lifecycle surface ──
    // These service classes are non-sealed with virtual *Async methods, so tests
    // mock them directly with Moq (same pattern as Customers above).

    /// <summary>Stripe subscription CRUD (create/update/cancel) — Story 35-4.</summary>
    Stripe.SubscriptionService Subscriptions { get; }

    /// <summary>Stripe subscription schedules (scheduled downgrade at period end) — Story 35-4.</summary>
    Stripe.SubscriptionScheduleService SubscriptionSchedules { get; }

    /// <summary>Stripe Checkout sessions (mode=subscription) — Story 35-4.</summary>
    Stripe.Checkout.SessionService CheckoutSessions { get; }
}

/// <summary>Concrete bundle wrapping the Stripe service classes over one client.</summary>
public sealed class StripeServices : IStripeServices
{
    public StripeServices(IStripeClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Customers = new CustomerService(client);
        Products = new ProductService(client);
        Prices = new PriceService(client);
        Meters = new MeterService(client);
        Subscriptions = new Stripe.SubscriptionService(client);
        SubscriptionSchedules = new Stripe.SubscriptionScheduleService(client);
        CheckoutSessions = new Stripe.Checkout.SessionService(client);
    }

    public CustomerService Customers { get; }
    public ProductService Products { get; }
    public PriceService Prices { get; }
    public MeterService Meters { get; }
    public Stripe.SubscriptionService Subscriptions { get; }
    public Stripe.SubscriptionScheduleService SubscriptionSchedules { get; }
    public Stripe.Checkout.SessionService CheckoutSessions { get; }
}
