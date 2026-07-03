using System.Text.Json;

namespace Tamma.Api.Services.Streaming;

/// <summary>
/// Story 32-23 (AC9, load-bearing) — the credential-safety choke point for the
/// run tap. Mirrors <c>AdminTenantEventsSseEndpoint.ScrubEvent</c>: every frame
/// payload written to the wire is rebuilt from an ALLOWLIST of safe keys, so a
/// secret / <c>BaseUrl</c> auth / raw provider header / raw prompt body / tool
/// argument or output that leaked into a payload upstream can NEVER reach an
/// observer. Anything off the allowlist is dropped.
///
/// <para>The safe surface is deliberately tiny: model-output <c>token</c>
/// deltas, tool <c>name</c>/<c>id</c>/<c>success</c>/<c>durationMs</c> (never
/// tool arguments/outputs), the 32-20 question/answer shape, and the terminal
/// turn summary. Plus the always-present <c>correlationId</c> + per-run
/// <c>seq</c>, which are not secrets.</para>
/// </summary>
public static class RunStreamFrameScrubber
{
    /// <summary>
    /// The allowlist of payload keys that survive the scrub. Documented as a
    /// constant so a reviewer can audit the entire streamed surface in one
    /// place (any future PR that widens it must review the secret-safety
    /// implications — and update the pinning test).
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedPayloadKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        // tool_call
        "toolName",
        "toolCallId",
        "turn",
        // tool_result
        "success",
        "durationMs",
        // token
        "delta",
        // question / answer (Story 32-20 shape)
        "question",
        "kind",
        "options",
        "answerer",
        "answer",
        // final (turn summary)
        "totalTurns",
        "totalTokens",
        "exhausted",
    };

    /// <summary>
    /// Rebuild <paramref name="frame"/> into a key-free wire payload:
    /// <c>{ correlationId, seq, ...allowlisted payload fields }</c>. The raw
    /// <see cref="RunStreamFrame.Payload"/> is serialised then filtered — only
    /// scalar/array values under an allowlisted key survive; nested objects and
    /// off-list keys are dropped.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Scrub(RunStreamFrame frame)
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["correlationId"] = frame.CorrelationId,
            ["seq"] = frame.Seq,
        };

        if (frame.Payload is null)
        {
            return bag;
        }

        JsonElement element;
        try
        {
            element = JsonSerializer.SerializeToElement(frame.Payload);
        }
        catch (Exception)
        {
            // A payload that can't even be serialised carries nothing safe — the
            // frame still ships with its correlationId + seq (never an exception
            // to the client).
            return bag;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return bag;
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (!AllowedPayloadKeys.Contains(prop.Name))
            {
                continue;
            }

            // Clone so the value stays valid after the source document is GC'd,
            // and so the SSE serializer re-emits it verbatim.
            bag[prop.Name] = prop.Value.Clone();
        }

        return bag;
    }
}
