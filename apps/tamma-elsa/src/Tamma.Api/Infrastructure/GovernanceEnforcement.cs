using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Tamma.Api.Services.Actions;
using Tamma.Core.Actions;

namespace Tamma.Api.Infrastructure;

/// <summary>
/// Story 43-9 <b>Decision D15</b> — the ENFORCEMENT OPT-IN marker. A route is
/// gated if and only if it carries this, and carrying it is a separate, visible
/// line in the diff from the <c>.Governs(key)</c> binding that names WHICH action
/// the route performs.
///
/// <para><b>Why binding and enforcing are two calls, and not one</b> (D15,
/// which OVERTURNS Story 43-8's stated design — 43-8 says four times that 43-9
/// would attach the filter inside <c>Governs()</c> "so annotating and enforcing
/// stay one call"):</para>
/// <list type="number">
///   <item><b>Blast radius must not be a helper's side effect.</b> 21 routes are
///   bound today. One <c>.AddEndpointFilter&lt;…&gt;()</c> inside
///   <see cref="GovernsExtensions.Governs"/> would have converted 17 of them into
///   live 409 gates simultaneously with no per-route review.</item>
///   <item><b>Structural beats keyed.</b> <c>POST /api/v1/llm/call</c> (Seam A)
///   simply never opts in, so "Seam A never blocks" is a fact about the wiring
///   rather than a carve-out keyed on the string <c>llm.call</c> inside a filter
///   — a carve-out a "clean up the special cases" commit would delete with
///   nothing going red.</item>
///   <item><b>The two authoring shapes do not share a mechanism.</b>
///   <see cref="GovernsExtensions.Governs"/> is a <c>RouteHandlerBuilder</c>
///   extension; the four <c>MentorshipController</c> actions are bound by an
///   ATTRIBUTE that never passes through it. A filter inside <c>Governs()</c>
///   would have enforced 17 routes and silently skipped 4 while reading as "all
///   bindings are now enforced". So the opt-in exists in BOTH planes:
///   <see cref="EnforcesGovernanceExtensions.EnforcesGovernance"/> for minimal
///   APIs (an <see cref="Microsoft.AspNetCore.Http.IEndpointFilter"/>) and
///   <see cref="EnforcesGovernanceAttribute"/> for controller actions (an MVC
///   <see cref="IAsyncActionFilter"/> — endpoint filters do not run for
///   controller endpoints).</item>
/// </list>
///
/// <para>Both shapes implement this interface, so a harness or a diagnostic looks
/// up exactly one type:
/// <c>endpoint.Metadata.GetMetadata&lt;IGovernanceEnforcementMetadata&gt;()</c>.
/// <c>GovernedEndpointEnforcementSweepTests</c> pins the opted-in set EXACTLY, so
/// both an accidental addition and an accidental omission go red.</para>
/// </summary>
public interface IGovernanceEnforcementMetadata
{
    /// <summary>Which authoring plane attached the opt-in (for diagnostics).</summary>
    string Plane { get; }
}

/// <summary>The minimal-API shape of <see cref="IGovernanceEnforcementMetadata"/>.</summary>
public sealed record GovernanceEnforcementMetadata : IGovernanceEnforcementMetadata
{
    /// <inheritdoc />
    public string Plane => "minimal-api";
}

/// <summary>
/// The CONTROLLER-ACTION shape of the enforcement opt-in (D15 reasoning #4). It
/// is both the metadata marker AND the filter, because MVC endpoints do not run
/// <see cref="Microsoft.AspNetCore.Http.IEndpointFilter"/>s — a single mechanism
/// covering both planes does not exist in ASP.NET Core, and pretending otherwise
/// is exactly how 4 of 21 bound routes would have been silently skipped.
///
/// <para>Applied ALONGSIDE <c>[Governs(ns, key)]</c>, never instead of it: the
/// binding says what the action is, this says that the gate decides it.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class EnforcesGovernanceAttribute : Attribute, IGovernanceEnforcementMetadata, IAsyncActionFilter
{
    /// <inheritdoc />
    public string Plane => "controller-action";

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var denial = await AutonomyGateEnforcement
            .EvaluateAsync(context.HttpContext, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (denial is not null)
        {
            context.Result = new ObjectResult(denial.Body)
            {
                StatusCode = StatusCodes.Status409Conflict,
            };
            return;
        }

        await next().ConfigureAwait(false);
    }
}

/// <summary>The minimal-API authoring shape of the D15 enforcement opt-in.</summary>
public static class EnforcesGovernanceExtensions
{
    /// <summary>
    /// Turn ON gate enforcement for a minimal-API route already bound with
    /// <see cref="GovernsExtensions.Governs"/>. Deliberately a SECOND call: see
    /// <see cref="IGovernanceEnforcementMetadata"/> for why.
    /// </summary>
    public static RouteHandlerBuilder EnforcesGovernance(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .WithMetadata(new GovernanceEnforcementMetadata())
            .AddEndpointFilter<AutonomyGateEndpointFilter>();
    }
}

