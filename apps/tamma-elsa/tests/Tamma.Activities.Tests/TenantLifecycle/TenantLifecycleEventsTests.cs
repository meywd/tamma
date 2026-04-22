using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.TenantLifecycle;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Story 28-5 — asserts the platform-event builder produces the
/// step-dedup-friendly tag shape and is free of PII.
/// </summary>
[TestFixture]
public class TenantLifecycleEventsTests
{
    private static readonly Guid Tenant =
        new("11111111-2222-3333-4444-555555555555");

    [Test]
    public void BuildEvent_PopulatesType_TenantId_AndTags()
    {
        var evt = TenantLifecycleEvents.BuildEvent(
            TenantLifecycleEvents.ProvisionStepCompleted,
            Tenant,
            step: "create-role",
            attempt: 2);

        evt.Type.Should().Be("TENANT.PROVISION.STEP_COMPLETED");
        evt.TenantId.Should().Be(Tenant);

        using var tags = JsonDocument.Parse(evt.Tags);
        tags.RootElement.GetProperty("tenantId").GetString().Should().Be(Tenant.ToString("D"));
        tags.RootElement.GetProperty("step").GetString().Should().Be("create-role");
        tags.RootElement.GetProperty("attempt").GetString().Should().Be("2");
    }

    [Test]
    public void BuildEvent_OmitsNullStepAndAttempt()
    {
        var evt = TenantLifecycleEvents.BuildEvent(
            TenantLifecycleEvents.CreatedSuccess,
            Tenant);

        using var tags = JsonDocument.Parse(evt.Tags);
        tags.RootElement.TryGetProperty("step", out _).Should().BeFalse();
        tags.RootElement.TryGetProperty("attempt", out _).Should().BeFalse();
    }

    [Test]
    public void BuildEvent_MetadataMarksEventSourceSystem()
    {
        var evt = TenantLifecycleEvents.BuildEvent(
            TenantLifecycleEvents.DeletedSuccess,
            Tenant);

        using var meta = JsonDocument.Parse(evt.Metadata);
        meta.RootElement.GetProperty("eventSource").GetString().Should().Be("system");
    }

    [Test]
    public void BuildEvent_DataIsEmptyJsonObjectByDefault()
    {
        var evt = TenantLifecycleEvents.BuildEvent(
            TenantLifecycleEvents.DeleteRequested,
            Tenant);
        evt.Data.Should().Be("{}");
    }

    [Test]
    public void BuildEvent_RejectsBlankType()
    {
        var act = () => TenantLifecycleEvents.BuildEvent("", Tenant);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void BuildEvent_TagsContainNoEmail()
    {
        // Doc 03 §2.3 / T14: payload must carry no PII. The builder accepts
        // arbitrary tag dictionaries — this guard makes sure the workflow's
        // standard tag set never leaks an email even by accident.
        var evt = TenantLifecycleEvents.BuildEvent(
            TenantLifecycleEvents.ProvisionStepStarted,
            Tenant,
            step: "create-role",
            attempt: 1,
            userId: Guid.NewGuid());

        evt.Tags.Should().NotContain("@");
        evt.Tags.Should().NotContain(".com");
    }
}
