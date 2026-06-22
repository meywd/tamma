using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Tamma.Api.Auth;

/// <summary>
/// Story 32-5 (T4, Finding C2) — requirement that the caller is the
/// <b>engine/service principal</b>, not an interactive tenant user.
///
/// <para>It succeeds <em>only</em> when the request carries a typed
/// <see cref="ServiceAuthPrincipal"/> on <see cref="HttpContext.Items"/> — the
/// principal that <see cref="ApiKeyAuthHandler"/> mints for a
/// <c>service</c>-scope API key (the engine's <c>Tamma:ApiToken</c>). A user
/// JWT authenticates through the JwtBearer scheme and never produces a
/// <see cref="ServiceAuthPrincipal"/>, so it is rejected (⇒ 403) — which is the
/// whole point: the internal LLM-mediation endpoint
/// (<c>POST /api/v1/llm/call</c>) drives arbitrary LLM spend + tool execution
/// on behalf of a tenant and must be reachable by the engine ONLY, not by any
/// authenticated tenant user.</para>
///
/// <para>This is deliberately a <em>type</em> check, not a permission check.
/// The established engine→API callbacks (<c>/api/engine/*</c>) ride the
/// <c>workflows:manage</c>/<c>workflows:view</c> permission policies, which a
/// tenant <c>owner</c>/<c>admin</c> JWT <em>also</em> satisfies — so they are
/// not actually engine-only. Keying off the service-principal TYPE closes that
/// gap for this endpoint without depending on a particular permission grant on
/// the engine key.</para>
/// </summary>
public sealed class ServicePrincipalRequirement : IAuthorizationRequirement;

/// <summary>
/// Authorization handler for <see cref="ServicePrincipalRequirement"/>. Reads
/// the typed <see cref="AuthPrincipal"/> set by <see cref="ApiKeyAuthHandler"/>
/// (via <see cref="HttpContextAuthExtensions.GetAuthPrincipal"/>) and succeeds
/// only for a <see cref="ServiceAuthPrincipal"/>.
/// </summary>
public sealed class ServicePrincipalHandler(IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<ServicePrincipalRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ServicePrincipalRequirement requirement)
    {
        var http = httpContextAccessor.HttpContext;
        if (http is null)
            return Task.CompletedTask; // fail closed — no context ⇒ no grant.

        // The ONLY accepted shape: a service-scope API key principal. A user
        // JWT (JwtBearer scheme) never lands a ServiceAuthPrincipal here, so it
        // falls through to a 403.
        if (http.GetAuthPrincipal() is ServiceAuthPrincipal)
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
