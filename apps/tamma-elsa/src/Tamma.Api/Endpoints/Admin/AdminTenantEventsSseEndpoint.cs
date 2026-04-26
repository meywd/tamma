using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tamma.Data;

namespace Tamma.Api.Endpoints.Admin;

/// <summary>
/// Story 28-11 AC3 — Server-Sent Events stream of platform events for
/// a single tenant. Mounted at
/// <c>GET /api/admin/tenants/{tenantId}/events/stream</c> behind
/// <c>OwnerAccess</c>. Used by the admin dashboard's TenantDetailPage
/// to surface live tenant lifecycle events without WebSocket
/// infrastructure.
///
/// <para><b>Protocol</b>: standard W3C Server-Sent Events. Each
/// platform_events row is serialised as one
/// <c>data: &lt;json&gt;\n\n</c> frame. The stream emits a
/// <c>: keepalive</c> comment line every 30s so HTTP/2 idle-timeouts
/// (nginx default 60s) don't drop the connection.</para>
///
/// <para><b>Polling cadence</b>: the endpoint polls
/// <c>platform_events</c> every 2 seconds for rows newer than the
/// last sent <c>SequenceNumber</c>. A future enhancement could
/// switch to Postgres LISTEN/NOTIFY for sub-second latency, but the
/// 2s tick is good enough for an admin dashboard and avoids piping
/// LISTEN into HTTP.</para>
///
/// <para><b>Lifetime</b>: the connection lives until the client
/// disconnects (cancellation token fires) or
/// <see cref="MaxStreamDurationSeconds"/> elapses (default 30 minutes
/// — long enough for a debugging session, short enough that an
/// abandoned tab doesn't hold a backend connection forever).</para>
/// </summary>
public static class AdminTenantEventsSseEndpoint
{
    /// <summary>
    /// Maximum stream lifetime — server kicks the client at this point
    /// so an abandoned dashboard tab doesn't pin a connection.
    /// </summary>
    public const int MaxStreamDurationSeconds = 30 * 60;

    /// <summary>How often to poll platform_events for new rows.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>How often to send the SSE keepalive comment line.</summary>
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(30);

    public static async Task StreamEvents(
        Guid tenantId,
        [FromServices] ControlPlaneDbContext db,
        [FromServices] TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("tenantId required", ct);
            return;
        }

        // Set SSE response headers BEFORE the first write. Caller
        // gets the 200 + content-type immediately so the browser's
        // EventSource opens the readyState=OPEN state right away.
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache, no-store";
        // Disable nginx response buffering so each event flushes
        // immediately. Without this, nginx batches writes and the
        // browser sees a single bulk delivery.
        http.Response.Headers["X-Accel-Buffering"] = "no";

        // Bound the total time we hold the connection — a forgotten
        // dashboard tab shouldn't hold a backend forever.
        using var streamCts = CancellationTokenSource
            .CreateLinkedTokenSource(ct, http.RequestAborted);
        streamCts.CancelAfter(TimeSpan.FromSeconds(MaxStreamDurationSeconds));
        var token = streamCts.Token;

        // Initial cursor: high-water-mark BEFORE we started streaming.
        // Anything strictly past this is "new" from the client's POV.
        long lastSequence = await db.PlatformEvents
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .Select(e => (long?)e.SequenceNumber)
            .MaxAsync(token).ConfigureAwait(false) ?? 0L;

        // Send an opening comment so the client immediately knows the
        // stream is live (helpful for spinner UIs that wait on first
        // byte before rendering "connected").
        await WriteAsync(http, $": stream-open tenantId={tenantId:D} cursor={lastSequence}\n\n", token);

        var lastKeepalive = timeProvider.GetUtcNow();

        while (!token.IsCancellationRequested)
        {
            try
            {
                var newEvents = await db.PlatformEvents
                    .AsNoTracking()
                    .Where(e => e.TenantId == tenantId
                                && e.SequenceNumber > lastSequence)
                    .OrderBy(e => e.SequenceNumber)
                    .Take(50)  // back-pressure cap per tick
                    .Select(e => new
                    {
                        e.Id,
                        e.Type,
                        e.SequenceNumber,
                        e.CreatedAt,
                        e.Tags,
                        e.Data,
                    })
                    .ToListAsync(token)
                    .ConfigureAwait(false);

                foreach (var evt in newEvents)
                {
                    var payload = JsonSerializer.Serialize(evt);
                    // Use the platform_events.id as the SSE id so
                    // browsers' Last-Event-ID semantics work (clients
                    // can resume from the last seen id after a
                    // reconnect).
                    await WriteAsync(http,
                        $"id: {evt.Id:D}\nevent: platform-event\ndata: {payload}\n\n",
                        token);
                    lastSequence = evt.SequenceNumber;
                }

                // Keepalive every 30s so proxies don't drop the
                // connection during a quiet period.
                if (timeProvider.GetUtcNow() - lastKeepalive >= KeepaliveInterval)
                {
                    await WriteAsync(http, ": keepalive\n\n", token);
                    lastKeepalive = timeProvider.GetUtcNow();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A transient DB error shouldn't kill the stream — log
                // a comment line and continue. The client sees a brief
                // pause; the next tick either recovers or hits the
                // outer cancellation.
                await WriteAsync(http,
                    $": error {ex.GetType().Name}\n\n", token);
            }

            try
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        // Final close marker so a client that's still reading sees a
        // clean termination instead of an abrupt EOF.
        try { await WriteAsync(http, ": stream-closing\n\n", CancellationToken.None); }
        catch { /* client already gone */ }
    }

    private static async Task WriteAsync(HttpContext http, string frame, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(frame);
        await http.Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
