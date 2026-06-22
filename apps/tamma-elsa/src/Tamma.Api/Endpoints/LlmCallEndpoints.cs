using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Agents;
using Tamma.Core.Logging;
using Tamma.Data;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 32-5 (T4, AC1/AC7) — <c>POST /api/v1/llm/call</c>, the internal,
/// engine-only mediation endpoint (sequence step F). It is the SINGLE managed
/// execution path: a workflow STEP never calls an external provider; it sends an
/// <see cref="LlmCallRequest"/> here and the API holds the credential, gates,
/// runs the agentic tool loop server-side, meters cost, and returns a key-free
/// <see cref="LlmCallResponse"/>.
///
/// <para><b>Auth.</b> The route is registered under the platform default policy
/// (the SAME plane as the other <c>TammaApiClient</c> callbacks —
/// agent-resolve / chain-resolve / diagnostics / provider-session and the
/// SaaS <c>/api/v1/llm/chat</c> callback). The engine sends
/// <c>Authorization: Bearer &lt;Tamma:ApiToken&gt;</c> (via
/// <c>TammaEngineAuthHandler</c>), authenticated by the platform JwtBearer/ApiKey
/// chain. A missing/invalid bearer ⇒ HTTP 401 produced by the auth pipeline
/// BEFORE this handler runs.</para>
///
/// <para><b>Status discipline (AC7 — load-bearing).</b> The handler delegates to
/// <see cref="IManagedAgent.RunAsync"/> (which ALWAYS returns a typed
/// <see cref="AgentRunResult"/> — a failure never throws, only a null request
/// guard may) and projects via <see cref="ILlmCallResponseMapper.ToHttpResult"/>:
/// 200 success / 200 success:false + preserved <c>httpStatusCode</c> / 400
/// SAAS_PROVIDER_NOT_ALLOWED / 403 TENANT_NOT_ENTITLED. A raw 5xx must NEVER
/// leak — it would be nulled by <c>TammaApiClient.PostAsync</c> and silently
/// break the engine's <c>RetryCheck</c>/circuit-breaker. To uphold that even
/// against an unexpected throw from the composition layer, the handler wraps the
/// call and maps any escape into a typed key-free <c>PROVIDER_ERROR</c> body with
/// a transient <c>httpStatusCode</c> of 0 (which RetryCheck WILL retry).</para>
/// </summary>
public static class LlmCallEndpoints
{
    /// <summary>
    /// Handle <c>POST /api/v1/llm/call</c>. Binds an <see cref="LlmCallRequest"/>,
    /// derives the tenant from <c>X-Tenant-Id</c> (via <see cref="ITenantContext"/>)
    /// when the body omits it, runs the managed agent, and maps the result to the
    /// §2.4 HTTP envelope — never a raw 5xx.
    /// </summary>
    public static async Task<IResult> CallLlm(
        LlmCallRequest request,
        ITenantContext tenantContext,
        IManagedAgent managed,
        ILlmCallResponseMapper mapper,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamma.Api.Endpoints.LlmCallEndpoints");

        // Body tenant wins; else the X-Tenant-Id-derived ambient tenant. Both
        // null ⇒ single-user / platform scope (ManagedAgentRequest.From applies
        // the precedence).
        var headerTenantId = tenantContext.TenantId;
        var managedRequest = ManagedAgentRequest.From(request, headerTenantId);

        logger.LogInformation(
            "call-LLM received: correlationId={CorrelationId}, role={Role}, persona={Persona}, "
            + "agentId={AgentId}, tenantId={TenantId}",
            LogSanitizer.Clean(managedRequest.CorrelationId),
            LogSanitizer.Clean(managedRequest.Role),
            LogSanitizer.Clean(managedRequest.Persona),
            managedRequest.AgentId,
            managedRequest.TenantId);

        AgentRunResult run;
        try
        {
            run = await managed.RunAsync(managedRequest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Caller aborted / host shutting down — let it propagate; this is not
            // a provider failure and is not a lost-run condition the engine retries.
            throw;
        }
        catch (Exception ex)
        {
            // ManagedAgent is contracted to never throw except on a null request
            // (which can't happen here — minimal-API binding rejects a null body
            // with a 400 before this runs). Any escape is therefore unexpected;
            // we STILL must not return a raw 5xx (it would null the engine's
            // LastDiagnostic.HttpStatusCode and break RetryCheck). Map to a typed
            // key-free PROVIDER_ERROR body with a transient httpStatusCode of 0.
            // CREDENTIAL SAFETY: never interpolate the collaborator exception
            // message into the caller-facing body (it could echo a key); the full
            // detail is captured server-side via the structured ERROR log.
            logger.LogError(ex,
                "call-LLM composition threw unexpectedly; mapping to a typed PROVIDER_ERROR "
                + "body (never a raw 5xx). correlationId={CorrelationId}, role={Role}, tenantId={TenantId}",
                LogSanitizer.Clean(managedRequest.CorrelationId),
                LogSanitizer.Clean(managedRequest.Role),
                managedRequest.TenantId);

            var fallback = new AgentRunResult
            {
                Success = false,
                Role = managedRequest.Role,
                CorrelationId = managedRequest.CorrelationId,
                FailureCode = AgentRunFailureCodes.ProviderError,
                FailureReason = "managed execution failed unexpectedly",
                HttpStatusCode = 0,
            };
            return mapper.ToHttpResult(fallback);
        }

        return mapper.ToHttpResult(run);
    }
}
