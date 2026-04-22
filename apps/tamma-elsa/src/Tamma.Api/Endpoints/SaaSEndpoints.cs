using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Auth;
using Tamma.Api.Services.SaaS;
using Tamma.Data;

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
        HttpContext http,
        ITenantContext tenantContext,
        ClaimsPrincipal principal)
    {
        if (body.Messages is null || body.Messages.Count == 0)
        {
            return Results.BadRequest(new { error = "messages[] must contain at least one entry" });
        }

        Guid? tenantId = ResolveTenantId(http, tenantContext, principal);
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

        // OpenAI-compatible response (audit finding 017): the deleted TS stub
        // returned a Chat Completions-shaped object and SDK clients (LangChain,
        // OpenRouter, internal tools) parse `choices[0].message.content`. Wrap
        // the Anthropic content into that shape, retain `usage` in
        // OpenAI-style snake_case-via-camelCase, and keep the Tamma-specific
        // `costUsd` + `text` extension fields so the new behaviour stays
        // observable.
        var modelOut = response.Model ?? body.Model ?? "claude-sonnet-4.5";
        return Results.Ok(new
        {
            id = $"chat_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            model = modelOut,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = response.Text ?? string.Empty
                    },
                    finishReason = "stop"
                }
            },
            usage = new
            {
                promptTokens = response.PromptTokens,
                completionTokens = response.CompletionTokens,
                totalTokens = response.TotalTokens
            },
            // Extension fields (not in the OpenAI shape but kept for the
            // dashboard / Tamma SDK that knows about them).
            text = response.Text,
            costUsd = response.CostUsd,
            meta = new
            {
                maxTokens = body.MaxTokens,
                temperature = body.Temperature,
                stub = false
            }
        });
    }

    // ─── Workflow status ────────────────────────────────────────────────────

    /// <summary>
    /// Payload for <see cref="UpdateWorkflowStatus"/>.
    ///
    /// <para>Audit finding 018: the original C# port reduced the contract to
    /// <c>{Status, Variables}</c>, silently dropping the <c>Step</c>,
    /// <c>Progress</c>, and <c>Message</c> fields the deleted TS endpoint
    /// stored on the instance. This DTO restores the TS shape so the
    /// dashboard "current step" tile stays current and worker progress is
    /// surfaced verbatim.</para>
    /// </summary>
    public sealed class WorkflowStatusRequestDto
    {
        public string Status { get; set; } = string.Empty;

        /// <summary>Current workflow step / activity. Required by the TS contract.</summary>
        public string? Step { get; set; }

        /// <summary>Optional progress percentage 0–100.</summary>
        public int? Progress { get; set; }

        /// <summary>Optional human-readable progress message.</summary>
        public string? Message { get; set; }

        /// <summary>Optional opaque variables blob to merge over the existing JSONB.</summary>
        public JsonElement? Variables { get; set; }
    }

    public static async Task<IResult> UpdateWorkflowStatus(
        Guid id,
        [FromBody] WorkflowStatusRequestDto body,
        [FromServices] IWorkflowLifecycleService lifecycle)
    {
        if (string.IsNullOrWhiteSpace(body.Status))
            return Results.BadRequest(new { error = "status is required" });

        if (string.IsNullOrWhiteSpace(body.Step))
            return Results.BadRequest(new { error = "step is required" });

        if (body.Progress is int p && (p < 0 || p > 100))
            return Results.BadRequest(new { error = "progress must be between 0 and 100" });

        // Merge step/progress/message into the variables payload before
        // persistence so the lifecycle service's existing JSON merge handles
        // them uniformly. CurrentActivity is set explicitly from `step`.
        var mergedVariables = MergeStatusExtras(body);

        var result = await lifecycle.UpdateStatusAsync(
            id, body.Status, mergedVariables, currentActivity: body.Step);

        if (!result.Success)
        {
            return result.ErrorReason == "not_found"
                ? Results.NotFound(new { error = "Instance not found" })
                : Results.BadRequest(new { error = result.ErrorReason });
        }

        return Results.Ok(new
        {
            ok = true,
            workflowId = id,
            status = body.Status,
            step = body.Step
        });
    }

    private static JsonElement? MergeStatusExtras(WorkflowStatusRequestDto body)
    {
        // Build a synthetic JSON object that includes any caller-supplied
        // variables PLUS the new step/progress/message fields. The lifecycle
        // service merges shallowly, so caller variables win on collision.
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();

            writer.WriteString("lastStep", body.Step);
            writer.WriteString("lastStatus", body.Status);
            if (body.Progress is int progress)
                writer.WriteNumber("progress", progress);
            if (!string.IsNullOrEmpty(body.Message))
                writer.WriteString("message", body.Message);

            if (body.Variables is JsonElement v && v.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in v.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        var doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }

    // ─── Workflow result ────────────────────────────────────────────────────

    /// <summary>
    /// Payload for <see cref="PostWorkflowResult"/>.
    ///
    /// <para>Audit finding 019: <c>Status</c> accepts the three terminal
    /// states (<c>completed | failed | cancelled</c>); collapsing cancelled
    /// into failed inflated the failure-rate SLA. <c>PrNumber</c>,
    /// <c>Error</c>, and <c>Duration</c> were promoted to first-class typed
    /// fields so downstream metrics don't have to dig through the
    /// <c>Result</c> blob.</para>
    /// </summary>
    public sealed class WorkflowResultRequestDto
    {
        public string Status { get; set; } = string.Empty;
        public int? PrNumber { get; set; }
        public string? Error { get; set; }
        public long? Duration { get; set; }
        public JsonElement? Result { get; set; }
    }

    private static readonly HashSet<string> _terminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "completed",
        "failed",
        "cancelled"
    };

    public static async Task<IResult> PostWorkflowResult(
        Guid id,
        [FromBody] WorkflowResultRequestDto body,
        [FromServices] IWorkflowLifecycleService lifecycle)
    {
        if (string.IsNullOrWhiteSpace(body.Status))
            return Results.BadRequest(new { error = "status is required" });

        if (!_terminalStatuses.Contains(body.Status))
        {
            return Results.BadRequest(new
            {
                error = "invalid_status",
                detail = "status must be one of: completed, failed, cancelled"
            });
        }

        var normalised = body.Status.ToLowerInvariant();

        // Build the result payload — fold the typed fields under the same
        // top-level shape the TS endpoint persisted. This way audit consumers
        // see {prNumber, error, duration, result?} without hunting for keys.
        var payload = BuildResultPayload(body);

        var outcome = await lifecycle.RecordResultAsync(id, payload, normalised);

        if (!outcome.Success)
        {
            return outcome.ErrorReason switch
            {
                "not_found" => Results.NotFound(new { error = "Instance not found" }),
                "invalid_status" => Results.BadRequest(new { error = "invalid_status" }),
                _ => Results.BadRequest(new { error = outcome.ErrorReason })
            };
        }

        return Results.Ok(new
        {
            ok = true,
            workflowId = id,
            status = normalised
        });
    }

    private static JsonElement BuildResultPayload(WorkflowResultRequestDto body)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            if (body.PrNumber is int pr) writer.WriteNumber("prNumber", pr);
            if (!string.IsNullOrEmpty(body.Error)) writer.WriteString("error", body.Error);
            if (body.Duration is long dur) writer.WriteNumber("duration", dur);
            if (body.Result is JsonElement r && r.ValueKind != JsonValueKind.Undefined)
            {
                writer.WritePropertyName("result");
                r.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        var doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }

    // ─── Key rotation ───────────────────────────────────────────────────────

    /// <summary>
    /// Rotate the API key for a GitHub-App installation, addressed by the
    /// numeric GitHub installation id (NOT the internal entity Guid).
    ///
    /// <para>Audit finding 020: the original C# port took <c>Guid id</c>,
    /// silently breaking every TS-era client which knows the installation only
    /// by its GitHub-issued numeric id (e.g. <c>12345678</c>). Reverted here;
    /// the route param is <c>long</c> to match TS, and the resolution to the
    /// internal entity Guid happens server-side.</para>
    /// </summary>
    public static async Task<IResult> RotateInstallationKey(
        long id,
        [FromServices] IApiKeyRotationService rotation,
        ClaimsPrincipal principal)
    {
        var callerUserId = TryGetUserId(principal);
        if (callerUserId is null)
            return Results.Unauthorized();

        var result = await rotation.RotateByInstallationIdAsync(id, callerUserId.Value);

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
            apiKey = result.PlaintextKey,
            // Audit finding 021 — provisioning summary: until a GitHub App
            // client is wired every repo entry is flagged
            // `github_client_not_configured`, but the documented shape
            // {total, success, failed, results[]} is preserved so SDK
            // clients written against the TS contract see the expected
            // structure.
            provisioning = result.Provisioning is not null ? new
            {
                total = result.Provisioning.Total,
                success = result.Provisioning.Success,
                failed = result.Provisioning.Failed,
                results = result.Provisioning.Results.Select(r => new
                {
                    owner = r.Owner,
                    repo = r.Repo,
                    success = r.Success,
                    error = r.Error
                })
            } : null
        });
    }

    // ─── Claim helpers ──────────────────────────────────────────────────────

    private static Guid? TryGetUserId(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    /// <summary>
    /// Resolve the caller's tenant in priority order:
    /// (1) ambient <see cref="ITenantContext"/> populated by
    ///     <c>TenantContextMiddleware</c> (covers JWT, ApiKey, and user-row
    ///     fallback paths);
    /// (2) <see cref="AuthPrincipal"/> tagged-union (in case middleware
    ///     bypassed this request, e.g. some test harnesses);
    /// (3) raw JWT claim — accepts both <c>tenantId</c> (current) and the
    ///     legacy <c>tid</c> name (audit finding: orgs flagged this endpoint
    ///     as still reading <c>tid</c> only).
    /// </summary>
    private static Guid? ResolveTenantId(
        HttpContext http, ITenantContext tenantContext, ClaimsPrincipal principal)
    {
        if (tenantContext.TenantId.HasValue)
            return tenantContext.TenantId.Value;

        var authPrincipal = http.GetAuthPrincipal();
        switch (authPrincipal)
        {
            case UserAuthPrincipal up:
                return up.TenantId;
            case InstallationAuthPrincipal ip when ip.TenantId.HasValue:
                return ip.TenantId.Value;
            case ServiceAuthPrincipal sp when sp.TenantId.HasValue:
                return sp.TenantId.Value;
        }

        var raw = principal.FindFirst("tenantId")?.Value
            ?? principal.FindFirst("tid")?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}
