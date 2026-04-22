using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 18-4 — onboarding wizard backing endpoints.
///
/// The wizard in the dashboard polls <c>GET /api/v1/onboarding/status</c> to
/// figure out which step to show: email-verified, org-created,
/// installation-linked, or "all done". When the user clicks
/// <c>Connect GitHub</c> it hits <c>GET /api/v1/onboarding/install-github</c>
/// which 302s to the GitHub App install page. After GitHub bounces the
/// user back to <c>GET /api/github/callback</c> (handled by
/// <see cref="GitHubEndpoints.Callback"/>), the dashboard wizard sees a
/// non-empty <c>installations</c> array on its next poll and advances
/// to the success step.
///
/// We deliberately keep this endpoint surface tight — a single status read
/// + a single install-redirect — so the bulk of the install flow stays in
/// <see cref="Services.GitHub.InstallationRouterService"/> (already
/// implemented). The full install plan in
/// <c>docs/stories/epic-18/18-4-github-app-installation-onboarding-impl-plan.md</c>
/// describes a much wider surface (per-repo activation, settings,
/// first-run workflow); those land in follow-up stories.
/// </summary>
public static class OnboardingEndpoints
{
    /// <summary>
    /// Default GitHub App install URL when <c>GitHubApp:InstallUrl</c> is not
    /// configured. The dev/test fallback points at a placeholder slug; deploy
    /// must override via configuration before any user-facing rollout.
    /// </summary>
    private const string DefaultInstallUrl =
        "https://github.com/apps/tamma-dev/installations/new";

    public static async Task<IResult> GetStatus(
        ClaimsPrincipal principal,
        IUserRepository userRepo,
        ITenantMembershipRepository membershipRepo,
        IInstallationRepository installations)
    {
        var userId = ResolveUserId(principal);
        if (userId is null) return Results.Unauthorized();

        var user = await userRepo.GetByIdAsync(userId.Value);
        if (user is null) return Results.Unauthorized();

        // ── 1. Email verification ─────────────────────────────────────────
        // GitHub-OAuth users come in pre-verified (no email click); the
        // password-auth flow gates this on User.EmailVerified being set by
        // the verify-email handler.
        var emailVerified = user.EmailVerified
            || string.Equals(user.AuthMethod, "github", StringComparison.OrdinalIgnoreCase);

        // ── 2. Org / tenant ───────────────────────────────────────────────
        // Active tenant on the user wins; if absent, fall through to
        // membership lookup (covers the "user accepted an invite but never
        // switched into it" edge). Personal tenants from /auth/register
        // count as a "has org" — the wizard skips org creation for them.
        var activeTenantId = user.TenantId;
        if (activeTenantId is null)
        {
            var memberships = await membershipRepo.GetUserTenantsAsync(user.Id);
            if (memberships.Count > 0)
                activeTenantId = memberships[0].TenantId;
        }
        var hasOrg = activeTenantId is not null;

        // ── 3. Installation links ─────────────────────────────────────────
        // We only count installations bound to the user's active tenant —
        // an orphan install (tenant_id null) does NOT satisfy the wizard.
        // The dashboard surfaces orphans separately via the success page's
        // claim-installation flow.
        var installationDtos = new List<OnboardingInstallationDto>();
        if (activeTenantId is not null)
        {
            var rows = await installations.ListByTenantAsync(activeTenantId.Value);
            foreach (var row in rows)
            {
                installationDtos.Add(new OnboardingInstallationDto(
                    InstallationId: row.InstallationId,
                    AccountLogin: row.AccountLogin,
                    AccountType: row.AccountType,
                    Suspended: row.SuspendedAt is not null,
                    RepoCount: row.Repos.Count(r => r.IsActive),
                    Repos: row.Repos
                        .Where(r => r.IsActive)
                        .OrderBy(r => r.RepoFullName)
                        .Take(20) // wizard only renders a preview; cap to keep payload small
                        .Select(r => new OnboardingRepoDto(r.RepoId, r.RepoFullName))
                        .ToList()));
            }
        }
        var hasInstallation = installationDtos.Any(d => !d.Suspended);

        return Results.Ok(new OnboardingStatusResponse(
            EmailVerified: emailVerified,
            HasOrg: hasOrg,
            TenantId: activeTenantId,
            HasInstallation: hasInstallation,
            InstallationCount: installationDtos.Count,
            Installations: installationDtos));
    }

