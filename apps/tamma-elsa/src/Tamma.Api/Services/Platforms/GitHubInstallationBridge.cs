using Microsoft.Extensions.DependencyInjection;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Reveal;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Services.Platforms;

/// <summary>
/// Epic 31 P2 — REGISTRY UNIFICATION (execution-plan seam 14). The GitHub App
/// callback historically wrote only <c>github_installations</c> (the App-plane
/// detail table), leaving App-installed tenants INVISIBLE to the driver plane
/// (<c>tenant_platform_installations</c> → <c>IPlatformResolver</c>) and the
/// BYOK tier. This bridge upserts the missing registry row:
///
/// <list type="bullet">
///   <item><c>platform_kind</c> = <c>github</c>,
///         <c>installation_external_id</c> = the GitHub installation id;</item>
///   <item>the credential secret holds the <c>GitHubAuth</c> JSON
///         App-installation REFERENCE
///         (<c>{"kind":"app","appId":…,"privateKeyPem":…,"installationId":…}</c>)
///         — never a plaintext PAT; the GitHub driver factory mints short-lived
///         installation tokens from it per call.</item>
/// </list>
///
/// <para>Idempotent by construction: an existing row for
/// (<c>github</c>, external id) short-circuits, so the callback can re-fire and
/// the startup backfill can re-run without duplicates.
/// <c>github_installations</c> remains the App-plane detail table — nothing is
/// removed from it.</para>
/// </summary>
public interface IGitHubInstallationBridge
{
    /// <summary>Ensure the tenant_platform_installations row exists for a
    /// linked App installation. Returns true when the row exists afterwards
    /// (pre-existing or newly created); false when bridging was impossible
    /// (no App config / no secret cabinet) — logged, never thrown.</summary>
    Task<bool> EnsureBridgedAsync(Guid tenantId, long installationId, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class GitHubInstallationBridge : IGitHubInstallationBridge
{
    internal const string WireKind = "github";
    internal const string DefaultBaseUrl = "https://api.github.com";

    private readonly ITenantPlatformInstallationRepository _installations;
    private readonly ISecretRevealService? _secrets;
    private readonly IPlatformInstallationEventEmitter _events;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly ILogger<GitHubInstallationBridge> _logger;

    public GitHubInstallationBridge(
        ITenantPlatformInstallationRepository installations,
        IPlatformInstallationEventEmitter events,
        IConfiguration configuration,
        TimeProvider time,
        ILogger<GitHubInstallationBridge> logger,
        ISecretRevealService? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(installations);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _installations = installations;
        _events = events;
        _configuration = configuration;
        _time = time;
        _logger = logger;
        _secrets = secrets;
    }

    /// <summary>The App-installation credential REFERENCE wire format the
    /// GitHub driver factory parses (GitHubAuth kind=app).</summary>
    internal static string BuildAppCredentialJson(long appId, string privateKeyPem, long installationId) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            kind = "app",
            appId,
            privateKeyPem,
            installationId,
        });

    public async Task<bool> EnsureBridgedAsync(Guid tenantId, long installationId, CancellationToken ct = default)
    {
        try
        {
            var externalId = installationId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Idempotency: an existing (github, externalId) row means the
            // bridge already ran (or the tenant connected by hand).
            var existing = await _installations
                .GetByExternalIdAsync(WireKind, externalId, ct)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.TenantId != tenantId)
                {
                    _logger.LogWarning(
                        "GitHub installation {InstallationId} is registered to tenant {ExistingTenant}; "
                        + "not re-bridging for tenant {TenantId}",
                        installationId, existing.TenantId, tenantId);
                }
                return true;
            }

            // The App-plane config the credential reference points at.
            var appIdRaw = _configuration["GitHub:AppId"];
            var privateKey = _configuration["GitHub:PrivateKey"];
            if (!long.TryParse(appIdRaw, out var appId) || appId <= 0
                || string.IsNullOrWhiteSpace(privateKey))
            {
                _logger.LogWarning(
                    "Cannot bridge GitHub installation {InstallationId} into "
                    + "tenant_platform_installations: GitHub:AppId / GitHub:PrivateKey not configured",
                    installationId);
                return false;
            }

