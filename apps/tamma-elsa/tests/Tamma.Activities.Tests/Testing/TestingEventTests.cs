using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Testing;

namespace Tamma.Activities.Tests.Testing;

/// <summary>
/// Completeness audit 2026-06-22 (<c>Testing.md</c> §Missing #3) — coverage for the
/// <c>TEST.*</c> / <c>GATE.*</c> DCB event mapping
/// (<see cref="EmitTestingEventActivity.BuildTammaEvent"/>) and the
/// <see cref="TestingEvents"/> status / escalation-reason convention.
///
/// <para>The core completeness gap was a real pipeline with NO audit events. These tests
/// pin the now-explicit contract: CI-trigger-failure, CI-timeout, auto-fix no-op, terminal
/// fail and terminal escalation are LOUD (error-status) audit rows carrying an
/// <c>escalationReason</c> and the underlying <c>errorDetail</c> — never a silent false
/// success.</para>
/// </summary>
[TestFixture]
public class TestingEventTests
{
    [Test]
    public void EventTypes_FollowAggregateActionStatusConvention()
    {
        TestingEvents.CiTriggeredSuccess.Should().Be("TEST.CI_TRIGGERED.SUCCESS");
        TestingEvents.CiTriggeredFailed.Should().Be("TEST.CI_TRIGGERED.FAILED");
        TestingEvents.ResultsReceived.Should().Be("TEST.RESULTS_RECEIVED.SUCCESS");
        TestingEvents.CiTimedOut.Should().Be("TEST.CI_TIMED_OUT.FAILED");
        TestingEvents.GateEvaluated.Should().Be("GATE.EVALUATED.SUCCESS");
        TestingEvents.AutofixCommitted.Should().Be("GATE.AUTOFIX_COMMITTED.SUCCESS");
        TestingEvents.AutofixNoop.Should().Be("GATE.AUTOFIX_NOOP.FAILED");
        TestingEvents.GatePassed.Should().Be("GATE.PASSED.SUCCESS");
        TestingEvents.GateFailed.Should().Be("GATE.FAILED.FAILED");
        TestingEvents.GateEscalated.Should().Be("GATE.ESCALATED.FAILED");
    }

    [Test]
    public void StatusForEvent_DegradedTerminalsAreError_RestAreSuccess()
    {
        // LOUD failures — never a silent false success.
        TestingEvents.StatusForEvent(TestingEvents.CiTriggeredFailed).Should().Be("error");
        TestingEvents.StatusForEvent(TestingEvents.CiTimedOut).Should().Be("error");
        TestingEvents.StatusForEvent(TestingEvents.AutofixNoop).Should().Be("error");
        TestingEvents.StatusForEvent(TestingEvents.GateFailed).Should().Be("error");
        TestingEvents.StatusForEvent(TestingEvents.GateEscalated).Should().Be("error");

        // Normal transitions.
        TestingEvents.StatusForEvent(TestingEvents.CiTriggeredSuccess).Should().Be("success");
        TestingEvents.StatusForEvent(TestingEvents.ResultsReceived).Should().Be("success");
        TestingEvents.StatusForEvent(TestingEvents.GateEvaluated).Should().Be("success");
        TestingEvents.StatusForEvent(TestingEvents.AutofixCommitted).Should().Be("success");
        TestingEvents.StatusForEvent(TestingEvents.GatePassed).Should().Be("success");
    }

