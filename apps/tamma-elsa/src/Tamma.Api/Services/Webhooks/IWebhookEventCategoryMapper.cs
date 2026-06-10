using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Services.Webhooks;

/// <summary>
/// Story 31-7 — maps a platform-native event type string (e.g.
/// GitHub <c>installation</c>, Gitea <c>push</c>, GitLab
/// <c>merge_request</c>) to the neutral
/// <see cref="WebhookEventCategory"/> handlers register against.
///
/// <para>Surfaced as a service (rather than a static helper) so the
/// receiver test suite can inject a fixture that exposes a known
/// taxonomy without depending on the production mapping table.</para>
/// </summary>
public interface IWebhookEventCategoryMapper
{
    /// <summary>
    /// Map <paramref name="eventType"/> for <paramref name="kind"/>
    /// to a <see cref="WebhookEventCategory"/>. Falls back to
    /// <see cref="WebhookEventCategory.Unknown"/> when no mapping
    /// exists — the dispatcher logs and drops Unknown events.
    /// </summary>
    WebhookEventCategory MapCategory(PlatformKind kind, string eventType);
}
