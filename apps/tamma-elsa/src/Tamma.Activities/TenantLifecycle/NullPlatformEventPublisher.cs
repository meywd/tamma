using Microsoft.Extensions.Logging;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Best-effort no-op <see cref="IPlatformEventPublisher"/> for the Elsa
/// engine process.
///
/// <para><b>Why this exists.</b> The tenant-lifecycle activities
/// (<c>TenantLifecycleActivity</c>, <c>CleanupStepActivity</c>,
/// <c>EmitCleanupTerminalEventActivity</c>) write CONTROL-PLANE
/// <c>platform_events</c> (a DIFFERENT store than tenant
/// <c>domain_events</c>) via <c>context.GetRequiredService&lt;IPlatformEventPublisher&gt;()</c>.
/// That publisher is registered only by <c>Tamma.Api</c>'s
/// <c>AddPlatformEventBus()</c>. The engine (<c>Tamma.ElsaServer</c>) hosts
/// the <c>CreateTenantWorkflow</c> / <c>DeleteTenantWorkflow</c> /
/// <c>CleanUpFailedTenantWorkflow</c> that run those activities, but cannot
/// reference <c>Tamma.Api</c> — so <c>GetRequiredService</c> THREW with
/// "No service for type IPlatformEventPublisher" at runtime, aborting the
/// lifecycle workflows.</para>
///
/// <para><b>Scope.</b> This is a DISTINCT path from the core tenant
/// <c>domain_events</c> drain (which now flows through
/// <c>POST /api/engine/events</c>). Persisting <c>platform_events</c> from
/// the engine needs a sibling <c>POST /api/engine/platform-events</c> →
/// <c>IPlatformEventRepository</c> callback (the engine has no control-plane
/// DB access by design). That is tracked as a FOLLOW-UP. Registering this
/// Null seam removes the hard crash today (mirrors the existing
/// <c>NullGitHubActionsClient</c> seam in <c>Program.cs</c>) so the
/// lifecycle workflows complete; the per-step platform telemetry is a
/// best-effort no-op (logged at WARN once) until the callback lands.</para>
/// </summary>
public sealed class NullPlatformEventPublisher : IPlatformEventPublisher
{
    private readonly ILogger<NullPlatformEventPublisher> _logger;

    public NullPlatformEventPublisher(ILogger<NullPlatformEventPublisher> logger) =>
        _logger = logger;

    public Task<PlatformEvent?> AppendAndPublishAsync(
        PlatformEvent evt,
        CancellationToken ct = default)
    {
        _logger.LogWarning(
            "platform_event.dropped type={Type} tenantId={TenantId} — engine process has no platform-event sink "
            + "(no /api/engine/platform-events callback yet). FOLLOW-UP: wire the control-plane callback.",
            evt.Type, evt.TenantId);
        return Task.FromResult<PlatformEvent?>(null);
    }
}
