using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Agents.Scripted;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Providers;

/// <summary>
/// 2026-08-13 (Epic 31 P5 follow-up) — pins that the scripted provider's
/// BUILT-IN cycle script satisfies every consumer the single-issue cycle
/// actually routes it through: the real registered document validators
/// (39-9 ring + the lifecycle's engine-side validation), the 39-7 reviewer
/// mapping (<see cref="ReviewProducerHelper.MapReviewerReply"/>), the task
/// review's verdict parse, the deployment pipeline's fail-closed status
/// parse, and cross-document coherence (test-spec task ids bind to plan task
/// ids). This fixture lives in Tamma.Activities.Tests because it needs BOTH
/// surfaces: the script (Tamma.Api) and the workflow helpers (Tamma.ElsaServer).
/// </summary>
[TestFixture]
public class ScriptedCycleScriptValidityTests
{
    // ── typed documents: the registry-example fallback is valid per type ──

    [TestCase("plan")]
    [TestCase("test-spec")]
    [TestCase("decomposition")]
    [TestCase("review")]
    [TestCase("findings")]
    [TestCase("ambiguity-assessment")]
    [TestCase("triage-decision")]
    [TestCase("clarification")]
    public void DocumentTypedResponse_PassesTheRealValidator(string typeKey)
    {
        var responder = new ScriptedLlmResponder();
        var response = responder.Respond(new ScriptedLlmCall(
            "scripted", "architect", "any-action", typeKey, "m", "corr"));

        response.Success.Should().BeTrue($"documentType '{typeKey}' must always be servable");

        using var doc = JsonDocument.Parse(response.ResponseText!);
        var verdict = DocumentTypeRegistry.Resolve(typeKey).Validate(doc.RootElement.Clone());
        verdict.IsValid.Should().BeTrue(
            $"the scripted '{typeKey}' payload must pass its own registered validator; got: " +
            string.Join("; ", verdict.Violations.Select(v => $"{v.Code}: {v.Message}")));
    }

    [Test]
    public void EveryRegisteredDocumentType_HasAServableScriptedPayload()
    {
        foreach (var type in DocumentTypeRegistry.All)
        {
            ScriptedCycleLibrary.DocumentExampleFor(type.Key).Should().NotBeNull(
                $"type '{type.Key}' must fall back to its registry example " +
                "(the drift suite guarantees ≥1 valid example per type)");
        }
    }

    // ── the review payload routes ACCEPT, not revise ──

    [Test]
    public void ScriptedReviewDefault_IsAnApprovingValidReview()
    {
        var responder = new ScriptedLlmResponder();
        var response = responder.Respond(new ScriptedLlmCall(
            "scripted", "architect", "plan-review", "review", "m", "corr"));

        using var doc = JsonDocument.Parse(response.ResponseText!);
        var verdict = DocumentTypeRegistry.Resolve("review").Validate(doc.RootElement.Clone());
        verdict.IsValid.Should().BeTrue(string.Join("; ", verdict.Violations.Select(v => v.Code)));

        doc.RootElement.GetProperty("decision").GetString().Should().Be("approve",
            "a request-changes default (the registry's first example) would loop the " +
            "lifecycle's revise rounds instead of reaching accept");
    }

    // ── reviewer cells: ONE reply shape serves BOTH consumers ──

    [Test]
    public void ApproveVerdict_MapsToAValidApprovingReview_ViaTheSingleReviewerMapper()
    {
        var subject = new ReviewSubject
        {
            Kind = "document",
            DocumentId = Guid.NewGuid(),
            DocumentType = "plan",
        };
        var map = ReviewProducerHelper.MapReviewerReply(
            ScriptedCycleLibrary.ApproveReviewVerdict, subject);

        map.Payload.Should().NotBeNull(
            "the 39-7 single-reviewer must map the scripted verdict onto a valid Review; violations: " +
            string.Join("; ", map.Violations.Select(v => $"{v.Code}: {v.Message}")));
        map.Payload!.Decision.Should().Be(ReviewDecision.Approve);
        map.Payload.Issues.Should().BeEmpty("no blocking issues — the lifecycle routes to accept");
    }

    [Test]
    public void ApproveVerdict_ParsesAsTaskReviewApproval()
    {
        // TaskReviewWorkflow reads the top-level "verdict" token directly and
        // requires exactly "approve" on all four roles to route Approved.
        using var doc = JsonDocument.Parse(ScriptedCycleLibrary.ApproveReviewVerdict);
        doc.RootElement.GetProperty("verdict").GetString().Should().Be("approve");
    }

    // ── cross-document coherence: test-spec task ids ⊆ plan task ids ──

