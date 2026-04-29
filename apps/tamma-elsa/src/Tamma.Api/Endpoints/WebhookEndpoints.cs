using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Logging;
using Tamma.Api.Services.Webhooks;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 31-7 — generalised webhook receiver. Replaces the
/// GitHub-specific <c>POST /api/github/webhooks</c> handler with a
/// platform-agnostic path:
///
/// <code>POST /api/webhooks/{platform}</code>
/// where <c>{platform}</c> ∈ <c>github | gitea | forgejo | gitlab</c>
/// (extends to <c>bitbucket</c> + <c>azure_devops</c> when stories
/// 31-11 / 31-12 land).
///
/// <para>The legacy path <c>POST /api/github/webhooks</c> is preserved
/// as a 308 redirect (preserves POST + body) with a
/// <c>Deprecation: true</c> header pointing at
/// <c>/api/webhooks/github</c>.</para>
///
/// <para><b>Pipeline</b>:
/// <list type="number">
///   <item>Parse <c>{platform}</c> path param → <see cref="PlatformKind"/> (400 on unknown).</item>
///   <item>Resolve the keyed <see cref="IWebhookSignatureVerifier"/>.</item>
///   <item>Read body (capped at the configured byte limit; 413 on overflow).</item>
///   <item>Resolve installation via <see cref="IPlatformResolver.ResolveForWebhookAsync"/>;
///         when matched, fetch the installation row's webhook secret via the
///         credential reader (Story 29 seam).</item>
///   <item>Verify the signature (<c>WebhookVerificationOutcome</c>):
///         <list type="bullet">
///           <item><c>Ok</c> → continue.</item>
///           <item><c>MissingHeader</c> / <c>BadSignature</c> → 401.</item>
///           <item><c>SecretNotConfigured</c> → 503 (audit finding 001 fail-closed).</item>
///         </list></item>
///   <item>Parse JSON; 400 on invalid.</item>
///   <item>Idempotency: <see cref="IPlatformWebhookDeliveryRepository.TryRecordAsync"/>;
///         duplicate → 200 without dispatch.</item>
///   <item>Dispatch via <see cref="IWebhookEventDispatcher"/> + return 200.</item>
/// </list>
/// </para>
///
/// <para><b>Cross-tenant invariant</b>: tenant lookup goes through
/// <see cref="IPlatformResolver.ResolveForWebhookAsync"/> — the receiver
/// NEVER trusts a tenant id from the request body or query string. A
/// webhook for tenant A's installation cannot reach tenant B's handlers
/// because (a) the resolver scopes by <c>(kind, externalId)</c>, (b) the
/// dispatcher scopes by <c>(kind, eventTypePattern)</c>, and (c) the
/// handler is contractually required to scope its own DB reads by
/// <see cref="PlatformWebhookEvent.TenantId"/>.</para>
/// </summary>
public static class WebhookEndpoints
{
    /// <summary>
    /// Maximum body bytes the receiver will buffer before returning
    /// 413 Payload Too Large. GitHub ships webhooks ≤25MB; default
    /// matches that ceiling. Configurable via <c>Webhooks:MaxBodyBytes</c>.
    /// </summary>
    public const int DefaultMaxBodyBytes = 25 * 1024 * 1024;

