using Tamma.Data.Entities;

namespace Tamma.Data.Abstractions;

/// <summary>
/// Thin port consumed by Story 28-5 tenant-lifecycle activities to append
/// a <see cref="PlatformEvent"/> to the control-plane log AND fan it out to
/// in-process subscribers in a single call. The intent is that callers
/// (workflow activities) reference this lightweight contract rather than
/// the full <c>Tamma.Api.Services.PlatformEvents.IPlatformEventBus</c> —
/// the activity assembly does not (and must not) depend on the API
/// surface.
///
/// <para>The default registration in
/// <c>PlatformEventsServiceCollectionExtensions.AddPlatformEventBus()</c>
/// supplies an adapter (<c>PlatformEventPublisher</c>) that forwards
/// straight to <c>InMemoryPlatformEventBus.AppendAndPublishAsync(IPlatformEventRepository, ...)</c>.
/// Tests inject a stub that records calls without any DB or pub/sub.</para>
///
/// <para>Semantics match the underlying bus contract:
/// <list type="bullet">
///   <item><description>Returns the persisted event on a fresh append, or
///     <c>null</c> when the row collided with the partial unique step-dedup
///     index from Story 28-1 (idempotent retry path — caller treats the
///     null result as "already recorded").</description></item>
///   <item><description>Subscriber exceptions are swallowed by the bus, so
///     <see cref="AppendAndPublishAsync"/> returning a non-null event means
///     persistence succeeded; it does NOT promise that every handler ran
///     to completion.</description></item>
///   <item><description>Lifetime: singleton. Resolves the per-request
///     <see cref="Repositories.IPlatformEventRepository"/> from the same
///     <see cref="IServiceProvider"/> the activity ran under.</description></item>
/// </list></para>
/// </summary>
public interface IPlatformEventPublisher
{
    /// <summary>
    /// Persist <paramref name="evt"/> via the platform event repository,
    /// then publish to subscribers when persistence succeeds. Returns the
    /// persisted event with the generated <c>Id</c>/<c>CreatedAt</c>, or
    /// <c>null</c> when the insert was a dedup no-op (subscribers already
    /// saw the original).
    /// </summary>
    Task<PlatformEvent?> AppendAndPublishAsync(
        PlatformEvent evt,
        CancellationToken ct = default);
}
