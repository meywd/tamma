using Cronos;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Story 41-30 (D3) — pure, Elsa-free, total, fail-closed cron/window math for
/// the tenant-aware scheduled-trigger seam. All the interesting logic of
/// <see cref="TenantScheduledTriggerService"/> lives here so it is
/// unit-testable without a host.
///
/// <para><b>Why Cronos directly and not Elsa's <c>ICronParser</c></b>
/// (Correction 1): <c>Elsa.Scheduling</c> 3.5.3 is already referenced and
/// ships <c>CronosCronParser</c>, but <c>ICronParser</c> exposes only
/// "next occurrence <i>from now</i>". Window computation needs "next
/// occurrence strictly after an <i>arbitrary anchor</i>" (the previous
/// window's instant), which <c>Cronos.CronExpression.GetNextOccurrence(
/// DateTimeOffset, TimeZoneInfo)</c> provides and <c>ICronParser</c> does
/// not. Cronos 0.11.0 is already in the restore graph via Elsa.Scheduling —
/// the explicit package reference declares the dependency, it downloads
/// nothing new. And Elsa's <c>Cron</c> trigger activity cannot replace the
/// seam either: it arms one trigger per workflow DEFINITION (no tenant
/// dimension) and its shipped scheduler (<c>LocalScheduler</c>) is
/// in-process, i.e. N pods arm N copies — the exact multi-pod defect
/// <c>HourlyAnalyticsRollupScheduler</c> needed an advisory lock to fix.
/// So: Elsa's PARSER (transitively), never Elsa's SCHEDULER.</para>
///
/// <para>Everything is UTC — standard 5-field expressions evaluated against
/// <see cref="TimeZoneInfo.Utc"/>, matching the house convention (AC5).</para>
/// </summary>
public static class ScheduleWindowCalculator
{
    /// <summary>
    /// Validate a standard 5-field cron expression. Fail-closed: any parse
    /// failure returns <c>false</c> with the parser's message — the admin API
    /// turns this into a typed 400 at WRITE time so a malformed expression
    /// can never throw at fire time (AC5).
    /// </summary>
    public static bool TryParse(string? cronExpression, out string? error)
    {
        if (string.IsNullOrWhiteSpace(cronExpression))
        {
            error = "Cron expression is required.";
            return false;
        }

        try
        {
            _ = CronExpression.Parse(cronExpression, CronFormat.Standard);
            error = null;
            return true;
        }
        catch (CronFormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// All window instants strictly after <paramref name="since"/> and at or
    /// before <paramref name="now"/>, ascending. Total: a malformed
    /// expression, a <paramref name="since"/> in the future, or an empty
    /// range all yield an EMPTY list (never negative, never a throw) —
    /// fire-time code cannot be brought down by row data. Bounded by
    /// <paramref name="maxWindows"/>: the list holds the FIRST (oldest)
    /// <paramref name="maxWindows"/> due occurrences, so when a backlog
    /// exceeds the cap the NEWEST occurrences are absent. Therefore this
    /// method must NEVER be used to pick "the most recent due window" —
    /// that is <see cref="ComputeDue"/>'s job (MAJOR-2 fix, 2026-07-29).
    /// Remaining valid uses: forward-looking next-due probes
    /// (<c>maxWindows: 1</c> from <c>now</c>) and tests.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> DueWindows(
        string cronExpression,
        DateTimeOffset since,
        DateTimeOffset now,
        int maxWindows = 1000)
    {
        if (!TryParse(cronExpression, out _)) return Array.Empty<DateTimeOffset>();
        if (since >= now) return Array.Empty<DateTimeOffset>();

        var cron = CronExpression.Parse(cronExpression, CronFormat.Standard);
        var windows = new List<DateTimeOffset>();
        var cursor = since;
        while (windows.Count < maxWindows)
        {
            var next = cron.GetNextOccurrence(cursor, TimeZoneInfo.Utc, inclusive: false);
            if (next is null || next > now) break;
            windows.Add(next.Value);
            cursor = next.Value;
        }

        return windows;
    }

    /// <summary>
    /// MAJOR-2 fix (2026-07-29) — the catch-up computation the fire path uses.
    /// Yields the TRUE latest due occurrence (the window AC6's bounded
    /// catch-up fires) regardless of how large the backlog is, plus the
    /// first due occurrence, the occurrence immediately before the last one
    /// (the newest SKIPPED window), and the total due count. The count walk
    /// is bounded by <paramref name="maxCount"/>; when the bound is hit the
    /// result is flagged <see cref="DueWindowResult.CountSaturated"/>
    /// (<see cref="DueWindowResult.DueCount"/> then means "at least") and the
    /// latest window is recovered by re-anchoring the walk near
    /// <paramref name="now"/> — never by returning a stale early window
    /// (the pre-fix defect: a capped ascending list fired the OLDEST
    /// occurrence when more than the cap were due).
    ///
    /// <para>Total like <see cref="DueWindows"/>: malformed cron, future
    /// <paramref name="since"/>, or an empty range yield the default result
    /// (<c>LastWindow = null</c>), never a throw.</para>
    /// </summary>
    public static DueWindowResult ComputeDue(
        string cronExpression,
        DateTimeOffset since,
        DateTimeOffset now,
        int maxCount = 100_000)
    {
        if (!TryParse(cronExpression, out _)) return default;
        if (since >= now) return default;
        if (maxCount < 1) maxCount = 1;

        var cron = CronExpression.Parse(cronExpression, CronFormat.Standard);
        DateTimeOffset? first = null, prev = null, last = null;
        var count = 0;
        var saturated = false;
        var cursor = since;
        while (true)
        {
            var next = cron.GetNextOccurrence(cursor, TimeZoneInfo.Utc, inclusive: false);
            if (next is null || next > now) break;
            first ??= next;
            prev = last;
            last = next;
            count++;
            cursor = next.Value;
            if (count >= maxCount)
            {
                saturated = true;
                break;
            }
        }

        if (last is null) return default;

        if (saturated)
        {
            // The bounded walk stopped at `cursor` — occurrences may remain
            // between there and `now`, and the FIRED window must be the true
            // latest one. Standard cron granularity is one minute, so a span
            // near `now` that contains any occurrence is cheap to walk;
            // widen until one hits (a backlog dense enough to saturate
            // virtually always hits the first span).
            var trueLast = LatestOccurrenceNotAfter(cron, cursor, now);
            if (trueLast is not null && trueLast != last)
            {
                last = trueLast;
                prev = LatestOccurrenceNotAfter(cron, since, trueLast.Value.AddTicks(-1)) ?? prev;
            }
            // trueLast == null means nothing was due after the walked cursor
            // — the walk had in fact reached the final due occurrence.
        }

        return new DueWindowResult(last, first, prev, count, saturated);
    }

    /// <summary>
    /// The latest occurrence strictly after <paramref name="floor"/> and at
    /// or before <paramref name="ceiling"/>, or <c>null</c> when there is
    /// none. Searches by widening spans back from the ceiling so the walk
    /// cost is proportional to the occurrences inside the FIRST span that
    /// contains any — never the whole (floor, ceiling] range unless every
    /// span is empty (only possible for sparse expressions, whose walks are
    /// short by definition).
    /// </summary>
    private static DateTimeOffset? LatestOccurrenceNotAfter(
        CronExpression cron, DateTimeOffset floor, DateTimeOffset ceiling)
    {
        if (floor >= ceiling) return null;

        foreach (var span in new[]
        {
            TimeSpan.FromHours(1), TimeSpan.FromDays(1),
            TimeSpan.FromDays(35), TimeSpan.FromDays(366),
        })
        {
            var anchor = ceiling - span;
            if (anchor < floor) anchor = floor;
            var probe = cron.GetNextOccurrence(anchor, TimeZoneInfo.Utc, inclusive: false);
            if (probe is null || probe > ceiling)
            {
                if (anchor == floor) return null; // the whole range is empty
                continue; // this span is empty — widen
            }
            return WalkToLast(cron, probe.Value, ceiling);
        }

        // Nothing within 366 days of the ceiling but the floor is earlier
        // still: full walk (sparse by construction — a dense expression
        // would have hit a span above).
        var start = cron.GetNextOccurrence(floor, TimeZoneInfo.Utc, inclusive: false);
        return start is null || start > ceiling ? null : WalkToLast(cron, start.Value, ceiling);
    }

    private static DateTimeOffset WalkToLast(
        CronExpression cron, DateTimeOffset from, DateTimeOffset ceiling)
    {
        var last = from;
        while (true)
        {
            var next = cron.GetNextOccurrence(last, TimeZoneInfo.Utc, inclusive: false);
            if (next is null || next > ceiling) return last;
            last = next.Value;
        }
    }

    /// <summary>
    /// The opaque window key: the ISO-8601 UTC instant of the window's
    /// scheduled fire time, e.g. <c>2026-07-27T03:00:00Z</c>. Derived,
    /// deterministic, lexicographically sorts in time order, and treated as
    /// an opaque string by every consumer (41-20 D8's contract — consumers
    /// scope their own ids from it; the seam never parses it back).
    /// </summary>
    public static string WindowKey(DateTimeOffset window)
        => window.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
}

/// <summary>
/// Result of <see cref="ScheduleWindowCalculator.ComputeDue"/> (MAJOR-2 fix,
/// 2026-07-29). <c>default</c> (<see cref="LastWindow"/> null,
/// <see cref="DueCount"/> 0) means nothing is due.
/// </summary>
/// <param name="LastWindow">The TRUE latest due occurrence — the one window
/// the bounded catch-up (D7/AC6) fires. Guaranteed the most recent even when
/// the count walk saturated.</param>
/// <param name="FirstWindow">The earliest due occurrence (equals
/// <paramref name="LastWindow"/> when exactly one is due) — the oldest
/// SKIPPED window when more than one is due.</param>
/// <param name="PreviousWindow">The occurrence immediately before
/// <paramref name="LastWindow"/> — the newest SKIPPED window. Non-null
/// whenever <paramref name="DueCount"/> &gt; 1.</param>
/// <param name="DueCount">Total due occurrences in <c>(since, now]</c>. When
/// <paramref name="CountSaturated"/> is true this is a floor ("at least
/// N"), not an exact count.</param>
/// <param name="CountSaturated">True when the counting walk hit its bound —
/// <paramref name="DueCount"/> saturated, but <paramref name="LastWindow"/>
/// is still exact.</param>
public readonly record struct DueWindowResult(
    DateTimeOffset? LastWindow,
    DateTimeOffset? FirstWindow,
    DateTimeOffset? PreviousWindow,
    int DueCount,
    bool CountSaturated);

/// <summary>
/// Story 41-30 (D4) — the tenant-aware advisory-lock key. The one thing
/// <see cref="HourlyAnalyticsRollupScheduler.ComputeAdvisoryLockKey"/> got
/// WRONG for this seam's purposes is pinned here by construction: its key has
/// <b>no tenant component</b> (<c>HourlyAnalyticsRollupScheduler.cs:241</c>),
/// so on a shared window one tenant's leader would suppress every other
/// tenant's fire. This key mixes <b>tenant + trigger + window</b>, so two
/// tenants on the same window always compete for DIFFERENT locks (AC2 — a
/// named regression test asserts exactly that).
///
/// <para>The lock remains necessary but NOT sufficient (Correction 3): it is
/// session-scoped and dies with a crashed pod's connection. The committed
/// <c>scheduled_trigger_fires</c> row is the durable half of at-most-once.</para>
/// </summary>
public static class ScheduleLockKey
{
    /// <summary>
    /// ASCII "SCHD" — the pg_locks-greppable namespace prefix for this seam,
    /// following the rollup scheduler's "RLUP" (<c>0x524C5550</c>) convention.
    /// </summary>
    internal const long Prefix = 0x53434844; // "SCHD"

