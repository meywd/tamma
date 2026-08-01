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
/// </summary>
public sealed class AutonomyGateEndpointFilter : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var denial = await AutonomyGateEnforcement
            .EvaluateAsync(context.HttpContext, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        return denial is not null
            ? Results.Json(denial.Body, statusCode: StatusCodes.Status409Conflict)
            : await next(context).ConfigureAwait(false);
    }
}
