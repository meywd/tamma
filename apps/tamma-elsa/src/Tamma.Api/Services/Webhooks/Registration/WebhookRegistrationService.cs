using System.Security.Cryptography;
using System.Text.Json;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Reveal;
using Tamma.Core.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;
using PModels = Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Services.Webhooks.Registration;

/// <summary>
/// Epic 31 P4 M3 — <c>effect:git.webhook.register</c> comes ALIVE. The first
/// production caller of <c>IGitPlatformClient.RegisterWebhookAsync</c>:
/// server-initiated provisioning plumbing invoked at platform connect (SaaS,
/// from <c>PlatformConnectService</c>) and at startup validation (single-user
/// <c>Platform:</c> config tier, from
/// <see cref="WebhookRegistrationStartupService"/>). Catalogued as MACHINERY
/// per the catalog row's own 43-12 note (first caller is provisioning
/// plumbing → machinery inventory): the gated decision is the human's
/// connect action / the operator's config; this service only executes it,
/// with a DCB audit event for every registration AND every skip.
///
/// <para><b>The §4 owner mechanism, applied.</b> Registration is a platform
/// action, so it is preceded by a capability check
/// (<see cref="SupportsWebhooks"/> — the driver advertises
/// <see cref="PlatformCapability.WebhookHmac"/> or
/// <see cref="PlatformCapability.WebhookStaticToken"/>) and every
/// cannot-proceed branch takes the defined ALTERNATIVE STEP: record
/// manual-registration-needed + emit <c>GIT.WEBHOOK_REGISTER.SKIPPED</c>.
/// Registration failure degrades to recorded per-repo failures
/// (<c>GIT.WEBHOOK_REGISTER.FAILED</c> / <c>PARTIAL</c>) — it NEVER blocks
/// connect and never throws to the caller.</para>
///
/// <para><b>Secret plumbing.</b> SaaS path: a fresh 256-bit secret is minted
/// into the Epic 29 secret cabinet (tenant scope) and its reference stored on
/// the installation row's <c>WebhookSecret{Scope,Name}</c> — exactly where the
/// 31-7 receiver's <c>WebhookSecretResolver</c> reads it back at delivery
/// time. Single-user path: the receiver verifies config-tier deliveries
/// against <c>Webhooks:Secrets:{kind}</c>, so the startup validator registers
/// with THAT configured value rather than minting one the receiver could
/// never see; an unset value is a documented manual-registration skip.</para>
/// </summary>
public interface IWebhookRegistrationService
{
    /// <summary>SaaS connect-time path: mint secret → store ref on the row →
    /// register the hook on the installation's accessible repos.</summary>
    Task<WebhookRegistrationOutcome> RegisterForInstallationAsync(
        IGitPlatformDriver driver,
        TenantPlatformInstallation row,
        Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>Single-user / config-tier path: register with an
    /// operator-supplied secret (no row to stamp).</summary>
    Task<WebhookRegistrationOutcome> RegisterWithSecretAsync(
        IGitPlatformDriver driver,
        PlatformKind kind,
        string webhookSecret,
        Guid? tenantId,
        CancellationToken ct = default);
}

/// <summary>Outcome summary. <see cref="Status"/> ∈ registered | partial |
/// failed | skipped. A skip carries the machine-readable reason the audit
/// event recorded (manual registration is needed for every skip).</summary>
public sealed record WebhookRegistrationOutcome(
    string Status,
    string? SkipReason,
    int ReposRegistered,
    int ReposFailed)
{
    public static WebhookRegistrationOutcome Skipped(string reason) =>
        new("skipped", reason, 0, 0);
}

public sealed class WebhookRegistrationService : IWebhookRegistrationService
{
    /// <summary>New config key (plan §P4 / owner question 7): the public base
    /// URL webhooks are delivered to. No value → manual-registration path.</summary>
    public const string PublicBaseUrlConfigKey = "Tamma:PublicBaseUrl";

    /// <summary>Audit event types (DCB, AGGREGATE.ACTION.STATUS).</summary>
    public const string SkippedEventType = "GIT.WEBHOOK_REGISTER.SKIPPED";
    public const string SuccessEventType = "GIT.WEBHOOK_REGISTER.SUCCESS";
    public const string PartialEventType = "GIT.WEBHOOK_REGISTER.PARTIAL";
    public const string FailedEventType = "GIT.WEBHOOK_REGISTER.FAILED";

    /// <summary>Upper bound on per-repo hook registrations in one pass — a
    /// tenant with hundreds of visible repos should scope the installation,
    /// not fan out unbounded API writes at connect time.</summary>
    internal const int MaxReposPerRegistration = 25;

    private readonly IConfiguration _config;
    private readonly ITenantPlatformInstallationRepository _installations;
    private readonly IEventRepository _events;
    private readonly ISecretRevealService? _secretReveal;
    private readonly TimeProvider _time;
    private readonly ILogger<WebhookRegistrationService> _logger;

