using System.Security.Claims;
using Tamma.Api.Auth;
using Tamma.Core.Actions;

namespace Tamma.Api.Services.Actions;

/// <summary>
/// Story 43-13 (AC1/D3) — <b>THE one place a <see cref="CallerKind"/> is computed
/// from auth state.</b> Every HTTP seam that passes a caller kind into the
/// autonomy gate calls this; <c>CallerKindResidencyTests</c> fails if a second
/// site grows the same inspection.
///
/// <para><b>The table (typed principal first, fail-closed to Llm):</b></para>
/// <list type="number">
/// <item><see cref="ServiceAuthPrincipal"/> / <see cref="InstallationAuthPrincipal"/>
/// → <see cref="CallerKind.Llm"/>. The engine token, any service key and any
/// GitHub-App installation key are fail-closed: deterministic workflow steps
/// share <c>TammaApiClient</c> with LLM-driven steps and cannot be told apart,
/// and until a call is provably human, the gate treats it as the model acting.</item>
/// <item><see cref="UserAuthPrincipal"/> → <see cref="CallerKind.Human"/> —
/// a user-scope key is a user credential.</item>
/// <item>No typed principal but a <c>"scope"</c> claim of <c>service</c> /
/// <c>installation</c> → <see cref="CallerKind.Llm"/> (belt-and-braces if
/// <c>HttpContext.Items</c> is lost across a context copy).</item>
/// <item>JWT plane: an authenticated identity with a resolvable user id
/// (<c>sub</c> / <c>NameIdentifier</c>) → <see cref="CallerKind.Human"/>.</item>
/// <item>Anything else (anonymous, malformed) → <see cref="CallerKind.Llm"/>.</item>
/// </list>
///
/// <para><b><see cref="CallerKind.Machinery"/> is NEVER returned here and has no
/// wire spelling at all.</b> It exists only as the in-process declaration Seam
/// D's helper makes (<c>BackgroundActionGate</c>). A machinery key scope or
/// header was rejected by design: a credential can be exfiltrated into an LLM
/// path (the recorded shell-curl bypass), and a wire-claimable "never gate me"
/// kind is a self-service bypass.</para>
///
/// <para><b>Why not <c>GetUserId() != null</c> alone:</b> that works only by
/// the accident that service-key OwnerIds are non-Guid strings
/// (<c>ApiKeyAuthHandler</c> puts the service NAME in <c>NameIdentifier</c>);
/// the typed principal is the honest source, and the claim checks below are
/// ordered so the service/installation scopes are ruled out BEFORE the user-id
/// fallback is consulted.</para>
/// </summary>
public static class CallerKindResolver
{
    /// <summary>Resolve the caller kind for the current request. See the class
    /// doc for the table.</summary>
    public static CallerKind Resolve(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        // 1/2 — the typed principal is the honest source.
        switch (http.GetAuthPrincipal())
        {
            case ServiceAuthPrincipal or InstallationAuthPrincipal:
                return CallerKind.Llm;
            case UserAuthPrincipal:
                return CallerKind.Human;
        }

        var user = http.User;

        // 3 — belt-and-braces: the api-key handler stamps a "scope" claim; if
        // Items was lost across a context copy, the claim still names the kind.
        var scope = user?.FindFirst("scope")?.Value;
        if (scope is "service" or "installation")
        {
            return CallerKind.Llm;
        }
        if (scope is "user")
        {
            return CallerKind.Human;
        }

        // 4 — the JWT plane: an authenticated person with a resolvable user id.
        if (user?.Identity?.IsAuthenticated == true && user.GetUserId() is not null)
        {
            return CallerKind.Human;
        }

        // 5 — fail closed: anonymous / malformed is the model until proven
        // otherwise.
        return CallerKind.Llm;
    }
}
