using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Channels;
using Tamma.Core.Documents.Policy;

namespace Tamma.Activities.Documents;

/// <summary>
/// Story 39-18 (D2, D8) — the engine→API channel publish hop. Implements 39-6's
/// <see cref="IAcceptanceRequestPublisher"/> seam (REPLACING
/// <see cref="LoggingAcceptanceRequestPublisher"/>) and the broader
/// <see cref="IEngineChannelPublisher"/>: it wraps a message in a
/// <see cref="ChannelEnvelope"/> and POSTs it to
/// <c>POST /api/engine/channel/outbox</c> (<c>EngineServiceOnly</c>), where the API
/// mints the durable outbox row(s) and best-effort fans out to the SignalR hub.
///
/// <para><b>Captive-dependency guard.</b> <see cref="TammaApiClient"/> is a typed
/// HTTP transient — captured in a singleton it is unsafe. This publisher resolves the
/// client per-call via <see cref="IServiceScopeFactory"/>, mirroring
/// <see cref="Tamma.Activities.TenantLifecycle.EngineApiPlatformEventPublisher"/>.</para>
///
/// <para><b>Degraded, never throws into the workflow.</b> A transport failure is
/// logged at ERROR and swallowed — the 39-8 gate still suspends (39-6 D6's contract),
/// and because the outbox row is the API's to mint, an unreachable API leaves the
/// request recoverable via the suspended bookmark + a re-publish admin action, never
/// lost work.</para>
/// </summary>
public sealed class EngineChannelPublisher : IAcceptanceRequestPublisher, IEngineChannelPublisher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EngineChannelPublisher> _logger;

    public EngineChannelPublisher(
        IServiceScopeFactory scopeFactory,
        ILogger<EngineChannelPublisher> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public Task PublishAsync(AcceptanceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The 39-5 AcceptanceRequest carries no tenant field, so the envelope's
        // TenantId is left empty here; the engine→API hop relies on the API to
        // scope the write (the EngineServiceOnly endpoint trusts the envelope /
        // X-Tenant-Id — the AppendPlatformEvents precedent). Threading the tenant
        // through 39-6's fixed publisher signature is a documented follow-up.
        var envelope = new ChannelEnvelope(
            MessageId: UuidV7.NewGuid(),
            TenantId: Guid.Empty,
            Audience: ChannelAudience.Orchestrator,
            RecipientUserId: null,
            Message: new AcceptanceRequested(request),
            CreatedAt: DateTimeOffset.UtcNow);

        return PublishAsync(envelope, ct);
    }

    /// <inheritdoc/>
    public async Task PublishAsync(ChannelEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        try
        {
            var envelopeJson = JsonSerializer.Serialize(envelope, DocumentJson.Options);
            var tenantId = envelope.TenantId == Guid.Empty ? null : envelope.TenantId.ToString();

            using var scope = _scopeFactory.CreateScope();
            var api = scope.ServiceProvider.GetRequiredService<TammaApiClient>();

            var ok = await api.PostChannelOutboxAsync(envelopeJson, tenantId, ct).ConfigureAwait(false);
            if (!ok)
            {
                _logger.LogError(
                    "channel_outbox.post_failed kind={Kind} audience={Audience} tenantId={TenantId} — " +
                    "the gate still suspends; the request is recoverable via the suspended bookmark.",
                    ChannelMessageKinds.KindOf(envelope.Message), envelope.Audience.ToWire(), envelope.TenantId);
            }
        }
        catch (Exception ex)
        {
            // Transport / serialization error — NEVER throw into the workflow.
            _logger.LogError(
                ex,
                "channel_outbox.publish_error audience={Audience} tenantId={TenantId}",
                envelope.Audience.ToWire(), envelope.TenantId);
        }
    }
}

/// <summary>
/// Story 39-18 — the engine-side seam for publishing any <see cref="ChannelEnvelope"/>
/// to the channel outbox (beyond 39-6's acceptance-request-only
/// <see cref="IAcceptanceRequestPublisher"/>). Implemented by
/// <see cref="EngineChannelPublisher"/>.
/// </summary>
public interface IEngineChannelPublisher
{
    /// <summary>Publish a channel envelope to the outbox via the engine→API hop.</summary>
    Task PublishAsync(ChannelEnvelope envelope, CancellationToken ct);
}
