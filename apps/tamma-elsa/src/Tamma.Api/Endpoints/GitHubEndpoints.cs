using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Logging;
using Tamma.Api.Services.GitHub;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

/// <summary>
/// GitHub App endpoints:
///   • GET  /api/github/callback — user return from the install flow
///   • POST /api/github/webhooks — HMAC-signed event delivery from GitHub
///
/// Webhook signature verification uses HMAC-SHA256 with timing-safe comparison
/// and MUST remain in place; missing header returns 401.
/// </summary>
public static class GitHubEndpoints
{
    private const string SuccessRedirectPath = "/onboarding/success";
    private const string ErrorRedirectPath = "/onboarding/error";
    private const string DefaultDashboardUrl = "https://dash.tamma.dev";

    /// <summary>
    /// GitHub App install callback. GitHub redirects the user here after they
    /// complete the install flow. Requires an authenticated user so we can
    /// bind the installation to their active tenant.
    /// </summary>
    public static async Task<IResult> Callback(
        HttpContext context,
        [FromServices] IConfiguration config,
        [FromServices] IInstallationRouterService router,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GitHubEndpoints.Callback");

        var dashboardBase = config["Dashboard:Url"] ?? DefaultDashboardUrl;

        // Parse query string
        var query = context.Request.Query;
        var installationIdRaw = query["installation_id"].FirstOrDefault();
        var setupAction = query["setup_action"].FirstOrDefault();

        if (string.IsNullOrEmpty(installationIdRaw))
        {
            return Results.BadRequest(new { error = "missing installation_id" });
        }

        if (!long.TryParse(installationIdRaw, out var installationId))
        {
            return Results.BadRequest(new { error = "invalid installation_id" });
        }

        // Audit finding 020 — accept Marketplace installs that lack a Tamma
        // session. TS persisted these as orphan rows (TenantId = null) so the
        // user could claim them later via the dashboard. Bouncing
        // unauthenticated callers to /onboarding/error breaks the
        // GitHub-Marketplace-first install flow that Story 18-4 explicitly
        // documents. We persist regardless; only the linking semantics
        // depend on whether a user is signed in.
        var userIdClaim = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        Guid? userId = null;
        if (!string.IsNullOrEmpty(userIdClaim) && Guid.TryParse(userIdClaim, out var parsedUserId))
        {
            userId = parsedUserId;
        }

        int? setupActionId = int.TryParse(setupAction, out var s) ? s : null;

        try
        {
            var result = await router.HandleCallbackAsync(installationId, setupActionId, userId);

            if (!result.Success)
            {
                var reason = result.ErrorReason ?? "unknown_error";
                logger.LogWarning(
                    "Install callback failed: {Reason} (installation={InstallationId}, user={UserId})",
                    reason, installationId, userId);
                return Results.Redirect($"{dashboardBase}{ErrorRedirectPath}?reason={reason}");
            }

            if (result.TenantId is null)
            {
                logger.LogInformation(
                    "Install callback persisted orphan installation {InstallationId} (no authenticated user)",
                    installationId);
                // Send the user to the dashboard's claim-installation flow so
                // they can sign in and adopt this row.
                return Results.Redirect(
                    $"{dashboardBase}{SuccessRedirectPath}?orphan=1&installation_id={installationId}");
            }

            logger.LogInformation(
                "Install callback linked installation {InstallationId} to tenant {TenantId}",
                installationId, result.TenantId);
            return Results.Redirect($"{dashboardBase}{SuccessRedirectPath}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Install callback threw for installation {InstallationId}", installationId);
            return Results.Redirect($"{dashboardBase}{ErrorRedirectPath}?reason=server_error");
        }
    }

