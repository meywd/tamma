using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// Completeness audit 2026-06-22 (<c>TriageContextGathering.md</c>) — coverage for
/// the TRIAGE.CONTEXT.* DCB event mapping
/// (<see cref="EmitTriageContextEventActivity.BuildTammaEvent"/>), the
/// <see cref="TriageContextEvents"/> status convention, and the
/// <see cref="TriageContextHelper"/> fail-closed extraction + item-type detection.
///
/// <para>The core completeness gap was a P0 <b>no-false-success</b> defect: a failed
/// LLM scan was silently coalesced to <c>"{}"</c> and reported "successful". These
/// tests pin the now-explicit contract: a failed scan → <c>failed</c> status / error
/// audit row; an empty scan → <c>empty</c> / warning; only a usable scan → <c>ok</c>
/// / success. They also pin the variable-contract-driving item-type detection.</para>
/// </summary>
[TestFixture]
public class TriageContextEventTests
{
    // ================================================================
    // TriageContextEvents — status convention (no false success)
    // ================================================================

    [Test]
    public void StatusForEvent_FailedIsError_EmptyIsWarning_CompletedIsSuccess()
    {
        TriageContextEvents.StatusForEvent(TriageContextEvents.Failed).Should().Be("error");
        TriageContextEvents.StatusForEvent(TriageContextEvents.Empty).Should().Be("warning");
        TriageContextEvents.StatusForEvent(TriageContextEvents.Completed).Should().Be("success");
        TriageContextEvents.StatusForEvent(TriageContextEvents.Started).Should().Be("success");
    }

    [Test]
    public void EventTypes_FollowAggregateActionStatusConvention()
    {
        TriageContextEvents.Started.Should().Be("TRIAGE.CONTEXT.STARTED");
        TriageContextEvents.Completed.Should().Be("TRIAGE.CONTEXT.COMPLETED");
        TriageContextEvents.Empty.Should().Be("TRIAGE.CONTEXT.EMPTY");
        TriageContextEvents.Failed.Should().Be("TRIAGE.CONTEXT.FAILED");
    }

    [Test]
    public void EventTypeForStatus_MapsStatusToTerminalEvent()
    {
        TriageContextEvents.EventTypeForStatus(TriageContextEvents.StatusFailed)
            .Should().Be(TriageContextEvents.Failed);
        TriageContextEvents.EventTypeForStatus(TriageContextEvents.StatusEmpty)
            .Should().Be(TriageContextEvents.Empty);
        TriageContextEvents.EventTypeForStatus(TriageContextEvents.StatusOk)
            .Should().Be(TriageContextEvents.Completed);
        // Anything unexpected defaults to COMPLETED (ok) — never silently FAILED.
        TriageContextEvents.EventTypeForStatus("whatever")
            .Should().Be(TriageContextEvents.Completed);
    }

    [Test]
    public void ParseTenantId_ValidGuid_Parses_InvalidOrEmpty_IsNull()
    {
        var g = Guid.NewGuid();
        TriageContextEvents.ParseTenantId(g.ToString()).Should().Be(g);
        TriageContextEvents.ParseTenantId("").Should().BeNull();
        TriageContextEvents.ParseTenantId(null).Should().BeNull();
        TriageContextEvents.ParseTenantId("not-a-guid").Should().BeNull();
    }

    // ================================================================
    // BuildTammaEvent — tags + data + status mapping
    // ================================================================

    [Test]
    public void BuildTammaEvent_Completed_HasSuccessStatus_AndHealthPayload()
    {
        var evt = EmitTriageContextEventActivity.BuildTammaEvent(
            TriageContextEvents.Completed,
            repository: "owner/repo",
            itemNumber: 7,
            tenantId: null,
            itemType: "issue",
            contextStatus: "ok",
            contextJsonLength: 128);

        evt.EventType.Should().Be("TRIAGE.CONTEXT.COMPLETED");
        evt.Status.Should().Be("success");

        evt.Tags!["repository"].Should().Be("owner/repo");
        evt.Tags!["itemId"].Should().Be("7");
        evt.Tags!["itemNumber"].Should().Be("7");
        evt.Tags!["itemSource"].Should().Be("issue");
        evt.Tags!["contextStatus"].Should().Be("ok");
        evt.Tags.Should().NotContainKey("tenantId", "single-user / platform-scope event");

        evt.Data["itemType"].Should().Be("issue");
        evt.Data["contextStatus"].Should().Be("ok");
        evt.Data["contextJsonLength"].Should().Be(128);
    }

