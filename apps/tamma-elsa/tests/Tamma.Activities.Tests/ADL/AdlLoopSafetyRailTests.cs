using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Activities.ADL;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// The three pure safety-rail primitives the autonomous loop leans on: the operator stop
/// switch, the spend-ceiling decision, and the config cache the watchdog re-arms from.
/// All three are deliberately Elsa-free so their rules are pinned without a runtime.
/// </summary>
[TestFixture]
public class AdlStopSwitchTests
{
    [Test]
    public void NotStopped_WhenNothingIsConfigured()
    {
        Switch(new Dictionary<string, string?>()).GetStopReason().Should().BeNull();
    }

    [Test]
    public void Stopped_WhenTheConfigFlagIsSet()
    {
        var reason = Switch(new Dictionary<string, string?>
        {
            [ConfigAdlStopSwitch.StoppedKey] = "true",
        }).GetStopReason();

        reason.Should().NotBeNull().And.Contain(ConfigAdlStopSwitch.StoppedKey,
            "the reason is the operator-facing audit string on ADL.LIMITS.CHECK.COMPLETED — "
            + "it has to name which switch was pulled");
    }

    [Test]
    public void Stopped_WhenTheStopFileExists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"adl-stop-{Guid.NewGuid():N}");
        File.WriteAllText(path, "");
        try
        {
            var reason = Switch(new Dictionary<string, string?>
            {
                [ConfigAdlStopSwitch.StopFilePathKey] = path,
            }).GetStopReason();

            reason.Should().NotBeNull().And.Contain(path,
                "the stop FILE is the no-restart path an operator reaches for mid-incident");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void NotStopped_WhenTheConfiguredStopFileIsAbsent()
    {
        Switch(new Dictionary<string, string?>
        {
            [ConfigAdlStopSwitch.StopFilePathKey] =
                Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}"),
        }).GetStopReason().Should().BeNull();
    }

    [Test]
    public void NotStopped_WhenTheFilePathIsBlanked()
    {
        // Empty path disables the file arm entirely — documented escape hatch for hosts
        // where the default location is not writable.
        Switch(new Dictionary<string, string?>
        {
            [ConfigAdlStopSwitch.StopFilePathKey] = "",
        }).GetStopReason().Should().BeNull();
    }

    [Test]
    public void NoConfiguration_IsNotAStop()
    {
        // A switch it cannot read must never be read as "stop": halting the autonomous
        // loop on an unreadable brake would be the same silent outage this lane closes.
        new ConfigAdlStopSwitch(null).GetStopReason().Should().BeNull();
    }

    private static ConfigAdlStopSwitch Switch(Dictionary<string, string?> values)
        => new(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
}

[TestFixture]
public class AdlSpendCeilingTests
{
    [Test]
    public void NoCaps_AlwaysContinues()
    {
        AdlSpendCeiling.Evaluate(spentUsd: 9_999m, tenantLimitUsd: 0m, adlCeilingUsd: 0m)
            .Stop.Should().BeFalse("a zero limit means unlimited, matching the budget contract");
    }

    [Test]
    public void UnderBothCaps_Continues()
    {
        AdlSpendCeiling.Evaluate(10m, tenantLimitUsd: 100m, adlCeilingUsd: 50m)
            .Stop.Should().BeFalse();
    }

    [Test]
    public void AtTheAdlCeiling_Stops()
    {
        var decision = AdlSpendCeiling.Evaluate(50m, tenantLimitUsd: 0m, adlCeilingUsd: 50m);

        decision.Stop.Should().BeTrue(">= is deliberate: the cap is a ceiling, not a target");
        decision.Reason.Should().Contain(AdlSpendCeiling.MaxSpendKey);
    }

    [Test]
    public void AtTheTenantLimit_Stops()
    {
        var decision = AdlSpendCeiling.Evaluate(100m, tenantLimitUsd: 100m, adlCeilingUsd: 0m);

        decision.Stop.Should().BeTrue();
        decision.Reason.Should().Contain("budget");
    }

    [Test]
    public void TheAdlCeilingWins_WhenItIsTighter()
    {
        var decision = AdlSpendCeiling.Evaluate(25m, tenantLimitUsd: 100m, adlCeilingUsd: 20m);

        decision.Stop.Should().BeTrue();
        decision.Reason.Should().Contain(AdlSpendCeiling.MaxSpendKey,
            "when both bite, the operator needs to know WHICH cap stopped the loop");
    }

    [Test]
    public void UnknownSpend_FailsClosed_OnlyWhenACeilingWasAskedFor()
    {
        AdlSpendCeiling.EvaluateUnknown(ceilingConfigured: true, "api down").Stop
            .Should().BeTrue("an operator who set a cap must not be silently uncapped by an outage");

        AdlSpendCeiling.EvaluateUnknown(ceilingConfigured: false, "api down").Stop
            .Should().BeFalse("with no cap there is nothing to evaluate — stopping would be self-inflicted");
    }

    [Test]
    public void NoBudgetOwner_DoesNotStopTheLoop()
    {
        // Reported loudly instead (WARN + ceilingEnforceable=false on the audit event).
        // Bricking a fresh deployment on its first tick, with no in-band recovery, is the
        // same class of outage as the silent death this lane removes.
        AdlSpendCeiling.EvaluateNoBudgetOwner().Stop.Should().BeFalse();
    }
}

[TestFixture]
public class AdlLoopConfigCacheTests
{
    [Test]
    public void StartsEmpty()
    {
        new AdlLoopConfigCache().Last.Should().BeNull();
    }

    [Test]
    public void RemembersTheLastRealConfig()
    {
        var cache = new AdlLoopConfigCache();
        cache.Remember("""{"repository":"owner/one"}""");
        cache.Remember("""{"repository":"owner/two"}""");

        cache.Last.Should().Be("""{"repository":"owner/two"}""");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("{}")]
    public void IgnoresEmptyConfig(string? configJson)
    {
        // A blank config would re-arm the loop against the DEFAULT repository, which is
        // worse than not re-arming; the watchdog must keep the last REAL one.
        var cache = new AdlLoopConfigCache();
        cache.Remember("""{"repository":"owner/real"}""");
        cache.Remember(configJson);

        cache.Last.Should().Be("""{"repository":"owner/real"}""");
    }
}