    [Test]
    public void TestSpecTaskIds_BindToThePlanTasks()
    {
        var responder = new ScriptedLlmResponder();
        var planJson = responder.Respond(new ScriptedLlmCall(
            "scripted", "architect", "plan-system-design", "plan", "m", "c")).ResponseText!;
        var specJson = responder.Respond(new ScriptedLlmCall(
            "scripted", "tester", "write-tests", "test-spec", "m", "c")).ResponseText!;

        using var plan = JsonDocument.Parse(planJson);
        var taskIds = plan.RootElement.GetProperty("tasks").EnumerateArray()
            .Select(t => t.GetProperty("id").GetString())
            .ToHashSet();
        taskIds.Should().NotBeEmpty();

        using var spec = JsonDocument.Parse(specJson);
        foreach (var testCase in spec.RootElement.GetProperty("testCases").EnumerateArray())
        {
            taskIds.Should().Contain(testCase.GetProperty("taskId").GetString(),
                "TestSpecDocumentType.ValidateWithContext binds every case to a task id " +
                "from the consumed plan — the scripted plan and test-spec must stay coherent");
        }
    }

    // ── deployment pipeline: fail-closed status parse ──

    [Test]
    public void StageSuccess_CarriesTheExplicitSuccessStatus()
    {
        using var doc = JsonDocument.Parse(ScriptedCycleLibrary.StageSuccess);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success",
            "DeploymentPipelineWorkflow.ExtractStageResult only advances on an explicit success");
    }

    // ── PO summary: {summary, links} contract ──

    [Test]
    public void PoSummary_ParsesWithSummaryAndLinks()
    {
        using var doc = JsonDocument.Parse(ScriptedCycleLibrary.PoSummary);
        doc.RootElement.GetProperty("summary").GetString().Should().NotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("links").ValueKind.Should().Be(JsonValueKind.Array);
    }

    // ── the reviewer panel roster is fully scripted ──

    [Test]
    public void EveryReviewPanelRole_HasAScriptedReviewerCell()
    {
        var responder = new ScriptedLlmResponder();
        foreach (var role in Enum.GetValues<AgentRole>())
        {
            AgentAction action;
            try
            {
                action = RolePhaseMap.GetReviewActionForRole(role);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue; // not on a review panel
            }

            var response = responder.Respond(new ScriptedLlmCall(
                "scripted", role.ToWire(), action.ToWire(), null, "m", "c"));
            response.Success.Should().BeTrue(
                $"reviewer cell ({role.ToWire()}, {action.ToWire()}) must be scripted — " +
                "the 39-7 panel roster dispatches it");
            // 2026-08-13: panel reviewer cells serve the CANONICAL Review —
            // reviewer llm-calls declare documentType="review" and the 39-9
            // ring validates them against the Review registry validator,
            // which the legacy verdict shape does not satisfy.
            response.ResponseText.Should().Be(ScriptedCycleLibrary.CanonicalReviewApprove);
        }
    }

    [Test]
    public void CanonicalReviewApprove_PassesTheReviewRegistryValidator()
    {
        // The exact ring the reviewer replies now go through: the Review
        // document type's registry validator (documentType="review").
        var type = Tamma.Core.Documents.DocumentTypeRegistry.Resolve("review");
        using var doc = JsonDocument.Parse(ScriptedCycleLibrary.CanonicalReviewApprove);
        var result = type.Validate(doc.RootElement);
        result.IsValid.Should().BeTrue(
            "the scripted canonical approve must pass the Review validator, or every " +
            $"panel member exhausts the 39-9 repair ring (violations: " +
            $"[{string.Join("; ", result.Violations.Select(v => v.Message))}])");
    }

    // ── chain resolution: config-driven provider selection ──

    [Test]
    public void ChainHelper_Precedence_CallerThenDbThenConfigThenDefault()
    {
        var allowAll = new Tamma.Activities.Security.ProviderAllowlist(
            Microsoft.Extensions.Options.Options.Create(
                new Tamma.Activities.Security.ProviderAllowlistOptions
                {
                    AdditionalProviders = { "scripted" },
                }));

        LlmProviderChainHelper.Resolve(
                new[] { "openai" }, new[] { "anthropic" }, new[] { "scripted" }, allowAll)
            .Should().Equal("openai");
        LlmProviderChainHelper.Resolve(
                null, new[] { "anthropic" }, new[] { "scripted" }, allowAll)
            .Should().Equal("anthropic");
        // Llm:DefaultProviderChain is the deployment-tier selection knob the
        // engine-driven E2E uses.
        LlmProviderChainHelper.Resolve(null, null, new[] { "scripted" }, allowAll)
            .Should().Equal("scripted");
        LlmProviderChainHelper.Resolve(null, null, null, allowAll)
            .Should().Equal("anthropic", "openai", "openrouter");
    }

    [Test]
    public void ChainHelper_FiltersThroughTheDiAllowlist_AndFailsLoudOnAllRejected()
    {
        var defaults = new Tamma.Activities.Security.ProviderAllowlist();

        // "scripted" is filtered OUT by the default allowlist (opt-in only)…
        LlmProviderChainHelper.Resolve(
                new[] { "scripted", "anthropic" }, null, null, defaults)
            .Should().Equal("anthropic");

        // …and an all-rejected chain fails loud, naming the config key.
        var act = () => LlmProviderChainHelper.Resolve(
            new[] { "scripted" }, null, null, defaults);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Security:ProviderAllowlist:AdditionalProviders*");
    }
}
