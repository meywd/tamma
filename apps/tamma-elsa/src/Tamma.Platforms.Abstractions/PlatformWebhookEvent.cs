using System.Text.Json;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-7 AC3 / AC5 — neutral webhook-event shape passed from the
/// receiver endpoint into <see cref="IWebhookEventDispatcher"/>. Each
/// driver-specific payload normalises into one of the
/// <see cref="WebhookEventCategory"/> buckets so handlers can branch on
/// the neutral category instead of platform-specific JSON paths.
///
/// <para>The raw bytes + parsed JSON are kept alongside the normalised
/// fields so handlers that genuinely need a platform-specific field
/// (e.g. a GitHub <c>installation.id</c>) can reach for it without the
/// receiver having to flatten every shape into the abstraction.</para>
///
/// <para><b>Tenant resolution</b>: the receiver calls
/// <see cref="IPlatformResolver.ResolveForWebhookAsync"/> with
/// <see cref="Kind"/> + <see cref="InstallationExternalId"/> and
/// populates <see cref="TenantId"/> + <see cref="Installation"/> when
/// the lookup succeeds. A null <see cref="TenantId"/> is legal — the
/// onboarding-handoff race (webhook arrives before the install
/// callback finishes linking) is handled by handlers that link the
/// installation themselves.</para>
/// </summary>
/// <param name="Kind">
/// Source platform — drives keyed-DI resolution of handlers and the
/// idempotency table's <c>platform_kind</c> column.
/// </param>
/// <param name="EventType">
/// Platform-native event name, lower-snake. GitHub uses
/// <c>X-GitHub-Event</c> (e.g. <c>installation</c>, <c>push</c>);
/// Gitea uses <c>X-Gitea-Event</c>; Forgejo mirrors Gitea; GitLab uses
/// <c>X-Gitlab-Event</c> (with a different vocabulary —
/// <c>Push Hook</c>, <c>Merge Request Hook</c>, ... lower-cased and
/// snake-cased by the receiver).
/// </param>
/// <param name="Action">
/// Platform-side action discriminator within
/// <see cref="EventType"/> (e.g. GitHub <c>installation</c>'s
/// <c>action</c> field — <c>created</c>, <c>deleted</c>, <c>suspend</c>).
/// Null when the platform doesn't ship a separate action field.
/// </param>
/// <param name="Category">Normalised event category for handler routing.</param>
/// <param name="DeliveryId">
/// Platform-supplied delivery identifier — UUID for GitHub, ULID-ish
/// string for GitLab. The idempotency table keys on
/// <c>(platform_kind, delivery_id)</c>; null is allowed but the
/// receiver logs a warning because re-delivery dedupe collapses.
/// </param>
/// <param name="InstallationExternalId">
/// Platform-side identifier for the installation/binding that produced
/// this delivery. GitHub: <c>installation.id</c>. GitLab: project id /
/// group id depending on hook scope. May be null on platform-level
/// events (e.g. GitHub <c>ping</c>).
/// </param>
/// <param name="RepoFullName">
/// <c>owner/repo</c> when the event is repo-scoped. Null otherwise.
/// </param>
/// <param name="TenantId">
/// Resolved owning tenant. Null when no installation row matched
/// <see cref="Kind"/>+<see cref="InstallationExternalId"/> — the
/// "webhook arrived before onboarding linked the install" race.
/// </param>
/// <param name="Installation">
/// Resolved installation record — provides the
/// <c>tenant_platform_installations.id</c> for handler downstream
/// work (e.g. linking a tenant via <c>InstallationRepository.LinkToTenantAsync</c>).
/// Null on the same race condition as <see cref="TenantId"/>.
/// </param>
/// <param name="RawBody">
/// The verbatim request body bytes. Only handlers that genuinely need
/// to verify or replay the original delivery should reach for this; the
/// receiver MUST NOT log it.
/// </param>
/// <param name="ParsedJson">
/// Pre-parsed JSON document. The receiver parses once and shares the
/// document across handlers to avoid the O(handlers) parse cost. Null
/// when the body wasn't valid JSON (the receiver short-circuits to 400
/// before dispatching, so handlers can assume non-null).
/// </param>
public sealed record PlatformWebhookEvent(
    PlatformKind Kind,
    string EventType,
    string? Action,
    WebhookEventCategory Category,
    string? DeliveryId,
    string? InstallationExternalId,
    string? RepoFullName,
    Guid? TenantId,
    PlatformInstallation? Installation,
    ReadOnlyMemory<byte> RawBody,
    JsonElement ParsedJson);

/// <summary>
/// Story 31-7 AC5 — normalised event taxonomy. Handlers register against
/// a category + platform pair so a handler bound to
/// <see cref="PullRequest"/> on GitHub doesn't accidentally fire for a
/// Gitea PR event (handlers usually need the platform-side shape).
///
/// <para>The receiver maps the platform's
/// <see cref="PlatformWebhookEvent.EventType"/> to one of these
/// buckets via a small dispatch table. Unknown event types fall into
/// <see cref="Unknown"/> and the dispatcher logs + drops them.</para>
/// </summary>
public enum WebhookEventCategory
{
    /// <summary>The receiver couldn't classify the event; handler dispatch is a no-op.</summary>
    Unknown = 0,

    /// <summary>
    /// Platform installation lifecycle — connected, suspended, deleted,
    /// repository selection changes. GitHub: <c>installation</c>,
    /// <c>installation_repositories</c>. Gitea/Forgejo: <c>repository</c>
    /// scope. GitLab: <c>system_hook</c> for project create/destroy.
    /// </summary>
    Installation = 1,

    /// <summary>
    /// Pull-request / merge-request lifecycle. GitHub: <c>pull_request</c>;
    /// Gitea: <c>pull_request</c>; GitLab: <c>merge_request</c>; Forgejo:
    /// <c>pull_request</c>.
    /// </summary>
    PullRequest = 2,

    /// <summary>Issue lifecycle. GitHub: <c>issues</c>; Gitea: <c>issues</c>; GitLab: <c>issue</c>.</summary>
    Issue = 3,

    /// <summary>Push to a branch. GitHub/Gitea/Forgejo/GitLab: <c>push</c>.</summary>
    Push = 4,

    /// <summary>
    /// CI run lifecycle. GitHub: <c>workflow_run</c> /
    /// <c>workflow_job</c>; GitLab: <c>pipeline</c>; Gitea/Forgejo:
    /// <c>workflow_run</c>.
    /// </summary>
    WorkflowRun = 5,

    /// <summary>Health-check ping. GitHub: <c>ping</c>; Gitea: <c>ping</c>; GitLab: none (returns Unknown).</summary>
    Ping = 6,
}