/// <summary>
/// One Seam C denial, plane-agnostic so the two authoring shapes render the same
/// body (AC8).
/// </summary>
/// <param name="Body">The 409 payload.</param>
internal sealed record GovernanceDenial(object Body);

/// <summary>
/// Story 43-9 Seam C (AC7/AC8, D6/D7) — the SHARED enforcement core both planes
/// call, so a minimal-API route and a controller action cannot drift apart in
/// what a denial means or what it looks like on the wire.
///
/// <para><b>Why an endpoint/action filter and not an
/// <c>IAuthorizationHandler</c></b> (D6): the middleware order is authentication
/// → <c>ProxyHeaderAuthMiddleware</c> → authorization → rate limiter →
/// impersonation → tenant context, so <c>ITenantContext.TenantId</c> is UNSET
/// during policy evaluation; and there is no
/// <c>IAuthorizationPolicyProvider</c> for dynamic per-action policies. A filter
/// runs after all of it.</para>
///
/// <para>Two security properties fall out of that placement and are pinned by
/// tests rather than left to comments:</para>
/// <list type="bullet">
///   <item>the gate does <b>not</b> inherit the two unconditional superuser
///   bypasses — <c>platformRole == "platform_admin"</c> and an api-key
///   <c>permission</c> claim of <c>"*"</c> both satisfy every
///   <c>PermissionRequirement</c>, and neither is consulted here. <b>A platform
///   admin can edit assignments but cannot bypass a governed effect.</b></item>
///   <item>it is unaffected by the Development-without-JWT blanket that
///   re-registers every named policy with <c>AllowAnonymousRequirement</c>: that
///   blanket rewrites AUTHORIZATION, and this is not authorization.</item>
/// </list>
///
/// <para><b>409, never 202</b> (D7). <c>TammaApiClient</c> discriminates on
/// nothing but <c>IsSuccessStatusCode</c>, and <b>202 is already a success code
/// on that client</b> (<c>QueueSlackNotificationAsync</c> →
/// <c>POST /api/v1/notifications/slack</c> → <c>Results.Accepted</c>), so a 202
/// "escalated" response would be indistinguishable from success and the engine
/// would proceed as if the effect had happened — the exact failure the gate
/// exists to prevent, introduced by the gate. 409 rather than 403 because the
/// caller IS authorized; the SYSTEM is not yet permitted to act autonomously.
/// Pinned by <c>Client_treats_202_as_success</c>.</para>
///
/// <para><b>FAILURE POSTURE — two different failures, two different directions,
/// and the split is deliberate:</b></para>
/// <list type="bullet">
///   <item><b>A STATIC WIRING fault fails CLOSED.</b> An enforced endpoint with
///   no <see cref="IActionGateMetadata"/> binding, or a host with no
///   <see cref="IAutonomyGate"/> registered, is a deterministic misconfiguration
///   — it cannot be caused by a transient outage, it is the same on every
///   request, and <c>GovernedEndpointEnforcementSweepTests</c> makes it
///   unreachable in a shipped build. Answering "proceed" to "enforce this route,
///   but I cannot tell what it does" would be a silent ungoverning.</item>
///   <item><b>A TRANSIENT EVALUATION fault fails OPEN.</b> If the gate itself
///   throws — a control-plane blip, an unresolvable principal — the request
///   proceeds and <c>ACTION.GATE.EVALUATION_FAILED</c> is emitted. This is the
///   posture the epic already took at Seam D (D8) and states in the plan's
///   Risks section: deny on a DECISION, never on an ERROR, so a blip degrades to
///   today's behaviour instead of stopping the platform. Note this does NOT
///   re-open the fail-closed posture inside the gate: an unreadable POLICY input
///   is a decision (<c>Unavailable</c> provenance, <c>Enforced</c> forced true)
///   and still blocks here.</item>
/// </list>
/// </summary>
internal static class AutonomyGateEnforcement
{
    /// <summary>The denial code (AC8).</summary>
    internal const string RequiresHumanCode = "ACTION.GATE.REQUIRES_HUMAN";

    /// <summary>The static-wiring-fault code (fail-closed arm).</summary>
    internal const string MisconfiguredCode = "ACTION.GATE.MISCONFIGURED";

    /// <summary>
    /// Header a caller may use to name the RUN a gate decision belongs to, so
    /// one human grant covers the whole correlation rather than one per retry
    /// (AC12). A <c>?correlationId=</c> query value is honoured too — several
    /// mediation routes already carry one there.
    /// </summary>
    internal const string CorrelationHeader = "X-Tamma-Correlation-Id";

