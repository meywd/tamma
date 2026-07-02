using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tamma.Api.Services.Secrets.Stopgap;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-5 (AC2) — resolves the Stripe webhook SIGNING secret through the
/// Epic 29 cabinet exactly as Story 35-1 wires the Stripe API key: the
/// <see cref="IRuntimeSecretResolver"/> by the cabinet name on
/// <see cref="BillingOptions.StripeWebhookSecretCabinetName"/>
/// (<c>SecretScope.Platform</c>). NEVER a raw <c>IConfiguration</c> read.
///
/// <para>Returns <c>null</c> when the secret is unresolvable so the endpoint can
/// fail closed with <c>503</c> (never fail open — GitHub audit finding 001).</para>
/// </summary>
public interface IStripeSigningSecretSource
{
    Task<string?> GetSigningSecretAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class CabinetStripeSigningSecretSource : IStripeSigningSecretSource
{
    private readonly IServiceProvider _serviceProvider;
    private readonly BillingOptions _options;
    private readonly ILogger<CabinetStripeSigningSecretSource> _logger;

    public CabinetStripeSigningSecretSource(
        IServiceProvider serviceProvider,
        IOptions<BillingOptions> options,
        ILogger<CabinetStripeSigningSecretSource> logger)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        // The runtime secret resolver is resolved lazily (mirrors 35-1's
        // StripeClientFactory) so the DI graph validates in hosts that don't wire
        // the Epic 29 cabinet (test fixtures, single-user dev). An absent resolver
        // → null → the endpoint fails CLOSED with 503, never open.
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> GetSigningSecretAsync(CancellationToken ct = default)
    {
        var resolver = _serviceProvider.GetService<IRuntimeSecretResolver>();
        if (resolver is null)
        {
            _logger.LogError(
                "Stripe webhook signing secret cannot be resolved: the Epic 29 runtime "
                + "secret resolver is not registered. Failing closed (503).");
            return null;
        }

        try
        {
            var secret = await resolver
                .GetAsync(_options.StripeWebhookSecretCabinetName, ct)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(secret) ? null : secret;
        }
        catch (MissingSecretException)
        {
            // Story 29-10 fail-fast mode: the cabinet row is absent. Treat as
            // unresolvable → the endpoint returns 503 (fail closed), never open.
            _logger.LogError(
                "Stripe webhook signing secret absent from cabinet '{CabinetName}'; "
                + "failing closed (503).",
                _options.StripeWebhookSecretCabinetName);
            return null;
        }
    }
}
