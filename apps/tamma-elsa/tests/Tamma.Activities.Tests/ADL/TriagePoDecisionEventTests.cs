using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// Completeness audit 2026-06-22 (<c>TriagePODecision.md</c> #3) — coverage for the
/// TRIAGE.PO_DECISION.* DCB event mapping
/// (<see cref="EmitTriagePoDecisionEventActivity.BuildTammaEvent"/>) and the
/// <see cref="TriagePoDecisionEvents"/> status convention. A failed/skipped PO step
/// must map to a loud (error/warning) status carrying the decision payload +
/// provider/cost, never a false "success" audit row.
/// </summary>
[TestFixture]
public class TriagePoDecisionEventTests
{
    // ================================================================
    // TriagePoDecisionEvents — type + status convention
    // ================================================================

    [Test]
    public void EventTypes_FollowAggregateActionStatusConvention()
    {
        TriagePoDecisionEvents.Started.Should().Be("TRIAGE.PO_DECISION.STARTED");
        TriagePoDecisionEvents.Completed.Should().Be("TRIAGE.PO_DECISION.COMPLETED");
        TriagePoDecisionEvents.Failed.Should().Be("TRIAGE.PO_DECISION.FAILED");
        TriagePoDecisionEvents.Skipped.Should().Be("TRIAGE.PO_DECISION.SKIPPED");
    }

    [Test]
    public void StatusForEvent_FailedIsError_SkippedIsWarning_OthersSuccess()
    {
        TriagePoDecisionEvents.StatusForEvent(TriagePoDecisionEvents.Failed).Should().Be("error");
        TriagePoDecisionEvents.StatusForEvent(TriagePoDecisionEvents.Skipped).Should().Be("warning");
        TriagePoDecisionEvents.StatusForEvent(TriagePoDecisionEvents.Completed).Should().Be("success");
        TriagePoDecisionEvents.StatusForEvent(TriagePoDecisionEvents.Started).Should().Be("success");
    }

    [Test]
    public void ParseTenantId_ValidGuid_Parses_InvalidOrEmpty_IsNull()
    {
        var g = Guid.NewGuid();
        TriagePoDecisionEvents.ParseTenantId(g.ToString()).Should().Be(g);
        TriagePoDecisionEvents.ParseTenantId("").Should().BeNull();
        TriagePoDecisionEvents.ParseTenantId(null).Should().BeNull();
        TriagePoDecisionEvents.ParseTenantId("nope").Should().BeNull();
    }

    // ================================================================
    // BuildTammaEvent — tags + data + status mapping
    // ================================================================

    [Test]
    public void BuildTammaEvent_Completed_HasSuccessStatus_DecisionPayload_AndIssueTag()
    {
        var evt = EmitTriagePoDecisionEventActivity.BuildTammaEvent(
            TriagePoDecisionEvents.Completed,
            repository: "owner/repo",
            itemNumber: 7,
            tenantId: null,
            decisionStatus: "ok",
            priority: "high",
            itemType: "bug",
            complexity: "medium",
            automation: "tamma-auto",
            providerUsed: "anthropic",
            costUsd: 0.0123m,
            error: null);

        evt.EventType.Should().Be("TRIAGE.PO_DECISION.COMPLETED");
        evt.Status.Should().Be("success");
        evt.Error.Should().BeNull();

        evt.Tags!["repository"].Should().Be("owner/repo");
        evt.Tags!["itemId"].Should().Be("7");
        evt.Tags!["issueId"].Should().Be("7", "the build-out spec tags with issueId from the item");
        evt.Tags!["provider"].Should().Be("anthropic");
        evt.Tags.Should().NotContainKey("tenantId");

        evt.Data["decisionStatus"].Should().Be("ok");
        evt.Data["priority"].Should().Be("high");
        evt.Data["type"].Should().Be("bug");
        evt.Data["automation"].Should().Be("tamma-auto");
        evt.Data["providerUsed"].Should().Be("anthropic");
        evt.Data["costUsd"].Should().Be(0.0123m);
    }

    [Test]
    public void BuildTammaEvent_Failed_HasErrorStatus_AndCarriesError_NoProviderTag()
    {
        var evt = EmitTriagePoDecisionEventActivity.BuildTammaEvent(
            TriagePoDecisionEvents.Failed,
            repository: "owner/repo",
            itemNumber: 99,
            tenantId: null,
            decisionStatus: "llm-failed",
            priority: null, itemType: null, complexity: null, automation: null,
            providerUsed: null,
            costUsd: 0m,
            error: "All providers in the chain failed");

        evt.Status.Should().Be("error");
        evt.Error.Should().Be("All providers in the chain failed");
        evt.Tags.Should().NotContainKey("provider", "no provider succeeded");
        evt.Data["decisionStatus"].Should().Be("llm-failed");
    }

    [Test]
    public void BuildTammaEvent_Skipped_HasWarningStatus()
    {
        var evt = EmitTriagePoDecisionEventActivity.BuildTammaEvent(
            TriagePoDecisionEvents.Skipped,
            "owner/repo", 0, null, "skipped",
            null, null, null, null, null, 0m, null);

        evt.Status.Should().Be("warning");
        // Item number 0 → no item tags.
        evt.Tags!.Should().NotContainKey("itemId");
        evt.Tags!.Should().NotContainKey("issueId");
    }

    [Test]
    public void BuildTammaEvent_WithTenant_StampsTenantTag()
    {
        var tenant = Guid.NewGuid();
        var evt = EmitTriagePoDecisionEventActivity.BuildTammaEvent(
            TriagePoDecisionEvents.Started, "owner/repo", 1, tenant,
            null, null, null, null, null, null, 0m, null);

        evt.Tags!["tenantId"].Should().Be(tenant.ToString("D"));
    }

    // ================================================================
    // ParseCost — tolerant of the loose result-dictionary cost value
    // ================================================================

    [Test]
    public void ParseCost_HandlesDecimalDoubleIntStringAndNull()
    {
        EmitTriagePoDecisionEventActivity.ParseCost(0.5m).Should().Be(0.5m);
        EmitTriagePoDecisionEventActivity.ParseCost(0.25d).Should().Be(0.25m);
        EmitTriagePoDecisionEventActivity.ParseCost(2).Should().Be(2m);
        EmitTriagePoDecisionEventActivity.ParseCost("1.5").Should().Be(1.5m);
        EmitTriagePoDecisionEventActivity.ParseCost(null).Should().Be(0m);
        EmitTriagePoDecisionEventActivity.ParseCost("garbage").Should().Be(0m);
    }
}