    [Test]
    public void BuildTammaEvent_Failed_HasErrorStatus()
    {
        // The core guarantee: a failed scan is a LOUD (error) audit row — never a
        // silent false success.
        var evt = EmitTriageContextEventActivity.BuildTammaEvent(
            TriageContextEvents.Failed,
            repository: "owner/repo",
            itemNumber: 99,
            tenantId: null,
            itemType: "security",
            contextStatus: "failed",
            contextJsonLength: 0);

        evt.EventType.Should().Be("TRIAGE.CONTEXT.FAILED");
        evt.Status.Should().Be("error");
        evt.Tags!["contextStatus"].Should().Be("failed");
        evt.Data["contextStatus"].Should().Be("failed");
        evt.Data["contextJsonLength"].Should().Be(0);
    }

    [Test]
    public void BuildTammaEvent_Empty_HasWarningStatus()
    {
        var evt = EmitTriageContextEventActivity.BuildTammaEvent(
            TriageContextEvents.Empty,
            repository: "owner/repo",
            itemNumber: 3,
            tenantId: null,
            itemType: "issue",
            contextStatus: "empty",
            contextJsonLength: 2);

        evt.Status.Should().Be("warning");
        evt.Tags!["contextStatus"].Should().Be("empty");
    }

    [Test]
    public void BuildTammaEvent_WithTenant_StampsTenantTag()
    {
        var tenant = Guid.NewGuid();
        var evt = EmitTriageContextEventActivity.BuildTammaEvent(
            TriageContextEvents.Started, "owner/repo", 1, tenant, "issue", null, 0);

        evt.Tags!["tenantId"].Should().Be(tenant.ToString("D"));
    }

    [Test]
    public void BuildTammaEvent_ZeroItemNumber_OmitsItemTags()
    {
        var evt = EmitTriageContextEventActivity.BuildTammaEvent(
            TriageContextEvents.Started, "owner/repo", 0, null, "issue", null, 0);

        evt.Tags!.Should().NotContainKey("itemId");
        evt.Tags!.Should().NotContainKey("itemNumber");
        evt.Tags!["repository"].Should().Be("owner/repo");
    }

    // ================================================================
    // TriageContextHelper.DetectItemType — parse-based, whitespace-robust (§5 #5)
    // ================================================================

    [Test]
    public void DetectItemType_SecurityAdvisory_DetectedRegardlessOfWhitespace()
    {
        // Pretty-printed JSON broke the prior substring sniff ("type":"security").
        const string pretty = """
        {
            "number": 12,
            "type": "security",
            "title": "CVE in lodash"
        }
        """;
        TriageContextHelper.DetectItemType(pretty).Should().Be("security");
    }

    [Test]
    public void DetectItemType_AdvisoryMarker_WithoutTypeField_IsSecurity()
    {
        const string json = """{ "number": 1, "advisory": { "ghsaId": "GHSA-xxxx" } }""";
        TriageContextHelper.DetectItemType(json).Should().Be("security");
    }

    [Test]
    public void DetectItemType_DependabotType_IsDependency()
    {
        const string json = """{ "number": 5, "type": "dependabot", "dependency": "lodash" }""";
        TriageContextHelper.DetectItemType(json).Should().Be("dependency");
    }

