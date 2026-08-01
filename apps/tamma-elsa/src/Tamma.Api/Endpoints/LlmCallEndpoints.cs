using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Actions;
using Tamma.Api.Services.Agents;
using Tamma.Core;
using Tamma.Core.Actions;
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
/// <para><b>Auth (Finding C2 — engine/service-only).</b> The route is registered
/// under the <c>EngineServiceOnly</c> policy, which requires the typed
/// <c>ServiceAuthPrincipal</c> that <c>ApiKeyAuthHandler</c> mints for a
/// <c>service</c>-scope key. The engine sends
/// <c>Authorization: Bearer &lt;Tamma:ApiToken&gt;</c> (via
/// <c>TammaEngineAuthHandler</c>); that token is a service-scope key, so it
/// satisfies the policy. A user JWT authenticates but never produces a
/// <c>ServiceAuthPrincipal</c> ⇒ HTTP 403. A missing/invalid bearer ⇒ HTTP 401,
/// both produced by the auth pipeline BEFORE this handler runs.</para>
///
/// <para><b>Tenant scope (Finding C1).</b> The acting tenant is the
/// auth-derived <see cref="ITenantContext"/> value (set by
/// <c>TenantContextMiddleware</c> from the service principal's
/// <c>X-Tenant-Id</c>/claims) — NEVER the request body. The body
/// <c>tenantId</c> carries no server-side authority, so a caller cannot be
/// gated / budgeted / credentialed as a tenant other than the one its
/// authenticated scope grants.</para>
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
///
/// <para><b>SEAM A — OBSERVE-ONLY IN EVERY VERSION</b> (Story 43-9 AC3, epic
/// decision D2). The handler evaluates the autonomy gate for
/// <c>agent-action:{request.Action}</c> when the request names one, writes the
/// audit row, and then <b>always proceeds, whatever the outcome</b>. Two
/// independent reasons, either sufficient:</para>
/// <list type="number">
///   <item>a <c>RequiresHuman</c> returned HERE reaches a <c>DispatchWorkflow</c>
///   whose CALLING workflow has no human route in 44 of 45 cases — the workflow
///   would suspend with nobody able to resume it, which is strictly worse than
///   proceeding;</item>
///   <item>blocking here AND at Seam E would DOUBLE-GATE deploy: the deployment
///   pipeline reaches the model through this very route
///   (<c>DeploymentPipelineWorkflow.StageDeployDispatch</c> → <c>llm-call</c>)
///   while Seam E gates the prod-approval decision. Agent-action enforcement
///   therefore lives ONLY at Seam E, where a real human wait exists.</item>
/// </list>
/// <para>This is structural, not a carve-out: under Story 43-9's D15 the route
/// carries <c>.Governs(effect:llm.call)</c> (a BINDING — metadata) and
/// deliberately does NOT carry <c>.EnforcesGovernance()</c> (the OPT-IN). Both
/// arms are pinned — <c>LlmCallSeam_NeverBlocks_EvenUnderEnforce</c> for the
/// behaviour and <c>LlmCallRoute_IsBound_ButNotEnforced</c> for the wiring — so
/// a future author who "completes" Seam A goes red on the wiring, not only on a
/// behaviour test that a filter change could route around.</para>
/// </summary>
public static class LlmCallEndpoints
{
    /// <summary>
    /// Handle <c>POST /api/v1/llm/call</c>. Binds an <see cref="LlmCallRequest"/>,
    /// takes the acting tenant from the auth-derived <see cref="ITenantContext"/>
    /// (Finding C1 — the body <c>tenantId</c> can never override it), runs the
    /// managed agent, and maps the result to the §2.4 HTTP envelope — never a raw
    /// 5xx.
    /// </summary>
    public static async Task<IResult> CallLlm(
        LlmCallRequest request,
        ITenantContext tenantContext,
        IManagedAgent managed,
        ILlmCallResponseMapper mapper,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        IAutonomyGate? autonomyGate = null,
        IGovernancePrincipalResolver? principals = null)
    {
        var logger = loggerFactory.CreateLogger("Tamma.Api.Endpoints.LlmCallEndpoints");

        // ── SEAM A — OBSERVE ONLY, IN EVERY VERSION (AC3 / epic D2) ─────────
        // Evaluate, audit, and PROCEED. There is deliberately no branch on the
        // outcome below this call and there must never be one: see the class
        // doc for the 44-of-45 no-human-route reason and the deploy
        // double-gating reason. The evaluation is wrapped because an observing
        // seam must not be able to fail a call it is not allowed to block.
        await ObserveAutonomyAsync(request, autonomyGate, principals, logger, ct)
            .ConfigureAwait(false);

        // Finding C1 — the AUTHORITATIVE tenant is the auth-derived ambient
        // tenant (ITenantContext, populated by TenantContextMiddleware from the
        // authenticated service principal's X-Tenant-Id/claims). The body's
        // tenantId is intentionally NOT consulted: it must never be able to name
        // a different tenant for the gate / budget / credential path. null ⇒
        // single-user / platform scope.
        var authoritativeTenantId = tenantContext.TenantId;

        // Story 39-9 (D2) — compose the document-content validation delegate from the
        // wire document-type KEY (a delegate cannot ride HTTP). Fail-loud: an unknown /
        // unregistered key throws a TammaError, which we map to the same non-retryable
        // AGENT_UNRESOLVED 422-in-200 envelope config failures use (fail-closed — never
        // "skip validation"). A null/empty key ⇒ null ⇒ no validation (the default for
        // the 30+ existing dispatchers; zero behaviour change).
        DocumentContentValidation? documentValidation;
        try
        {
            documentValidation = DocumentValidationBinder.Bind(request.DocumentType);
        }
        catch (TammaError ex)
        {
            logger.LogWarning(
                "call-LLM rejected an unknown/unregistered documentType; failing the run as "
                + "AGENT_UNRESOLVED (422). documentType={DocumentType}, correlationId={CorrelationId}, code={Code}",
                LogSanitizer.Clean(request.DocumentType),
                LogSanitizer.Clean(request.CorrelationId),
                LogSanitizer.Clean(ex.Code));

            var unresolved = new AgentRunResult
            {
                Success = false,
                Role = request.Role,
                CorrelationId = request.CorrelationId,
                FailureCode = AgentRunFailureCodes.AgentUnresolved,
                FailureReason = "unknown or unregistered documentType",
                HttpStatusCode = 422,
            };
            return mapper.ToHttpResult(unresolved);
        }

        var managedRequest = ManagedAgentRequest.From(request, authoritativeTenantId, documentValidation);

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

    /// <summary>
    /// Seam A's whole implementation: evaluate <c>agent-action:{Action}</c> when
    /// the request names one, and return. It has NO return value on purpose —
    /// there is nothing a caller could branch on, which is the cheapest way to
    /// make "Seam A never blocks" true by construction rather than by discipline.
    ///
    /// <para>The gate itself writes the audit row
    /// (<c>ActionGateEventsService.EmitDecisionAsync</c>), so this method neither
    /// duplicates nor suppresses it. Note the volume gate: a shipped-default
    /// allow emits nothing, which is why a 40-step run does not write 40 "nothing
    /// happened" rows — but a resolution that came from a real policy row, a
    /// degraded read, or a block DOES get a row, and that is the observation this
    /// seam exists to produce.</para>
    ///
    /// <para>Every failure is swallowed at WARNING. An observe-only seam that can
    /// 500 the request it is observing would be a blocking seam with extra steps
    /// — precisely the outcome D2 forbids.</para>
    /// </summary>
    private static async Task ObserveAutonomyAsync(
        LlmCallRequest request,
        IAutonomyGate? gate,
        IGovernancePrincipalResolver? principals,
        ILogger logger,
        CancellationToken ct)
    {
        if (gate is null || string.IsNullOrWhiteSpace(request.Action)) return;

        try
        {
            var principal = principals is null
                ? GovernancePrincipal.Platform
                : await principals.ResolveAsync(caller: null, ct).ConfigureAwait(false);

            // The agent-action plane, not effect:llm.call: the interesting
            // question at this seam is WHICH agent step is running, and the
            // effect member is what the ROUTE is bound to for the drift
            // harnesses. An uncatalogued action key resolves Automated with
            // reason `uncatalogued` (epic D2) rather than throwing.
            _ = await gate.EvaluateAsync(
                new AutonomyQuery(
                    new ActionKey(ActionNamespace.AgentAction, request.Action!),
                    principal,
                    Role: request.Role,
                    Operation: "POST /api/v1/llm/call",
                    Target: request.Action,
                    CorrelationId: request.CorrelationId),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Seam A autonomy observation failed for action={Action}; the call PROCEEDS "
                + "(this seam never blocks, in any version). correlationId={CorrelationId}",
                LogSanitizer.Clean(request.Action),
                LogSanitizer.Clean(request.CorrelationId));
        }
    }
}
