namespace Tamma.Api.Services.Actions;

/// <summary>
/// Adversarial review F9 (2026-08-01) — <b>the bounded deadline every 43-9 seam
/// evaluates the gate under.</b>
///
/// <para><b>Why it exists.</b> Each seam's failure posture is a <c>catch</c>, and
/// a <c>catch</c> covers a THROW, not a HANG. The gate reaches the control plane
/// (the snapshot store's lazy refresh, the acceptance-rules read, the
/// authorization ledger); a Postgres that accepts the connection and then never
/// answers produces no exception at all. Without a deadline that hangs the HTTP
/// request until the client disconnects at Seam C/E, and stalls a sweeper's loop
/// indefinitely at Seam D — neither open nor closed, which is the one outcome
/// neither posture accounts for.</para>
///
/// <para><b>Why the timeout fails OPEN, like the other transient faults.</b> A
/// timed-out evaluation carries no decision: nothing was resolved, so there is
/// nothing to enforce. It is the same "deny on a DECISION, never on an ERROR"
/// posture the seams already take on a throw (D8), and the same residual, which
/// is mitigated by alerting on <c>ACTION.GATE.EVALUATION_FAILED</c> volume. The
/// fix here is that the request now RESOLVES — one way or the other — instead of
/// hanging. Note this does NOT touch the gate's own fail-CLOSED handling of an
/// unreadable policy input (that is a decision), nor
/// <see cref="Tamma.Core.Actions.AutonomyGateDecisionUnrecordedException"/> (that
/// is a decision whose audit row failed).</para>
///
/// <para><b>Why a constant and not configuration.</b> A configurable governance
/// deadline is a configurable way to turn the gate off: set it to zero and every
/// evaluation times out into the fail-open arm. The value is deliberately far
/// above any healthy evaluation (an in-process snapshot read plus at most one
/// small control-plane query) and far below any client's patience.</para>
/// </summary>
public static class AutonomyGateDeadline
{
    /// <summary>The wall-clock bound on ONE gate evaluation at a seam.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A token that cancels when <paramref name="ct"/> does OR when
    /// <see cref="Default"/> elapses. The caller MUST dispose the returned source
    /// and MUST distinguish the two causes — <paramref name="ct"/> firing is the
    /// caller's own cancellation (a client disconnect, a host shutting down) and
    /// is never a governance failure; the deadline firing is a transient
    /// evaluation fault.
    /// </summary>
    public static CancellationTokenSource CreateLinkedSource(CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(Default);
        return source;
    }

    /// <summary>
    /// True when a caught <see cref="OperationCanceledException"/> came from THIS
    /// deadline rather than from <paramref name="callerToken"/>. Written as a
    /// question about the caller's token (not about the exception's token) because
    /// a linked source hands the linked token to the exception either way.
    /// </summary>
    public static bool IsDeadline(CancellationToken callerToken) =>
        !callerToken.IsCancellationRequested;
}