    /// <summary>
    /// GitHub webhook receiver. Verifies HMAC-SHA256 signature before dispatch.
    /// Ported from the deleted TypeScript webhook handler.
    /// </summary>
    public static async Task<IResult> Webhooks(
        HttpContext context,
        [FromServices] IConfiguration config,
        [FromServices] IInstallationRouterService router,
        [FromServices] IGitHubWebhookDeliveryRepository deliveryRepo,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GitHubEndpoints.Webhooks");

        // ── 1. Signature verification ──────────────────────────────────────
        var signature = context.Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        if (string.IsNullOrEmpty(signature))
        {
            return Results.Unauthorized();
        }

        context.Request.EnableBuffering();
        string body;
        using (var reader = new StreamReader(
            context.Request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true))
        {
            body = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;
        }

        // Audit finding 001 (P0 security): never fail open on missing secret.
        // TS made the secret a required plugin option (`GitHubWebhookOptions.webhookSecret: string`),
        // so an unset secret was unreachable. The C# port previously short-circuited
        // verification when the secret was empty, turning a misconfiguration into a
        // public, unauthenticated webhook surface. We now reject every webhook
        // outright when no secret is configured.
        var secret = config["GitHub:WebhookSecret"];
        if (string.IsNullOrEmpty(secret))
        {
            logger.LogError("Webhook rejected: GitHub:WebhookSecret is not configured");
            return Results.Unauthorized();
        }
        if (!VerifySignature(secret, body, signature))
        {
            logger.LogWarning("Webhook rejected: invalid signature");
            return Results.Unauthorized();
        }

        // ── 2. Event header ────────────────────────────────────────────────
        var eventType = context.Request.Headers["X-GitHub-Event"].FirstOrDefault();
        if (string.IsNullOrEmpty(eventType))
        {
            return Results.BadRequest(new { error = "Missing X-GitHub-Event header" });
        }

        // ── 3. Parse + dispatch ────────────────────────────────────────────
        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            payload = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Webhook rejected: invalid JSON body");
            return Results.BadRequest(new { error = "Invalid JSON" });
        }

        // ── 3a. Idempotency (audit findings 003 + 019) ──
        // GitHub sets X-GitHub-Delivery to a UUID stable across retry attempts
        // for the same logical delivery. Reject duplicates before dispatch so
        // the durable Postgres-backed task queue doesn't accumulate redundant
        // work. Missing/invalid delivery headers fall through with a warn log
        // — backward-compat for replayed historical payloads.
        var deliveryHeader = context.Request.Headers["X-GitHub-Delivery"].FirstOrDefault();
        if (!string.IsNullOrEmpty(deliveryHeader) && Guid.TryParse(deliveryHeader, out var parsedDeliveryId))
        {
            var action = TryGetActionForLog(payload);
            long? installationId = TryGetInstallationIdForLog(payload);
            var inserted = await deliveryRepo.TryRecordAsync(
                parsedDeliveryId, eventType, action, installationId);
            if (!inserted)
            {
                logger.LogInformation(
                    "Webhook {Event} delivery {DeliveryId} skipped (duplicate)",
                    LogSanitizer.Clean(eventType), parsedDeliveryId);
                return Results.Ok(new
                {
                    received = true,
                    @event = eventType,
                    skipped = true,
                    reason = "duplicate_delivery"
                });
            }
        }
        else if (!string.IsNullOrEmpty(deliveryHeader))
        {
            logger.LogWarning(
                "Webhook {Event} carried non-UUID X-GitHub-Delivery header — proceeding without idempotency",
                LogSanitizer.Clean(eventType));
        }

        try
        {
            var result = await router.HandleWebhookAsync(eventType, payload);
            logger.LogInformation(
                "Webhook {Event} (action={Action}) processed, skipped={Skipped}, taskId={TaskId}",
                LogSanitizer.Clean(result.EventType), LogSanitizer.Clean(result.Action), result.Skipped, result.TaskId);

            // Events queued for async processing advertise queued:true + taskId
            // so the webhook sender can correlate later observability.
            if (result.TaskId is not null)
            {
                return Results.Ok(new
                {
                    received = true,
                    @event = result.EventType,
                    action = result.Action,
                    skipped = false,
                    queued = true,
                    taskId = result.TaskId.Value.ToString()
                });
            }

            return Results.Ok(new
            {
                received = true,
                @event = result.EventType,
                action = result.Action,
                skipped = result.Skipped
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Webhook {Event} handler threw", LogSanitizer.Clean(eventType));
            return Results.Problem(
                "Internal error processing webhook",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    // ─── Idempotency log helpers ────────────────────────────────────────────

    private static string? TryGetActionForLog(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (!payload.TryGetProperty("action", out var actionEl)) return null;
        return actionEl.ValueKind == JsonValueKind.String ? actionEl.GetString() : null;
    }

    private static long? TryGetInstallationIdForLog(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (!payload.TryGetProperty("installation", out var installation)) return null;
        if (installation.ValueKind != JsonValueKind.Object) return null;
        if (!installation.TryGetProperty("id", out var idEl)) return null;
        return idEl.ValueKind switch
        {
            JsonValueKind.Number when idEl.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(idEl.GetString(), out var s) => s,
            _ => null
        };
    }

    // ─── HMAC verification ──────────────────────────────────────────────────

    private static bool VerifySignature(string secret, string body, string signatureHeader)
    {
        if (!signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            return false;

        var expected = signatureHeader["sha256=".Length..];
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        var computed = Convert.ToHexString(hash).ToLowerInvariant();

        var a = Encoding.UTF8.GetBytes(computed);
        var b = Encoding.UTF8.GetBytes(expected.ToLowerInvariant());
        if (a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