    /// <summary>
    /// HTTP entry point for <c>POST /api/webhooks/{platform}</c>.
    /// </summary>
    public static async Task<IResult> Receive(
        string platform,
        HttpContext context,
        [FromServices] IConfiguration config,
        [FromServices] IServiceProvider services,
        [FromServices] IPlatformWebhookDeliveryRepository deliveryRepo,
        [FromServices] IWebhookEventDispatcher dispatcher,
        [FromServices] IPlatformResolver platformResolver,
        [FromServices] IPlatformCredentialReader credentialReader,
        [FromServices] IWebhookEventCategoryMapper categoryMapper,
        [FromServices] IWebhookSecretResolver secretResolver,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamma.Api.WebhookEndpoints");
        var maxBytes = config.GetValue<int?>("Webhooks:MaxBodyBytes") ?? DefaultMaxBodyBytes;

        // ── 1. Platform path → PlatformKind ─────────────────────────────────
        if (!TryParsePlatform(platform, out var kind))
        {
            logger.LogWarning(
                "Webhook rejected: unknown platform path '{Platform}'",
                LogSanitizer.Clean(platform));
            return Results.BadRequest(new { error = "unknown_platform", platform = platform });
        }

        // ── 2. Resolve verifier (keyed-DI) ──────────────────────────────────
        var verifier = services.GetKeyedService<IWebhookSignatureVerifier>(kind);
        if (verifier is null)
        {
            logger.LogError(
                "No IWebhookSignatureVerifier registered for {Kind}; refusing delivery",
                kind);
            return Results.Problem(
                detail: $"No signature verifier registered for platform '{platform}'",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (verifier.Kind != kind)
        {
            logger.LogError(
                "Verifier registered under {Kind} reports Kind={ActualKind}; refusing",
                kind, verifier.Kind);
            return Results.Problem(
                detail: "Verifier registration mismatch",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // ── 3. Read body (with cap) ─────────────────────────────────────────
        ReadOnlyMemory<byte> body;
        try
        {
            body = await ReadBodyAsync(context.Request.Body, maxBytes, ct).ConfigureAwait(false);
        }
        catch (PayloadTooLargeException)
        {
            logger.LogWarning(
                "Webhook rejected: body exceeded {MaxBytes} bytes for {Kind}",
                maxBytes, kind);
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // ── 4. Look up installation + secret for this delivery ──────────────
        // The receiver only has the platform-side external id from the
        // payload — IPlatformResolver maps that to a tenant_platform_installations
        // row. We fetch the secret from the row's WebhookSecret{Scope,Name}
        // via IWebhookSecretResolver so each tenant's installation gets its
        // own secret. Cross-tenant safety: ResolveForWebhookAsync NEVER
        // returns a row from a tenant other than the one the externalId
        // belongs to (see Story 31-2 PlatformResolver docs).
        Guid? tenantId = null;
        Tamma.Platforms.Abstractions.Models.PlatformInstallation? installation = null;
        string? webhookSecret = null;

        // Peek the JSON body for the installation external id BEFORE
        // verification so we can fetch the secret to verify against.
        // This is safe — we're only reading, not dispatching, and an
        // attacker forging a JSON body with a victim's externalId still
        // can't produce a valid HMAC without the victim's secret.
        var externalId = TryExtractInstallationExternalId(body, kind, logger);

        if (!string.IsNullOrEmpty(externalId))
        {
            // Story 31-2 — drives tenant resolution AND warms the
            // driver cache so subsequent (kind, tenantId) lookups in
            // the same TTL window are O(1). Resolution may throw
            // InvalidOperationException when no
            // IGitPlatformDriverFactory is registered for the kind
            // yet (single-user mode without 31-3/31-4/31-6 drivers
            // wired); that's an expected operating mode for 31-7
            // alone — log + continue. The secret resolver below
            // enriches tenantId regardless of whether the driver
            // composes successfully.
            try
            {
                var driver = await platformResolver
                    .ResolveForWebhookAsync(kind, externalId, ct)
                    .ConfigureAwait(false);
                _ = driver;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex,
                    "Driver resolution failed for {Kind}/{ExternalId}; continuing with tenant enrichment via secret resolver",
                    kind, LogSanitizer.Clean(externalId));
            }

            installation = await secretResolver
                .ResolveInstallationAsync(kind, externalId, ct)
                .ConfigureAwait(false);
            if (installation is not null)
            {
                tenantId = installation.TenantId;
                webhookSecret = await secretResolver
                    .ReadWebhookSecretAsync(installation, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                logger.LogDebug(
                    "No installation row for {Kind}/{ExternalId} — webhook will dispatch with TenantId=null (onboarding handoff race)",
                    kind, LogSanitizer.Clean(externalId));
            }
        }

        // Fall back to a globally-configured secret only when the
        // installation didn't have one set (single-user-mode default
        // path). The config key is platform-scoped:
        //   Webhooks:Secrets:github
        //   Webhooks:Secrets:gitea
        //   Webhooks:Secrets:gitlab
        // Operators can also set the legacy GitHub:WebhookSecret which
        // we read for backward-compat with Story 18 wiring.
        if (string.IsNullOrEmpty(webhookSecret))
        {
            webhookSecret = config[$"Webhooks:Secrets:{PlatformKindWire.ToWire(kind)}"];
            if (string.IsNullOrEmpty(webhookSecret) && kind == PlatformKind.GitHub)
            {
                webhookSecret = config["GitHub:WebhookSecret"];
            }
        }

        // ── 5. Verify signature ─────────────────────────────────────────────
        var verifyResult = await verifier.VerifyAsync(
            body,
            webhookSecret,
            name => context.Request.Headers.TryGetValue(name, out var values)
                ? values.FirstOrDefault()
                : null,
            ct).ConfigureAwait(false);

        switch (verifyResult.Outcome)
        {
            case WebhookVerificationOutcome.Ok:
                break;
            case WebhookVerificationOutcome.SecretNotConfigured:
                logger.LogError(
                    "Webhook rejected: no secret configured for {Kind} (audit finding 001 fail-closed)",
                    kind);
                return Results.Problem(
                    detail: "Webhook secret not configured for this platform",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            case WebhookVerificationOutcome.MissingHeader:
            case WebhookVerificationOutcome.BadSignature:
            default:
                logger.LogWarning(
                    "Webhook rejected ({Outcome}) for {Kind}: {Reason}",
                    verifyResult.Outcome, kind,
                    LogSanitizer.Clean(verifyResult.Reason));
                return Results.Unauthorized();
        }

        // ── 6. Parse JSON (after signature verifies — no dispatch on bad bodies) ─
        JsonElement parsed;
        try
        {
            using var doc = JsonDocument.Parse(body);
            parsed = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Webhook rejected: invalid JSON body ({Kind})", kind);
            return Results.BadRequest(new { error = "invalid_json" });
        }

        // ── 7. Pull event type + delivery id from headers ───────────────────
        var (eventType, action) = ExtractEventType(context, kind, parsed);
        if (string.IsNullOrEmpty(eventType))
        {
            logger.LogWarning("Webhook rejected: missing event type header ({Kind})", kind);
            return Results.BadRequest(new { error = "missing_event_type" });
        }

        var deliveryId = ExtractDeliveryId(context, kind);
        var repoFullName = TryExtractRepoFullName(parsed);

        // ── 8. Idempotency ──────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(deliveryId))
        {
            var inserted = await deliveryRepo.TryRecordAsync(
                kind, deliveryId, eventType, externalId, ct).ConfigureAwait(false);
            if (!inserted)
            {
                logger.LogInformation(
                    "Webhook {Kind}/{EventType} delivery {DeliveryId} skipped (duplicate)",
                    kind, LogSanitizer.Clean(eventType), LogSanitizer.Clean(deliveryId));
                return Results.Ok(new
                {
                    received = true,
                    @event = eventType,
                    skipped = true,
                    reason = "duplicate_delivery",
                });
            }
        }
        else
        {
            logger.LogWarning(
                "Webhook {Kind}/{EventType} carried no delivery id — proceeding without idempotency",
                kind, LogSanitizer.Clean(eventType));
        }

        // ── 9. Dispatch ─────────────────────────────────────────────────────
        var category = categoryMapper.MapCategory(kind, eventType);
        var evt = new PlatformWebhookEvent(
            Kind: kind,
            EventType: eventType,
            Action: action,
            Category: category,
            DeliveryId: deliveryId,
            InstallationExternalId: externalId,
            RepoFullName: repoFullName,
            TenantId: tenantId,
            Installation: installation,
            RawBody: body,
            ParsedJson: parsed);

        var dispatched = await dispatcher.DispatchAsync(evt, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Webhook {Kind}/{EventType} (action={Action}, delivery={DeliveryId}, tenant={TenantId}) → {Dispatched} handler(s)",
            kind, LogSanitizer.Clean(eventType), LogSanitizer.Clean(action),
            LogSanitizer.Clean(deliveryId), tenantId, dispatched);

        return Results.Ok(new
        {
            received = true,
            @event = eventType,
            action,
            dispatched,
        });
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private static bool TryParsePlatform(string raw, out PlatformKind kind)
    {
        // Map URL slugs to wire-form before delegating to the shared
        // PlatformKindWire parser. Slugs are lowercase, kebab/snake-stripped.
        var normalised = raw?.Trim().ToLowerInvariant() switch
        {
            "github" => "github",
            "gitea" => "gitea",
            "forgejo" => "forgejo",
            "gitlab" => "gitlab",
            "bitbucket" => "bitbucket",
            "azure-devops" or "azure_devops" => "azure_devops",
            _ => null,
        };
        if (normalised is null)
        {
            kind = default;
            return false;
        }
        return PlatformKindWire.TryParse(normalised, out kind);
    }

    private static (string EventType, string? Action) ExtractEventType(
        HttpContext context, PlatformKind kind, JsonElement parsed)
    {
        // Each platform names its event header differently. Map to a
        // normalised lower-snake event type + optional action.
        switch (kind)
        {
            case PlatformKind.GitHub:
            {
                var et = context.Request.Headers["X-GitHub-Event"].FirstOrDefault() ?? "";
                var action = TryGetStringField(parsed, "action");
                return (et, action);
            }
            case PlatformKind.Gitea:
            case PlatformKind.Forgejo:
            {
                var et = context.Request.Headers["X-Gitea-Event"].FirstOrDefault()
                    ?? context.Request.Headers["X-Forgejo-Event"].FirstOrDefault()
                    ?? "";
                var action = TryGetStringField(parsed, "action");
                return (et, action);
            }
            case PlatformKind.GitLab:
            {
                var et = context.Request.Headers["X-Gitlab-Event"].FirstOrDefault() ?? "";
                // GitLab uses "Push Hook"/"Merge Request Hook" — normalise.
                var normalised = NormaliseGitLabEvent(et);
                var action = TryGetStringField(parsed, "object_attributes.action")
                    ?? TryGetStringField(parsed, "action");
                return (normalised, action);
            }
            default:
                return ("", null);
        }
    }

    private static string NormaliseGitLabEvent(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        // "Merge Request Hook" → "merge_request"
        // "Push Hook" → "push"
        // "Issue Hook" → "issue"
        // "Pipeline Hook" → "pipeline"
        var lowered = raw.ToLowerInvariant();
        if (lowered.EndsWith(" hook")) lowered = lowered[..^5];
        return lowered.Replace(' ', '_');
    }

    private static string? ExtractDeliveryId(HttpContext context, PlatformKind kind)
    {
        return kind switch
        {
            PlatformKind.GitHub => context.Request.Headers["X-GitHub-Delivery"].FirstOrDefault(),
            PlatformKind.Gitea or PlatformKind.Forgejo =>
                context.Request.Headers["X-Gitea-Delivery"].FirstOrDefault()
                ?? context.Request.Headers["X-Forgejo-Delivery"].FirstOrDefault(),
            PlatformKind.GitLab =>
                context.Request.Headers["X-Gitlab-Event-UUID"].FirstOrDefault(),
            _ => null,
        };
    }

    private static string? TryExtractInstallationExternalId(
        ReadOnlyMemory<byte> body, PlatformKind kind, ILogger logger)
    {
        if (body.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return kind switch
            {
                PlatformKind.GitHub =>
                    TryGetStringField(root, "installation.id")
                    ?? TryGetNumberFieldAsString(root, "installation.id"),
                PlatformKind.Gitea or PlatformKind.Forgejo =>
                    // Gitea/Forgejo don't have an installation concept;
                    // fall back to repo.id which is what the platform
                    // associates the webhook with.
                    TryGetNumberFieldAsString(root, "repository.id")
                    ?? TryGetStringField(root, "repository.full_name"),
                PlatformKind.GitLab =>
                    TryGetNumberFieldAsString(root, "project.id")
                    ?? TryGetNumberFieldAsString(root, "project_id"),
                _ => null,
            };
        }
        catch (JsonException)
        {
            // Fall through — verifier will reject on JSON parse failure
            // anyway. We don't want to short-circuit the verification
            // path on a parse error.
            return null;
        }
    }

    private static string? TryExtractRepoFullName(JsonElement root)
    {
        return TryGetStringField(root, "repository.full_name")
            ?? TryGetStringField(root, "project.path_with_namespace");
    }

    private static string? TryGetStringField(JsonElement root, string dottedPath)
    {
        var el = TryGetField(root, dottedPath);
        if (el is null) return null;
        return el.Value.ValueKind == JsonValueKind.String ? el.Value.GetString() : null;
    }

    private static string? TryGetNumberFieldAsString(JsonElement root, string dottedPath)
    {
        var el = TryGetField(root, dottedPath);
        if (el is null) return null;
        if (el.Value.ValueKind == JsonValueKind.Number)
        {
            return el.Value.TryGetInt64(out var n) ? n.ToString() : el.Value.GetRawText();
        }
        return null;
    }

    private static JsonElement? TryGetField(JsonElement root, string dottedPath)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var current = root;
        foreach (var part in dottedPath.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!current.TryGetProperty(part, out var next)) return null;
            current = next;
        }
        return current;
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBodyAsync(
        Stream body, int maxBytes, CancellationToken ct)
    {
        // Stream the body into a pooled buffer; trip a 413 if it
        // exceeds maxBytes before EOF.
        using var ms = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            while ((read = await body.ReadAsync(rented.AsMemory(0, rented.Length), ct).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + read > maxBytes)
                {
                    throw new PayloadTooLargeException();
                }
                await ms.WriteAsync(rented.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
        return ms.ToArray();
    }

    private sealed class PayloadTooLargeException : Exception { }
}
