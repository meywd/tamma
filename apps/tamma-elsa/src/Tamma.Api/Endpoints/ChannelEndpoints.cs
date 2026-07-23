using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Services.Channels;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Channels;
using Tamma.Data;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 39-18 (D2) — the engine→API channel enqueue seam. The lifecycle engine
/// (<c>Tamma.ElsaServer</c>) registers no outbox repository, so it publishes channel
/// messages through this fail-loud HTTP hop (mirrors <c>POST /api/engine/events</c>).
/// Gated <c>EngineServiceOnly</c> (service principal) — a user JWT never reaches it,
/// closing the forgery vector, exactly like the events/documents engine callbacks.
/// </summary>
public static class ChannelEndpoints
{
    /// <summary>The engine POST body: the <c>ChannelEnvelope</c> serialized with <c>DocumentJson.Options</c>.</summary>
    public sealed record EnqueueRequest([property: JsonPropertyName("envelopeJson")] string EnvelopeJson);

    // ================================================================
    // POST /api/engine/channel/outbox   (EngineServiceOnly)
    // ================================================================

    public static async Task<IResult> EnqueueFromEngine(
        [FromBody] EnqueueRequest req,
        [FromServices] ChannelOutboxService service,
        [FromServices] ITenantContext tenantContext,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Tamma.Api.ChannelEndpoints");

        if (req is null || string.IsNullOrWhiteSpace(req.EnvelopeJson))
            return Results.BadRequest(new { error = "envelopeJson is required" });

        ChannelEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ChannelEnvelope>(req.EnvelopeJson, DocumentJson.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "channel enqueue: envelopeJson failed to deserialize");
            return Results.BadRequest(new { error = "envelopeJson is not a valid ChannelEnvelope" });
        }
        if (envelope is null)
            return Results.BadRequest(new { error = "envelopeJson deserialized to null" });

        // Server-derives the authoritative tenant. This is EngineServiceOnly (a trusted
        // service principal), so — like AppendPlatformEvents — the envelope's TenantId
        // is trusted for the body-carried cross-tenant path; a present X-Tenant-Id
        // (ITenantContext) wins when set.
        var tenantId = tenantContext.TenantId is { } ambient && ambient != Guid.Empty
            ? ambient
            : envelope.TenantId;
        if (tenantId == Guid.Empty)
            return Results.BadRequest(new { error = "no tenant could be resolved for the channel message" });

        var scoped = envelope with { TenantId = tenantId };

        try
        {
            var rows = await service.EnqueueAsync(scoped);
            return Results.Json(
                new { ok = true, enqueued = rows.Count },
                statusCode: StatusCodes.Status202Accepted);
        }
        catch (TammaError ex) when (ex.Code == "CHANNEL.MESSAGE.INVALID")
        {
            logger.LogWarning("channel enqueue rejected: {Message}", ex.Message);
            return Results.BadRequest(new { error = "channel_message_invalid", detail = ex.Message });
        }
    }
}