            if (_secrets is null)
            {
                _logger.LogWarning(
                    "Cannot bridge GitHub installation {InstallationId}: secret cabinet not wired",
                    installationId);
                return false;
            }

            // Write the App-installation reference through the Epic 29 seam.
            var secretName = $"github/app-install-{externalId}";
            try
            {
                await _secrets.IssueCreateAsync(
                    name: secretName,
                    scope: SecretScope.Tenant,
                    tenantId: tenantId,
                    purpose: SecretPurpose.ApiKey,
                    initialPlaintext: BuildAppCredentialJson(appId, privateKey!, installationId),
                    consumerRefs: null,
                    ownerUserId: Guid.Empty, // system-owned (no acting user on a callback/backfill)
                    rotationSchedule: null,
                    ct: ct).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                // Slug collision — a previous partial bridge already minted the
                // secret; the deterministic name means its plaintext is the
                // same reference, so reuse it.
                _logger.LogInformation(
                    "Secret {SecretName} already exists for tenant {TenantId} — reusing", secretName, tenantId);
            }

            // Primary iff the tenant has no github installation yet — an
            // operator-connected BYOK row keeps its primacy.
            var tenantGithub = await _installations
                .GetByTenantKindAsync(tenantId, WireKind, ct)
                .ConfigureAwait(false);

            var now = _time.GetUtcNow().UtcDateTime;
            var row = await _installations.CreateAsync(new TenantPlatformInstallation
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlatformKind = WireKind,
                BaseUrl = DefaultBaseUrl,
                InstallationExternalId = externalId,
                CredentialSecretScope = "tenant",
                CredentialSecretName = secretName,
                Status = "connected",
                IsPrimary = tenantGithub is null,
                MetadataJson = "{\"source\":\"github-app-bridge\"}",
                CreatedAt = now,
                UpdatedAt = now,
            }, ct).ConfigureAwait(false);

            await _events.EmitConnectedAsync(
                tenantId, PlatformKind.GitHub, row.Id, externalId, actorUserId: null, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Bridged GitHub App installation {InstallationId} into tenant_platform_installations "
                + "for tenant {TenantId} (row {RowId}, primary={Primary})",
                installationId, tenantId, row.Id, row.IsPrimary);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Bridging must never fail the install-link flow — degraded
            // fidelity (tenant resolves via the config tier) beats a broken
            // callback.
            _logger.LogError(ex,
                "Failed to bridge GitHub installation {InstallationId} for tenant {TenantId}",
                installationId, tenantId);
            return false;
        }
    }
}

/// <summary>
/// Epic 31 P2 — the ONE-TIME BACKFILL for App installations linked BEFORE the
/// bridge existed. Runs once at startup (background, non-blocking): every
/// active <c>github_installations</c> row with a tenant link is offered to
/// <see cref="IGitHubInstallationBridge"/>, whose idempotency makes re-runs
/// no-ops — the sweep can run on every boot without duplicates (the
/// RetireSweepHostedService one-shot shape, minus the timer).
/// </summary>
public sealed class GitHubInstallationBridgeBackfillService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GitHubInstallationBridgeBackfillService> _logger;

    public GitHubInstallationBridgeBackfillService(
        IServiceScopeFactory scopeFactory,
        ILogger<GitHubInstallationBridgeBackfillService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var installations = scope.ServiceProvider.GetRequiredService<IInstallationRepository>();
            var bridge = scope.ServiceProvider.GetRequiredService<IGitHubInstallationBridge>();

            var rows = await installations.ListActiveAsync().ConfigureAwait(false);
            var linked = rows.Where(r => r.TenantId is not null).ToList();
            if (linked.Count == 0)
            {
                return;
            }

            var bridged = 0;
            foreach (var row in linked)
            {
                stoppingToken.ThrowIfCancellationRequested();
                if (await bridge.EnsureBridgedAsync(row.TenantId!.Value, row.InstallationId, stoppingToken)
                    .ConfigureAwait(false))
                {
                    bridged++;
                }
            }
            _logger.LogInformation(
                "GitHub installation backfill: {Bridged}/{Total} linked installations present in "
                + "tenant_platform_installations", bridged, linked.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            // The backfill is best-effort; a failure here must not take the
            // host down. It re-runs on the next boot.
            _logger.LogError(ex, "GitHub installation backfill failed (will retry on next startup)");
        }
    }
}
