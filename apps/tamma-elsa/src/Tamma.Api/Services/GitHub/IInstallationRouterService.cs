using System.Text.Json;

namespace Tamma.Api.Services.GitHub;

/// <summary>
/// Result of a GitHub App installation callback (OAuth return after install).
/// </summary>
public sealed record CallbackResult(
    bool Success,
    Guid? InstallationEntityId,
    long InstallationId,
    Guid? TenantId,
    string? ErrorReason);

/// <summary>
/// Result of dispatching a GitHub webhook payload to the appropriate handler.
/// </summary>
public sealed record WebhookResult(
    string EventType,
    string? Action,
    bool Skipped);

/// <summary>
/// Orchestrates GitHub App installation lifecycle:
///   • OAuth callback when a user completes the install flow
///   • Webhook events for installation + installation_repositories
///
/// Persists state via <see cref="Tamma.Data.Repositories.IInstallationRepository"/>
/// and emits audit events via <see cref="Tamma.Data.Repositories.IEventRepository"/>.
/// </summary>
public interface IInstallationRouterService
{
    /// <summary>
    /// Handle the OAuth redirect after a user installs the Tamma GitHub App.
    /// Binds the installation to the calling user's active tenant.
    /// </summary>
    /// <param name="installationId">GitHub App installation ID from the query string.</param>
    /// <param name="setupActionId">Optional GitHub <c>setup_action</c> identifier.</param>
    /// <param name="callingUserId">Authenticated user ID (from JWT claim).</param>
    Task<CallbackResult> HandleCallbackAsync(
        long installationId,
        int? setupActionId,
        Guid callingUserId);

    /// <summary>
    /// Dispatch a verified webhook payload to the appropriate handler.
    /// Unknown events return <c>skipped = true</c>.
    /// </summary>
    /// <param name="eventType">Value of the <c>X-GitHub-Event</c> header.</param>
    /// <param name="payload">Decoded JSON payload.</param>
    Task<WebhookResult> HandleWebhookAsync(string eventType, JsonElement payload);
}
