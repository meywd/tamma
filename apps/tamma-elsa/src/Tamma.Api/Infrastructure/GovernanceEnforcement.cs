using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Tamma.Api.Services.Actions;
using Tamma.Core.Actions;
using Tamma.Core.Logging;

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
///   <item><b>A DECISION THAT COULD NOT BE RECORDED still BLOCKS</b> (review F2,
///   2026-08-01). <see cref="AutonomyGateDecisionUnrecordedException"/> is not a
///   transient fault: the gate decided, and only the audit row failed. It is
///   caught BEFORE the catch-all and the decision it carries is re-applied. The
///   distinction matters because the two failures are CORRELATED — 43-5's
///   fail-closed degradation fires exactly when the control plane is unreadable,
///   and <c>domain_events</c> lives in the same Postgres — so without it one DB
///   blip produced the fail-closed decision and then converted it back into a
///   pass.</item>
///   <item><b>A HANG is bounded</b> (review F9). Every evaluation runs under
///   <see cref="AutonomyGateDeadline"/>: a catch covers a throw, not a gate that
///   never returns, and an unbounded evaluation hangs the request until the
///   client disconnects — neither open nor closed. A timeout resolves into the
///   transient arm above.</item>
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
    /// or the 409 denial. See the class doc's failure posture.
    ///
    /// <para><b>It throws exactly one thing</b> (review F10, 2026-08-01 — the doc
    /// previously claimed "NEVER throws" while doing this): an
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> — the
    /// CALLER's token, i.e. the client disconnected or the host is shutting down —
    /// is cancelled. That is not a governance failure and must not be laundered
    /// into a governance decision in either direction. The
    /// <see cref="AutonomyGateDeadline"/> timeout is a different cancellation and
    /// is handled here, not rethrown.</para>
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
        // F10 — resolved OUT HERE, with the other static wiring. It used to sit
        // inside the try below as GetRequiredService<>(), so a host that
        // registered the gate but not the resolver threw into the TRANSIENT
        // catch-all and the request PROCEEDED — a static wiring fault failing
        // OPEN, against this class's own stated split. Not reachable today (both
        // are registered by the same extension method), which is precisely why it
        // needs to be structural rather than incidental.
        var principals = services.GetService<IGovernancePrincipalResolver>();

        if (binding is null || gate is null || principals is null)
        {
            // FAIL CLOSED — static wiring, not a blip. See the class doc.
            logger?.LogError(
                "Governance enforcement is opted in for {Method} {Path} but {Missing}. The request "
                + "is REFUSED (409 {Code}) rather than silently ungoverned; this is a wiring fault, "
                + "not a policy decision.",
                // Method and Path are attacker-controlled: a percent-encoded %0A
                // decodes into PathString.Value, and this message asserts a
                // governance outcome. A forged copy of THIS line is a false audit
                // record, so it goes through the same LogSanitizer every other
                // request-path log site in the API uses.
                LogSanitizer.Clean(http.Request.Method),
                LogSanitizer.Clean(http.Request.Path.Value),
                binding is null
                    ? "the endpoint carries no .Governs(actionKey) binding"
                    : gate is null
                        ? "no IAutonomyGate is registered in this host"
                        : "no IGovernancePrincipalResolver is registered in this host",
                MisconfiguredCode);

            return new GovernanceDenial(new
            {
                code = MisconfiguredCode,
                error = "This endpoint opted into governance enforcement but the gate cannot be "
                    + "evaluated for it (missing binding, missing gate registration, or missing "
                    + "principal resolver).",
            });
        }

        AutonomyDecision decision;
        GovernancePrincipal principal;
        var correlationId = ResolveCorrelationId(http);
        // F9 — a catch covers a THROW, not a HANG.
        using var deadline = AutonomyGateDeadline.CreateLinkedSource(ct);
        try
        {
            principal = await principals.ResolveAsync(http.User, deadline.Token)
                .ConfigureAwait(false);

            decision = await gate.EvaluateAsync(
                new AutonomyQuery(
                    binding.Action,
                    principal,
                    Role: null,
                    Operation: $"{http.Request.Method} {http.Request.Path}",
                    Target: http.Request.Path.Value,
                    CorrelationId: correlationId,
                    // F4 — Seam C BLOCKS, so it may spend a single-use grant.
                    SeamCanBlock: true,
                    // Story 43-13 — WHO is asking, from the ONE resolver: a
                    // user credential passes ungated (ReasonCallerHuman); the
                    // engine token and everything not provably human is the
                    // LLM, fail-closed.
                    Caller: CallerKindResolver.Resolve(http)),
                deadline.Token).ConfigureAwait(false);
        }
        catch (AutonomyGateDecisionUnrecordedException unrecorded)
        {
            // F2 — NOT a transient fault. The gate decided; only the record of it
            // failed. Answering "proceed" here is how a blip in the one Postgres
            // that holds BOTH action_assignments and domain_events turns a
            // fail-closed denial back into a pass.
            logger?.LogError(unrecorded,
                "Autonomy gate DECIDED {Outcome} for {Action} at {Method} {Path} but the audit row "
                + "could not be written. The decision STANDS — an unrecordable block is still a "
                + "block, never a transient fault.",
                unrecorded.Decision.Outcome,
                unrecorded.Decision.Action.ToWire(),
                LogSanitizer.Clean(http.Request.Method),
                LogSanitizer.Clean(http.Request.Path.Value));

            if (!unrecorded.Blocks) return null;
            return Denial(unrecorded.Decision, correlationId, authorizationId: null);
        }
        catch (OperationCanceledException) when (!AutonomyGateDeadline.IsDeadline(ct))
        {
            // The CALLER cancelled (client gone / host stopping). Not governance.
            throw;
        }
        catch (Exception ex)
        {
            // FAIL OPEN — deny on a DECISION, never on an ERROR (D8 posture,
            // applied at every seam). The gate's own fail-CLOSED handling of an
            // unreadable policy input is a decision and is unaffected by this.
            logger?.LogError(ex,
                "Autonomy gate evaluation FAILED for {Action} at {Method} {Path}; the request "
                + "PROCEEDS ungated (deny on a decision, never on an error) and "
                + "ACTION.GATE.EVALUATION_FAILED is emitted.",
                // Sanitized for the same reason as the wiring-fault log above, and
                // more urgently: this line says the request PROCEEDED UNGATED, so a
                // forged copy could convince an auditor a gate was bypassed when it
                // was not — or hide a real one in noise. Action comes from the
                // catalog, not the request, so it needs no cleaning.
                binding.Action.ToWire(),
                LogSanitizer.Clean(http.Request.Method),
                LogSanitizer.Clean(http.Request.Path.Value));

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
        // than a null field. `correlationId` is never null here (see
        // ResolveCorrelationId), so a row is always attempted; the id comes back
        // null only when the ledger itself is unavailable, which is honest.
        Guid? authorizationId = null;
        var requests = services.GetService<IActionAuthorizationRequests>();
        if (requests is not null)
        {
            authorizationId = await requests
                .RequestAsync(principal, decision, correlationId, ct)
                .ConfigureAwait(false);
        }

        return Denial(decision, correlationId, authorizationId);
    }

    /// <summary>The 409 body — one shape, whether the decision arrived normally
    /// or came back attached to an unrecorded-decision failure (F2).</summary>
    private static GovernanceDenial Denial(
        AutonomyDecision decision, string correlationId, Guid? authorizationId) =>
        new(new
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

    /// <summary>
    /// The run a gate decision belongs to: the <see cref="CorrelationHeader"/>
    /// header, else a <c>?correlationId=</c> query value, else the ROUTE-DERIVED
    /// correlation below. NEVER the request body (reading it here would consume
    /// the stream the handler binds from) and never a per-request identity such as
    /// <c>HttpContext.TraceIdentifier</c> — a correlation that changes on every
    /// retry cannot carry one human decision across a run, which is the entire
    /// purpose of the ledger.
    ///
    /// <para><b>Why it can no longer return null</b> (review F5, 2026-08-01). Not
    /// one opted-in route sends the header or the query value — every one of them
    /// is an engine mediation route called by <c>TammaApiClient</c>, which sets
    /// neither — so this returned null on every real request, no pending row was
    /// ever minted at Seam C, and the 409 nonetheless told the caller to "Grant the
    /// pending authorization (POST …/{id}/decide)" with no id and no row in
    /// existence. The 409 was unactionable: the block could not be cleared by a
    /// person, only by editing policy.</para>
    ///
    /// <para><b>The derived value is the METHOD and the CONCRETE PATH.</b> That
    /// makes it DETERMINISTIC — the retry of the same request derives the same
    /// correlation and finds the grant a person made — which is the one property
    /// the ledger needs (it is keyed by principal + correlation + target, which is
    /// why a per-request id would be useless). It is narrow: the concrete path,
    /// not the route pattern, so a grant for <c>acme/widget</c> does not cover
    /// <c>acme/other</c>; and it is still SINGLE-USE, so the grant covers the next
    /// call and no more. It is prefixed <c>route:</c> so an auditor can tell a
    /// derived correlation from a run correlation the caller supplied.</para>
    ///
    /// <para>The query string is deliberately EXCLUDED from the derived value: it
    /// is caller-controlled and unbounded, so including it would let a caller mint
    /// an unbounded number of distinct pending rows for one effect.</para>
    /// </summary>
    private static string ResolveCorrelationId(HttpContext http)
    {
        if (http.Request.Headers.TryGetValue(CorrelationHeader, out var header))
        {
            var value = header.ToString();
            if (!string.IsNullOrWhiteSpace(value)) return Bounded(value);
        }

        if (http.Request.Query.TryGetValue("correlationId", out var query))
        {
            var value = query.ToString();
            if (!string.IsNullOrWhiteSpace(value)) return Bounded(value);
        }

        return Bounded($"{DerivedCorrelationPrefix}{http.Request.Method} {http.Request.Path.Value}");
    }

    /// <summary>Marks a correlation this seam derived rather than one the caller
    /// named.</summary>
    internal const string DerivedCorrelationPrefix = "route:";

    /// <summary>
    /// <c>action_authorizations.CorrelationId</c> is <c>varchar(200)</c>. A value
    /// past that would make the ledger insert fail (and the 409 carry a null id
    /// again), so an over-long correlation collapses to a DETERMINISTIC digest of
    /// itself rather than being truncated — truncation would silently merge two
    /// different long correlations into one grant.
    /// </summary>
    private static string Bounded(string value) =>
        value.Length <= MaxCorrelationLength
            ? value
            : "sha256:" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>The ledger column's width.</summary>
    internal const int MaxCorrelationLength = 200;
}
