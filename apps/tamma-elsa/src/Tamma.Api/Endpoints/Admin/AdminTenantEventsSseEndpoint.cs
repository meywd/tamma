using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamma.Data;
using Tamma.Data.Abstractions;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

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
///
/// <para><b>Round-2 hardening (M4 / M5 / M14 / M15)</b>:
/// <list type="bullet">
///   <item><b>M4</b> — the response payload is scrubbed: only the
///     curated fields below survive serialisation. Tags + Data are
///     filtered to an allowlist of safe keys
///     (<see cref="AllowedTagKeys"/>) so sensitive material that
///     leaked into a tag/payload upstream cannot reach the
///     dashboard client.</item>
///   <item><b>M5</b> — every poll tick acquires a fresh
///     <see cref="ControlPlaneDbContext"/> from
///     <see cref="IDbContextFactory{TContext}"/> + disposes it before
///     the next sleep. The 30-min stream no longer pins one scoped
///     CP connection.</item>
///   <item><b>M14</b> — JSON serialisation uses the host's configured
///     <see cref="JsonOptions"/> (camelCase + web defaults) so the
///     wire format matches the rest of the API surface.</item>
///   <item><b>M15</b> — consecutive errors on the poll path are
///     counted; after <see cref="MaxConsecutiveErrors"/> failures the
///     stream emits <c>event: end</c> + <c>data: {"reason":"upstream_error"}</c>
///     and breaks. <see cref="TenantNotFoundException"/> ends with
///     <c>tenant_not_found</c>; <see cref="OperationCanceledException"/>
///     propagates normally.</item>
/// </list>
/// </para>
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

    /// <summary>
    /// M15 — number of consecutive poll-tick errors before the stream
    /// gives up + closes with <c>event: end</c>. Five gives a brief
    /// recovery window for transient blips while still bounding the
    /// time a wedged stream wastes.
    /// </summary>
    public const int MaxConsecutiveErrors = 5;

    /// <summary>
    /// M4 — allowlist of tag keys + payload top-level keys that survive
    /// the response scrub. Anything else in <c>Tags</c> / <c>Data</c>
    /// JSONB is dropped before the frame is written. Documented as a
    /// constant so reviewers can audit the surface area in one place.
    ///
    /// <para>Approved keys:</para>
    /// <list type="bullet">
    ///   <item><c>tenantId</c> — already known to the client (it's in the URL).</item>
    ///   <item><c>step</c> — short kebab-case lifecycle step id.</item>
    ///   <item><c>attempt</c> — small integer.</item>
    ///   <item><c>actorUserId</c> — admin operator audit attribution.</item>
    ///   <item><c>actorEmail</c> — admin operator audit attribution.</item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedTagKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "tenantId",
        "step",
        "attempt",
        "actorUserId",
        "actorEmail",
    };

    public static async Task StreamEvents(
        Guid tenantId,
        [FromServices] IDbContextFactory<ControlPlaneDbContext> dbFactory,
        [FromServices] IOptions<HttpJsonOptions> jsonOptions,
        [FromServices] ILoggerFactory loggerFactory,
        HttpContext http,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("AdminTenantEventsSse");

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

        // M14 — match the rest of the API: camelCase property names,
        // web defaults. Falls back to web-defaults when no host options
        // are registered (unit tests). Build a serializer-options
        // snapshot once outside the loop to avoid per-frame allocations.
        var serializerOptions = jsonOptions.Value.SerializerOptions
            ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);

        // Initial cursor: high-water-mark BEFORE we started streaming.
        // Anything strictly past this is "new" from the client's POV.
        // M5 — short-lived context; disposed before the loop sleeps.
        long lastSequence;
        await using (var initDb = await dbFactory.CreateDbContextAsync(token).ConfigureAwait(false))
        {
            lastSequence = await initDb.PlatformEvents
                .AsNoTracking()
                .Where(e => e.TenantId == tenantId)
                .Select(e => (long?)e.SequenceNumber)
                .MaxAsync(token).ConfigureAwait(false) ?? 0L;
        }

        // Send an opening comment so the client immediately knows the
        // stream is live (helpful for spinner UIs that wait on first
        // byte before rendering "connected").
        await WriteAsync(http, $": stream-open tenantId={tenantId:D} cursor={lastSequence}\n\n", token);

        var lastKeepalive = DateTimeOffset.UtcNow;
        var consecutiveErrors = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                List<RawEvent> newEvents;
                // M5 — fresh context per tick. Lifetime is bounded to
                // the EF query + projection; the stream's 2-second sleep
                // happens AFTER the context disposes so we never hold a
                // CP connection across the idle window.
                await using (var db = await dbFactory.CreateDbContextAsync(token).ConfigureAwait(false))
                {
                    newEvents = await db.PlatformEvents
                        .AsNoTracking()
                        .Where(e => e.TenantId == tenantId
                                    && e.SequenceNumber > lastSequence)
                        .OrderBy(e => e.SequenceNumber)
                        .Take(50)  // back-pressure cap per tick
                        .Select(e => new RawEvent(
                            e.Id,
                            e.Type,
                            e.SequenceNumber,
                            e.CreatedAt,
                            e.Tags,
                            e.Data))
                        .ToListAsync(token)
                        .ConfigureAwait(false);
                }

                foreach (var raw in newEvents)
                {
                    // M4 — scrub the raw row to the public DTO before
                    // serialisation. Tags + Data are filtered against
                    // AllowedTagKeys so any sensitive value that leaked
                    // into the JSONB upstream never reaches the client.
                    var safe = ScrubEvent(raw);
                    var payload = JsonSerializer.Serialize(safe, serializerOptions);
                    // Use the platform_events.id as the SSE id so
                    // browsers' Last-Event-ID semantics work (clients
                    // can resume from the last seen id after a
                    // reconnect).
                    await WriteAsync(http,
                        $"id: {raw.Id:D}\nevent: platform-event\ndata: {payload}\n\n",
                        token);
                    lastSequence = raw.SequenceNumber;
                }

                // Keepalive every 30s so proxies don't drop the
                // connection during a quiet period.
                if (DateTimeOffset.UtcNow - lastKeepalive >= KeepaliveInterval)
                {
                    await WriteAsync(http, ": keepalive\n\n", token);
                    lastKeepalive = DateTimeOffset.UtcNow;
                }

                // Tick succeeded — reset the consecutive-error counter.
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // M15 — propagate normal cancellation; no end-event,
                // no error counter touch.
                break;
            }
            catch (TenantNotFoundException)
            {
                // M15 — explicit not-found end. Tenant deleted while
                // the stream was open; tell the client + close.
                logger.LogInformation(
                    "tenant.events_sse.tenant_not_found tenantId={TenantId}",
                    tenantId);
                try
                {
                    await WriteAsync(http,
                        "event: end\ndata: {\"reason\":\"tenant_not_found\"}\n\n",
                        CancellationToken.None);
                }
                catch { /* client already gone */ }
                return;
            }
            catch (Exception ex)
            {
                // M15 — count consecutive errors. Emit a redacted
                // <c>: error</c> comment so the client sees the stream
                // is still alive (in case of a transient blip), but
                // close the stream after MaxConsecutiveErrors.
                consecutiveErrors++;
                logger.LogWarning(
                    ex,
                    "tenant.events_sse.tick_failed tenantId={TenantId} consecutiveErrors={ConsecutiveErrors}",
                    tenantId, consecutiveErrors);
                try
                {
                    await WriteAsync(http,
                        $": error {ex.GetType().Name} ({consecutiveErrors}/{MaxConsecutiveErrors})\n\n",
                        token);
                }
                catch { /* client already gone */ }

                if (consecutiveErrors >= MaxConsecutiveErrors)
                {
                    logger.LogError(
                        "tenant.events_sse.upstream_error_giving_up tenantId={TenantId} consecutiveErrors={ConsecutiveErrors}",
                        tenantId, consecutiveErrors);
                    try
                    {
                        await WriteAsync(http,
                            "event: end\ndata: {\"reason\":\"upstream_error\"}\n\n",
                            CancellationToken.None);
                    }
                    catch { /* client already gone */ }
                    return;
                }
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

    /// <summary>
    /// Internal raw row shape projected from EF. Stays internal because
    /// the wire shape is the scrubbed <see cref="SanitizedEvent"/>.
    /// </summary>
    private sealed record RawEvent(
        Guid Id,
        string Type,
        long SequenceNumber,
        DateTime CreatedAt,
        string? Tags,
        string? Data);

    /// <summary>
    /// M4 — public SSE payload. Every field is fixed-vocabulary
    /// (event type + sequence + timestamp + curated tag bag). The raw
    /// JSONB <c>Data</c> is intentionally NOT carried — admin
    /// dashboards consume the typed event surface, not raw payloads.
    /// </summary>
    public sealed record SanitizedEvent(
        Guid Id,
        string Type,
        long SequenceNumber,
        DateTime CreatedAt,
        IReadOnlyDictionary<string, string> Tags);

    /// <summary>
    /// M4 — strips the raw row to the public, allowlisted shape.
    /// Tags JSONB is parsed; only keys in <see cref="AllowedTagKeys"/>
    /// survive. Malformed JSON yields an empty tag bag (no exception
    /// leaks to the client). Internal — the raw row never escapes the
    /// endpoint without going through this scrub.
    /// </summary>
    private static SanitizedEvent ScrubEvent(RawEvent raw)
    {
        var safeTags = ParseAllowedTags(raw.Tags);
        return new SanitizedEvent(
            raw.Id,
            raw.Type,
            raw.SequenceNumber,
            raw.CreatedAt,
            safeTags);
    }

    /// <summary>
    /// Test seam — scrubs a raw row tuple to the public DTO using the
    /// same logic as the production poll loop. Tests assert the
    /// allowlist + JSON-malformed handling without spinning up the
    /// full SSE pipeline.
    /// </summary>
    internal static SanitizedEvent ScrubForTesting(
        Guid id,
        string type,
        long sequenceNumber,
        DateTime createdAt,
        string? tags,
        string? data)
        => ScrubEvent(new RawEvent(id, type, sequenceNumber, createdAt, tags, data));

    private static IReadOnlyDictionary<string, string> ParseAllowedTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson) || tagsJson == "{}")
            return _emptyTags;
        try
        {
            using var doc = JsonDocument.Parse(tagsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return _emptyTags;

            Dictionary<string, string>? bag = null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!AllowedTagKeys.Contains(prop.Name)) continue;
                // Stringify scalars (we never need objects/arrays in
                // the curated tag bag — those don't appear in our
                // allowlist).
                var value = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null,
                };
                if (value is null) continue;
                bag ??= new Dictionary<string, string>(StringComparer.Ordinal);
                bag[prop.Name] = value;
            }
            return (IReadOnlyDictionary<string, string>?)bag ?? _emptyTags;
        }
        catch (JsonException)
        {
            return _emptyTags;
        }
    }

    private static readonly IReadOnlyDictionary<string, string> _emptyTags =
        new Dictionary<string, string>(0);

    private static async Task WriteAsync(HttpContext http, string frame, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(frame);
        await http.Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
