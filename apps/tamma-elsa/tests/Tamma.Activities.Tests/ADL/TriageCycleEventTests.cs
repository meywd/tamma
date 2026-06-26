using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// Completeness audit 2026-06-22 (<c>TriageItemCycle.md</c> #3) — coverage for the
/// cycle-scoped TRIAGE.ISSUE.* DCB event mapping
/// (<see cref="EmitTriageCycleEventActivity.BuildTammaEvent"/>) and the
/// <see cref="TriageCycleEvents"/> status convention. A skipped/failed cycle must map
/// to a loud (warning/error) status carrying the item key + classification tags, never
/// a false "success" audit row.
/// </summary>
[TestFixture]
public class TriageCycleEventTests
{
    [Test]
    public void EventTypes_FollowAggregateActionStatusConvention()
    {
        TriageCycleEvents.Started.Should().Be("TRIAGE.ISSUE.STARTED");
        TriageCycleEvents.Completed.Should().Be("TRIAGE.ISSUE.COMPLETED");
        TriageCycleEvents.Skipped.Should().Be("TRIAGE.ISSUE.SKIPPED");
        TriageCycleEvents.Failed.Should().Be("TRIAGE.ISSUE.FAILED");
    }

    [Test]
    public void StatusForEvent_FailedIsError_SkippedIsWarning_OthersSuccess()
    {
        TriageCycleEvents.StatusForEvent(TriageCycleEvents.Failed).Should().Be("error");
        TriageCycleEvents.StatusForEvent(TriageCycleEvents.Skipped).Should().Be("warning");
        TriageCycleEvents.StatusForEvent(TriageCycleEvents.Completed).Should().Be("success");
        TriageCycleEvents.StatusForEvent(TriageCycleEvents.Started).Should().Be("success");
    }

    [Test]
    public void LabelsInvalid_FollowsConvention_AndIsWarning()
    {
        // MINOR (#7) — dropped out-of-vocab labels are recorded as a loud (warning) audit
        // row rather than silently discarded; non-terminal (apply still proceeds).
        TriageCycleEvents.LabelsInvalid.Should().Be("TRIAGE.LABELS.INVALID");
        TriageCycleEvents.StatusForEvent(TriageCycleEvents.LabelsInvalid).Should().Be("warning");
    }

    [Test]
    public void ParseTenantId_ValidGuid_Parses_InvalidOrEmpty_IsNull()
    {
        var g = Guid.NewGuid();
        TriageCycleEvents.ParseTenantId(g.ToString()).Should().Be(g);
        TriageCycleEvents.ParseTenantId("").Should().BeNull();
        TriageCycleEvents.ParseTenantId(null).Should().BeNull();
        TriageCycleEvents.ParseTenantId("nope").Should().BeNull();
    }

    [Test]
    public void JsonConstructor_AndLoggerConstructor_DoNotThrow()
    {
        FluentActions.Invoking(() => new EmitTriageCycleEventActivity()).Should().NotThrow();
    }

    // ================================================================
    // BuildTammaEvent — tags + data + status mapping
    // ================================================================

    [Test]
    public void BuildTammaEvent_Completed_HasSuccessStatus_ClassificationTags_AndIssueTag()
    {
        var evt = EmitTriageCycleEventActivity.BuildTammaEvent(
            TriageCycleEvents.Completed,
            repository: "owner/repo",
            itemKey: "owner/repo#7",
            itemNumber: 7,
            tenantId: null,
            itemSource: "issue",
            itemType: "bug",
            priority: "high",
            automation: "tamma-auto",
            decisionStatus: "ok",
            reason: null);

        evt.EventType.Should().Be("TRIAGE.ISSUE.COMPLETED");
        evt.Status.Should().Be("success");
        evt.Error.Should().BeNull();

        evt.Tags!["repository"].Should().Be("owner/repo");
        evt.Tags!["itemKey"].Should().Be("owner/repo#7");
        evt.Tags!["issueId"].Should().Be("7");
        evt.Tags!["itemSource"].Should().Be("issue");
        evt.Tags!["type"].Should().Be("bug");
        evt.Tags!["priority"].Should().Be("high");
        evt.Tags!["automation"].Should().Be("tamma-auto");
        evt.Tags.Should().NotContainKey("tenantId");

        evt.Data["type"].Should().Be("bug");
        evt.Data["priority"].Should().Be("high");
        evt.Data["decisionStatus"].Should().Be("ok");
    }

    [Test]
    public void BuildTammaEvent_Failed_HasErrorStatus_AndCarriesReasonAsError()
    {
        var evt = EmitTriageCycleEventActivity.BuildTammaEvent(
            TriageCycleEvents.Failed,
            repository: "owner/repo",
            itemKey: "owner/repo#9",
            itemNumber: 9,
            tenantId: null,
            itemSource: "issue",
            itemType: null, priority: null, automation: null,
            decisionStatus: "llm-failed",
            reason: "decisionUnusable:llm-failed");

        evt.Status.Should().Be("error");
        evt.Error.Should().Be("decisionUnusable:llm-failed");
        // No classification tags on a failed cycle (none decided).
        evt.Tags.Should().NotContainKey("type");
        evt.Data["decisionStatus"].Should().Be("llm-failed");
    }

    [Test]
    public void BuildTammaEvent_Skipped_HasWarningStatus_NoErrorField()
    {
        var evt = EmitTriageCycleEventActivity.BuildTammaEvent(
            TriageCycleEvents.Skipped,
            "owner/repo", "owner/repo#3", 3, null, "issue",
            null, null, null, "", "panel-failed");

        evt.Status.Should().Be("warning");
        // reason is carried as the error field ONLY on FAILED (loud), never on SKIPPED.
        evt.Error.Should().BeNull();
    }

    [Test]
    public void BuildTammaEvent_WithTenant_StampsTenantTag()
    {
        var tenant = Guid.NewGuid();
        var evt = EmitTriageCycleEventActivity.BuildTammaEvent(
            TriageCycleEvents.Started, "owner/repo", "owner/repo#1", 1, tenant, "issue",
            null, null, null, null, null);

        evt.Tags!["tenantId"].Should().Be(tenant.ToString("D"));
    }

    [Test]
    public void BuildTammaEvent_AlertNoNumber_HasItemKeyButNoIssueTag()
    {
        var evt = EmitTriageCycleEventActivity.BuildTammaEvent(
            TriageCycleEvents.Started, "owner/repo", "owner/repo:dependabot:CVE-1", 0, null, "dependabot",
            null, null, null, null, null);

        evt.Tags!["itemKey"].Should().Be("owner/repo:dependabot:CVE-1");
        evt.Tags!["itemSource"].Should().Be("dependabot");
        evt.Tags.Should().NotContainKey("issueId");
        evt.Tags.Should().NotContainKey("itemId");
    }
}
