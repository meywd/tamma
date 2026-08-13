using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Tamma.Api.Dtos.Engine;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Ci;
using Tamma.Data;

namespace Tamma.Api.Tests.Engine;

/// <summary>
/// Epic 31 P3 (seam 4) — CONTRACT pins for <c>POST /api/engine/trigger-ci</c>,
/// written BEFORE the handler was rerouted off <c>IGitHubEngineCallbackService</c>
/// and onto the governed CI-mediation core. These pin the response SHAPES the
/// deployed Elsa activities consume, which must survive the reroute unchanged:
///
/// <list type="bullet">
///   <item>success ⇒ 200 <c>{dispatched, workflowFile, branch}</c>;</item>
///   <item>no usable CI credential ⇒ <b>503</b> with
///     <c>error = "github_client_not_configured"</c> (+ a <c>detail</c> string) —
///     the legacy "GitHub App client not wired" contract, now meaning "no
///     platform driver resolved";</item>
///   <item>platform failure ⇒ 502 <c>{error}</c>;</item>
///   <item>missing repository / branchName / workflowFile ⇒ 400
///     <c>{error}</c> (validation precedes any mediation call).</item>
/// </list>
/// </summary>
[TestFixture]
public class EngineTriggerCiContractTests
{
    private Mock<ICiMediationService> _ci = null!;
    private StubTenantContext _tenant = null!;

    private sealed class StubTenantContext : ITenantContext
    {
        public Guid? TenantId { get; private set; } = Guid.NewGuid();
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    [SetUp]
    public void SetUp()
    {
        _ci = new Mock<ICiMediationService>(MockBehavior.Strict);
        _tenant = new StubTenantContext();
    }

    private static DefaultHttpContext Context() => new()
    {
        RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider(),
        Response = { Body = new MemoryStream() },
    };

    private static async Task<(int Status, JsonElement Body)> Exec(IResult result)
    {
        var ctx = Context();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var raw = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var body = string.IsNullOrWhiteSpace(raw)
            ? default
            : JsonDocument.Parse(raw).RootElement.Clone();
        return (ctx.Response.StatusCode, body);
    }

    private Task<IResult> Call(TriggerCiRequest req) =>
        EngineEndpoints.TriggerCi(req, _ci.Object, _tenant, CancellationToken.None);

    private void MediationReturns(CiMediationResult result) => _ci
        .Setup(c => c.TriggerTestsAsync(
            It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<TriggerTestsRequest>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(result);

    // ================================================================
    // Success shape — {dispatched, workflowFile, branch}
    // ================================================================

    [Test]
    public async Task Success_Returns200_WithDispatchedWorkflowFileBranch()
    {
        MediationReturns(new CiMediationResult
        {
            Success = true,
            Outcome = "Triggered",
            TestRun = new CiTestRunDto { RunId = "42", Status = "queued" },
        });

        var (status, body) = await Exec(await Call(
            new TriggerCiRequest("acme/widgets", "feature", "agent.yml", null)));

        status.Should().Be(200);
        body.GetProperty("dispatched").GetBoolean().Should().BeTrue();
        body.GetProperty("workflowFile").GetString().Should().Be("agent.yml");
        body.GetProperty("branch").GetString().Should().Be("feature");
    }

    [Test]
    public async Task Success_DelegatesIntoTheMediationCore_WithTenantRepoWorkflowAndInputs()
    {
        TriggerTestsRequest? seen = null;
        Guid? seenTenant = null;
        string? seenRepo = null;
        _ci.Setup(c => c.TriggerTestsAsync(
                It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<TriggerTestsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid?, string, TriggerTestsRequest, CancellationToken>((t, r, b, _) =>
            {
                seenTenant = t; seenRepo = r; seen = b;
            })
            .ReturnsAsync(new CiMediationResult { Success = true });

        await Call(new TriggerCiRequest(
            "acme/widgets", "feature", "agent.yml",
            new Dictionary<string, string> { ["issue"] = "7" }));

        seenTenant.Should().Be(_tenant.TenantId, "the acting tenant is the auth-derived ITenantContext");
        seenRepo.Should().Be("acme/widgets");
        seen!.Branch.Should().Be("feature");
        seen.WorkflowFile.Should().Be("agent.yml");
        seen.Inputs.Should().ContainKey("issue");
        seen.CorrelationId.Should().NotBeNullOrWhiteSpace();
    }

    // ================================================================
    // The 503 github_client_not_configured contract
    // ================================================================

    [Test]
    public async Task NoResolvableCredential_Returns503_GithubClientNotConfiguredEnvelope()
    {
        MediationReturns(new CiMediationResult
        {
            Success = false,
            Outcome = "Error",
            FailureCode = CiFailureCodes.TokenUnavailable,
        });

        var (status, body) = await Exec(await Call(
            new TriggerCiRequest("acme/widgets", "feature", "agent.yml", null)));

        status.Should().Be(503);
        body.GetProperty("error").GetString().Should().Be("github_client_not_configured",
            "the deployed activities branch on this exact legacy error code");
        body.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
    }

    // ================================================================
    // Failure shapes — 502 {error} / 403 guard denial
    // ================================================================

    [Test]
    public async Task PlatformFailure_Returns502_ErrorShape()
    {
        MediationReturns(new CiMediationResult
        {
            Success = false,
            Outcome = "Error",
            FailureCode = CiFailureCodes.PlatformError,
            FailureReason = "404: not found",
        });

        var (status, body) = await Exec(await Call(
            new TriggerCiRequest("acme/widgets", "feature", "agent.yml", null)));

        status.Should().Be(502);
        body.GetProperty("error").GetString().Should().Be("404: not found");
    }

    [Test]
    public async Task GuardDenied_Returns403_ErrorShape()
    {
        MediationReturns(new CiMediationResult
        {
            Success = false,
            Outcome = "Error",
            FailureCode = CiFailureCodes.RepoNotAuthorized,
            FailureReason = "the acting tenant is not authorized for this repository",
        });

        var (status, body) = await Exec(await Call(
            new TriggerCiRequest("acme/widgets", "feature", "agent.yml", null)));

        status.Should().Be(403);
        body.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
    }

    // ================================================================
    // Validation precedes mediation (mediation strict-mock: never called)
    // ================================================================

    [TestCase(null, "feature", "wf.yml", "repository is required")]
    [TestCase("acme/widgets", null, "wf.yml", "branchName is required")]
    [TestCase("acme/widgets", "feature", null, "workflowFile is required")]
    public async Task MissingRequiredField_Returns400_BeforeAnyMediationCall(
        string? repo, string? branch, string? workflowFile, string expectedError)
    {
        var (status, body) = await Exec(await Call(
            new TriggerCiRequest(repo!, branch!, workflowFile!, null)));

        status.Should().Be(400);
        body.GetProperty("error").GetString().Should().Be(expectedError);
        _ci.VerifyNoOtherCalls();
    }

    [Test]
    public async Task InvalidRepoFormat_Returns400_BeforeAnyMediationCall()
    {
        var (status, body) = await Exec(await Call(
            new TriggerCiRequest("not-owner-slash-repo", "feature", "wf.yml", null)));

        status.Should().Be(400);
        body.GetProperty("error").GetString().Should().Be("Invalid repo format");
        _ci.VerifyNoOtherCalls();
    }
}
