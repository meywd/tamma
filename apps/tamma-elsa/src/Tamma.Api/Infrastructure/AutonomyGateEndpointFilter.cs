using Tamma.Core.Logging;

namespace Tamma.Api.Infrastructure;

/// <summary>
/// Story 43-9 Seam C (AC7/AC8) — the MINIMAL-API half of the D15 enforcement
/// opt-in. Attached only by
/// <see cref="EnforcesGovernanceExtensions.EnforcesGovernance"/>, never by
/// <see cref="GovernsExtensions.Governs"/>; the controller half is
/// <see cref="EnforcesGovernanceAttribute"/>, because an
/// <see cref="IEndpointFilter"/> does not run for MVC endpoints.
///
/// <para>All the reasoning — why a filter rather than an
/// <c>IAuthorizationHandler</c>, why 409 and not 202, and why a static wiring
/// fault fails closed while a transient evaluation fault fails open — lives on
/// <see cref="AutonomyGateEnforcement"/>, which both planes call so they cannot
/// drift.</para>
///
/// <para><b>THE FILTER REQUIRES THE MARKER</b> (adversarial review F8,
/// 2026-08-01). <c>GovernedEndpointEnforcementSweepTests</c> pins the opted-in set
/// EXACTLY, and it — like every harness — computes enforcement from
/// <see cref="IGovernanceEnforcementMetadata"/> alone. This filter used to enforce
/// whether or not that metadata was present, so a route written as
/// <c>.Governs(key).AddEndpointFilter&lt;AutonomyGateEndpointFilter&gt;()</c> —
/// without <c>.EnforcesGovernance()</c> — returned live 409s while the pin
/// reported it unenforced: the ratchet bypassed, silently, in the direction that
/// adds enforcement nobody reviewed. The filter now refuses to gate a route that
/// carries no marker, and says so as a WIRING FAULT (409
/// <c>ACTION.GATE.MISCONFIGURED</c>) rather than proceeding: the route is
/// misconfigured either way, and a misconfiguration on an enforcement surface is
/// the fail-CLOSED case by this epic's own split. No live divergence exists today
/// — <c>.EnforcesGovernance()</c> attaches both — which is exactly why the
/// invariant has to be structural rather than a convention.</para>
/// </summary>
public sealed class AutonomyGateEndpointFilter : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;
        if (http.GetEndpoint()?.Metadata.GetMetadata<IGovernanceEnforcementMetadata>() is null)
        {
            // F8 — attached without the opt-in marker. Enforcing here would make
            // the route gate while every harness reports it ungated.
            http.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger<AutonomyGateEndpointFilter>()
                .LogError(
                    "AutonomyGateEndpointFilter is attached to {Method} {Path} but the endpoint "
                    + "carries no IGovernanceEnforcementMetadata opt-in marker. Enforcing would "
                    + "gate a route every governance harness reports as UNENFORCED, so the request "
                    + "is REFUSED (409 {Code}). Use .EnforcesGovernance(), which attaches both.",
                    LogSanitizer.Clean(http.Request.Method),
                    LogSanitizer.Clean(http.Request.Path.Value),
                    AutonomyGateEnforcement.MisconfiguredCode);

            return Results.Json(
                new
                {
                    code = AutonomyGateEnforcement.MisconfiguredCode,
                    error = "This endpoint has the autonomy-gate filter attached without the "
                        + "enforcement opt-in marker. Bind it with .EnforcesGovernance(), which "
                        + "attaches the marker and the filter together.",
                },
                statusCode: StatusCodes.Status409Conflict);
        }

        var denial = await AutonomyGateEnforcement
            .EvaluateAsync(http, http.RequestAborted)
            .ConfigureAwait(false);

        return denial is not null
            ? Results.Json(denial.Body, statusCode: StatusCodes.Status409Conflict)
            : await next(context).ConfigureAwait(false);
    }
}