    /// <summary>
    /// Evaluate the gate for the current request. Returns <c>null</c> to proceed,
    /// or the 409 denial. NEVER throws: see the class doc's failure posture.
    /// </summary>
    internal static async Task<GovernanceDenial?> EvaluateAsync(
        HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        var services = http.RequestServices;
        var logger = services.GetService<ILoggerFactory>()
            ?.CreateLogger("Tamma.Api.Infrastructure.AutonomyGateEnforcement");

        var binding = http.GetEndpoint()?.Metadata.GetMetadata<IActionGateMetadata>();
        var gate = services.GetService<IAutonomyGate>();

        if (binding is null || gate is null)
        {
            // FAIL CLOSED — static wiring, not a blip. See the class doc.
            logger?.LogError(
                "Governance enforcement is opted in for {Method} {Path} but {Missing}. The request "
                + "is REFUSED (409 {Code}) rather than silently ungoverned; this is a wiring fault, "
                + "not a policy decision.",
                http.Request.Method, http.Request.Path,
                binding is null
                    ? "the endpoint carries no .Governs(actionKey) binding"
                    : "no IAutonomyGate is registered in this host",
                MisconfiguredCode);

            return new GovernanceDenial(new
            {
                code = MisconfiguredCode,
                error = "This endpoint opted into governance enforcement but the gate cannot be "
                    + "evaluated for it (missing binding or missing gate registration).",
            });
        }

        AutonomyDecision decision;
        GovernancePrincipal principal;
        var correlationId = ResolveCorrelationId(http);
        try
        {
            var principals = services.GetRequiredService<IGovernancePrincipalResolver>();
            principal = await principals.ResolveAsync(http.User, ct).ConfigureAwait(false);

            decision = await gate.EvaluateAsync(
                new AutonomyQuery(
                    binding.Action,
                    principal,
                    Role: null,
                    Operation: $"{http.Request.Method} {http.Request.Path}",
                    Target: http.Request.Path.Value,
                    CorrelationId: correlationId),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // FAIL OPEN — deny on a DECISION, never on an ERROR (D8 posture,
            // applied at every seam). The gate's own fail-CLOSED handling of an
            // unreadable policy input is a decision and is unaffected by this.
            logger?.LogError(ex,
                "Autonomy gate evaluation FAILED for {Action} at {Method} {Path}; the request "
                + "PROCEEDS ungated (deny on a decision, never on an error) and "
                + "ACTION.GATE.EVALUATION_FAILED is emitted.",
                binding.Action.ToWire(), http.Request.Method, http.Request.Path);

            var events = services.GetService<ActionGateEventsService>();
            if (events is not null)
            {
                await events.EmitEvaluationFailedAsync(
                    binding.Action.ToWire(), ex.Message, tenantId: null, userId: null)
                    .ConfigureAwait(false);
            }
            return null;
        }

        // Observe-only resolutions and allows both proceed. `Enforced` false is
        // the admin's explicit "report but do not block" (43-5's per-field
        // ladder); honouring it here is what makes an observe-mode rollout
        // possible without a second code path.
        if (!decision.Enforced || decision.Outcome == AutonomyOutcome.Automated)
        {
            return null;
        }

        // AC12(c) — mint (or re-find) the PENDING row a person decides on, so
        // the 409's authorizationId is something the caller can act on rather
        // than a null field. Only possible with a correlation id: the ledger is
        // keyed by (principal, correlation, target) and a grant keyed to a
        // request-scoped identity could never be found again.
        Guid? authorizationId = null;
        if (correlationId is not null)
        {
            var requests = services.GetService<IActionAuthorizationRequests>();
            if (requests is not null)
            {
                authorizationId = await requests
                    .RequestAsync(principal, decision, correlationId, ct)
                    .ConfigureAwait(false);
            }
        }

        return new GovernanceDenial(new
        {
            code = RequiresHumanCode,
            action = decision.Action.ToWire(),
            group = decision.Group.ToWire(),
            effectiveMinAutonomy = decision.EffectiveMinAutonomy,
            autonomyLevel = decision.AutonomyLevel,
            authorizationId,
            correlationId,
            reason = decision.Reason,
            assignmentSource = decision.Source.ToString(),
            error = "The autonomy policy for this action does not permit the system to perform it "
                + "without a person. Grant the pending authorization "
                + "(POST /api/actions/authorizations/{id}/decide) or lower the action's threshold.",
        });
    }

    /// <summary>
    /// The run a gate decision belongs to: the <see cref="CorrelationHeader"/>
    /// header, else a <c>?correlationId=</c> query value, else null. NEVER the
    /// request body (reading it here would consume the stream the handler binds
    /// from) and never a per-request identity such as
    /// <c>HttpContext.TraceIdentifier</c> — a correlation that changes on every
    /// retry cannot carry one human decision across a run, which is the entire
    /// purpose of the ledger.
    /// </summary>
    private static string? ResolveCorrelationId(HttpContext http)
    {
        if (http.Request.Headers.TryGetValue(CorrelationHeader, out var header))
        {
            var value = header.ToString();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        if (http.Request.Query.TryGetValue("correlationId", out var query))
        {
            var value = query.ToString();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }
}
