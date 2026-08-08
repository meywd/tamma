using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Reveal;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Services.Onboarding;

/// <summary>
/// Story 31-9 — backend-side workflow for the onboarding platform
/// picker's <c>POST /api/onboarding/install</c> endpoint.
///
/// <para>Composes:</para>
/// <list type="number">
///   <item>Story 29's secret cabinet (<see cref="ISecretRevealService"/>)
///         — credential plaintext is written through the same Epic 29
///         seam every other credentialled subsystem uses; no bypass.</item>
///   <item>Story 31-2's
///         <see cref="ITenantPlatformInstallationRepository"/> —
///         persists the row that the resolver will read on subsequent
///         workflow runs.</item>
///   <item>Story 31-1's
///         <see cref="IGitPlatformDriverFactory"/> — the freshly-
///         persisted credential is read back through the production
///         driver path so a bad token fails before we declare the
///         install ready.</item>
///   <item>Story 31-2's
///         <see cref="IPlatformInstallationEventEmitter"/> — emits
///         <c>PLATFORM.INSTALLATION.CONNECTED.SUCCESS</c> for the
///         dashboard event feed.</item>
/// </list>
///
/// <para><b>Mode behavior</b>:</para>
/// <list type="bullet">
///   <item>single-user: the caller's tenant id from the JWT is the
///         implicit-default-tenant value; rows + secrets are scoped to
///         that synthetic tenant.</item>
///   <item>SaaS: each tenant's installations are stored under the
///         caller's real tenant id; cross-tenant reads / writes are
///         excluded by the repository's tenant-scoped queries.</item>
/// </list>
///
/// <para><b>Auth gating</b> happens at the endpoint mapping site
/// (<c>RequireAuthorization("PlatformsManage")</c>) — the service does
/// not re-check role; it trusts that the caller is admin+ if it got
/// here.</para>
/// </summary>
public sealed class PlatformConnectService : IPlatformConnectService
{
    private readonly ITenantPlatformInstallationRepository _installations;
    private readonly ISecretRevealService _secretReveal;
    private readonly IServiceProvider _services;
    private readonly IPlatformInstallationEventEmitter _events;
    private readonly TimeProvider _time;
    private readonly ILogger<PlatformConnectService> _logger;

