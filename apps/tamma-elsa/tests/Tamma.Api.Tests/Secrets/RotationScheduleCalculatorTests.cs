using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// Tests for <see cref="RotationScheduleCalculator"/>. Covers:
/// <list type="bullet">
///   <item><description>None / Days / Cron dispatch.</description></item>
///   <item><description>DST boundary safety — UTC arithmetic stays
///     correct across spring-forward / fall-back / leap-year /
///     leap-second-adjacent dates (~6 cases per Story 29-1 plan).</description></item>
///   <item><description>Cron evaluator registration seam (Story
///     29-2 wires the real Cronos parser).</description></item>
/// </list>
///
/// <para><b>Test isolation</b>: <see cref="RotationScheduleCalculator.RegisterCronEvaluator"/>
/// stores the evaluator in a static field. Cron tests register the
/// evaluator inside <c>[SetUp]</c> and clear it in <c>[TearDown]</c>
/// so they don't bleed into adjacent tests.</para>
/// </summary>
[TestFixture]
public class RotationScheduleCalculatorTests
{
    [SetUp]
    public void Setup()
    {
        // Default: no evaluator registered. Individual tests register
        // a fake when needed.
        RotationScheduleCalculator.RegisterCronEvaluator(null);
    }

    [TearDown]
    public void TearDown()
    {
        RotationScheduleCalculator.RegisterCronEvaluator(null);
    }

