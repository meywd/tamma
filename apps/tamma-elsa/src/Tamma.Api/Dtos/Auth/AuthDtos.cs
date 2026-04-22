namespace Tamma.Api.Dtos.Auth;

public record RegisterRequest(string Email, string Password, string? DisplayName);
public record RegisterResponse(Guid UserId, string Message);
public record LoginRequest(string Email, string Password);
public record LoginResponse(string AccessToken, string RefreshToken, int ExpiresIn, UserInfo User);
public record RefreshRequest(string? RefreshToken);
public record RefreshResponse(string AccessToken, string RefreshToken, int ExpiresIn);

/// <summary>
/// Story 28-9 — request body for <c>POST /api/v1/auth/switch-org</c>.
/// Carries the target tenant id plus an optional current refresh token; when
/// present the rotator revokes only that specific token (precise rotation),
/// when absent it revokes every refresh token for the user (defensive
/// rotation). Distinct from <see cref="Tamma.Api.Dtos.Orgs.SwitchOrgRequest"/>
/// (Story 18-3) which only carried <c>TenantId</c>.
/// </summary>
public record SwitchOrgRequest(Guid TenantId, string? RefreshToken = null);

/// <summary>
/// Story 28-9 — response from <c>POST /api/v1/auth/switch-org</c>. Includes
/// the rotated refresh token so non-cookie clients (CLI, integration tests)
/// can keep refreshing without a re-login.
/// </summary>
public record SwitchOrgResponse(
    string AccessToken,
    string RefreshToken,
    Guid TenantId,
    string Role,
    int ExpiresIn);
public record PasswordResetRequestDto(string Email);
public record PasswordResetConfirmDto(string Token, string NewPassword);
public record VerifyEmailRequest(string Token);
public record ResendVerificationRequest(string Email);
public record UserInfo(Guid Id, string Email, string? DisplayName, string Role, Guid? TenantId);

/// <summary>
/// Response wrapper for <c>/api/auth/me</c>. Mirrors the TS shape
/// <c>{ user: { id, username, githubId, role, ... } }</c> — the unified-nav
/// fetch reads <c>response.user.*</c>.
/// </summary>
public record MeResponse(MeUserPayload User);

public record MeUserPayload(
    Guid Id,
    string Email,
    string? DisplayName,
    long? GitHubId,
    string? Username,
    string Role,
    string PlatformRole,
    string AuthMethod,
    Guid? TenantId,
    List<MembershipInfo> Memberships);

public record MembershipInfo(Guid TenantId, string TenantName, string Role);

/// <summary>
/// Response from the nginx <c>auth_request</c> gate. nginx only inspects the
/// HTTP status (200 → allow, 401/403 → block) so the body is informational.
/// </summary>
public record RoleCheckResponse(bool Allowed, string Role);
