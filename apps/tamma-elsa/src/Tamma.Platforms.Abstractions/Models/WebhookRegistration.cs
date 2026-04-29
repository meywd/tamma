namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// Result of a successful
/// <see cref="IGitPlatformClient.RegisterWebhookAsync"/> call.
///
/// <para>The <see cref="Id"/> is platform-scoped and opaque — callers
/// store it for later "delete this webhook" calls. The
/// <see cref="Url"/> is the hook destination Tamma registered, so a
/// reconciliation job can re-fetch and verify it still matches the
/// configured value.</para>
/// </summary>
public sealed record WebhookRegistration(
    string Id,
    string Url,
    IReadOnlyList<string> Events,
    bool Active);
