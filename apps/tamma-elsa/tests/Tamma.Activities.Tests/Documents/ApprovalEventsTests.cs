using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Documents;

namespace Tamma.Activities.Tests.Documents;

/// <summary>
/// Story 39-8 (AC1 constants, AC6 mapping half). Pins the exact four event-type constant
/// strings + the <see cref="ApprovalEvents.StatusForEvent"/> mapping, and the
/// <see cref="EmitEscalationEventActivity.BuildTammaEvent"/> tag/data mapping — crucially that
/// the document lineage is embedded as a nested JSON OBJECT (never a bare string).
/// </summary>
[TestFixture]
public class ApprovalEventsTests
{
    [Test]
    public void Constant_strings_are_pinned()
    {
        ApprovalEvents.Requested.Should().Be("APPROVAL.REQUESTED");
        ApprovalEvents.Provided.Should().Be("APPROVAL.PROVIDED");
        ApprovalEvents.EscalationTriggered.Should().Be("ESCALATION.TRIGGERED");
        ApprovalEvents.EscalationResolved.Should().Be("ESCALATION.RESOLVED");
    }

    [Test]
    public void StatusForEvent_TriggeredIsErrorRow_RequestedIsStarted_RestSuccess()
    {
        ApprovalEvents.StatusForEvent(ApprovalEvents.EscalationTriggered).Should().Be("error",
            "the exception surface is a LOUD error row");
        ApprovalEvents.StatusForEvent(ApprovalEvents.Requested).Should().Be("started");
        ApprovalEvents.StatusForEvent(ApprovalEvents.Provided).Should().Be("success",
            "a decision (incl. a human reject) is a legitimate transition, not an error");
        ApprovalEvents.StatusForEvent(ApprovalEvents.EscalationResolved).Should().Be("success");
    }

    [Test]
    public void ParseTenantId_returns_null_for_empty_or_unparseable()
    {
        ApprovalEvents.ParseTenantId(null).Should().BeNull();
        ApprovalEvents.ParseTenantId("").Should().BeNull();
        ApprovalEvents.ParseTenantId("not-a-guid").Should().BeNull();
        var g = Guid.NewGuid();
        ApprovalEvents.ParseTenantId(g.ToString()).Should().Be(g);
    }

    [Test]
    public void BuildTammaEvent_Triggered_MapsTagsAndData_AndEmbedsLineageAsObject()
    {
        var tenant = Guid.NewGuid();
        var lineageJson = "{\"drafts\":[{\"id\":\"d1\",\"state\":\"draft\"}],\"roundsUsed\":2}";

        var evt = EmitEscalationEventActivity.BuildTammaEvent(
            ApprovalEvents.EscalationTriggered,
            escalationId: "esc-1",
            outcome: "rounds-exhausted",
            lineageJson: lineageJson,
            rulesReference: "system-default@3",
            channel: "orchestrator",
            issueId: "issue-9",
            documentId: "doc-7",
            documentType: "design",
            correlationId: "corr-5",
            sessionId: "sess-3",
            tenantId: tenant,
            detail: "rounds ran out");

        evt.EventType.Should().Be(ApprovalEvents.EscalationTriggered);
        evt.Status.Should().Be("error");

        evt.Tags!["issueId"].Should().Be("issue-9");
        evt.Tags!["documentId"].Should().Be("doc-7");
        evt.Tags!["documentType"].Should().Be("design");
        evt.Tags!["correlationId"].Should().Be("corr-5");
        evt.Tags!["escalationId"].Should().Be("esc-1");
        evt.Tags!["sessionId"].Should().Be("sess-3");
        evt.Tags!["tenantId"].Should().Be(tenant.ToString("D"));

        evt.Data["outcome"].Should().Be("rounds-exhausted");
        evt.Data["rulesReference"].Should().Be("system-default@3");
        evt.Data["channel"].Should().Be("orchestrator");
        evt.Data["detail"].Should().Be("rounds ran out");

        // AC6 — lineage is a nested JSON object, NOT a bare string.
        evt.Data["lineage"].Should().BeAssignableTo<JsonNode>();
        var node = (JsonNode)evt.Data["lineage"]!;
        node.GetValueKind().Should().Be(JsonValueKind.Object,
            "the lineage payload must be a JSON object so a handler can read every field");
        node["roundsUsed"]!.GetValue<int>().Should().Be(2);
    }

    [Test]
    public void BuildTammaEvent_MalformedLineage_IsWrappedNeverBareString()
    {
        var evt = EmitEscalationEventActivity.BuildTammaEvent(
            ApprovalEvents.EscalationTriggered, "esc-2", "acceptor-judgment",
            lineageJson: "not json", rulesReference: null, channel: null,
            issueId: null, documentId: null, documentType: null, correlationId: null,
            sessionId: null, tenantId: null, detail: null);

        evt.Data["lineage"].Should().BeAssignableTo<JsonNode>();
        ((JsonNode)evt.Data["lineage"]!).GetValueKind().Should().Be(JsonValueKind.Object,
            "even a malformed lineage must be wrapped in an object, never surfaced as a bare string");
    }

    [Test]
    public void BuildTammaEvent_Resolved_IsSuccessRow()
    {
        var evt = EmitEscalationEventActivity.BuildTammaEvent(
            ApprovalEvents.EscalationResolved, "esc-3", null,
            lineageJson: "{}", rulesReference: null, channel: "user",
            issueId: "i", documentId: "d", documentType: "t", correlationId: "c",
            sessionId: "s", tenantId: null, detail: null);

        evt.Status.Should().Be("success");
    }
}
