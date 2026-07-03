namespace Tamma.Api.Services.Streaming;

/// <summary>
/// Story 32-23 (AC4) — the closed frame vocabulary of the streaming run tap.
/// Every SSE frame emitted by <c>GET /api/v1/llm/runs/{correlationId}/stream</c>
/// carries one of these as its <c>event:</c> name. Payloads are key-free
/// (credential-safety, AC9) and correlated by <c>correlationId</c>.
/// </summary>
public static class RunStreamFrameType
{
    /// <summary>A model-output token delta (produced only when the runner
    /// enables provider token streaming). Payload: <c>{ delta }</c>.</summary>
    public const string Token = "token";

    /// <summary>A tool invocation started. Payload: <c>{ toolName, toolCallId, turn }</c>.
    /// Bridged from <c>TOOL_LOOP.TOOL_EXECUTING</c> via the sink. The tool
    /// arguments are NEVER streamed (they may carry secrets — AC9).</summary>
    public const string ToolCall = "tool_call";

    /// <summary>A tool invocation finished. Payload: <c>{ toolName, toolCallId,
    /// success, durationMs }</c>. Bridged from <c>TOOL_LOOP.TOOL_COMPLETED</c>.
    /// The tool output is NEVER streamed (AC9).</summary>
    public const string ToolResult = "tool_result";

    /// <summary>An interactive question raised by the run (produced by Story
    /// 32-20; this story defines the shape + transport). Payload:
    /// <c>{ question, kind, options?, answerer? }</c>.</summary>
    public const string Question = "question";

    /// <summary>An answer to a prior question (Story 32-20). Payload:
    /// <c>{ answer, answerer }</c>.</summary>
    public const string Answer = "answer";

    /// <summary>The terminal turn summary the engine already received. Payload:
    /// <c>{ success, totalTurns, totalTokens, exhausted, durationMs }</c>. The
    /// tap closes cleanly (<c>event: end</c>) when this is published.</summary>
    public const string Final = "final";
}
