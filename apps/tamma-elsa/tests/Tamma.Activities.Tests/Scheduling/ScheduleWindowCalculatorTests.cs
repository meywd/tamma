using FluentAssertions;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Scheduling;

/// <summary>
/// Story 41-30 — pure-math coverage for
/// <see cref="ScheduleWindowCalculator"/> + <see cref="ScheduleLockKey"/>
/// (AC2, AC5). No host, no DB, no Elsa.
/// </summary>
[TestFixture]
public class ScheduleWindowCalculatorTests
{
    // ── TryParse: the accept/reject table AC5's write-time 400 relies on ──

    [TestCase("0 3 * * *")]
    [TestCase("*/15 * * * *")]
    [TestCase("0 0 1 1 *")]
    [TestCase("30 2 * * 1-5")]
    [TestCase("0 */4 * * *")]
    [TestCase("5 4 * * SUN")]
    public void TryParse_Accepts_Standard_5Field_Expressions(string cron)
    {
        ScheduleWindowCalculator.TryParse(cron, out var error).Should().BeTrue();
        error.Should().BeNull();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not a cron")]
    [TestCase("0 3 * *")] // 4 fields
    [TestCase("0 0 3 * * *")] // 6 fields — seconds are not part of the contract
    [TestCase("61 * * * *")] // minute out of range
    [TestCase("0 25 * * *")] // hour out of range
    [TestCase("0 0 32 * *")] // day out of range
    [TestCase("0 0 * 13 *")] // month out of range
    public void TryParse_Rejects_Malformed_Expressions_WithAnError(string? cron)
    {
        ScheduleWindowCalculator.TryParse(cron, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace(
            "the admin API surfaces this as the typed 400 body (AC5)");
    }

    // ── DueWindows ──

    [Test]
    public void DueWindows_Crosses_An_Hour_Boundary()
    {
        var since = new DateTimeOffset(2026, 07, 27, 02, 29, 00, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 07, 27, 03, 30, 00, TimeSpan.Zero);

        var windows = ScheduleWindowCalculator.DueWindows("0 * * * *", since, now);

        windows.Should().Equal(new DateTimeOffset(2026, 07, 27, 03, 00, 00, TimeSpan.Zero));
    }

    [Test]
    public void DueWindows_Excludes_TheAnchor_AndIncludes_Now_Exactly()
    {
        var since = new DateTimeOffset(2026, 07, 27, 03, 00, 00, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 07, 27, 04, 00, 00, TimeSpan.Zero);

        var windows = ScheduleWindowCalculator.DueWindows("0 * * * *", since, now);

        windows.Should().HaveCount(1,
            "strictly-after semantics: an anchor equal to a fired window must not re-yield it");
        windows[0].Should().Be(now);
    }

    [Test]
    public void DueWindows_On_A_European_DstTransition_Instant_IsANoOp_EverythingIsUtc()
    {
        // Europe/Berlin springs forward 2026-03-29 02:00 → 03:00 local. In
        // UTC nothing happens: an hourly schedule yields exactly one window
        // per UTC hour straight through the transition.
        var since = new DateTimeOffset(2026, 03, 29, 00, 30, 00, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 03, 29, 03, 30, 00, TimeSpan.Zero);

        var windows = ScheduleWindowCalculator.DueWindows("0 * * * *", since, now);

        windows.Should().HaveCount(3, "01:00Z, 02:00Z, 03:00Z — no gap, no double");
    }

    [Test]
    public void DueWindows_Handles_A_LeapDay()
    {
        var since = new DateTimeOffset(2023, 01, 01, 00, 00, 00, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 01, 01, 00, 00, 00, TimeSpan.Zero);

        var windows = ScheduleWindowCalculator.DueWindows("0 0 29 2 *", since, now);

        windows.Should().HaveCount(1, "2024 is the only leap year in the range");
        windows[0].Should().Be(new DateTimeOffset(2024, 02, 29, 00, 00, 00, TimeSpan.Zero));
    }

    [Test]
    public void DueWindows_WithASinceInTheFuture_IsEmpty_NotNegative()
    {
        var now = new DateTimeOffset(2026, 07, 27, 03, 00, 00, TimeSpan.Zero);
        var since = now.AddHours(2);

        ScheduleWindowCalculator.DueWindows("0 * * * *", since, now)
            .Should().BeEmpty("clock skew must not produce a throw or a bogus window");
    }

    [Test]
    public void DueWindows_WithAMalformedCron_IsEmpty_FailClosed()
    {
        var now = new DateTimeOffset(2026, 07, 27, 03, 00, 00, TimeSpan.Zero);

        ScheduleWindowCalculator.DueWindows("garbage", now.AddDays(-1), now)
            .Should().BeEmpty("row data must never be able to throw at fire time");
    }

    [Test]
    public void DueWindows_IsBounded_By_MaxWindows()
    {
        var since = new DateTimeOffset(2026, 07, 01, 00, 00, 00, TimeSpan.Zero);
        var now = since.AddDays(20); // 28 800 minutely windows without the bound

        var windows = ScheduleWindowCalculator.DueWindows("* * * * *", since, now, maxWindows: 10);

        windows.Should().HaveCount(10);
    }

    // ── ComputeDue (MAJOR-2 fix, 2026-07-29) — the fire path's catch-up
    // computation must yield the TRUE latest due window regardless of the
    // backlog size ──

    [Test]
    public void ComputeDue_MinutelyCron_18HourGap_YieldsTheMostRecentDueWindow_AndTheTrueCount()
    {
        var now = new DateTimeOffset(2026, 07, 27, 12, 30, 00, TimeSpan.Zero);
        var since = now.AddHours(-18); // 1080 due windows — over the OLD 1000 cap

        var due = ScheduleWindowCalculator.ComputeDue("* * * * *", since, now);

        due.LastWindow.Should().Be(now,
            "MAJOR-2 — the pre-fix capped ascending list held the OLDEST 1000 windows, "
            + "so the 'most recent' fired window was ~7h stale");
        due.FirstWindow.Should().Be(since.AddMinutes(1));
        due.PreviousWindow.Should().Be(now.AddMinutes(-1));
        due.DueCount.Should().Be(1080);
        due.CountSaturated.Should().BeFalse();
    }

    [Test]
    public void ComputeDue_WhenTheCountCapSaturates_StillYieldsTheTrueLatestWindow_AndFlagsIt()
    {
        var now = new DateTimeOffset(2026, 07, 27, 12, 30, 00, TimeSpan.Zero);
        var since = now.AddHours(-18);

        var due = ScheduleWindowCalculator.ComputeDue("* * * * *", since, now, maxCount: 1000);

        due.LastWindow.Should().Be(now,
            "the counting cap must never make the FIRED window stale — the walk re-anchors near now");
        due.PreviousWindow.Should().Be(now.AddMinutes(-1));
        due.DueCount.Should().Be(1000, "the count saturates at the cap");
        due.CountSaturated.Should().BeTrue("so consumers know DueCount means 'at least'");
    }

    [Test]
    public void ComputeDue_SparseCron_SaturatedCap_StillFindsTheTrueLatestWindow_AndItsPredecessor()
    {
        var now = new DateTimeOffset(2026, 07, 27, 12, 30, 00, TimeSpan.Zero);
        var since = now.AddHours(-24);

        // Hourly with a cap of 2: the primary walk stops a long way from now.
        var due = ScheduleWindowCalculator.ComputeDue("0 * * * *", since, now, maxCount: 2);

        due.LastWindow.Should().Be(new DateTimeOffset(2026, 07, 27, 12, 00, 00, TimeSpan.Zero));
        due.PreviousWindow.Should().Be(new DateTimeOffset(2026, 07, 27, 11, 00, 00, TimeSpan.Zero),
            "the newest SKIPPED window must be the one immediately before the fired window, "
            + "not wherever the capped walk happened to stop");
        due.CountSaturated.Should().BeTrue();
    }

    [Test]
    public void ComputeDue_SingleDueWindow_FirstEqualsLast_AndHasNoPredecessor()
    {
        var now = new DateTimeOffset(2026, 07, 27, 12, 30, 00, TimeSpan.Zero);
        var since = now.AddMinutes(-61);

        var due = ScheduleWindowCalculator.ComputeDue("0 * * * *", since, now);

        due.LastWindow.Should().Be(new DateTimeOffset(2026, 07, 27, 12, 00, 00, TimeSpan.Zero));
        due.FirstWindow.Should().Be(due.LastWindow);
        due.PreviousWindow.Should().BeNull();
        due.DueCount.Should().Be(1);
        due.CountSaturated.Should().BeFalse();
    }

    [Test]
    public void ComputeDue_MalformedCron_FutureSince_OrEmptyRange_YieldTheDefault_FailClosed()
    {
        var now = new DateTimeOffset(2026, 07, 27, 12, 30, 00, TimeSpan.Zero);

        ScheduleWindowCalculator.ComputeDue("garbage", now.AddDays(-1), now)
            .Should().Be(default(DueWindowResult), "row data must never throw at fire time");
        ScheduleWindowCalculator.ComputeDue("0 * * * *", now.AddHours(2), now)
            .Should().Be(default(DueWindowResult), "clock skew must not produce a bogus window");
        ScheduleWindowCalculator.ComputeDue("0 * * * *", now.AddSeconds(-30), now)
            .Should().Be(default(DueWindowResult), "no occurrence in the range ⇒ nothing due");
    }

    // ── WindowKey ──

    [Test]
    public void WindowKey_IsDeterministic_AndIso8601Utc()
    {
        var window = new DateTimeOffset(2026, 07, 27, 03, 00, 00, TimeSpan.Zero);

        var a = ScheduleWindowCalculator.WindowKey(window);
        var b = ScheduleWindowCalculator.WindowKey(window);

        a.Should().Be("2026-07-27T03:00:00Z");
        a.Should().Be(b);
    }

    [Test]
    public void WindowKeys_Sort_Lexicographically_In_TimeOrder()
    {
        var t0 = new DateTimeOffset(2026, 07, 27, 03, 00, 00, TimeSpan.Zero);
        var keys = Enumerable.Range(0, 30)
            .Select(i => ScheduleWindowCalculator.WindowKey(t0.AddHours(i * 7)))
            .ToList();

        keys.Should().BeInAscendingOrder(StringComparer.Ordinal,
            "consumers may order by the opaque key without parsing it (41-20 D8)");
    }

    // ── ScheduleLockKey (AC2) ──

    /// <summary>
    /// THE regression pin for <c>HourlyAnalyticsRollupScheduler.cs:241</c> —
    /// <c>ComputeAdvisoryLockKey(year, dayOfYear, hour)</c> has NO tenant
    /// component, so one tenant's leader suppressed every other tenant's fire
    /// for the same window. If a refactor ever drops the tenant id from this
    /// seam's key derivation, this test fails.
    /// </summary>
    [Test]
    public void LockKey_TwoTenants_SameTriggerName_SameWindow_ProduceDifferentKeys_TheHourlyRollupTenantlessKeyBug()
    {
        var triggerId = Guid.NewGuid(); // same schedule identity
        const string windowKey = "2026-07-27T03:00:00Z"; // same window

        var tenantA = ScheduleLockKey.Compute(Guid.NewGuid(), triggerId, windowKey);
        var tenantB = ScheduleLockKey.Compute(Guid.NewGuid(), triggerId, windowKey);

        tenantA.Should().NotBe(tenantB,
            "tenant A's advisory lock must never suppress tenant B's fire for the same window (AC2)");
    }

    [Test]
    public void LockKey_IsDeterministic_AcrossPods()
    {
        var tenantId = Guid.Parse("5a0a1fd6-3f8f-4b41-9f2f-91d9a5f1af01");
        var triggerId = Guid.Parse("0e6df3f4-52d5-4a3e-9a41-1b6a8e2f9c02");

        var a = ScheduleLockKey.Compute(tenantId, triggerId, "2026-07-27T03:00:00Z");
        var b = ScheduleLockKey.Compute(tenantId, triggerId, "2026-07-27T03:00:00Z");

        a.Should().Be(b, "every pod competing for the same (tenant, trigger, window) must agree");
    }

    [Test]
    public void LockKey_Differs_By_Trigger_And_By_Window()
    {
        var tenantId = Guid.NewGuid();
        var triggerId = Guid.NewGuid();

        var baseline = ScheduleLockKey.Compute(tenantId, triggerId, "2026-07-27T03:00:00Z");

        ScheduleLockKey.Compute(tenantId, Guid.NewGuid(), "2026-07-27T03:00:00Z")
            .Should().NotBe(baseline, "one stuck trigger must not poison another's lock");
        ScheduleLockKey.Compute(tenantId, triggerId, "2026-07-27T04:00:00Z")
            .Should().NotBe(baseline, "one stuck window must not poison the next window's lock");
    }
}
