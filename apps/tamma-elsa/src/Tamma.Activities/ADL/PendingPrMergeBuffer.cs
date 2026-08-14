using System.Collections.Concurrent;

namespace Tamma.Activities.ADL;

/// <summary>
/// 2026-08-13 (engine-driven E2E run 39) — the SELF-MERGE RACE buffer for the
/// cycle's merged-PR wait.
///
/// <para><b>The race.</b> When Tamma itself performs the merge (the
/// merge-approval gate's happy path), the platform's merged-PR webhook fires
/// IMMEDIATELY — observed 1 s BEFORE the cycle transitioned into
/// <see cref="WaitForPRMergedActivity"/> and registered its bookmark. The
/// webhook forward then 404'd ("no suspended bookmark"), the once-only
/// delivery was lost, and a successfully merged PR sat on the 12 h SLA before
/// escalating needs-human — on every self-merged cycle, every platform.</para>
///
/// <para><b>The fix (reconcile-on-register).</b> The resume endpoint, on a
/// bookmark miss, RECORDS the merge here keyed by the bookmark name it
/// computed; the wait activity CONSUMES the record before suspending and, on a
/// hit, completes its <c>Merged</c> outcome immediately — no webhook is lost
/// within the process lifetime. In-memory is deliberate: the buffer only
/// bridges a seconds-wide in-process ordering race, the entry is consumed by
/// the very next wait registration, and an engine restart inside that window
/// still falls back to the wait's durable 12 h SLA edge (the pre-existing
/// exception path). Entries expire after <see cref="Ttl"/> so a merge webhook
/// for a wait that never registers (a cycle that faulted before reaching the
/// wait) cannot leak.</para>
/// </summary>
public sealed class PendingPrMergeBuffer
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, (string? MergeSha, DateTimeOffset At)> _pending =
        new(StringComparer.Ordinal);

    /// <summary>Record a merged-PR notification that found no suspended wait.</summary>
    public void Record(string bookmarkName, string? mergeSha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bookmarkName);
        Sweep();
        _pending[bookmarkName] = (mergeSha, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Consume (remove + return) a recorded merge for this bookmark name.
    /// True exactly once per recorded notification.
    /// </summary>
    public bool TryConsume(string bookmarkName, out string? mergeSha)
    {
        mergeSha = null;
        if (string.IsNullOrWhiteSpace(bookmarkName)) return false;
        if (!_pending.TryRemove(bookmarkName, out var entry)) return false;
        if (DateTimeOffset.UtcNow - entry.At > Ttl) return false; // expired — fall to the SLA path.
        mergeSha = entry.MergeSha;
        return true;
    }

    private void Sweep()
    {
        var cutoff = DateTimeOffset.UtcNow - Ttl;
        foreach (var (key, value) in _pending)
        {
            if (value.At < cutoff) _pending.TryRemove(key, out _);
        }
    }
}
