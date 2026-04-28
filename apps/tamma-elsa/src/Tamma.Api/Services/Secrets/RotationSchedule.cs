namespace Tamma.Api.Services.Secrets;

/// <summary>
/// How often a secret should be rotated. Discriminated-union shape
/// (None / Days / Cron) per Story 29-1 AC2; matched on by the
/// <see cref="RotationScheduleCalculator"/> to compute
/// <c>NextRotationDueAt</c>.
///
/// <para>Use the static factories — <see cref="None"/>,
/// <see cref="EveryDays"/>, <see cref="Cron"/> — rather than the
/// constructor to keep call sites readable.</para>
///
/// <example>
/// <code>
/// var schedule = RotationSchedule.EveryDays(90);
/// var due = RotationScheduleCalculator.NextDue(schedule, lastRotated);
/// </code>
/// </example>
/// </summary>
public sealed record RotationSchedule
{
    /// <summary>
    /// Discriminator for the union variant. Pattern-match on this in
    /// the calculator + admin UI rather than inspecting the value
    /// fields directly.
    /// </summary>
    public RotationScheduleKind Kind { get; }

    /// <summary>
    /// Cadence in days. Set when <see cref="Kind"/> is
    /// <see cref="RotationScheduleKind.Days"/>; null otherwise.
    /// </summary>
    public int? Days { get; }

    /// <summary>
    /// Standard 6-field cron expression (seconds-included). Set when
    /// <see cref="Kind"/> is <see cref="RotationScheduleKind.Cron"/>;
    /// null otherwise. Parsed lazily by the calculator so this record
    /// stays free of cron-library dependencies.
    /// </summary>
    public string? CronExpression { get; }

    private RotationSchedule(RotationScheduleKind kind, int? days, string? cron)
    {
        Kind = kind;
        Days = days;
        CronExpression = cron;
    }

    /// <summary>Never auto-rotate; operator must rotate manually.</summary>
    public static RotationSchedule None { get; } =
        new(RotationScheduleKind.None, days: null, cron: null);

    /// <summary>Rotate every <paramref name="days"/> days. Must be positive.</summary>
    public static RotationSchedule EveryDays(int days)
    {
        if (days <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(days),
                days,
                "Rotation cadence must be a positive number of days.");
        return new RotationSchedule(RotationScheduleKind.Days, days, cron: null);
    }

    /// <summary>
    /// Rotate on the schedule described by the cron
    /// <paramref name="expression"/>. The expression is not parsed
    /// here — the calculator parses on demand so callers that never
    /// query <c>NextRotationDueAt</c> avoid the parse cost.
    /// </summary>
    public static RotationSchedule Cron(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ArgumentException(
                "Cron expression must be non-empty.", nameof(expression));
        return new RotationSchedule(RotationScheduleKind.Cron, days: null, expression);
    }

    /// <summary>
    /// Render the schedule as a human-readable string for logs and the
    /// admin UI. Round-trippable via <see cref="TryParse"/>.
    /// </summary>
    public override string ToString() => Kind switch
    {
        RotationScheduleKind.None => "none",
        RotationScheduleKind.Days => $"days:{Days}",
        RotationScheduleKind.Cron => $"cron:{CronExpression}",
        _ => "unknown"
    };

    /// <summary>
    /// Parse a string previously produced by <see cref="ToString"/>.
    /// Returns <c>true</c> on success and writes the parsed schedule to
    /// <paramref name="schedule"/>; otherwise returns <c>false</c>.
    /// </summary>
    public static bool TryParse(string? raw, out RotationSchedule schedule)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            schedule = None;
            return false;
        }

        if (string.Equals(raw, "none", StringComparison.OrdinalIgnoreCase))
        {
            schedule = None;
            return true;
        }

        if (raw.StartsWith("days:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(raw.AsSpan(5), out var days) &&
            days > 0)
        {
            schedule = EveryDays(days);
            return true;
        }

        if (raw.StartsWith("cron:", StringComparison.OrdinalIgnoreCase) &&
            raw.Length > 5)
        {
            schedule = Cron(raw[5..]);
            return true;
        }

        schedule = None;
        return false;
    }
}

/// <summary>Variant tag for <see cref="RotationSchedule"/>.</summary>
public enum RotationScheduleKind
{
    /// <summary>Manual rotation only.</summary>
    None,

    /// <summary>Rotate every N days.</summary>
    Days,

    /// <summary>Rotate on a cron schedule.</summary>
    Cron
}
