using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Activities.Blocker;
using Tamma.Activities.Blocker.Models;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Blocker;

/// <summary>
/// Completeness build-out 2026-06-22 (<c>BlockerDiagnosis.md</c>, 7-1G AC2/AC6/AC9) —
/// coverage for the blocker-diagnosis correctness + observability fixes:
///   - the <c>BLOCKER.*</c> DCB event catalogue + status convention (no false success),
///   - <see cref="EmitBlockerEventActivity.BuildTammaEvent"/> tag/data mapping,
///   - the AC9 OTel metric increments (<see cref="BlockerMetrics"/>),
///   - the durable per-level wait-minutes resolution (<see cref="DetectProgressActivity.ResolveWaitMinutes(int, int?, string)"/>),
///   - the terminal-status precedence that fixes the always-"Escalated" bug and adds the
///     real <c>Timeout</c> terminal (<see cref="BlockerDiagnosisWorkflow.ResolveStatus"/> /
///     <see cref="BlockerDiagnosisWorkflow.TerminalEventType"/>).
/// </summary>
[TestFixture]
public class BlockerEventTests
{
    // ================================================================
    // BlockerEvents — type catalogue + status convention
    // ================================================================

    [Test]
    public void EventTypes_FollowAggregateActionStatusConvention()
    {
        BlockerEvents.DiagnosedSuccess.Should().Be("BLOCKER.DIAGNOSED.SUCCESS");
        BlockerEvents.DiagnosedFailed.Should().Be("BLOCKER.DIAGNOSED.FAILED");
        BlockerEvents.ResolutionAttempted.Should().Be("BLOCKER.RESOLUTION_ATTEMPTED");
        BlockerEvents.ProgressDetected.Should().Be("BLOCKER.PROGRESS_DETECTED");
        BlockerEvents.ProgressTimedOut.Should().Be("BLOCKER.PROGRESS_TIMED_OUT");
        BlockerEvents.Escalated.Should().Be("BLOCKER.ESCALATED");
        BlockerEvents.Resolved.Should().Be("BLOCKER.RESOLVED");
        BlockerEvents.TimedOut.Should().Be("BLOCKER.TIMED_OUT");
    }

    [Test]
    public void StatusForEvent_TimeoutAndFailedDiagnosisAreError_RestAreSuccess()
    {
        // A never-answered escalation (TIMED_OUT) and a failed diagnosis are LOUD error rows
        // — never a silent false success.
        BlockerEvents.StatusForEvent(BlockerEvents.TimedOut).Should().Be("error");
        BlockerEvents.StatusForEvent(BlockerEvents.DiagnosedFailed).Should().Be("error");

        BlockerEvents.StatusForEvent(BlockerEvents.DiagnosedSuccess).Should().Be("success");
        BlockerEvents.StatusForEvent(BlockerEvents.ResolutionAttempted).Should().Be("success");
        BlockerEvents.StatusForEvent(BlockerEvents.ProgressDetected).Should().Be("success");
        // A per-level progress timeout simply advances the ladder — expected, NOT error.
        BlockerEvents.StatusForEvent(BlockerEvents.ProgressTimedOut).Should().Be("success");
        BlockerEvents.StatusForEvent(BlockerEvents.Escalated).Should().Be("success");
        BlockerEvents.StatusForEvent(BlockerEvents.Resolved).Should().Be("success");
    }

    [Test]
    public void ParseTenantId_ValidGuid_Parses_InvalidOrEmpty_IsNull()
    {
        var g = Guid.NewGuid();
        BlockerEvents.ParseTenantId(g.ToString()).Should().Be(g);
        BlockerEvents.ParseTenantId("").Should().BeNull();
        BlockerEvents.ParseTenantId(null).Should().BeNull();
        BlockerEvents.ParseTenantId("not-a-guid").Should().BeNull();
    }

    // ================================================================
    // EmitBlockerEventActivity.BuildTammaEvent — tags + data + status
    // ================================================================

    [Test]
    public void BuildTammaEvent_Diagnosed_StampsTagsAndConfidence_SuccessStatus()
    {
        var evt = EmitBlockerEventActivity.BuildTammaEvent(
            BlockerEvents.DiagnosedSuccess,
            sessionId: "sess-1", storyId: "story-7", juniorId: "junior-9",
            tenantId: null,
            blockerType: "TechnicalKnowledgeGap", severity: "High",
            level: null, attempt: 0, confidence: 0.82,
            progressType: null, resolutionTimeSeconds: 0);

        evt.EventType.Should().Be("BLOCKER.DIAGNOSED.SUCCESS");
        evt.Status.Should().Be("success");
        evt.Tags!["sessionId"].Should().Be("sess-1");
        evt.Tags!["storyId"].Should().Be("story-7");
        evt.Tags!["juniorId"].Should().Be("junior-9");
        evt.Tags!["blockerType"].Should().Be("TechnicalKnowledgeGap");
        evt.Tags!["severity"].Should().Be("High");
        evt.Tags.Should().NotContainKey("tenantId", "single-user / platform-scope event");
        evt.Data["confidence"].Should().Be(0.82);
    }

