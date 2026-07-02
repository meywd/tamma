namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-1 — bound billing configuration. The Stripe secret key and the
/// webhook signing secret are resolved at runtime through the Epic 29 cabinet
/// (<c>IRuntimeSecretResolver</c>) by the cabinet names below — never read raw
/// from <c>IConfiguration</c> in production. Only non-secret knobs (cabinet
/// names, default currency) bind from config.
///
/// <para>Bound from the <c>Billing</c> configuration section in
/// <c>BillingServiceCollectionExtensions.AddTammaBilling</c>.</para>
/// </summary>
public sealed class BillingOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Billing";

    /// <summary>
    /// Cabinet name for the Stripe secret API key — a platform-scoped
    /// (<c>SecretScope.Platform</c>, <c>SecretPurpose.ApiKey</c>) cabinet row.
    /// </summary>
    public string StripeSecretKeyCabinetName { get; set; } = "billing/stripe-secret-key";

    /// <summary>
    /// Cabinet name for the Stripe webhook signing secret — a platform-scoped
    /// (<c>SecretScope.Platform</c>, <c>SecretPurpose.Webhook</c>) cabinet row.
    /// This story only stores the <i>reference</i>; webhook ingestion is 35-5.
    /// </summary>
    public string StripeWebhookSecretCabinetName { get; set; } = "billing/stripe-webhook-secret";

    /// <summary>Default ISO-4217 settlement currency for new customers/prices.</summary>
    public string DefaultCurrency { get; set; } = "usd";

    /// <summary>
    /// Story 35-4 — the URL Stripe Checkout redirects to on success. Non-secret;
    /// binds from config (<c>Billing:CheckoutSuccessUrl</c>).
    /// </summary>
    public string CheckoutSuccessUrl { get; set; } = "https://app.tamma.dev/billing/success";

    /// <summary>
    /// Story 35-4 — the URL Stripe Checkout redirects to on cancel. Non-secret;
    /// binds from config (<c>Billing:CheckoutCancelUrl</c>).
    /// </summary>
    public string CheckoutCancelUrl { get; set; } = "https://app.tamma.dev/billing/cancel";
}
