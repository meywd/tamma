using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Engine-side <see cref="IPlatformEventPublisher"/>: the engine has no
/// control-plane DB access, so it POSTs platform events to
/// <c>POST /api/engine/platform-events</c> (mirroring the domain_events
/// drain that flows through <c>POST /api/engine/events</c>).
///
/// <para>Persistence and idempotency happen server-side. On POST failure
/// the publisher logs a WARN and returns <c>null</c> (degraded, not
/// throwing) — same philosophy as the prior
/// <see cref="NullPlatformEventPublisher"/>, but events now land durably
/// when the API is reachable.</para>
///
/// <para><b>Captive-dependency guard.</b> <see cref="TammaApiClient"/>
/// is registered as a typed <see cref="Microsoft.Extensions.Http"/>
/// transient — capturing it in a singleton field is unsafe. Instead this
/// publisher resolves the client per-call via
/// <see cref="IServiceScopeFactory"/>, mirroring the pattern used by
/// <c>Tamma.Api.Services.PlatformEvents.PlatformEventPublisher</c>.</para>
/// </summary>
public sealed class EngineApiPlatformEventPublisher : IPlatformEventPublisher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EngineApiPlatformEventPublisher> _logger;

    public EngineApiPlatformEventPublisher(
        IServiceScopeFactory scopeFactory,
        ILogger<EngineApiPlatformEventPublisher> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<PlatformEvent?> AppendAndPublishAsync(
        PlatformEvent evt,
        CancellationToken ct = default)
    {
        if (evt is null) return null;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var api = scope.ServiceProvider.GetRequiredService<TammaApiClient>();

            var record = new PlatformEventRecord(
                evt.Id,
                evt.Type,
                evt.TenantId,
                evt.UserId,
                ParseTags(evt.Tags),
                ToJsonElement(evt.Metadata),
                ToJsonElement(evt.Data),
                evt.CreatedAt == default ? null : evt.CreatedAt);

            var ok = await api.AppendPlatformEventsAsync(new[] { record }, ct).ConfigureAwait(false);
            if (!ok)
            {
                _logger.LogWarning(
                    "platform_event.post_failed type={Type} tenantId={TenantId}",
                    evt.Type, evt.TenantId);
                return null;
            }

            return evt;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "platform_event.publish_error type={Type} tenantId={TenantId}",
                evt.Type, evt.TenantId);
            return null;
        }
    }

    // -----------------------------------------------------------------------
    // Mapping helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses the JSONB-as-string <c>Tags</c> column into the dictionary
    /// shape the wire record expects. Returns <c>null</c> for empty/null JSON
    /// (the server treats missing/null tags the same as an empty object).
    /// </summary>
    private static IReadOnlyDictionary<string, string?>? ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return null;

        return JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
    }

    /// <summary>
    /// Parses the JSONB-as-string <c>Metadata</c> / <c>Data</c> columns into
    /// a <see cref="JsonElement?"/>. Returns <c>null</c> for empty/null JSON.
    /// Uses <see cref="JsonElement.Clone"/> so the element is independent of
    /// the parent <see cref="JsonDocument"/> lifetime.
    /// </summary>
    private static JsonElement? ToJsonElement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return null;

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
