using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.TestDoubles;

/// <summary>
/// Shared test double for <see cref="IPlatformEventPublisher"/> that
/// captures every appended event into an in-memory list. Auto-assigns
/// <c>Id</c> and <c>CreatedAt</c> on append (matching the real Postgres
/// repository behaviour) so tests can round-trip the returned record.
///
/// <para>Consolidates the per-file <c>RecordingEventPublisher</c> /
/// <c>RecordingPlatformEventPublisher</c> stubs scattered across
/// <c>AdminTenantsTests</c>, <c>AdminTenantsAuditAndNoteTests</c>,
/// <c>AdminImpersonationTests</c>, <c>QuickWinsRound2Tests</c>,
/// <c>AuthAuditEventTests</c>, <c>SwitchOrgEndpointTests</c>, and
/// <c>AlertEventEmitterTests</c> (PF-C4 cleanup).</para>
///
/// <para>Exposes both <c>Events</c> and <c>Appended</c> properties so
/// existing call sites (which use one name or the other) compile
/// unchanged.</para>
/// </summary>
internal sealed class RecordingPlatformEventPublisher : IPlatformEventPublisher
{
    /// <summary>Recorded events ("Events" naming — used by Admin/Auth tests).</summary>
    public List<PlatformEvent> Events { get; } = new();

    /// <summary>Alias for <see cref="Events"/> ("Appended" naming — used by Alert tests).</summary>
    public List<PlatformEvent> Appended => Events;

    public Task<PlatformEvent?> AppendAndPublishAsync(
        PlatformEvent evt, CancellationToken ct = default)
    {
        if (evt.Id == Guid.Empty) evt.Id = Guid.NewGuid();
        if (evt.CreatedAt == default) evt.CreatedAt = DateTime.UtcNow;
        Events.Add(evt);
        return Task.FromResult<PlatformEvent?>(evt);
    }
}