    [Test]
    public void BuildTammaEvent_WithTenant_StampsTenantTag()
    {
        var tenant = Guid.NewGuid();
        var evt = EmitBlockerEventActivity.BuildTammaEvent(
            BlockerEvents.ResolutionAttempted,
            "sess-1", "story-1", "junior-1", tenant,
            "DebuggingStuck", "Medium", "Hint", attempt: 1, confidence: 0,
            progressType: null, resolutionTimeSeconds: 0);

        evt.Tags!["tenantId"].Should().Be(tenant.ToString("D"));
        evt.Tags!["level"].Should().Be("Hint");
        evt.Data["attempt"].Should().Be(1);
    }

    [Test]
    public void BuildTammaEvent_TimedOut_IsErrorStatus_NotFalseSuccess()
    {
        var evt = EmitBlockerEventActivity.BuildTammaEvent(
            BlockerEvents.TimedOut,
            "sess-1", "story-1", "junior-1", null,
            "ExternalDependency", "Critical", "Escalation", attempt: 4, confidence: 0,
            progressType: null, resolutionTimeSeconds: 90000);

        evt.EventType.Should().Be("BLOCKER.TIMED_OUT");
        evt.Status.Should().Be("error");
        evt.Data["resolutionTimeSeconds"].Should().Be(90000d);
    }

    [Test]
    public void BuildTammaEvent_ProgressDetected_CarriesProgressType()
    {
        var evt = EmitBlockerEventActivity.BuildTammaEvent(
            BlockerEvents.ProgressDetected,
            "sess-1", "story-1", "junior-1", null,
            "DebuggingStuck", null, "Guidance", attempt: 0, confidence: 0,
            progressType: "new-commit", resolutionTimeSeconds: 0);

        evt.Data["progressType"].Should().Be("new-commit");
        evt.Status.Should().Be("success");
    }

    [Test]
    public void BuildTammaEvent_OmitsEmptyTagsAndZeroData()
    {
        var evt = EmitBlockerEventActivity.BuildTammaEvent(
            BlockerEvents.Resolved,
            sessionId: "", storyId: "", juniorId: "", tenantId: null,
            blockerType: "", severity: "", level: "", attempt: 0, confidence: 0,
            progressType: "", resolutionTimeSeconds: 0);

        evt.Tags!.Should().NotContainKey("sessionId");
        evt.Tags!.Should().NotContainKey("blockerType");
        evt.Data.Should().NotContainKey("attempt");
        evt.Data.Should().NotContainKey("confidence");
        evt.Data.Should().NotContainKey("resolutionTimeSeconds");
    }

    // ================================================================
    // DetectProgressActivity.ResolveWaitMinutes — durable per-level wait + config
    // ================================================================

    [Test]
    public void ResolveWaitMinutes_ExplicitInputWins()
    {
        DetectProgressActivity.ResolveWaitMinutes(42, configValue: 99, level: "Hint").Should().Be(42);
    }

    [Test]
    public void ResolveWaitMinutes_ConfigUsedWhenNoExplicit()
    {
        DetectProgressActivity.ResolveWaitMinutes(0, configValue: 99, level: "Hint").Should().Be(99);
    }

    [Test]
    public void ResolveWaitMinutes_FallsBackToPerLevelDefaults()
    {
        DetectProgressActivity.ResolveWaitMinutes(0, configValue: null, level: "Hint").Should().Be(15);
        DetectProgressActivity.ResolveWaitMinutes(0, configValue: null, level: "Guidance").Should().Be(30);
        DetectProgressActivity.ResolveWaitMinutes(0, configValue: null, level: "Assistance").Should().Be(45);
        DetectProgressActivity.ResolveWaitMinutes(0, configValue: 0, level: "Unknown").Should().Be(15);
    }

    // ================================================================
    // Terminal status precedence — fixes always-"Escalated"; adds Timeout
    // ================================================================

    [Test]
    public void ResolveStatus_ResolvedWins()
    {
        BlockerDiagnosisWorkflow.ResolveStatus(isResolved: true, timedOut: false)
            .Should().Be(BlockerResolutionStatus.Resolved);
        // Resolved takes precedence even if a timeout flag was also set.
        BlockerDiagnosisWorkflow.ResolveStatus(isResolved: true, timedOut: true)
            .Should().Be(BlockerResolutionStatus.Resolved);
    }