    public WebhookRegistrationService(
        IConfiguration config,
        ITenantPlatformInstallationRepository installations,
        IEventRepository events,
        TimeProvider time,
        ILogger<WebhookRegistrationService> logger,
        ISecretRevealService? secretReveal = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(installations);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _installations = installations;
        _events = events;
        _secretReveal = secretReveal;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<WebhookRegistrationOutcome> RegisterForInstallationAsync(
        IGitPlatformDriver driver,
        TenantPlatformInstallation row,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(row);
        var kind = driver.Kind;

        try
        {
            // ── §4 check steps, in order; every failed check takes the
            //    alternative step (record + audit), never a hard failure. ──
            var deliveryUrl = ComputeDeliveryUrl(kind);
            if (deliveryUrl is null)
            {
                return await SkipAsync(kind, row.TenantId,
                    "no_public_base_url",
                    "Tamma:PublicBaseUrl is not configured — register the webhook manually "
                    + $"(target: <your-public-url>/api/webhooks/{PlatformKindWire.ToWire(kind)})",
                    ct).ConfigureAwait(false);
            }

            if (!SupportsWebhooks(driver))
            {
                return await SkipAsync(kind, row.TenantId,
                    "capability_unsupported",
                    "The resolved driver does not advertise a webhook capability — manual registration needed",
                    ct).ConfigureAwait(false);
            }

            if (_secretReveal is null)
            {
                return await SkipAsync(kind, row.TenantId,
                    "secret_store_unavailable",
                    "No secret cabinet is wired — cannot mint a per-installation webhook secret; manual registration needed",
                    ct).ConfigureAwait(false);
            }

            // ── Mint the per-installation secret + stamp the row. ──
            var secretPlaintext = MintSecret();
            var wire = PlatformKindWire.ToWire(kind);
            var nowSuffix = _time.GetUtcNow().UtcDateTime.ToString(
                "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var secretName = $"{wire}/webhook-{nowSuffix}";

            await _secretReveal.IssueCreateAsync(
                name: secretName,
                scope: SecretScope.Tenant,
                tenantId: row.TenantId,
                purpose: SecretPurpose.SigningKey,
                initialPlaintext: secretPlaintext,
                consumerRefs: null,
                ownerUserId: actorUserId,
                rotationSchedule: null,
                ct: ct).ConfigureAwait(false);

            row.WebhookSecretScope = "tenant";
            row.WebhookSecretName = secretName;
            await _installations.UpdateAsync(row, ct).ConfigureAwait(false);

            return await RegisterOnReposAsync(
                driver, kind, row.TenantId, deliveryUrl, secretPlaintext, secretName, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The never-block-connect guarantee: any unexpected failure
            // degrades to a recorded skip.
            _logger.LogWarning(ex,
                "Webhook registration failed unexpectedly for {Kind} installation {RowId}; connect proceeds",
                kind, row.Id);
            return await SkipAsync(kind, row.TenantId,
                "registration_error",
                $"Unexpected failure ({ex.GetType().Name}) — register the webhook manually",
                ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<WebhookRegistrationOutcome> RegisterWithSecretAsync(
        IGitPlatformDriver driver,
        PlatformKind kind,
        string webhookSecret,
        Guid? tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        try
        {
            var deliveryUrl = ComputeDeliveryUrl(kind);
            if (deliveryUrl is null)
            {
                return await SkipAsync(kind, tenantId,
                    "no_public_base_url",
                    "Tamma:PublicBaseUrl is not configured — register the webhook manually",
                    ct).ConfigureAwait(false);
            }
            if (!SupportsWebhooks(driver))
            {
                return await SkipAsync(kind, tenantId,
                    "capability_unsupported",
                    "The resolved driver does not advertise a webhook capability — manual registration needed",
                    ct).ConfigureAwait(false);
            }
            if (string.IsNullOrWhiteSpace(webhookSecret))
            {
                return await SkipAsync(kind, tenantId,
                    "no_webhook_secret_configured",
                    $"Webhooks:Secrets:{PlatformKindWire.ToWire(kind)} is not configured — the receiver could "
                    + "not verify deliveries; register manually once a secret is set",
                    ct).ConfigureAwait(false);
            }

            return await RegisterOnReposAsync(
                driver, kind, tenantId, deliveryUrl, webhookSecret, secretRef: "config", ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Config-tier webhook registration failed unexpectedly for {Kind}; startup proceeds", kind);
            return await SkipAsync(kind, tenantId,
                "registration_error",
                $"Unexpected failure ({ex.GetType().Name}) — register the webhook manually",
                ct).ConfigureAwait(false);
        }
    }

    // ─── the shared registration core ─────────────────────────────────────

    private async Task<WebhookRegistrationOutcome> RegisterOnReposAsync(
        IGitPlatformDriver driver,
        PlatformKind kind,
        Guid? tenantId,
        string deliveryUrl,
        string secretPlaintext,
        string secretRef,
        CancellationToken ct)
    {
        var events = EventsFor(kind);
        var registered = new List<string>();
        var failed = new List<string>();

        await foreach (var repo in driver.Client.ListAccessibleReposAsync(ct).ConfigureAwait(false))
        {
            if (registered.Count + failed.Count >= MaxReposPerRegistration)
            {
                _logger.LogWarning(
                    "Webhook registration for {Kind} hit the {Max}-repo cap; remaining repos need manual registration",
                    kind, MaxReposPerRegistration);
                break;
            }

            var result = await driver.Client.RegisterWebhookAsync(
                new PModels.RegisterWebhookRequest(
                    Owner: repo.Owner,
                    RepoName: repo.Name,
                    DeliveryUrl: deliveryUrl,
                    Events: events,
                    Secret: secretPlaintext,
                    Active: true),
                ct).ConfigureAwait(false);

            var slug = $"{repo.Owner}/{repo.Name}";
            if (result is PlatformResult<PModels.WebhookRegistration>.Ok)
            {
                registered.Add(slug);
            }
            else
            {
                // Per-repo failure is RECORDED, never thrown (the plan's
                // degrade-to-recorded-failure requirement).
                failed.Add(slug);
                _logger.LogWarning(
                    "Webhook registration failed for {Repo} on {Kind}: {Result}",
                    LogSanitizer.Clean(slug), kind, result.GetType().Name);
            }
        }

        var status = (registered.Count, failed.Count) switch
        {
            (0, 0) => "registered", // no repos visible — nothing to do, not a failure
            (> 0, 0) => "registered",
            (> 0, > 0) => "partial",
            _ => "failed",
        };
        var eventType = status switch
        {
            "registered" => SuccessEventType,
            "partial" => PartialEventType,
            _ => FailedEventType,
        };

        await EmitAsync(eventType, kind, tenantId, new
        {
            deliveryUrl,
            secretRef,
            reposRegistered = registered,
            reposFailed = failed,
        }, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Webhook registration for {Kind}: {Status} ({Ok} registered, {Bad} failed) → {Url}",
            kind, status, registered.Count, failed.Count, LogSanitizer.Clean(deliveryUrl));

        return new WebhookRegistrationOutcome(status, null, registered.Count, failed.Count);
    }

    private async Task<WebhookRegistrationOutcome> SkipAsync(
        PlatformKind kind, Guid? tenantId, string reason, string detail, CancellationToken ct)
    {
        _logger.LogInformation(
            "Webhook registration skipped for {Kind}: {Reason} — {Detail}",
            kind, reason, detail);
        await EmitAsync(SkippedEventType, kind, tenantId, new
        {
            reason,
            detail,
            manualRegistrationNeeded = true,
        }, ct).ConfigureAwait(false);
        return WebhookRegistrationOutcome.Skipped(reason);
    }

    private async Task EmitAsync(
        string type, PlatformKind kind, Guid? tenantId, object data, CancellationToken ct)
    {
        _ = ct;
        try
        {
            await _events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = type,
                TenantId = tenantId,
                Tags = JsonSerializer.Serialize(new
                {
                    platformKind = PlatformKindWire.ToWire(kind),
                    tenantId = tenantId?.ToString(),
                    eventSource = "system",
                }),
                Metadata = JsonSerializer.Serialize(new { workflowVersion = "1.0.0", eventSource = "system" }),
                Data = JsonSerializer.Serialize(data),
                CreatedAt = DateTime.UtcNow,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{EventType} audit append failed", type);
        }
    }

    internal string? ComputeDeliveryUrl(PlatformKind kind)
    {
        var baseUrl = _config[PublicBaseUrlConfigKey];
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        return $"{baseUrl.TrimEnd('/')}/api/webhooks/{PlatformKindWire.ToWire(kind)}";
    }

    /// <summary>The §4 capability check for registration: the platform must
    /// offer a webhook auth scheme our receiver can verify.</summary>
    internal static bool SupportsWebhooks(IGitPlatformDriver driver) =>
        driver.Capabilities.Contains(PlatformCapability.WebhookHmac)
        || driver.Capabilities.Contains(PlatformCapability.WebhookStaticToken);

    /// <summary>Event vocabulary per platform — GitLab names them
    /// differently (see the driver's boolean-flag mapping).</summary>
    internal static IReadOnlyList<string> EventsFor(PlatformKind kind) =>
        kind == PlatformKind.GitLab
            ? new[] { "push", "merge_request", "issue", "pipeline" }
            : new[] { "push", "pull_request", "issues", "workflow_run" };

    private static string MintSecret() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
