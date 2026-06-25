using Microsoft.Extensions.Configuration;

namespace Tamma.Activities.Blocker;

/// <summary>
/// Completeness build-out 2026-06-22 (<c>BlockerDiagnosis.md</c> §Missing #12, 7-1G AC3) —
/// shared per-collector wait cap for the parallel signal-collection fan-out. A hung
/// integration call (slow / rate-limited GitHub) must NOT block the whole diagnosis: each
/// collector races its work against a configurable deadline
/// (<c>BlockerDiagnosis:SignalCollectionTimeoutSeconds</c>, default 15s = the AC3 cap). On
/// expiry the collector lands as <c>CollectionSucceeded=false</c> (fail-soft partial signal)
/// rather than hanging the join — the diagnosis proceeds on whatever signals resolved.
///
/// <para>The integration methods do not accept a <see cref="System.Threading.CancellationToken"/>,
/// so this races the work against <see cref="Task.Delay(TimeSpan)"/> and abandons the slow
/// task (its result is discarded) rather than cooperatively cancelling — a contained,
/// fail-soft cap, not a hard kill.</para>
/// </summary>
public static class BlockerSignalTimeout
{
    /// <summary>The AC3 default: a 15-second per-collector deadline.</summary>
    public const int DefaultTimeoutSeconds = 15;

    /// <summary>
    /// Resolve the per-collector timeout (seconds) from config, clamped to ≥ 1s, falling
    /// back to <see cref="DefaultTimeoutSeconds"/>.
    /// </summary>
    public static int ResolveTimeoutSeconds(IConfiguration? configuration)
    {
        var configured = configuration?.GetValue<int?>("BlockerDiagnosis:SignalCollectionTimeoutSeconds");
        return configured is > 0 ? configured.Value : DefaultTimeoutSeconds;
    }

    /// <summary>
    /// Run <paramref name="work"/> under the resolved deadline. Returns <c>true</c> when the
    /// work completed in time (the caller's signal is fully populated), <c>false</c> when the
    /// deadline won (the caller leaves the signal as <c>CollectionSucceeded=false</c>).
    /// </summary>
    public static async Task<bool> RunAsync(IConfiguration? configuration, Func<Task> work)
    {
        var timeout = TimeSpan.FromSeconds(ResolveTimeoutSeconds(configuration));
        var workTask = work();
        var completed = await Task.WhenAny(workTask, Task.Delay(timeout));
        if (completed != workTask)
        {
            // The deadline won: we ABANDON (don't cancel — the integration methods take no
            // CancellationToken) the slow work task. It keeps running and will eventually
            // mutate its own collector signal in the background, but that is harmless: the
            // aggregator reads ONLY each signal's CollectionSucceeded flag, which stays false
            // on a timed-out collector (the caller never sets it on this path). Any exception
            // the abandoned task later throws is observed by the discarded continuation below
            // (so it is not an unobserved-task crash), not propagated to the caller.
            _ = workTask.ContinueWith(
                static t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            return false;
        }

        // Surface any exception from the (now-completed) work task to the caller's catch.
        await workTask;
        return true;
    }
}