    [Test]
    public void EscalationReasons_AreDistinctAndNonEmpty()
    {
        var reasons = new[]
        {
            TestingEvents.ReasonCritical,
            TestingEvents.ReasonRetryExhausted,
            TestingEvents.ReasonCiTimeout,
            TestingEvents.ReasonCiTriggerFailed,
            TestingEvents.ReasonAutofixNoop,
        };
        reasons.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r));
        reasons.Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void ParseTenantId_ValidGuid_Parses_InvalidOrEmpty_IsNull()
    {
        var g = Guid.NewGuid();
        TestingEvents.ParseTenantId(g.ToString()).Should().Be(g);
        TestingEvents.ParseTenantId("").Should().BeNull();
        TestingEvents.ParseTenantId(null).Should().BeNull();
        TestingEvents.ParseTenantId("not-a-guid").Should().BeNull();
    }

    [Test]
    public void BuildTammaEvent_GateEvaluated_HasSuccessStatus_AndGatePayload()
    {
        var evt = EmitTestingEventActivity.BuildTammaEvent(
            TestingEvents.GateEvaluated,
            sessionId: "tdd-7",
            repository: "owner/repo",
            branch: "feat/x",
            runId: "run-123",
            tenantId: null,
            attempt: 1,
            maxAttempts: 3,
            outcome: "MajorIssues",
            score: 72.5,
            skillLevel: 3,
            filesChanged: -1,
            escalationReason: null,
            errorDetail: null);

        evt.EventType.Should().Be("GATE.EVALUATED.SUCCESS");
        evt.Status.Should().Be("success");

        evt.Tags!["sessionId"].Should().Be("tdd-7");
        evt.Tags!["repository"].Should().Be("owner/repo");
        evt.Tags!["branch"].Should().Be("feat/x");
        evt.Tags!["runId"].Should().Be("run-123");
        evt.Tags!["attempt"].Should().Be("1");
        evt.Tags.Should().NotContainKey("tenantId", "single-user / platform-scope event");

        evt.Data["outcome"].Should().Be("MajorIssues");
        evt.Data["score"].Should().Be(72.5);
        evt.Data["skillLevel"].Should().Be(3);
        evt.Data.Should().NotContainKey("filesChanged", "negative sentinel is omitted");
        evt.Data.Should().NotContainKey("escalationReason");
    }

    [Test]
    public void BuildTammaEvent_Escalated_HasErrorStatus_AndSurfacesReasonAndCause()
    {
        // The core no-false-success guarantee: an escalation is a LOUD (error) audit row
        // that carries the reason AND the real underlying cause, not a generic string.
        var evt = EmitTestingEventActivity.BuildTammaEvent(
            TestingEvents.GateEscalated,
            sessionId: "tdd-9",
            repository: "owner/repo",
            branch: "feat/y",
            runId: "run-9",
            tenantId: null,
            attempt: 3,
            maxAttempts: 3,
            outcome: "MajorIssues",
            score: 40,
            skillLevel: 4,
            filesChanged: -1,
            escalationReason: TestingEvents.ReasonRetryExhausted,
            errorDetail: "2 lint error(s) still present after 3 fix attempt(s)");

        evt.EventType.Should().Be("GATE.ESCALATED.FAILED");
        evt.Status.Should().Be("error");
        evt.Data["escalationReason"].Should().Be("retry-budget-exhausted");
        evt.Data["errorDetail"].Should().Be("2 lint error(s) still present after 3 fix attempt(s)");
    }

    [Test]
    public void BuildTammaEvent_AutofixCommitted_RecordsFilesChanged()
    {
        var evt = EmitTestingEventActivity.BuildTammaEvent(
            TestingEvents.AutofixCommitted,
            sessionId: "tdd-1", repository: "owner/repo", branch: "b", runId: "run-1",
            tenantId: null, attempt: 1, maxAttempts: 3,
            outcome: null, score: -1, skillLevel: 0, filesChanged: 4,
            escalationReason: null, errorDetail: null);

        evt.Status.Should().Be("success");
        evt.Data["filesChanged"].Should().Be(4);
        evt.Data.Should().NotContainKey("score", "negative sentinel is omitted");
        evt.Data.Should().NotContainKey("skillLevel", "zero sentinel is omitted");
    }

    [Test]
    public void BuildTammaEvent_AutofixNoop_HasErrorStatus()
    {
        var evt = EmitTestingEventActivity.BuildTammaEvent(
            TestingEvents.AutofixNoop,
            sessionId: "tdd-1", repository: "owner/repo", branch: "b", runId: "run-1",
            tenantId: null, attempt: 2, maxAttempts: 3,
            outcome: "MajorIssues", score: 50, skillLevel: 3, filesChanged: 0,
            escalationReason: TestingEvents.ReasonAutofixNoop,
            errorDetail: "LLM fix produced no file changes");

        evt.Status.Should().Be("error");
        evt.Data["filesChanged"].Should().Be(0, "a zero-files-changed commit is recorded, not omitted");
        evt.Data["escalationReason"].Should().Be("autofix-no-op");
    }

    [Test]
    public void BuildTammaEvent_WithTenant_StampsTenantTag()
    {
        var tenant = Guid.NewGuid();
        var evt = EmitTestingEventActivity.BuildTammaEvent(
            TestingEvents.CiTriggeredSuccess,
            sessionId: "s", repository: "owner/repo", branch: "b", runId: "run-1",
            tenantId: tenant, attempt: 0, maxAttempts: 3,
            outcome: null, score: -1, skillLevel: 0, filesChanged: -1,
            escalationReason: null, errorDetail: null);

        evt.Tags!["tenantId"].Should().Be(tenant.ToString("D"));
    }

    [Test]
    public void BuildTammaEvent_AlwaysCarriesAttemptTag_EvenAtZero()
    {
        var evt = EmitTestingEventActivity.BuildTammaEvent(
            TestingEvents.CiTriggeredSuccess,
            sessionId: null, repository: null, branch: null, runId: null,
            tenantId: null, attempt: 0, maxAttempts: 3,
            outcome: null, score: -1, skillLevel: 0, filesChanged: -1,
            escalationReason: null, errorDetail: null);

        evt.Tags!["attempt"].Should().Be("0");
        evt.Tags!.Should().NotContainKey("sessionId");
        evt.Tags!.Should().NotContainKey("repository");
    }
}
