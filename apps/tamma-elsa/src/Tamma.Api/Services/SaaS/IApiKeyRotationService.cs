namespace Tamma.Api.Services.SaaS;

/// <summary>
/// Result of an API-key rotation attempt.
/// </summary>
/// <param name="Success">True when a new key was generated and persisted.</param>
/// <param name="PlaintextKey">
/// Newly-generated plaintext key. ONE-TIME value — only returned on the
/// successful rotation response. Callers must surface it to the end-user on
/// this response and never log or persist it.
/// </param>
/// <param name="KeyPrefix">First 16 characters of the plaintext key, safe for UI/audit.</param>
/// <param name="KeyId">Identifier of the newly-persisted <c>api_keys</c> row.</param>
/// <param name="ErrorReason">Short machine-readable reason when <see cref="Success"/> is false.</param>
public sealed record KeyRotationResult(
    bool Success,
    string? PlaintextKey,
    string? KeyPrefix,
    Guid? KeyId,
    string? ErrorReason);

/// <summary>
/// Rotates the API key associated with a GitHub-App installation.
///
/// Contract:
/// <list type="bullet">
///   <item>Authorize caller: must be owner/admin on the installation's tenant.</item>
///   <item>Generate fresh key, hash it, persist via <see cref="Tamma.Data.Repositories.IApiKeyRepository"/>.</item>
///   <item>Emit <c>API_KEY.ROTATED</c> audit event.</item>
///   <item>Return plaintext key to caller ONCE — it is never retrievable again.</item>
/// </list>
/// </summary>
public interface IApiKeyRotationService
{
    /// <summary>
    /// Rotate the installation's API key, addressed by the internal entity Guid
    /// (the row's primary key on <c>github_installations</c>).
    /// </summary>
    /// <param name="installationEntityId">Primary key of the <c>github_installations</c> row.</param>
    /// <param name="callerUserId">Authenticated user attempting the rotation.</param>
    Task<KeyRotationResult> RotateAsync(Guid installationEntityId, Guid callerUserId);

    /// <summary>
    /// Rotate the installation's API key, addressed by the GitHub-issued
    /// numeric installation id (the value GitHub puts in webhook payloads
    /// and the value that's stored on <c>github_installations.installation_id</c>).
    ///
    /// <para>Audit finding 020 — this is the URL contract every TS-era SaaS
    /// client speaks. The caller need not know the internal entity Guid.</para>
    /// </summary>
    Task<KeyRotationResult> RotateByInstallationIdAsync(long installationId, Guid callerUserId);
}
