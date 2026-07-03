using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.Activities.LlmCall.Models;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// Epic 38 follow-up #21 — <see cref="CreateReleaseActivity"/> is a thin
/// <see cref="Tamma.Activities.LlmCall.TammaApiClient"/> client (the mediated
/// release step). These cover the wire-response → outcome mapping, the fail-closed
/// null-response path, the RELEASE.CREATED.SUCCESS/FAILED event build, and the
/// cutover proof: the engine activity injects NO credential-holding vendor service
/// (so <c>TAMMA001</c> stays satisfied).
/// </summary>
[TestFixture]
public class CreateReleaseActivityTests
{
    // ===================================================================
    // MapResponse (wire → outcome)
    // ===================================================================

    [Test]
    public void Map_Created_ProjectsReleaseOutputs()
    {
        var outcome = CreateReleaseActivity.MapResponse(new GitCallResponse
        {
            Success = true, Outcome = "Created",
            ReleaseId = 55, ReleaseUrl = "https://gh/releases/55", ReleaseTag = "deploy-abc1234",
        });

        outcome.Outcome.Should().Be("Created");
        outcome.ReleaseId.Should().Be(55);
        outcome.ReleaseUrl.Should().Be("https://gh/releases/55");
        outcome.ReleaseTag.Should().Be("deploy-abc1234");
    }

    [Test]
    public void Map_Failure_ProjectsErrorCode_AndReason()
    {
        var outcome = CreateReleaseActivity.MapResponse(new GitCallResponse
        {
            Success = false, Outcome = "Error", FailureCode = "PLATFORM_ERROR", FailureReason = "422: tag exists",
        });

        outcome.Outcome.Should().Be("Error");
        outcome.ErrorCode.Should().Be("PLATFORM_ERROR");
        outcome.FailureReason.Should().Be("422: tag exists");
    }

    [Test]
    public void Map_NullResponse_FailsClosed()
    {
        var outcome = CreateReleaseActivity.MapResponse(null);
        outcome.Outcome.Should().Be("Error");
        outcome.ErrorCode.Should().Be("git-mediation-unavailable");
    }

    [Test]
    public void Map_SuccessButNotCreatedOutcome_FailsClosed()
    {
        // A success envelope with an unexpected outcome must NOT be treated as a
        // created release (no fabricated success).
        var outcome = CreateReleaseActivity.MapResponse(new GitCallResponse { Success = true, Outcome = "Done" });
        outcome.Outcome.Should().Be("Error");
    }

    // ===================================================================
    // BuildReleaseEvent (DCB audit event)
    // ===================================================================

    [Test]
    public void BuildReleaseEvent_Success_Emits_ReleaseCreatedSuccess_WithTagsAndData()
    {
        var evt = CreateReleaseActivity.BuildReleaseEvent(
            success: true, issueNumber: 7, repository: "acme/widgets", tag: "deploy-abc1234",
            releaseUrl: "https://gh/releases/55", releaseId: 55, tenantId: "t-1", error: null);

        evt.EventType.Should().Be(DeployEvents.ReleaseCreatedSuccess);
        evt.Status.Should().Be("success");
        evt.Error.Should().BeNull();
        evt.Tags.Should().ContainKey("repository").WhoseValue.Should().Be("acme/widgets");
        evt.Tags.Should().ContainKey("tag").WhoseValue.Should().Be("deploy-abc1234");
        evt.Tags.Should().ContainKey("issueNumber").WhoseValue.Should().Be("7");
        evt.Tags.Should().ContainKey("tenantId").WhoseValue.Should().Be("t-1");
        evt.Data.Should().ContainKey("releaseUrl").WhoseValue.Should().Be("https://gh/releases/55");
        evt.Data.Should().ContainKey("releaseId").WhoseValue.Should().Be(55L);
    }

    [Test]
    public void BuildReleaseEvent_Failure_Emits_ReleaseCreatedFailed_LoudWithReason()
    {
        var evt = CreateReleaseActivity.BuildReleaseEvent(
            success: false, issueNumber: 7, repository: "acme/widgets", tag: "deploy-abc1234",
            releaseUrl: null, releaseId: null, tenantId: null, error: "422: tag exists");

        evt.EventType.Should().Be(DeployEvents.ReleaseCreatedFailed);
        evt.Status.Should().Be("error");
        evt.Error.Should().Be("422: tag exists");
        evt.Data.Should().ContainKey("reason").WhoseValue.Should().Be("422: tag exists");
        DeployEvents.IsFailureType(evt.EventType).Should().BeTrue(
            "a failed release is a loud (error-status) audit row, never a silent success");
    }

    // ===================================================================
    // Cutover proof (TAMMA001) — no credential-holding vendor injection
    // ===================================================================

    [Test]
    public void CreateReleaseActivity_InjectsNoCredentialHoldingVendorService()
    {
        var type = typeof(CreateReleaseActivity);

        foreach (var ctor in type.GetConstructors())
        {
            ctor.GetParameters()
                .Any(p => typeof(IGitHubIntegrationService).IsAssignableFrom(p.ParameterType)
                          || typeof(IIntegrationService).IsAssignableFrom(p.ParameterType))
                .Should().BeFalse("CreateReleaseActivity must not inject a credential-holding integration service");
        }

        type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Any(f => typeof(IGitHubIntegrationService).IsAssignableFrom(f.FieldType)
                      || typeof(IIntegrationService).IsAssignableFrom(f.FieldType))
            .Should().BeFalse("CreateReleaseActivity must hold no credential-holding integration-service field");
    }
}
