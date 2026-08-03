using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Services.Actions;
using Tamma.Core.Actions;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-13 D3 — the resolver table, one test per row. The resolver is THE
/// single place a <see cref="CallerKind"/> is computed from auth state (AC1);
/// <c>CallerKindResidencyTests</c> pins that no second site grows.
/// </summary>
[TestFixture]
public class CallerKindResolverTests
{
    private static DefaultHttpContext Http(
        AuthPrincipal? typed = null, ClaimsPrincipal? user = null)
    {
        var http = new DefaultHttpContext();
        if (typed is not null) http.SetAuthPrincipal(typed);
        if (user is not null) http.User = user;
        return http;
    }

    private static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "test"));

    [Test]
    public void AServicePrincipal_IsLlm()
    {
        // The engine token (Tamma:ApiToken via TammaEngineAuthHandler) and any
        // service-scope key mint this shape. Fail-closed: deterministic workflow
        // steps share TammaApiClient with LLM steps and cannot be told apart.
        var http = Http(new ServiceAuthPrincipal(
            Guid.NewGuid(), "tamma-engine", Array.Empty<string>(), null));

        CallerKindResolver.Resolve(http).Should().Be(CallerKind.Llm);
    }

    [Test]
    public void AnInstallationPrincipal_IsLlm()
    {
        // A GitHub-App installation credential is not provably a person.
        var http = Http(new InstallationAuthPrincipal(Guid.NewGuid(), 4242, null));

        CallerKindResolver.Resolve(http).Should().Be(CallerKind.Llm);
    }

    [Test]
    public void AUserPrincipal_IsHuman()
    {
        // A user-scope API key is a user credential.
        var http = Http(new UserAuthPrincipal(
            Guid.NewGuid(), Guid.NewGuid(), "admin", Guid.NewGuid()));

        CallerKindResolver.Resolve(http).Should().Be(CallerKind.Human);
    }

    [Test]
    public void AJwtSubPrincipal_IsHuman()
    {
        // The dashboard plane: MapInboundClaims=false, so the user id lives in
        // the verbatim `sub` claim.
        var http = Http(user: Authenticated(
            new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new Claim("role", "admin")));

        CallerKindResolver.Resolve(http).Should().Be(CallerKind.Human);
    }

    [Test]
    public void AServiceScopeClaim_WithoutTheTypedPrincipal_IsStillLlm()
    {
        // Belt-and-braces (D3 rule 3): if HttpContext.Items is lost across a
        // context copy, the api-key handler's "scope" claim still names the
        // kind — and it must be checked BEFORE any user-id fallback, because a
        // service key's NameIdentifier is a service NAME, not a Guid, and that
        // is an accident this resolver refuses to build on.
        var http = Http(user: Authenticated(
            new Claim(ClaimTypes.NameIdentifier, "tamma-engine"),
            new Claim("scope", "service")));

        CallerKindResolver.Resolve(http).Should().Be(CallerKind.Llm);
    }

    [Test]
    public void AnAnonymousCaller_IsLlm()
    {
        // Fail-closed (D3 rule 5): anonymous / malformed is the model until
        // proven otherwise.
        var http = Http(user: new ClaimsPrincipal(new ClaimsIdentity()));
        CallerKindResolver.Resolve(http).Should().Be(CallerKind.Llm);

        CallerKindResolver.Resolve(Http()).Should().Be(CallerKind.Llm);
    }

    [Test]
    public void AnAuthenticatedIdentity_WithNoResolvableUserId_IsLlm()
    {
        // Authenticated but no `sub`/NameIdentifier Guid — not provably human.
        var http = Http(user: Authenticated(new Claim("name", "mystery")));

        CallerKindResolver.Resolve(http).Should().Be(CallerKind.Llm);
    }

    [Test]
    public void MachineryIsNeverResolvedFromTheWire()
    {
        // AC1/D3 — Machinery has NO wire spelling: no principal shape, claim or
        // header may produce it (a wire-claimable "never gate me" kind is a
        // self-service bypass; the shell-curl hole makes that concrete). The
        // hostile inputs below try the obvious spellings.
        var attempts = new[]
        {
            Http(user: Authenticated(new Claim("scope", "machinery"))),
            Http(user: Authenticated(new Claim("callerKind", "machinery"))),
            Http(new ServiceAuthPrincipal(
                Guid.NewGuid(), "machinery", Array.Empty<string>(), null)),
        };

        foreach (var http in attempts)
        {
            CallerKindResolver.Resolve(http).Should().NotBe(CallerKind.Machinery,
                "Machinery exists only as Seam D's in-process declaration");
        }
    }
}
