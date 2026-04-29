using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Services.Onboarding;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 31-9 — onboarding picker backend. Two routes:
/// <list type="bullet">
///   <item><c>GET /api/onboarding/platforms</c> — returns the list of
///         platforms the picker can render. Shape includes
///         <c>kind</c>, <c>displayName</c>, <c>available</c> (true if
///         a real driver factory is registered), and
///         <c>capabilities</c> from the static matrix. Members can
///         see this — it leaks no secrets.</item>
///   <item><c>POST /api/onboarding/install</c> — wires a credential to
///         the cabinet + writes the installation row. Auth-gated by
///         <c>PlatformsManage</c> (admin+owner). Returns 400 with a
///         hint string when the auth probe fails or any input is
///         malformed.</item>
///   <item><c>GET /api/onboarding/installations</c> — lists the
///         caller's tenant connected platforms (post-onboarding
///         settings panel).</item>
/// </list>
/// </summary>
public static class PlatformInstallEndpoints
{
    /// <summary>
    /// Member-visible: every platform kind the static capability matrix
    /// covers, plus an <c>available</c> flag indicating whether a real
    /// driver factory is registered. Bitbucket / AzureDevOps appear
    /// with <c>available=false</c> until 31-11 / 31-12 ship; the picker
    /// renders them as "coming soon".
    /// </summary>
    public static IResult ListPlatforms([FromServices] IServiceProvider services)
    {
        var items = new List<object>();
        foreach (var kind in Enum.GetValues<PlatformKind>())
        {
            var factory = services.GetKeyedService<IGitPlatformDriverFactory>(kind);
            var capabilities = PlatformKindCapabilityMatrix
                .DefaultsFor(kind)
                .Select(c => c.ToString())
                .ToArray();
            items.Add(new
            {
                kind = kind.ToString(),
                displayName = DisplayName(kind),
                available = factory is not null,
                capabilities,
                authMode = AuthMode(kind),
            });
        }
        return Results.Ok(new { items, count = items.Count });
    }

    /// <summary>
    /// Auth-gated POST. The endpoint trusts route authorization
    /// (<c>PlatformsManage</c>) for the role check; it derives the
    /// tenant from the JWT's <c>tenantId</c> / <c>tid</c> claim so the
    /// caller cannot inject a different tenant via the body.
    /// </summary>
    public static async Task<IResult> Install(
        [FromBody] PlatformInstallRequestBody body,
        ClaimsPrincipal principal,
        [FromServices] IPlatformConnectService connect,
        HttpContext http)
    {
        if (body is null)
            return Results.BadRequest(new { error = "request body is required" });

        var userId = ResolveUserId(principal);
        if (userId is null) return Results.Unauthorized();

        var tenantId = ResolveTenantId(principal);
        if (tenantId is null)
            return Results.BadRequest(new
            {
                error = "no tenant in JWT — caller must be in a tenant context",
            });

        if (!Enum.TryParse<PlatformKind>(body.Kind, ignoreCase: true, out var kind))
            return Results.BadRequest(new
            {
                error = $"unknown platform kind '{body.Kind}'",
            });

        if (string.IsNullOrWhiteSpace(body.BaseUrl))
            return Results.BadRequest(new { error = "baseUrl is required" });
        if (string.IsNullOrWhiteSpace(body.CredentialPlaintext))
            return Results.BadRequest(new { error = "credentialPlaintext is required" });

        var request = new PlatformConnectRequest(
            TenantId: tenantId.Value,
            ActorUserId: userId.Value,
            Kind: kind,
            BaseUrl: body.BaseUrl,
            ExternalId: string.IsNullOrWhiteSpace(body.ExternalId) ? null : body.ExternalId,
            CredentialPlaintext: body.CredentialPlaintext);

        var result = await connect.ConnectAsync(request, http.RequestAborted)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return Results.BadRequest(new
            {
                error = result.ErrorCode,
                hint = result.ErrorHint,
            });
        }

        return Results.Ok(new
        {
            installationId = result.InstallationId,
            kind = result.Kind?.ToString(),
            baseUrl = result.BaseUrl,
            externalId = result.ExternalId,
            status = "connected",
        });
    }

    /// <summary>
    /// Member-visible: list connected platforms for the caller's
    /// tenant. The picker page calls this on first load to show the
    /// "already connected" hint; the settings panel uses it as the
    /// table source.
    /// </summary>
    public static async Task<IResult> ListInstallations(
        ClaimsPrincipal principal,
        [FromServices] IPlatformConnectService connect,
        HttpContext http)
    {
        var tenantId = ResolveTenantId(principal);
        if (tenantId is null)
            return Results.Ok(new { items = Array.Empty<object>(), count = 0 });

        var rows = await connect
            .ListForTenantAsync(tenantId.Value, http.RequestAborted)
            .ConfigureAwait(false);

        var items = rows.Select(r => new
        {
            installationId = r.InstallationId,
            kind = r.Kind.ToString(),
            baseUrl = r.BaseUrl,
            externalId = r.ExternalId,
            status = r.Status,
            isPrimary = r.IsPrimary,
            createdAt = r.CreatedAt,
        }).ToList();

        return Results.Ok(new { items, count = items.Count });
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static Guid? ResolveUserId(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static Guid? ResolveTenantId(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst("tenantId")?.Value
            ?? principal.FindFirst("tid")?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static string DisplayName(PlatformKind kind) => kind switch
    {
        PlatformKind.GitHub => "GitHub",
        PlatformKind.Gitea => "Gitea",
        PlatformKind.Forgejo => "Forgejo",
        PlatformKind.GitLab => "GitLab",
        PlatformKind.Bitbucket => "Bitbucket",
        PlatformKind.AzureDevOps => "Azure DevOps",
        _ => kind.ToString(),
    };

    private static string AuthMode(PlatformKind kind) => kind switch
    {
        // GitHub uses the App-install deep-link flow (Story 18-4).
        PlatformKind.GitHub => "github_app",
        // Gitea / Forgejo / GitLab accept a PAT or bot token.
        PlatformKind.Gitea => "personal_access_token",
        PlatformKind.Forgejo => "personal_access_token",
        PlatformKind.GitLab => "personal_access_token",
        // Bitbucket / AzureDevOps drivers ship later — placeholder mode
        // tells the UI to show the coming-soon variant.
        PlatformKind.Bitbucket => "coming_soon",
        PlatformKind.AzureDevOps => "coming_soon",
        _ => "coming_soon",
    };
}

/// <summary>
/// Body shape for <c>POST /api/onboarding/install</c>. The picker UI
/// in <c>packages/dashboard-user</c> mirrors this record's field names
/// 1:1 — keep them in sync.
/// </summary>
public sealed record PlatformInstallRequestBody(
    string Kind,
    string BaseUrl,
    string? ExternalId,
    string CredentialPlaintext);
