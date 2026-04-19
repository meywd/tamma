using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IUserRepository
{
    Task<User> CreateAsync(User user);
    Task<User?> GetByIdAsync(Guid id);
    Task<User?> GetByEmailAsync(string email);
    Task<User?> GetByGitHubIdAsync(long githubId);

    /// <summary>
    /// Lookup by the SHA-256 hash of an email-verification token. Returns
    /// only non-deleted users with an unexpired-looking token (the caller is
    /// expected to re-check expiry against the wall clock).
    /// </summary>
    Task<User?> GetByEmailVerificationTokenHashAsync(string tokenHash);

    Task<(List<User> Users, int Total)> ListAsync(int limit, int offset, string? role);
    Task<User> UpdateAsync(User user);
    Task SoftDeleteAsync(Guid id);
    Task UpdateActiveTenantAsync(Guid userId, Guid tenantId);

    /// <summary>
    /// Marks the user's email as verified and clears the verification-token
    /// fields. No-op when the user is already verified.
    /// </summary>
    Task SetEmailVerifiedAsync(Guid id);

    /// <summary>Updates the verification-token fields for a resend.</summary>
    Task UpdateVerificationTokenAsync(Guid id, string tokenHash, DateTime expiresAt);

    /// <summary>Stores a fresh argon2id password hash for the user.</summary>
    Task UpdatePasswordHashAsync(Guid id, string passwordHash);

    /// <summary>
    /// Updates the user's <c>auth_method</c>. Used by OAuth account-linking
    /// (<c>email</c> + <c>github</c> → <c>both</c>).
    /// </summary>
    Task UpdateAuthMethodAsync(Guid id, string authMethod);

    /// <summary>Sets the GitHub identity fields after OAuth account linking.</summary>
    Task SetGitHubIdAsync(Guid id, long githubId, string githubLogin);

    /// <summary>Targeted update of the <c>last_active_at</c> timestamp.</summary>
    Task UpdateLastActiveAsync(Guid id);

    /// <summary>
    /// Returns the user's per-user provider settings JSON (defaults to
    /// <c>"{}"</c>). Stored on <c>users.settings JSONB</c> per Story 18-1
    /// (SaaS-mode equivalent of <c>~/.tamma/providers.json</c>).
    /// </summary>
    Task<string> GetUserSettingsAsync(Guid id);

    /// <summary>Persists the user's per-user provider settings JSON.</summary>
    Task UpdateUserSettingsAsync(Guid id, string settingsJson);
}