    [Test]
    public void DetectItemType_PlainIssue_IsIssue()
    {
        const string json = """{ "number": 3, "type": "bug", "title": "broken" }""";
        TriageContextHelper.DetectItemType(json).Should().Be("issue");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not json at all")]
    public void DetectItemType_NullOrEmptyOrMalformed_DefaultsToIssue_NeverThrows(string? json)
    {
        TriageContextHelper.DetectItemType(json).Should().Be("issue");
    }

    [Test]
    public void DetectItemType_MalformedButSecurityMarkers_TolerantSniff()
    {
        // Not parseable JSON, but carries the security marker → tolerant fallback.
        const string broken = """{ "type":"security" ... truncated""";
        TriageContextHelper.DetectItemType(broken).Should().Be("security");
    }

    // ================================================================
    // TriageContextHelper.ExtractContext — fail-closed (the P0 regression)
    // ================================================================

    [Test]
    public void ExtractContext_NullResult_IsFailed_NotEmptyAsSuccess()
    {
        var (json, status) = TriageContextHelper.ExtractContext(null);
        status.Should().Be(TriageContextEvents.StatusFailed);
        json.Should().Be("{}");
    }

    [Test]
    public void ExtractContext_SuccessFalse_IsFailed()
    {
        // The all-providers-failed shape: success=false. Must NOT be presented as
        // gathered context.
        var result = new Dictionary<string, object>
        {
            ["success"] = false,
            ["llmResponse"] = "partial garbage",
        };
        var (json, status) = TriageContextHelper.ExtractContext(result);
        status.Should().Be(TriageContextEvents.StatusFailed);
        json.Should().Be("{}");
    }

    [Test]
    public void ExtractContext_NoLlmResponse_IsFailed()
    {
        var result = new Dictionary<string, object> { ["success"] = true };
        var (json, status) = TriageContextHelper.ExtractContext(result);
        status.Should().Be(TriageContextEvents.StatusFailed);
        json.Should().Be("{}");
    }

    [Test]
    public void ExtractContext_BlankResponse_IsEmpty_NotOk()
    {
        var result = new Dictionary<string, object>
        {
            ["success"] = true,
            ["llmResponse"] = "   ",
        };
        var (json, status) = TriageContextHelper.ExtractContext(result);
        status.Should().Be(TriageContextEvents.StatusEmpty);
    }

    [Test]
    public void ExtractContext_EmptyObject_IsEmpty_NotOk()
    {
        var result = new Dictionary<string, object>
        {
            ["success"] = true,
            ["llmResponse"] = "here it is: {}",
        };
        var (json, status) = TriageContextHelper.ExtractContext(result);
        status.Should().Be(TriageContextEvents.StatusEmpty);
        json.Should().Be("{}");
    }

    [Test]
    public void ExtractContext_StructuredObject_IsOk()
    {
        var result = new Dictionary<string, object>
        {
            ["success"] = true,
            ["llmResponse"] = """Findings: {"relevantFiles": [{"path": "a.cs"}]} done""",
        };
        var (json, status) = TriageContextHelper.ExtractContext(result);
        status.Should().Be(TriageContextEvents.StatusOk);
        json.Should().Contain("relevantFiles");
    }

    [Test]
    public void ExtractContext_FreeFormProse_IsOk_WrappedAsRawContext()
    {
        var result = new Dictionary<string, object>
        {
            ["success"] = true,
            ["llmResponse"] = "The affected module is widely used across the codebase.",
        };
        var (json, status) = TriageContextHelper.ExtractContext(result);
        status.Should().Be(TriageContextEvents.StatusOk);
        json.Should().Contain("rawContext");
    }

    [Test]
    public void ExtractContext_AbsentSuccessFlag_TreatedAsSuccess()
    {
        // Back-compat: a caller that doesn't surface "success" but returns a usable
        // response is treated as success (matching the panel's convention).
        var result = new Dictionary<string, object>
        {
            ["llmResponse"] = """{"dependencies": [{"name": "lodash"}]}""",
        };
        var (json, status) = TriageContextHelper.ExtractContext(result);
        status.Should().Be(TriageContextEvents.StatusOk);
        json.Should().Contain("dependencies");
    }
}
