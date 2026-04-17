using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Services.GitHub;

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

        // Must be an authenticated user to link the install to a tenant.
        var userIdClaim = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            var unauthedUri = $"{dashboardBase}{ErrorRedirectPath}?reason=unauthenticated";
            return Results.Redirect(unauthedUri);
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

        var secret = config["GitHub:WebhookSecret"];
        if (!string.IsNullOrEmpty(secret) && !VerifySignature(secret, body, signature))
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

        try
        {
            var result = await router.HandleWebhookAsync(eventType, payload);
            logger.LogInformation(
                "Webhook {Event} (action={Action}) processed, skipped={Skipped}, taskId={TaskId}",
                result.EventType, result.Action, result.Skipped, result.TaskId);

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
            logger.LogError(ex, "Webhook {Event} handler threw", eventType);
            return Results.Problem(
                "Internal error processing webhook",
                statusCode: StatusCodes.Status500InternalServerError);
        }
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