    public PlatformConnectService(
        ITenantPlatformInstallationRepository installations,
        ISecretRevealService secretReveal,
        IServiceProvider services,
        IPlatformInstallationEventEmitter events,
        TimeProvider time,
        ILogger<PlatformConnectService> logger)
    {
        ArgumentNullException.ThrowIfNull(installations);
        ArgumentNullException.ThrowIfNull(secretReveal);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _installations = installations;
        _secretReveal = secretReveal;
        _services = services;
        _events = events;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PlatformConnectResult> ConnectAsync(
        PlatformConnectRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ── Validate the kind. The picker UI is supposed to filter
        //    deferred kinds out, but defence-in-depth: a member of the
        //    PlatformKind enum that has no factory registered would
        //    pass the enum check yet fail at validation time. Bitbucket
        //    + AzureDevOps fall into that bucket today.
        if (!Enum.IsDefined(typeof(PlatformKind), request.Kind))
        {
            return PlatformConnectResult.Failure(
                "invalid_kind",
                $"PlatformKind '{request.Kind}' is not a known value.");
        }

        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            return PlatformConnectResult.Failure(
                "invalid_base_url",
                "Base URL is required.");
        }

        if (string.IsNullOrWhiteSpace(request.CredentialPlaintext))
        {
            return PlatformConnectResult.Failure(
                "invalid_credential",
                "Credential plaintext is required.");
        }

        // No driver for deferred kinds yet — render-side flag this as
        // coming-soon, but if a caller smuggles past the picker we
        // reject cleanly.
        var factory = _services.GetKeyedService<IGitPlatformDriverFactory>(request.Kind);
        if (factory is null)
        {
            return PlatformConnectResult.Failure(
                "driver_unavailable",
                $"No driver is registered for {request.Kind}. " +
                "This platform is coming soon.");
        }

        // Write the credential to the cabinet first. We use a slug
        // shape that mirrors the Story 31-2 doc'd pattern:
        //   <kind>/<tenant-suffix>
        // (the tenant-suffix is 8 hex chars to keep the slug short
        // while still avoiding collisions if a tenant disconnects +
        // reconnects against the same kind — the row's deleted_at +
        // a fresh secret name avoids replay).
        var wireKind = PlatformResolver.ToWireKind(request.Kind);
        var nowSuffix = _time.GetUtcNow().UtcDateTime
            .ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var secretName = $"{wireKind}/install-{nowSuffix}";

        SecretMetadata storedSecret;
        try
        {
            var stored = await _secretReveal.IssueCreateAsync(
                name: secretName,
                scope: SecretScope.Tenant,
                tenantId: request.TenantId,
                purpose: SecretPurpose.ApiKey,
                initialPlaintext: request.CredentialPlaintext,
                consumerRefs: null,
                ownerUserId: request.ActorUserId,
                rotationSchedule: null,
                ct: ct).ConfigureAwait(false);
            storedSecret = stored.Metadata;
        }
        catch (ArgumentException ex)
        {
            // Slug collision or invalid name — surface as a 400.
            return PlatformConnectResult.Failure(
                "credential_write_failed",
                ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to write platform credential to secret cabinet for tenant " +
                "{TenantId} kind {Kind}",
                request.TenantId, request.Kind);
            return PlatformConnectResult.Failure(
                "credential_write_failed",
                "Could not store credential.");
        }

        // Driver-side dry-run: pull the driver via the factory using
        // the freshly-stored plaintext. We call the factory directly
        // (not the resolver) so the result is bound to the row we are
        // about to create — no caching surprise. This proves the
        // credential can authenticate before we persist a row that
        // would otherwise fail at first webhook delivery.
        IGitPlatformDriver driver;
        try
        {
            var probeInstallation = new PlatformInstallation(
                Id: Guid.Empty,
                TenantId: request.TenantId,
                Kind: request.Kind,
                BaseUrl: request.BaseUrl,
                InstallationExternalId: request.ExternalId);
            driver = await factory
                .CreateAsync(probeInstallation, request.CredentialPlaintext, ct)
                .ConfigureAwait(false);

            if (factory.Kind != request.Kind)
            {
                return PlatformConnectResult.Failure(
                    "driver_misconfigured",
                    $"Factory for {request.Kind} reports Kind={factory.Kind}.");
            }

            // Probe the driver. Different platforms accept different
            // probe shapes — we run a no-op pagination over the
            // accessible-repos enumerable. Drivers without that
            // capability return an empty sequence; the call still
            // exercises the auth handshake.
            await foreach (var _ in driver.Client.ListAccessibleReposAsync(ct)
                .ConfigureAwait(false))
            {
                break; // first item is enough to prove auth — bail.
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Driver probe failed for tenant {TenantId} kind {Kind} baseUrl {BaseUrl}",
                request.TenantId, request.Kind, request.BaseUrl);
            return PlatformConnectResult.Failure(
                "auth_probe_failed",
                $"Could not authenticate with {request.Kind} at {request.BaseUrl}. " +
                "Verify your credential and base URL.");
        }

        // Insert the registry row. Soft-delete + UNIQUE on
        // (tenant, kind, external_id) guards us against duplicate
        // rows; the repository throws on a collision.
        var now = _time.GetUtcNow().UtcDateTime;
        var row = new TenantPlatformInstallation
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            PlatformKind = wireKind,
            BaseUrl = request.BaseUrl,
            InstallationExternalId = request.ExternalId,
            CredentialSecretScope = "tenant",
            CredentialSecretName = secretName,
            Status = "connected",
            IsPrimary = true,
            MetadataJson = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        };

        TenantPlatformInstallation persisted;
        try
        {
            persisted = await _installations.CreateAsync(row, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to persist tenant_platform_installations row for " +
                "tenant {TenantId} kind {Kind}",
                request.TenantId, request.Kind);
            return PlatformConnectResult.Failure(
                "row_persist_failed",
                "Could not save the installation.");
        }

