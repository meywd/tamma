using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Services.SaaS;

namespace Tamma.Api.Endpoints;

/// <summary>
/// SaaS-lane endpoints ported from the deleted TypeScript
/// <c>packages/api/src/routes/saas/*</c> modules (Epic 19 Phase 3).
///
/// Each handler delegates to a service in <see cref="Tamma.Api.Services.SaaS"/>
/// so the minimal-API surface stays thin and the logic stays testable.
/// </summary>
public static class SaaSEndpoints
{
    // ─── LLM proxy ──────────────────────────────────────────────────────────

    /// <summary>Payload for <see cref="LlmChat"/>.</summary>
    public sealed class LlmChatRequestDto
    {
        public string? Model { get; set; }
        public List<LlmChatMessageDto> Messages { get; set; } = new();
        public int? MaxTokens { get; set; }
        public double? Temperature { get; set; }
    }

    /// <summary>Chat turn in <see cref="LlmChatRequestDto"/>.</summary>
    public sealed class LlmChatMessageDto
    {
        public string Role { get; set; } = "user";
        public string Content { get; set; } = string.Empty;
    }

    public static async Task<IResult> LlmChat(
        [FromBody] LlmChatRequestDto body,
        [FromServices] ILlmProxyService proxy,
        ClaimsPrincipal principal)
    {
        if (body.Messages is null || body.Messages.Count == 0)
        {
            return Results.BadRequest(new { error = "messages[] must contain at least one entry" });
        }

        Guid? tenantId = TryGetTenantId(principal);
        var request = new ChatRequest(
            Model: body.Model,
            Messages: body.Messages
                .Select(m => new ChatMessage(m.Role ?? "user", m.Content ?? string.Empty))
                .ToList(),
            MaxTokens: body.MaxTokens,
            Temperature: body.Temperature);

        var response = await proxy.ChatAsync(request, tenantId);

        if (!response.Success)
        {
            return response.ErrorReason switch
            {
                "budget_exceeded" => Results.Json(
                    new { error = "budget_exceeded", detail = "tenant budget exceeded" },
                    statusCode: StatusCodes.Status402PaymentRequired),
                "invalid_request" => Results.BadRequest(new { error = response.ErrorReason }),
                _ => Results.Json(
                    new { error = response.ErrorReason ?? "upstream_error" },
                    statusCode: StatusCodes.Status502BadGateway)
            };
        }

        return Results.Ok(new
        {
            model = response.Model,
            text = response.Text,
            usage = new
            {
                promptTokens = response.PromptTokens,
                completionTokens = response.CompletionTokens,
                totalTokens = response.TotalTokens
            },
            costUsd = response.CostUsd
        });
    }

    // ─── Workflow status ────────────────────────────────────────────────────

    /// <summary>Payload for <see cref="UpdateWorkflowStatus"/>.</summary>
    public sealed class WorkflowStatusRequestDto
    {
        public string Status { get; set; } = string.Empty;
        public JsonElement? Variables { get; set; }
    }

    public static async Task<IResult> UpdateWorkflowStatus(
        Guid id,
        [FromBody] WorkflowStatusRequestDto body,
        [FromServices] IWorkflowLifecycleService lifecycle)
    {
        if (string.IsNullOrWhiteSpace(body.Status))
            return Results.BadRequest(new { error = "status is required" });

        var result = await lifecycle.UpdateStatusAsync(id, body.Status, body.Variables);

        if (!result.Success)
        {
            return result.ErrorReason == "not_found"
                ? Results.NotFound(new { error = "Instance not found" })
                : Results.BadRequest(new { error = result.ErrorReason });
        }

        return Results.Ok(new { ok = true, workflowId = id, status = body.Status });
    }

    // ─── Workflow result ────────────────────────────────────────────────────

    /// <summary>Payload for <see cref="PostWorkflowResult"/>.</summary>
    public sealed class WorkflowResultRequestDto
    {
        public string Status { get; set; } = string.Empty;
        public JsonElement? Result { get; set; }
    }

    public static async Task<IResult> PostWorkflowResult(
        Guid id,
        [FromBody] WorkflowResultRequestDto body,
        [FromServices] IWorkflowLifecycleService lifecycle)
    {
        if (string.IsNullOrWhiteSpace(body.Status))
            return Results.BadRequest(new { error = "status is required" });

        var success = string.Equals(body.Status, "completed", StringComparison.OrdinalIgnoreCase);

        // Normalise the result payload so downstream code never has to juggle
        // "missing vs empty-object vs null".
        var payload = body.Result ?? JsonDocument.Parse("{}").RootElement;

        var outcome = await lifecycle.RecordResultAsync(id, payload, success);

        if (!outcome.Success)
        {
            return outcome.ErrorReason == "not_found"
                ? Results.NotFound(new { error = "Instance not found" })
                : Results.BadRequest(new { error = outcome.ErrorReason });
        }

        return Results.Ok(new
        {
            ok = true,
            workflowId = id,
            status = success ? "completed" : "failed"
        });
    }

    // ─── Key rotation ───────────────────────────────────────────────────────

    public static async Task<IResult> RotateInstallationKey(
        Guid id,
        [FromServices] IApiKeyRotationService rotation,
        ClaimsPrincipal principal)
    {
        var callerUserId = TryGetUserId(principal);
        if (callerUserId is null)
            return Results.Unauthorized();

        var result = await rotation.RotateAsync(id, callerUserId.Value);

        if (!result.Success)
        {
            return result.ErrorReason switch
            {
                "not_found" => Results.NotFound(new { error = "Installation not found" }),
                "no_tenant" => Results.BadRequest(new { error = "Installation is not linked to a tenant" }),
                "suspended" => Results.Json(new { error = "Installation is suspended" },
                    statusCode: StatusCodes.Status403Forbidden),
                "forbidden" => Results.Json(new { error = "Forbidden" },
                    statusCode: StatusCodes.Status403Forbidden),
                _ => Results.BadRequest(new { error = result.ErrorReason ?? "rotation_failed" })
            };
        }

        return Results.Ok(new
        {
            ok = true,
            installationId = id,
            keyId = result.KeyId,
            keyPrefix = result.KeyPrefix,
            // One-time plaintext reveal. The caller has exactly one opportunity
            // to capture and surface it to the end-user.
            apiKey = result.PlaintextKey
        });
    }

    // ─── Claim helpers ──────────────────────────────────────────────────────

    private static Guid? TryGetUserId(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static Guid? TryGetTenantId(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst("tid")?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}
