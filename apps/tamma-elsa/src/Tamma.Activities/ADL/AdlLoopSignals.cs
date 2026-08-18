namespace Tamma.Activities.ADL;

/// <summary>
/// DCB event catalogue for the autonomous loop's OWN liveness — distinct from the
/// per-cycle events (<see cref="CycleEvents"/>) because these describe the
/// orchestrator itself, not any one issue.
///
/// <para>The loop restarts itself: <c>cooldown → DispatchAdl → Finish</c>, and
/// <see cref="DispatchAdlActivity"/> dispatches the successor. Nothing else
/// dispatches <c>adl-orchestrator</c>. So "the dispatch failed" and "no cycle has
/// been dispatched for N minutes" are the two facts an operator has to be able to
/// SEE, durably, without tailing a rotating log file — hence these event types.</para>
/// </summary>
public static class AdlLoopEvents
{
    /// <summary>Prefix for the self-restart dispatch (<c>.STARTED/.COMPLETED/.FAILED</c>).</summary>
    public const string SelfDispatch = "ADL.SELF.DISPATCH";

    /// <summary>
    /// Terminal, error-status event emitted when every restart-dispatch attempt failed.
    /// Its presence means the loop has STOPPED — the successor instance does not exist.
    /// </summary>
    public const string SelfDispatchFailed = SelfDispatch + ".FAILED";

    /// <summary>
    /// The watchdog observed no live <c>adl-orchestrator</c> instance for longer than
    /// the stall threshold while the loop had previously been running. Error status.
    /// </summary>
    public const string LoopStalled = "ADL.LOOP.STALLED";

    /// <summary>The watchdog re-dispatched the orchestrator after a stall.</summary>
    public const string LoopReArmed = "ADL.LOOP.REARMED";

    /// <summary>
    /// The watchdog found a stall but did NOT re-arm — because the operator stop switch
    /// is engaged, or re-arm is disabled, or no config seed was available. Error status:
    /// the loop is still down and a human has to act.
    /// </summary>
    public const string LoopReArmSkipped = "ADL.LOOP.REARM_SKIPPED";

    /// <summary>
    /// A cycle instance was left mid-execution by a host crash / deploy (no bookmark to
    /// resume from) and was force-terminated by the recovery sweep. Error status.
    /// </summary>
    public const string CycleOrphaned = "ADL.CYCLE.ORPHANED";

    /// <summary>An agent run entered its long inline wait (the crash-exposure window).</summary>
    public const string AgentInFlight = "AGENT.EXECUTION.INFLIGHT";
}

/// <summary>
/// Process-local memory of the config the autonomous loop is currently running with.
///
/// <para><b>Why this exists.</b> The orchestrator's config arrives as a dispatch INPUT
/// (<c>configJson</c>) and is carried forward from instance to instance by
/// <see cref="DispatchAdlActivity"/>. If the chain breaks, the successor instance does
/// not exist, and with it the only copy of the running config: a watchdog that re-arms
/// the loop with an empty config would restart it against the DEFAULT repository, which
/// is worse than not restarting at all. <see cref="DispatchAdlActivity"/> therefore
/// remembers the config here BEFORE it dispatches, so the watchdog can re-arm with the
/// config the loop was actually using.</para>
///
/// <para>Deliberately in-process and non-durable: it is a best-effort improvement on the
/// configured seed (<c>Adl:Watchdog:ConfigJson</c>), never the only source. A fresh host
/// has an empty cache and falls back to the configured seed. Registered as a singleton
/// (mirrors <see cref="PendingPrMergeBuffer"/>); a null instance is tolerated everywhere.</para>
/// </summary>
public sealed class AdlLoopConfigCache
{
    private string? _last;

    /// <summary>Record the config the loop is (re)starting with. Blank input is ignored.</summary>
    public void Remember(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson) || configJson.Trim() == "{}") return;
        Volatile.Write(ref _last, configJson);
    }

    /// <summary>The last remembered config, or null when this host has not seen a tick yet.</summary>
    public string? Last => Volatile.Read(ref _last);
}