    /// <summary>
    /// Issues a 302 to GitHub's App install page with a signed
    /// <c>state</c> param so the callback (audit-finding 020) can re-bind the
    /// new install to the caller's active tenant. The state token is a
    /// short-lived (10 min) HS256 JWT signed with the same Jwt:Secret used
    /// for the session cookie — it carries (userId, tenantId, nonce) and is
    /// validated + nonce-checked by <see cref="GitHubEndpoints.Callback"/>.
    /// </summary>
    /// <remarks>
    /// The GitHub App page is the "external" half of the OAuth-style hop;
    /// we cannot lock it down beyond signing the state. The flow is the
    /// same shape used by GitHub OAuth (<see cref="AuthEndpoints.GitHubAuth"/>).
    /// When <c>GitHubApp:InstallUrl</c> is unset we fall back to the
    /// canonical <c>tamma-dev</c> slug; ops can override per-environment.
    /// </remarks>
    public static IResult InstallGitHub(
        ClaimsPrincipal principal,
        [FromServices] IConfiguration config)
    {
        var userId = ResolveUserId(principal);
        if (userId is null) return Results.Unauthorized();

        var tenantClaim = principal.FindFirst("tenantId")?.Value
            ?? principal.FindFirst("tid")?.Value;
        Guid? tenantId = Guid.TryParse(tenantClaim, out var parsedTid) ? parsedTid : null;

        var installUrl = (config["GitHubApp:InstallUrl"]
            ?? config["GitHub:InstallUrl"]
            ?? DefaultInstallUrl).TrimEnd('/');

        // Build the state token. We intentionally avoid persisting the nonce
        // here — replay protection lives at the callback boundary (Step 3
        // in the impl plan); the JWT's `exp` claim caps the abuse window
        // to 10 minutes, which is short enough that the
        // existing-installation upsert in the callback (router.HandleCallbackAsync)
        // is the practical replay defence: re-running the same state simply
        // re-links the same install to the same tenant — a no-op.
        var state = IssueStateToken(config, userId.Value, tenantId);

        var redirect = installUrl.Contains('?')
            ? $"{installUrl}&state={Uri.EscapeDataString(state)}"
            : $"{installUrl}?state={Uri.EscapeDataString(state)}";

        return Results.Redirect(redirect);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static Guid? ResolveUserId(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    /// <summary>
    /// Mints a short-lived HS256 JWT to carry (userId, tenantId, nonce)
    /// across the GitHub redirect hop. We use the same <c>Jwt:Secret</c>
    /// the session cookie does so there is a single rotation surface.
    ///
    /// Public so tests can verify the round-trip semantics without
    /// invoking the full HTTP stack.
    /// </summary>
    public static string IssueStateToken(IConfiguration config, Guid userId, Guid? tenantId)
    {
        var secret = config["Jwt:Secret"];
        if (string.IsNullOrEmpty(secret) || secret.Length < 32)
        {
            // Fail loudly in dev; production deploys must set Jwt:Secret to
            // the same 32+ char value used by IJwtService.
            throw new InvalidOperationException(
                "Jwt:Secret must be set (>= 32 chars) to mint onboarding state tokens.");
        }

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
            .ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var exp = now.AddMinutes(10).ToUnixTimeSeconds();

        var header = Base64UrlEncode("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"u8.ToArray());
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["sub"] = userId.ToString(),
                ["tid"] = tenantId?.ToString(),
                ["nonce"] = nonce,
                ["exp"] = exp,
                ["iat"] = now.ToUnixTimeSeconds(),
                ["typ"] = "github-install-state",
            });
        var payload = Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(payloadJson));

        var signingInput = $"{header}.{payload}";
        using var hmac = new HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        var sig = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(signingInput));
        var signature = Base64UrlEncode(sig);

        return $"{signingInput}.{signature}";
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

/// <summary>
/// DTO returned from <c>GET /api/v1/onboarding/status</c>. Field order +
/// names match the dashboard's <c>OnboardingStatus</c> TypeScript interface
/// in <c>packages/dashboard/src/services/onboarding/onboarding-api-client.ts</c>.
/// </summary>
public sealed record OnboardingStatusResponse(
    bool EmailVerified,
    bool HasOrg,
    Guid? TenantId,
    bool HasInstallation,
    int InstallationCount,
    IReadOnlyList<OnboardingInstallationDto> Installations);

public sealed record OnboardingInstallationDto(
    long InstallationId,
    string AccountLogin,
    string AccountType,
    bool Suspended,
    int RepoCount,
    IReadOnlyList<OnboardingRepoDto> Repos);

public sealed record OnboardingRepoDto(long RepoId, string FullName);
