namespace Tamma.Api.Dtos.Auth;

public record RegisterRequest(string Email, string Password, string? DisplayName);
public record RegisterResponse(Guid UserId, string Message);
public record LoginRequest(string Email, string Password);
public record LoginResponse(string AccessToken, int ExpiresIn, UserInfo User);
public record RefreshResponse(string AccessToken, int ExpiresIn);
public record PasswordResetRequestDto(string Email);
public record PasswordResetConfirmDto(string Token, string NewPassword);
public record VerifyEmailRequest(string Token);
public record ResendVerificationRequest(string Email);
public record UserInfo(Guid Id, string Email, string? DisplayName, string Role, Guid? TenantId);
public record MeResponse(Guid Id, string Email, string? DisplayName, string Role, Guid? TenantId, List<MembershipInfo> Memberships);
public record MembershipInfo(Guid TenantId, string TenantName, string Role);
public record RoleCheckResponse(string Role, string[] Permissions);