        // Fire the event. Failure to emit must NOT block the install —
        // the emitter swallows + logs internally per Story 31-2.
        await _events
            .EmitConnectedAsync(
                tenantId: request.TenantId,
                kind: request.Kind,
                installationRowId: persisted.Id,
                installationExternalId: persisted.InstallationExternalId,
                actorUserId: request.ActorUserId,
                ct)
            .ConfigureAwait(false);

        // Epic 31 P4 M3 — git.webhook.register goes live: mint the
        // per-installation webhook secret into the cabinet, stamp its ref on
        // the row (WebhookSecret{Scope,Name} — where the 31-7 receiver reads
        // it back), and register the hook on the installation's accessible
        // repos via driver.Client.RegisterWebhookAsync. EVERY cannot-proceed
        // state (no Tamma:PublicBaseUrl, capability unsupported, no cabinet,
        // per-repo API failures) degrades to a recorded
        // GIT.WEBHOOK_REGISTER.SKIPPED/PARTIAL/FAILED audit event — it NEVER
        // blocks connect (the service catches everything internally).
        var registration = _services
            .GetService<Tamma.Api.Services.Webhooks.Registration.IWebhookRegistrationService>();
        if (registration is not null)
        {
            await registration
                .RegisterForInstallationAsync(driver, persisted, request.ActorUserId, ct)
                .ConfigureAwait(false);
        }

        return PlatformConnectResult.Success(
            installationId: persisted.Id,
            kind: request.Kind,
            baseUrl: persisted.BaseUrl,
            externalId: persisted.InstallationExternalId,
            secretName: secretName);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlatformConnectionDto>> ListForTenantAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        var rows = await _installations.ListByTenantAsync(tenantId, ct).ConfigureAwait(false);
        if (rows.Count == 0) return Array.Empty<PlatformConnectionDto>();

        var result = new List<PlatformConnectionDto>(rows.Count);
        foreach (var row in rows)
        {
            if (!PlatformResolver.TryParseKind(row.PlatformKind, out var kind)) continue;
            result.Add(new PlatformConnectionDto(
                InstallationId: row.Id,
                Kind: kind,
                BaseUrl: row.BaseUrl,
                ExternalId: row.InstallationExternalId,
                Status: row.Status,
                IsPrimary: row.IsPrimary,
                CreatedAt: DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)));
        }
        return result;
    }
}

/// <summary>
/// Story 31-9 — input shape for the platform-connect workflow.
/// </summary>
public sealed record PlatformConnectRequest(
    Guid TenantId,
    Guid ActorUserId,
    PlatformKind Kind,
    string BaseUrl,
    string? ExternalId,
    string CredentialPlaintext);

/// <summary>
/// Story 31-9 — output shape. <see cref="Success"/>-shaped responses
/// carry the persisted row id; failure responses carry an error code +
/// hint suitable for the picker UI to render inline.
/// </summary>
public sealed record PlatformConnectResult(
    bool Succeeded,
    Guid? InstallationId,
    PlatformKind? Kind,
    string? BaseUrl,
    string? ExternalId,
    string? SecretName,
    string? ErrorCode,
    string? ErrorHint)
{
    public static PlatformConnectResult Success(
        Guid installationId,
        PlatformKind kind,
        string baseUrl,
        string? externalId,
        string secretName) =>
        new(true, installationId, kind, baseUrl, externalId, secretName, null, null);

    public static PlatformConnectResult Failure(string code, string hint) =>
        new(false, null, null, null, null, null, code, hint);
}

/// <summary>
/// Story 31-9 — list-row shape for the connected-platforms panel.
/// </summary>
public sealed record PlatformConnectionDto(
    Guid InstallationId,
    PlatformKind Kind,
    string BaseUrl,
    string? ExternalId,
    string Status,
    bool IsPrimary,
    DateTime CreatedAt);

/// <summary>
/// Service-layer surface so endpoint code can be swapped for a fake in
/// unit tests without spinning up the full secret cabinet + driver
/// stack.
/// </summary>
public interface IPlatformConnectService
{
    Task<PlatformConnectResult> ConnectAsync(
        PlatformConnectRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<PlatformConnectionDto>> ListForTenantAsync(
        Guid tenantId, CancellationToken ct = default);
}
