using System.Text;
using System.Text.Json;

namespace Tamma.Api.Services.Engine.Lifecycle;

/// <summary>
/// Thin helper for writing
/// <see href="https://html.spec.whatwg.org/multipage/server-sent-events.html">
/// Server-Sent Events</see> frames to an <see cref="HttpResponse"/>. Ported
/// from the deleted TS helpers <c>sseHeaders</c> / <c>sendSSE</c> in
/// <c>packages/api/src/routes/engine/index.ts</c>.
///
/// <para>Wire format:
/// <code>
/// event: &lt;type&gt;\n
/// data: &lt;json&gt;\n
/// \n
/// </code>
/// Plus <c>: &lt;comment&gt;\n\n</c> for keep-alive heartbeats that clients
/// ignore as empty comment frames.</para>
/// </summary>
internal static class SseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Write the standard SSE response headers. Must be called before the
    /// first <see cref="WriteEventAsync"/>; once any body bytes have been
    /// flushed the response headers are frozen.
    /// </summary>
    public static void WriteHeaders(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";
        // X-Accel-Buffering disables nginx output buffering so SSE frames
        // arrive at the client in real time rather than batched at a proxy
        // boundary.
        response.Headers["X-Accel-Buffering"] = "no";
    }

    /// <summary>
    /// Serialize <paramref name="payload"/> as JSON and write a single SSE
    /// <c>event:/data:</c> frame, then flush so the client receives it
    /// immediately.
    /// </summary>
    public static async Task WriteEventAsync(
        HttpResponse response,
        string eventName,
        object payload,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        // Single-line JSON keeps the frame compact and avoids SSE's
        // per-line-prefix requirement for multi-line data.
        var frame = $"event: {eventName}\ndata: {json}\n\n";
        var bytes = Encoding.UTF8.GetBytes(frame);
        await response.Body.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write an SSE comment frame — used as a keep-alive heartbeat. The
    /// ":heartbeat\n\n" shape matches the TS implementation and is ignored
    /// by all compliant <c>EventSource</c> clients.
    /// </summary>
    public static async Task WriteCommentAsync(
        HttpResponse response,
        string comment,
        CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes($":{comment}\n\n");
        await response.Body.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