    /// <summary>
    /// Deterministic 64-bit advisory-lock id from
    /// <c>(tenantId, triggerId, windowKey)</c>. Pure FNV-1a mix — every pod
    /// competing for the same (tenant, trigger, window) computes the same
    /// key; any differing component changes it.
    /// </summary>
    public static long Compute(Guid tenantId, Guid triggerId, string windowKey)
    {
        ArgumentNullException.ThrowIfNull(windowKey);

        unchecked
        {
            const ulong fnvOffset = 14695981039346656037UL;
            const ulong fnvPrime = 1099511628211UL;

            var hash = fnvOffset;
            foreach (var b in tenantId.ToByteArray())
                hash = (hash ^ b) * fnvPrime;
            foreach (var b in triggerId.ToByteArray())
                hash = (hash ^ b) * fnvPrime;
            foreach (var ch in windowKey)
            {
                hash = (hash ^ (byte)ch) * fnvPrime;
                hash = (hash ^ (byte)(ch >> 8)) * fnvPrime;
            }

            // Keep the greppable "SCHD" prefix in the high bits and fold the
            // hash into the remaining 32 (the RLUP layout convention).
            long high = Prefix ^ (long)(hash >> 32);
            return (high << 32) | (long)(hash & 0xFFFFFFFFUL);
        }
    }
}
