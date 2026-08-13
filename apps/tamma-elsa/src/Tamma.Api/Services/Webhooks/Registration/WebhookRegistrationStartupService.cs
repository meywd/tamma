using Microsoft.Extensions.DependencyInjection;
using Tamma.Platforms;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Services.Webhooks.Registration;

/// <summary>
/// Epic 31 P4 M3 — single-user-mode startup validation
/// (<c>automation:webhook-registration-startup</c>): when the
/// <c>Platform:</c> config tier is active, register the platform webhook on
/// the config-tier installation's accessible repos so inbound events (CI
/// wake, merged-PR resume, installation lifecycle) actually arrive.
///
/// <para><b>Degradation, per the §4 owner mechanism.</b> Every cannot-proceed
/// state — no <c>Tamma:PublicBaseUrl</c>, no
/// <c>Webhooks:Secrets:{kind}</c> secret (the value the 31-7 receiver
/// verifies config-tier deliveries against), driver without a webhook
/// capability, unreachable platform — degrades to a
/// <c>GIT.WEBHOOK_REGISTER.SKIPPED</c> audit event describing the manual
/// path. It NEVER fails startup.</para>
///
/// <para>SaaS tenants are untouched: their registration runs at connect time
/// (<c>PlatformConnectService</c> → <see cref="IWebhookRegistrationService"/>).
/// Registration is idempotent on re-runs at the platform level for
/// Gitea/GitLab-style hooks only insofar as duplicates are visible in the
/// repo's hook list; operators who restart often should prefer a stable
/// <c>Webhooks:Secrets:{kind}</c> + <c>Webhooks:RegisterOnStartup=false</c>
/// once the hook exists.</para>
/// </summary>
public sealed class WebhookRegistrationStartupService : BackgroundService
{
    internal const string EnabledConfigKey = "Webhooks:RegisterOnStartup";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<WebhookRegistrationStartupService> _logger;

    public WebhookRegistrationStartupService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<WebhookRegistrationStartupService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown during startup — nothing to do
        }
        catch (Exception ex)
        {
            // Startup validation NEVER takes the host down.
            _logger.LogWarning(ex, "Startup webhook registration pass failed; continuing startup");
        }
    }

    /// <summary>One pass (test seam). Returns the outcome, or null when the
    /// pass is not applicable (disabled / no config tier).</summary>
    internal async Task<WebhookRegistrationOutcome?> RunOnceAsync(CancellationToken ct)
    {
        if (!_config.GetValue(EnabledConfigKey, true))
        {
            _logger.LogDebug("Startup webhook registration disabled ({Key}=false)", EnabledConfigKey);
            return null;
        }

        using var scope = _scopeFactory.CreateScope();
        var options = scope.ServiceProvider.GetService<SingleUserPlatformOptions>();
        if (options is null || !options.IsConfigured)
        {
            // No config tier — SaaS-only deployment; connect-time registration
            // owns the job.
            return null;
        }
        if (!PlatformKindWire.TryParse(options.Kind!, out var kind))
        {
            _logger.LogWarning(
                "Platform:Kind '{Kind}' did not parse — startup webhook registration skipped",
                options.Kind);
            return null;
        }

        var resolver = scope.ServiceProvider.GetRequiredService<IPlatformResolver>();
        var registration = scope.ServiceProvider.GetRequiredService<IWebhookRegistrationService>();

        var resolution = await resolver.ResolveForMediationAsync(null, ct).ConfigureAwait(false);
        if (resolution is null)
        {
            _logger.LogWarning(
                "Platform: config tier is set but no driver resolved — startup webhook registration skipped");
            return null;
        }

        // The receiver verifies config-tier deliveries against
        // Webhooks:Secrets:{kind} (plus the legacy GitHub:WebhookSecret) — so
        // registration must use THAT value; minting a fresh one would create a
        // hook the receiver rejects.
        var wire = PlatformKindWire.ToWire(kind);
        var secret = _config[$"Webhooks:Secrets:{wire}"];
        if (string.IsNullOrEmpty(secret) && kind == PlatformKind.GitHub)
        {
            secret = _config["GitHub:WebhookSecret"];
        }

        return await registration.RegisterWithSecretAsync(
            resolution.Driver, kind, secret ?? "", tenantId: null, ct).ConfigureAwait(false);
    }
}
