using System;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Documents;

namespace Tamma.Activities.Tests.Documents;

/// <summary>
/// Story 41-2 (D7), plan step 2 — the shared Epic-41 domain-event emitter's OWN unit test.
///
/// <para>Added 2026-07-29 in the conformance round. Plan step 2 said this activity "ships
/// with its own unit test (<c>Tamma.Activities.Tests/Documents/</c>)"; it did not — the only
/// executing coverage was three <c>StatusForEvent</c> assertions borrowed inside
/// <c>AcceptanceCriteriaAuthoringWorkflowStructureTests</c> and
/// <c>AdrAuthoringWorkflowStructureTests</c>, which pin the family constants of ONE binding
/// each, not the emitter's generic contract. One activity now serves the whole five-story
/// batch (41-2/41-3/41-4/41-5/41-6 and 41-9), so its suffix rule and its tag/data mapping
/// are the shared surface a regression would break everywhere at once.</para>
/// </summary>
[TestFixture]
public class EmitDomainLifecycleEventActivityTests
{
    // ── StatusForEvent — the suffix convention, family-agnostic ──────────

    [TestCase("ACCEPTANCE_CRITERIA.STARTED", "started")]
    [TestCase("ADR.STARTED", "started")]
    [TestCase("ACCEPTANCE_CRITERIA.DRAFTED", "success")]
    [TestCase("ACCEPTANCE_CRITERIA.ACCEPTED", "success")]
    [TestCase("ADR.SUPERSEDED", "success")]
    [TestCase("ACCEPTANCE_CRITERIA.FAILED", "error")]
    [TestCase("ADR.REJECTED", "error")]
    [TestCase("SOME_FUTURE_FAMILY.ESCALATED", "error")]
    public void StatusForEvent_DerivesTheStatusFromTheSuffix(string type, string expected)
        => EmitDomainLifecycleEventActivity.StatusForEvent(type).Should().Be(expected);

    [Test]
    public void StatusForEvent_TreatsAnAbsentTypeAsAnError_NeverAFalseSuccess()
    {
        EmitDomainLifecycleEventActivity.StatusForEvent(null).Should().Be("error");
        EmitDomainLifecycleEventActivity.StatusForEvent("").Should().Be("error");
        EmitDomainLifecycleEventActivity.StatusForEvent("   ").Should().Be("error");
    }

    [Test]
    public void StatusForEvent_IsCaseAndPositionSensitive_SoASuffixMustReallyBeASuffix()
    {
        // A family whose NAME contains 'FAILED' mid-string is not a failure row, and the
        // convention is Ordinal — a lowercase suffix is not the wire form.
        EmitDomainLifecycleEventActivity.StatusForEvent("FAILED_LOGIN.ACCEPTED").Should().Be("success");
        EmitDomainLifecycleEventActivity.StatusForEvent("ACCEPTANCE_CRITERIA.failed").Should().Be("success");
    }

    // ── ParseTenantId — empty / single-user / garbage ⇒ platform scope ───

    [Test]
    public void ParseTenantId_YieldsNullForEveryNonGuidForm()
    {
        EmitDomainLifecycleEventActivity.ParseTenantId(null).Should().BeNull();
        EmitDomainLifecycleEventActivity.ParseTenantId("").Should().BeNull();
        EmitDomainLifecycleEventActivity.ParseTenantId("single-user").Should().BeNull();

        var tenant = Guid.NewGuid();
        EmitDomainLifecycleEventActivity.ParseTenantId(tenant.ToString()).Should().Be(tenant);
    }

    // ── BuildTammaEvent — the tag/data mapping ───────────────────────────

    [Test]
    public void BuildTammaEvent_CarriesEveryQueryableTag_AndTheTypedStatus()
    {
        var tenant = Guid.NewGuid();

        var evt = EmitDomainLifecycleEventActivity.BuildTammaEvent(
            "ACCEPTANCE_CRITERIA.DRAFTED", "issue-1", "meywd/tamma", "corr-1", tenant,
            "doc-1", "4 criteria drafted", "{\"clarification\":\"c-1\"}");

        evt.EventType.Should().Be("ACCEPTANCE_CRITERIA.DRAFTED");
        evt.Status.Should().Be("success");
        evt.Tags.Should().Contain("issueId", "issue-1");
        evt.Tags.Should().Contain("repository", "meywd/tamma");
        evt.Tags.Should().Contain("correlationId", "corr-1");
        evt.Tags.Should().Contain("documentId", "doc-1");
        evt.Tags["tenantId"].Should().Be(tenant.ToString("D"));
        evt.Data.Should().Contain("detail", "4 criteria drafted");
        evt.Data.Should().Contain("payload", "{\"clarification\":\"c-1\"}");
    }

    [Test]
    public void BuildTammaEvent_OmitsEmptyTagsAndData_RatherThanWritingBlanks()
    {
        // The STARTED emission has no document id yet and usually no structured payload;
        // a blank tag would pollute the DCB index it is meant to key.
        var evt = EmitDomainLifecycleEventActivity.BuildTammaEvent(
            "ACCEPTANCE_CRITERIA.STARTED", "issue-1", "", null, null, "", null, "   ");

        evt.Status.Should().Be("started");
        evt.Tags.Should().ContainKey("issueId");
        evt.Tags.Keys.Should().NotContain(new[] { "repository", "correlationId", "documentId", "tenantId" });
        evt.Data.Should().BeEmpty();
    }

    [Test]
    public void BuildTammaEvent_KeepsAFailureLoud_EvenWithAnEmptyDetail()
    {
        var evt = EmitDomainLifecycleEventActivity.BuildTammaEvent(
            "ACCEPTANCE_CRITERIA.FAILED", "issue-1", null, null, null, null, "", null);

        evt.Status.Should().Be("error",
            "a degraded exit is an error row whether or not the binding managed to describe it");
    }
}
