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
    /// <paramref name="maxWindows"/> as a defence against a pathological
    /// every-second-like backlog (the catch-up policy D7 only ever fires the
    /// most recent window anyway).
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
