using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tamma.Api.Services.Secrets.Reveal;

/// <summary>
/// Story 29-3 background service that flips every
/// <c>status='unused'</c> row in <c>secret_reveal_tokens</c> whose
/// expiry has passed to <c>status='expired'</c>, at a fixed 30-second
/// cadence per the plan (step 3).
///
/// <para>The sweep itself is a single
/// <see cref="ISecretRevealService.SweepExpiredAsync"/> call — the
/// hosted service is a thin loop that provides the
/// <see cref="IServiceScope"/> boundary + the period timer +
/// cancellation plumbing. Every iteration resolves a fresh scope so
/// the scoped <see cref="ISecretRevealService"/> gets a fresh
/// <c>DbContext</c> and does not leak a long-lived handle.</para>
///
/// <para>Errors are caught and logged at warning severity — the
/// sweeper is best-effort (tokens that miss a sweep window still
/// fail the <c>Consume</c> check because the service also checks
/// <see cref="SecretRevealTokenRow.ExpiresAt"/> at consume time and
/// flips stragglers inline).</para>
/// </summary>
public sealed class RevealTokenSweeper : BackgroundService
{
    /// <summary>Fixed sweep cadence per Story 29-3 AC3.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RevealTokenSweeper> _logger;
    private readonly TimeProvider _timeProvider;

    public RevealTokenSweeper(
        IServiceScopeFactory scopeFactory,
        ILogger<RevealTokenSweeper> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "RevealTokenSweeper starting; interval={Interval}", SweepInterval);

        using var timer = new PeriodicTimer(SweepInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                // Seam D (Story 43-9 AC9) — ONE gate call per tick, deny-only.
                // Sited in ExecuteAsync rather than inside RunOneSweepAsync so the
                // internal single-iteration seam this class exposes for tests
                // stays a pure "do the work" call.
                if (!await Tamma.Api.Services.Actions.BackgroundActionGateAccessor
                        .MayRunTickAsync(
                            _scopeFactory,
                            Tamma.Core.Actions.BackgroundActor.RevealTokenSweeper,
                            tenantId: null, stoppingToken).ConfigureAwait(false))
                {
                    continue;
                }

                await RunOneSweepAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    /// <summary>
    /// Resolve a scope, run one sweep pass, log the result. Exposed
    /// internal so tests can drive a single iteration without timing.
    /// </summary>
    internal async Task RunOneSweepAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider
                .GetRequiredService<ISecretRevealService>();
            var flipped = await service.SweepExpiredAsync(ct).ConfigureAwait(false);
            if (flipped > 0)
            {
                _logger.LogDebug(
                    "Reveal sweeper flipped {Flipped} expired token rows",
                    flipped);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation — propagate to ExecuteAsync.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Reveal sweeper iteration failed; continuing");
        }
    }
}
