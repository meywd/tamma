using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ToolExecution;

namespace Tamma.Api.Services.Streaming;

/// <summary>
/// Story 32-23 (AC3) — the LIVE <see cref="IToolLoopEventSink"/> that replaces
/// <c>NullToolLoopEventSink</c> when streaming is enabled. Instead of dropping
/// each <c>TOOL_LOOP.*</c> event, it maps it to the run-tap frame vocabulary
/// and publishes it onto the in-process <see cref="ILlmRunStreamBus"/>, keyed
/// by the <c>workflowInstanceId</c> (== <c>correlationId</c>) the emitter
/// threads through every event payload.
///
/// <para>Registered in <c>Tamma.Api</c> behind the app-level streaming flag; when
/// the flag is off the registration stays <c>NullToolLoopEventSink</c> (a
/// graceful no-op — the tap simply shows no live tool frames, never an error).</para>
///
/// <para><b>Fire-and-forget:</b> the bus never blocks or throws into the
/// producer; this sink adds a defensive swallow so a mapping/serialisation
/// hiccup can never fault the tool loop.</para>
///
/// <para><b>Credential safety (AC9):</b> only fixed-vocabulary fields
/// (<c>toolName</c>/<c>toolCallId</c>/<c>turn</c>/<c>success</c>/<c>durationMs</c>)
/// are copied into the frame — never tool arguments or outputs. Every frame is
/// additionally re-scrubbed at write time by the tap endpoint.</para>
/// </summary>
public sealed class BusToolLoopEventSink : IToolLoopEventSink
{
    private readonly ILlmRunStreamBus _bus;
    private readonly ILogger<BusToolLoopEventSink>? _logger;

    public BusToolLoopEventSink(ILlmRunStreamBus bus, ILogger<BusToolLoopEventSink>? logger = null)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task WriteEventAsync(string eventType, object data, CancellationToken cancellationToken = default)
    {
        try
        {
            JsonElement el;
            try
            {
                el = JsonSerializer.SerializeToElement(data);
            }
            catch
            {
                return; // unserialisable payload — nothing safe to route
            }

            if (el.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var correlationId = ReadString(el, "workflowInstanceId");
            if (string.IsNullOrEmpty(correlationId))
            {
                // No correlationId => can't route to a per-run stream. The
                // buffered run is unaffected (this is pure observability).
                return;
            }

            var mapped = Map(eventType, el);
            if (mapped is null)
            {
                return; // TURN_STARTED / TURN_COMPLETED / unknown => ignored
            }

            var frame = new RunStreamFrame(mapped.Value.FrameType, correlationId, 0, mapped.Value.Payload);
            await _bus.PublishAsync(correlationId, frame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A sink failure must NEVER fault the tool loop (AC5) — swallow + log.
            _logger?.LogWarning(ex,
                "BusToolLoopEventSink swallowed a publish error; the run is unaffected. eventType={EventType}",
                eventType);
        }
    }

    /// <summary>
    /// Map a <c>TOOL_LOOP.*</c> event to a run-tap frame. Returns <c>null</c> for
    /// events with no tap representation (turn progress). Exposed for unit tests.
    /// </summary>
    internal static (string FrameType, object Payload)? Map(string eventType, JsonElement data)
        => eventType switch
        {
            "TOOL_LOOP.TOOL_EXECUTING" => (RunStreamFrameType.ToolCall, new
            {
                toolName = ReadString(data, "toolName"),
                toolCallId = ReadString(data, "toolCallId"),
                turn = ReadLong(data, "turnNumber"),
            }),
            "TOOL_LOOP.TOOL_COMPLETED" => (RunStreamFrameType.ToolResult, new
            {
                toolName = ReadString(data, "toolName"),
                toolCallId = ReadString(data, "toolCallId"),
                success = ReadBool(data, "success"),
                durationMs = ReadLong(data, "durationMs"),
            }),
            "TOOL_LOOP.COMPLETED" => (RunStreamFrameType.Final, new
            {
                success = !ReadBool(data, "exhausted"),
                totalTurns = ReadLong(data, "totalTurns"),
                totalTokens = ReadLong(data, "totalTokens"),
                exhausted = ReadBool(data, "exhausted"),
                durationMs = ReadLong(data, "totalDurationMs"),
            }),
            _ => null,
        };

    private static string? ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static long ReadLong(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt64(out var l)
            ? l
            : 0L;

    private static bool ReadBool(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