    // ────────────────────────────────────────────────────────────────────────
    // None
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public void NextDue_None_AlwaysReturnsNull()
    {
        var now = new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero);
        RotationScheduleCalculator.NextDue(RotationSchedule.None, null, now)
            .Should().BeNull();
        RotationScheduleCalculator.NextDue(RotationSchedule.None, now.AddDays(-30), now)
            .Should().BeNull();
    }

    // ────────────────────────────────────────────────────────────────────────
    // Days — anchor selection
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public void NextDue_Days_NoLastRotation_AnchorsOnNow()
    {
        var now = new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero);
        var schedule = RotationSchedule.EveryDays(90);

        var due = RotationScheduleCalculator.NextDue(schedule, null, now);

        due.Should().Be(now.AddDays(90));
    }

    [Test]
    public void NextDue_Days_WithLastRotation_AnchorsOnThat()
    {
        var lastRotated = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = lastRotated.AddDays(30);
        var schedule = RotationSchedule.EveryDays(90);

        var due = RotationScheduleCalculator.NextDue(schedule, lastRotated, now);

        due.Should().Be(lastRotated.AddDays(90),
            because: "the cadence is anchored on last-rotated, not on 'now'");
    }

    [Test]
    public void NextDue_Days_LocalTimeAnchor_NormalisesToUtc()
    {
        // Caller passes a -05:00 offset; the result must still be a
        // UTC instant N days later (no off-by-an-hour from the offset
        // shift).
        var lastRotatedLocal = new DateTimeOffset(
            2026, 4, 21, 7, 0, 0, TimeSpan.FromHours(-5));
        var now = lastRotatedLocal.AddDays(1);

        var due = RotationScheduleCalculator.NextDue(
            RotationSchedule.EveryDays(7),
            lastRotatedLocal,
            now);

        due!.Value.Offset.Should().Be(TimeSpan.Zero);
        due.Value.UtcDateTime.Should().Be(
            lastRotatedLocal.UtcDateTime.AddDays(7));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Days — DST + leap-year + month-boundary cases
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// DST boundary cases. Each row is
    /// (lastRotatedAt, daysCadence, expectedDueUtc) — the calculator
    /// must yield the expected UTC instant regardless of any local-
    /// time DST transitions in the interval. UTC arithmetic is
    /// DST-agnostic by construction; these tests pin that contract so
    /// a regression to local-time math would surface immediately.
    /// </summary>
    public static IEnumerable<TestCaseData> DstBoundaryCases()
    {
        // 1. Crosses US spring-forward (2026-03-08 02:00 local).
        yield return new TestCaseData(
                new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero),
                30,
                new DateTimeOffset(2026, 3, 30, 0, 0, 0, TimeSpan.Zero))
            .SetName("Days_CrossesUsSpringForward");

        // 2. Crosses US fall-back (2026-11-01 02:00 local).
        yield return new TestCaseData(
                new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero),
                30,
                new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero))
            .SetName("Days_CrossesUsFallBack");

        // 3. Crosses EU spring-forward (2026-03-29 01:00 UTC).
        yield return new TestCaseData(
                new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
                90,
                new DateTimeOffset(2026, 5, 30, 0, 0, 0, TimeSpan.Zero))
            .SetName("Days_CrossesEuSpringForward");

        // 4. Crosses EU fall-back (2026-10-25 01:00 UTC).
        yield return new TestCaseData(
                new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
                30,
                new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero))
            .SetName("Days_CrossesEuFallBack");

        // 5. Crosses February 29 in a leap year (2028 is a leap year).
        yield return new TestCaseData(
                new DateTimeOffset(2028, 2, 1, 0, 0, 0, TimeSpan.Zero),
                30,
                new DateTimeOffset(2028, 3, 2, 0, 0, 0, TimeSpan.Zero))
            .SetName("Days_AcrossLeapDay");

        // 6. Year boundary.
        yield return new TestCaseData(
                new DateTimeOffset(2026, 12, 15, 12, 0, 0, TimeSpan.Zero),
                30,
                new DateTimeOffset(2027, 1, 14, 12, 0, 0, TimeSpan.Zero))
            .SetName("Days_AcrossYearBoundary");

        // 7. Month boundary at end of 31-day month into 30-day month.
        yield return new TestCaseData(
                new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero),
                30,
                new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero))
            .SetName("Days_AcrossShortMonth");
    }

    [TestCaseSource(nameof(DstBoundaryCases))]
    public void NextDue_Days_StaysCorrectAcrossBoundaries(
        DateTimeOffset lastRotated,
        int days,
        DateTimeOffset expected)
    {
        var due = RotationScheduleCalculator.NextDue(
            RotationSchedule.EveryDays(days),
            lastRotated,
            now: lastRotated.AddDays(1));

        due.Should().Be(expected);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Cron — evaluator seam
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public void NextDue_Cron_WithoutEvaluator_Throws()
    {
        var schedule = RotationSchedule.Cron("0 0 0 * * ?");
        var now = new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero);

        Action act = () =>
            RotationScheduleCalculator.NextDue(schedule, null, now);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Cron*evaluator*");
    }

    [Test]
    public void NextDue_Cron_WithEvaluator_DispatchesAndReturnsResult()
    {
        var fixedResult = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        string? sawExpression = null;
        DateTimeOffset? sawAnchor = null;

        RotationScheduleCalculator.RegisterCronEvaluator((expr, from) =>
        {
            sawExpression = expr;
            sawAnchor = from;
            return fixedResult;
        });

        var now = new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero);
        var schedule = RotationSchedule.Cron("0 0 0 1 * ?");

        var due = RotationScheduleCalculator.NextDue(schedule, null, now);

        due.Should().Be(fixedResult);
        sawExpression.Should().Be("0 0 0 1 * ?");
        sawAnchor.Should().Be(now);
    }

    [Test]
    public void NextDue_Cron_AnchorsOnLastRotated_WhenSet()
    {
        DateTimeOffset? sawAnchor = null;
        RotationScheduleCalculator.RegisterCronEvaluator((_, from) =>
        {
            sawAnchor = from;
            return from.AddDays(1);
        });

        var lastRotated = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = lastRotated.AddDays(15);
        var schedule = RotationSchedule.Cron("0 0 0 * * ?");

        RotationScheduleCalculator.NextDue(schedule, lastRotated, now);

        sawAnchor.Should().Be(lastRotated);
    }

    [Test]
    public void NextDue_Cron_EvaluatorReturnsNull_Throws()
    {
        RotationScheduleCalculator.RegisterCronEvaluator((_, _) => null);

        var schedule = RotationSchedule.Cron("invalid");
        var now = new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero);

        Action act = () =>
            RotationScheduleCalculator.NextDue(schedule, null, now);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no future fire time*");
    }

    // ────────────────────────────────────────────────────────────────────────
    // RotationSchedule constructor + parsers
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public void EveryDays_RejectsZeroAndNegative()
    {
        Action zero = () => RotationSchedule.EveryDays(0);
        Action neg = () => RotationSchedule.EveryDays(-1);
        zero.Should().Throw<ArgumentOutOfRangeException>();
        neg.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void Cron_RejectsBlank()
    {
        Action blank = () => RotationSchedule.Cron("  ");
        blank.Should().Throw<ArgumentException>();
    }

    [TestCase("none", RotationScheduleKind.None)]
    [TestCase("NONE", RotationScheduleKind.None)]
    [TestCase("days:30", RotationScheduleKind.Days)]
    [TestCase("DAYS:90", RotationScheduleKind.Days)]
    [TestCase("cron:0 0 0 * * ?", RotationScheduleKind.Cron)]
    public void TryParse_RoundTripsValidExpressions(string raw, RotationScheduleKind expected)
    {
        var ok = RotationSchedule.TryParse(raw, out var schedule);
        ok.Should().BeTrue();
        schedule.Kind.Should().Be(expected);
    }

    [TestCase("days:0")]
    [TestCase("days:-1")]
    [TestCase("days:abc")]
    [TestCase("cron:")]
    [TestCase("garbage")]
    [TestCase("")]
    [TestCase(null)]
    public void TryParse_RejectsInvalid(string? raw)
    {
        var ok = RotationSchedule.TryParse(raw, out _);
        ok.Should().BeFalse();
    }

    [Test]
    public void ToString_ProducesParseableForm()
    {
        RotationSchedule.None.ToString().Should().Be("none");
        RotationSchedule.EveryDays(7).ToString().Should().Be("days:7");
        RotationSchedule.Cron("0 0 0 * * ?").ToString().Should().Be("cron:0 0 0 * * ?");
    }
}
