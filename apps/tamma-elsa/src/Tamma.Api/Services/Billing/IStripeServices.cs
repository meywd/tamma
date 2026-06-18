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
}

/// <summary>Concrete bundle wrapping the four service classes over one client.</summary>
public sealed class StripeServices : IStripeServices
{
    public StripeServices(IStripeClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Customers = new CustomerService(client);
        Products = new ProductService(client);
        Prices = new PriceService(client);
        Meters = new MeterService(client);
    }

    public CustomerService Customers { get; }
    public ProductService Products { get; }
    public PriceService Prices { get; }
    public MeterService Meters { get; }
}
