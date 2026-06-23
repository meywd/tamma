using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// Completeness audit 2026-06-22 — coverage for the TRIAGE.PANEL.* DCB event
/// mapping (<see cref="EmitTriageEventActivity.BuildTammaEvent"/>) and the
/// <see cref="TriageEvents"/> status convention. A degraded/failed panel must map
/// to a loud (warning/error) status, never a false "success" audit row, and the
/// roster-health payload (roleCount/succeededCount/failedRoles) must be carried so
/// the audit trail can see a degraded panel.
/// </summary>
[TestFixture]
public class TriagePanelEventTests
{
    // ================================================================
    // TriageEvents — status convention (no false success)
    // ================================================================

    [Test]
    public void StatusForEvent_FailedIsError_PartialIsWarning_CompletedIsSuccess()
    {
        TriageEvents.StatusForEvent(TriageEvents.PanelFailed).Should().Be("error");
        TriageEvents.StatusForEvent(TriageEvents.PanelPartial).Should().Be("warning");
        TriageEvents.StatusForEvent(TriageEvents.PanelCompleted).Should().Be("success");
        TriageEvents.StatusForEvent(TriageEvents.PanelStarted).Should().Be("success");
    }

    [Test]
    public void EventTypes_FollowAggregateActionStatusConvention()
    {
        TriageEvents.PanelStarted.Should().Be("TRIAGE.PANEL.STARTED");
        TriageEvents.PanelCompleted.Should().Be("TRIAGE.PANEL.COMPLETED");
        TriageEvents.PanelPartial.Should().Be("TRIAGE.PANEL.PARTIAL");
        TriageEvents.PanelFailed.Should().Be("TRIAGE.PANEL.FAILED");
    }

    [Test]
    public void ParseTenantId_ValidGuid_Parses_InvalidOrEmpty_IsNull()
    {
        var g = Guid.NewGuid();
        TriageEvents.ParseTenantId(g.ToString()).Should().Be(g);
        TriageEvents.ParseTenantId("").Should().BeNull();
        TriageEvents.ParseTenantId(null).Should().BeNull();
        TriageEvents.ParseTenantId("not-a-guid").Should().BeNull();
    }

    // ================================================================
    // BuildTammaEvent — tags + data + status mapping
    // ================================================================

    [Test]
    public void BuildTammaEvent_Completed_HasSuccessStatus_AndHealthPayload()
    {
        var evt = EmitTriageEventActivity.BuildTammaEvent(
            TriageEvents.PanelCompleted,
            repository: "owner/repo",
            itemNumber: 7,
            tenantId: null,
            roleCount: 4,
            succeededCount: 4,
            failedRolesJson: "[]");

        evt.EventType.Should().Be("TRIAGE.PANEL.COMPLETED");
        evt.Status.Should().Be("success");

        evt.Tags!["repository"].Should().Be("owner/repo");
        evt.Tags!["itemId"].Should().Be("7");
        evt.Tags!["itemNumber"].Should().Be("7");
        evt.Tags.Should().NotContainKey("tenantId", "single-user / platform-scope event");

        evt.Data["roleCount"].Should().Be(4);
        evt.Data["succeededCount"].Should().Be(4);
        evt.Data["failedRoles"].Should().BeOfType<List<string>>()
            .Which.Should().BeEmpty();
    }

    [Test]
    public void BuildTammaEvent_Failed_HasErrorStatus_AndFailedRoster()
    {
        // The core guarantee: a failed panel is a LOUD (error) audit row carrying
        // the failed roster — never a silent false success.
        var evt = EmitTriageEventActivity.BuildTammaEvent(
            TriageEvents.PanelFailed,
            repository: "owner/repo",
            itemNumber: 99,
            tenantId: null,
            roleCount: 4,
            succeededCount: 0,
            failedRolesJson: """["security","developer","devops","tester"]""");

        evt.EventType.Should().Be("TRIAGE.PANEL.FAILED");
        evt.Status.Should().Be("error");
        evt.Data["succeededCount"].Should().Be(0);
        evt.Data["failedRoles"].Should().BeOfType<List<string>>()
            .Which.Should().BeEquivalentTo("security", "developer", "devops", "tester");
    }

    [Test]
    public void BuildTammaEvent_Partial_HasWarningStatus()
    {
        var evt = EmitTriageEventActivity.BuildTammaEvent(
            TriageEvents.PanelPartial,
            repository: "owner/repo",
            itemNumber: 3,
            tenantId: null,
            roleCount: 4,
            succeededCount: 3,
            failedRolesJson: """["tester"]""");

        evt.Status.Should().Be("warning");
        evt.Data["failedRoles"].Should().BeOfType<List<string>>()
            .Which.Should().ContainSingle().Which.Should().Be("tester");
    }

    [Test]
    public void BuildTammaEvent_WithTenant_StampsTenantTag()
    {
        var tenant = Guid.NewGuid();
        var evt = EmitTriageEventActivity.BuildTammaEvent(
            TriageEvents.PanelStarted,
            repository: "owner/repo",
            itemNumber: 1,
            tenantId: tenant,
            roleCount: 4,
            succeededCount: 0,
            failedRolesJson: "[]");

        evt.Tags!["tenantId"].Should().Be(tenant.ToString("D"));
    }

    [Test]
    public void BuildTammaEvent_ZeroItemNumber_OmitsItemTags()
    {
        var evt = EmitTriageEventActivity.BuildTammaEvent(
            TriageEvents.PanelStarted, "owner/repo", 0, null, 4, 0, "[]");

        evt.Tags!.Should().NotContainKey("itemId");
        evt.Tags!.Should().NotContainKey("itemNumber");
        evt.Tags!["repository"].Should().Be("owner/repo");
    }

    // ================================================================
    // ParseFailedRoles — tolerant of malformed JSON (event still emits)
    // ================================================================

    [Test]
    public void ParseFailedRoles_ValidArray_Parses()
    {
        EmitTriageEventActivity.ParseFailedRoles("""["a","b"]""")
            .Should().BeEquivalentTo("a", "b");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not json")]
    [TestCase("{}")]
    public void ParseFailedRoles_NullOrMalformed_ReturnsEmpty_NeverThrows(string? json)
    {
        EmitTriageEventActivity.ParseFailedRoles(json).Should().BeEmpty();
    }
}
