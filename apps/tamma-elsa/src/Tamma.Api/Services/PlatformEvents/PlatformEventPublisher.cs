using Microsoft.Extensions.DependencyInjection;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.PlatformEvents;

/// <summary>
/// Singleton adapter that lets Story 28-5 tenant-lifecycle activities
/// reach the in-process <see cref="IPlatformEventBus"/> without their host
/// assembly (Tamma.Activities) taking a project reference on Tamma.Api.
///
/// <para>Publishes via <see cref="IPlatformEventBus.AppendAndPublishAsync"/>
/// so the event is durably appended to <c>platform_events</c> before any
/// in-process subscriber sees it. The per-call
/// <see cref="IPlatformEventRepository"/> is resolved from a fresh DI
/// scope so the publisher works equally well from a singleton-rooted
/// background activity (Elsa) and from a per-request endpoint.</para>
/// </summary>
public sealed class PlatformEventPublisher : IPlatformEventPublisher
{
    private readonly IPlatformEventBus _bus;
    private readonly IServiceScopeFactory _scopeFactory;

    public PlatformEventPublisher(
        IPlatformEventBus bus,
        IServiceScopeFactory scopeFactory)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _scopeFactory = scopeFactory
            ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task<PlatformEvent?> AppendAndPublishAsync(
        PlatformEvent evt,
        CancellationToken ct = default)
    {
        if (evt is null) throw new ArgumentNullException(nameof(evt));

        // Open a scope so the scoped IPlatformEventRepository (and its
        // ControlPlaneDbContext) live for the duration of this single
        // append+publish call and dispose deterministically afterwards.
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider
            .GetRequiredService<IPlatformEventRepository>();

        return await _bus.AppendAndPublishAsync(repo, evt, ct).ConfigureAwait(false);
    }
}
