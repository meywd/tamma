namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Pure function: given a <see cref="RotationSchedule"/> and the last
/// rotation timestamp, return the next due timestamp. Used by
/// <see cref="SecretMetadataFactory"/> to populate
/// <see cref="SecretMetadata.NextRotationDueAt"/>.
///
/// <para>All math is UTC. <see cref="DateTimeOffset"/> arithmetic on
/// <c>AddDays</c> is intentionally DST-agnostic — adding 90 days to a
/// UTC instant always yields a UTC instant exactly 90×86400 seconds
/// later, regardless of any local-time DST transitions in between.
/// Tests cover spring-forward / fall-back / leap-year boundaries to
/// pin this contract.</para>
///
/// <para><b>Cron support</b>: the calculator does not parse cron
/// expressions itself — a delegate
/// (<see cref="CronEvaluator"/>) is invoked when one is registered
/// via <see cref="RegisterCronEvaluator"/>. Story 29-2 wires the real
/// Cronos-backed parser; until then, calling <see cref="NextDue"/> on
/// a cron schedule throws <see cref="NotSupportedException"/>. This
/// keeps the interface-only story 29-1 free of cron-library
/// dependencies while giving 29-2 a clear seam.</para>
/// </summary>
public static class RotationScheduleCalculator
{
    /// <summary>
    /// Delegate signature for a cron evaluator. Implementations parse
    /// <paramref name="cronExpression"/> and return the first fire time
    /// strictly after <paramref name="from"/> (UTC). Throw
    /// <see cref="ArgumentException"/> for malformed expressions.
    /// </summary>
    public delegate DateTimeOffset? CronEvaluator(
        string cronExpression, DateTimeOffset from);

    private static CronEvaluator? _cronEvaluator;

    /// <summary>
    /// Plug in a cron evaluator (Story 29-2 wires Cronos). Pass
    /// <c>null</c> to clear the registration — useful for tests that
    /// want to assert the unregistered behaviour.
    /// </summary>
    public static void RegisterCronEvaluator(CronEvaluator? evaluator)
    {
        _cronEvaluator = evaluator;
    }

    /// <summary>
    /// Compute the next due timestamp.
    /// </summary>
    /// <param name="schedule">Required.</param>
    /// <param name="lastRotatedAt">Last successful rotation; null when
    /// no rotation has happened yet.</param>
    /// <param name="now">Current UTC time. Used as the anchor when
    /// <paramref name="lastRotatedAt"/> is null (so a freshly-created
    /// secret with a Days schedule has its first due-date computed off
    /// "now" rather than the unix epoch).</param>
    /// <returns>UTC instant of the next due rotation, or
    /// <c>null</c> when the schedule is
    /// <see cref="RotationScheduleKind.None"/>.</returns>
    /// <exception cref="NotSupportedException">A cron schedule was
    /// supplied but no evaluator has been registered. Story 29-2 wires
    /// the evaluator at composition time.</exception>
    public static DateTimeOffset? NextDue(
        RotationSchedule schedule,
        DateTimeOffset? lastRotatedAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return schedule.Kind switch
        {
            RotationScheduleKind.None => null,
            RotationScheduleKind.Days => NextDueByDays(
                schedule.Days!.Value, lastRotatedAt, now),
            RotationScheduleKind.Cron => NextDueByCron(
                schedule.CronExpression!, lastRotatedAt, now),
            _ => throw new InvalidOperationException(
                $"Unhandled RotationScheduleKind: {schedule.Kind}.")
        };
    }

    private static DateTimeOffset NextDueByDays(
        int days, DateTimeOffset? lastRotatedAt, DateTimeOffset now)
    {
        // Anchor off the last-rotation when present (next due = anchor +
        // N days), otherwise off "now" so a freshly-created secret has
        // its first due-date N days out instead of N days after the
        // unix epoch.
        var anchor = lastRotatedAt ?? now;
        // ToUniversalTime is a no-op for an already-UTC offset but
        // guards against callers passing a local-time DateTimeOffset.
        return anchor.ToUniversalTime().AddDays(days);
    }

    private static DateTimeOffset NextDueByCron(
        string cronExpression,
        DateTimeOffset? lastRotatedAt,
        DateTimeOffset now)
    {
        var evaluator = _cronEvaluator
            ?? throw new NotSupportedException(
                "RotationSchedule.Cron requires a registered evaluator. " +
                "Call RotationScheduleCalculator.RegisterCronEvaluator " +
                "during composition (Story 29-2 wires the Cronos-backed " +
                "parser).");

        var anchor = (lastRotatedAt ?? now).ToUniversalTime();
        return evaluator(cronExpression, anchor)
            ?? throw new InvalidOperationException(
                $"Cron evaluator returned no future fire time for " +
                $"expression '{cronExpression}' anchored at {anchor:O}.");
    }
}
