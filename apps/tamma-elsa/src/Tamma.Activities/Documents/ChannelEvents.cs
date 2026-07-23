namespace Tamma.Activities.Documents;

/// <summary>
/// Story 39-18 (Design Decision D8) — the ONLY DCB event family THIS story emits:
/// <c>GUIDANCE.*</c>. Every other kind that crosses these channels is evented by its
/// owning story (<c>APPROVAL.*</c> / <c>ESCALATION.*</c> — 39-8; <c>TASK.*</c> —
/// 39-20; <c>CHAT.*</c> — 39-19), so the channels never invent a parallel audit
/// trail (AC6). Type pattern follows <c>AGGREGATE.ACTION.STATUS</c> (mirrors
/// <see cref="ApprovalEvents"/>'s <c>APPROVAL.*</c> naming).
///
/// <para>Emitted fail-loud by <c>ChannelOutboxService</c> via
/// <c>IEventRepository.AppendAsync</c> when a guidance message is enqueued — the
/// event IS part of the operation (the 39-8 disposition posture), not a best-effort
/// side-car.</para>
/// </summary>
public static class ChannelEvents
{
    /// <summary>A guidance question was enqueued to the orchestrator. Informational (started) row.</summary>
    public const string GuidanceRequested = "GUIDANCE.REQUESTED";

    /// <summary>A guidance reply was provided. Normal (success) row.</summary>
    public const string GuidanceProvided = "GUIDANCE.PROVIDED";

    /// <summary>
    /// Status convention: <c>GUIDANCE.REQUESTED</c> is an informational (started)
    /// row; <c>GUIDANCE.PROVIDED</c> is a normal (success) row. Mirrors
    /// <see cref="ApprovalEvents.StatusForEvent"/> / 39-6's <c>DocumentEvents</c>.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        GuidanceRequested => "started",
        _ => "success",
    };
}