    [Test]
    public void ResolveStatus_TimedOut_ProducesRealTimeoutTerminal()
    {
        BlockerDiagnosisWorkflow.ResolveStatus(isResolved: false, timedOut: true)
            .Should().Be(BlockerResolutionStatus.Timeout);
    }

    [Test]
    public void ResolveStatus_DefaultsToEscalated_WhenNotResolvedAndNotTimedOut()
    {
        BlockerDiagnosisWorkflow.ResolveStatus(isResolved: false, timedOut: false)
            .Should().Be(BlockerResolutionStatus.Escalated);
    }

    [Test]
    public void TerminalEventType_MatchesStatusPrecedence()
    {
        BlockerDiagnosisWorkflow.TerminalEventType(true, false).Should().Be(BlockerEvents.Resolved);
        BlockerDiagnosisWorkflow.TerminalEventType(false, true).Should().Be(BlockerEvents.TimedOut);
        BlockerDiagnosisWorkflow.TerminalEventType(false, false).Should().Be(BlockerEvents.Escalated);
    }

    // ================================================================
    // BlockerMetrics — AC9 instrument increments
    // ================================================================

    [Test]
    public void Metrics_RecordDiagnosed_IncrementsTotal()
    {
        var before = BlockerMetrics.TotalCount;
        BlockerMetrics.RecordDiagnosed("TechnicalKnowledgeGap", tenant: null);
        BlockerMetrics.TotalCount.Should().Be(before + 1);
    }

    [Test]
    public void Metrics_RecordTerminals_IncrementMatchingCounters()
    {
        var beforeResolved = BlockerMetrics.ResolvedCount;
        var beforeEscalated = BlockerMetrics.EscalatedCount;
        var beforeTimedOut = BlockerMetrics.TimedOutCount;

        BlockerMetrics.RecordResolved("DebuggingStuck", null, "Guidance", TimeSpan.FromMinutes(5));
        BlockerMetrics.RecordEscalated("ExternalDependency", null, TimeSpan.FromMinutes(60));
        BlockerMetrics.RecordTimedOut("PersonalBlocker", null, TimeSpan.FromHours(24));

        BlockerMetrics.ResolvedCount.Should().Be(beforeResolved + 1);
        BlockerMetrics.EscalatedCount.Should().Be(beforeEscalated + 1);
        BlockerMetrics.TimedOutCount.Should().Be(beforeTimedOut + 1);
    }

    // ================================================================
    // BlockerSignalTimeout — 15s per-collector cap (7-1G AC3)
    // ================================================================

    private static IConfiguration ConfigWithTimeout(int seconds)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BlockerDiagnosis:SignalCollectionTimeoutSeconds"] = seconds.ToString()
            })
            .Build();

    [Test]
    public void SignalTimeout_DefaultIs15Seconds_AndConfigOverrides()
    {
        BlockerSignalTimeout.DefaultTimeoutSeconds.Should().Be(15);
        BlockerSignalTimeout.ResolveTimeoutSeconds(configuration: null).Should().Be(15);
        BlockerSignalTimeout.ResolveTimeoutSeconds(ConfigWithTimeout(5)).Should().Be(5);
    }

    [Test]
    public async Task SignalTimeout_FastWork_CompletesInTime()
    {
        var ran = false;
        var completed = await BlockerSignalTimeout.RunAsync(ConfigWithTimeout(5), async () =>
        {
            await Task.Yield();
            ran = true;
        });

        completed.Should().BeTrue();
        ran.Should().BeTrue();
    }

    [Test]
    public async Task SignalTimeout_SlowWork_ReportsTimeout_DoesNotHang()
    {
        // A collector that out-runs the deadline must return false (CollectionSucceeded=false)
        // rather than block the join. With a 1s configured deadline, a 60s work task loses the
        // race and the helper returns promptly (we cap the test at 15s to prove no hang).
        var completed = await BlockerSignalTimeout
            .RunAsync(ConfigWithTimeout(1), () => Task.Delay(TimeSpan.FromSeconds(60)))
            .WaitAsync(TimeSpan.FromSeconds(15));

        completed.Should().BeFalse();
    }

    [Test]
    public async Task SignalTimeout_WorkThrows_PropagatesToCaller()
    {
        Func<Task> act = async () => await BlockerSignalTimeout.RunAsync(ConfigWithTimeout(5), () =>
            throw new InvalidOperationException("boom"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }
}
