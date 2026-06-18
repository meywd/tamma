using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stripe;
using Tamma.Api.Services.Secrets.Stopgap;
using Tamma.Core;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-1 (AC5, AC13) — builds the Stripe <see cref="IStripeServices"/>
/// bundle from the cabinet-resolved secret key. The key is resolved through the
/// Epic 29 cabinet (<see cref="IRuntimeSecretResolver"/>) by the configured
/// cabinet name — cabinet-first, with the resolver's own Story-29-10 prod
/// fail-fast semantics. This factory adds a defence-in-depth AC5 guard: in
/// production a null/blank key (e.g. dev fallback enabled but no value present)
/// is a hard boot error rather than serving 500s on first request.
///
/// <para>The factory is registered as a SINGLETON (see
/// <c>BillingServiceCollectionExtensions</c>) so the resolved key is cached
/// in-process and read at most once per process; the <see cref="SemaphoreSlim"/>
/// gate then serialises the one-time resolve across concurrent first callers.
/// The key value is NEVER logged.</para>
/// </summary>
public interface IStripeServicesFactory
{
    /// <summary>
    /// Resolve the Stripe key from the cabinet and build the service bundle.
    /// Throws <see cref="TammaError"/> (<c>BILLING.STRIPE.NO_KEY</c>) in
    /// production when no key is available.
    /// </summary>
    Task<IStripeServices> CreateAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class StripeClientFactory : IStripeServicesFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly BillingOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<StripeClientFactory> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IStripeServices? _cached;

    public StripeClientFactory(
        IServiceProvider serviceProvider,
        IOptions<BillingOptions> options,
        IHostEnvironment environment,
        ILogger<StripeClientFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        // The runtime secret resolver is resolved lazily (see CreateAsync) so the
        // DI graph validates even in hosts that don't wire the Epic 29 cabinet
        // (e.g. test fixtures, single-user dev). Billing only needs it on the
        // first real Stripe call (SaaS).
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IStripeServices> CreateAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is not null) return _cached;

            var cabinetName = _options.StripeSecretKeyCabinetName;
            var resolver = _serviceProvider.GetService<IRuntimeSecretResolver>()
                ?? throw new TammaError(
                    "BILLING.STRIPE.NO_SECRET_RESOLVER",
                    "Billing requires the Epic 29 runtime secret resolver "
                    + "(IRuntimeSecretResolver) to read the Stripe key from the cabinet, "
                    + "but it is not registered. Configure ConnectionStrings:SecretStore "
                    + "or :ControlPlane so the resolver is wired.",
                    new Dictionary<string, object?> { ["cabinetName"] = cabinetName },
                    retryable: false,
                    severity: TammaErrorSeverity.Critical);
            var key = await resolver.GetAsync(cabinetName, ct).ConfigureAwait(false);

            // AC5 — production must NOT run billing on a missing key. Never log
            // the key value; only whether it resolved.
            if (string.IsNullOrWhiteSpace(key))
            {
                if (!_environment.IsDevelopment())
                {
                    _logger.LogError(
                        "Billing boot refused: no Stripe secret key in cabinet "
                        + "'{CabinetName}' (SecretScope.Platform). Import it via "
                        + "`migrate-secrets` or the secret-store admin before "
                        + "enabling billing in production.",
                        cabinetName);
                    throw new TammaError(
                        "BILLING.STRIPE.NO_KEY",
                        $"No Stripe secret key resolved from cabinet '{cabinetName}'. "
                        + "Production refuses to run billing without a cabinet key.",
                        new Dictionary<string, object?> { ["cabinetName"] = cabinetName },
                        retryable: false,
                        severity: TammaErrorSeverity.Critical);
                }

                _logger.LogWarning(
                    "No Stripe secret key resolved from cabinet '{CabinetName}'. "
                    + "Billing SDK calls will fail until a key is configured "
                    + "(development only — production fails fast).",
                    cabinetName);
            }

            _logger.LogDebug(
                "Resolved Stripe secret key from cabinet '{CabinetName}' (found={Found}).",
                cabinetName, !string.IsNullOrWhiteSpace(key));

            var client = new StripeClient(key);
            _cached = new StripeServices(client);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}
