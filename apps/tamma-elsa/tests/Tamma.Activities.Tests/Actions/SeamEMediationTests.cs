using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Activities.Policy;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// Story 43-9 <b>Seam E</b> (AC8's <c>Client_treats_202_as_success</c>, AC10) —
/// the mediation half of the gate: the client method the engine calls, and the
/// characterization test that ENCODES why a Seam C denial is 409 rather than 202.
/// </summary>
[TestFixture]
public class SeamEMediationTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static TammaApiClient Build(StubHandler handler)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tamma:ApiUrl"] = "http://tamma.test",
            })
            .Build();
        return new TammaApiClient(new HttpClient(handler), NullLogger<TammaApiClient>.Instance, cfg);
    }

    // ====================================================================
    // AC10 — the gate-evaluation call
    // ====================================================================

    [Test]
    public async Task EvaluateGovernanceAsync_postsToTheMediationRoute_andParsesTheDecision()
    {
        var payload = JsonSerializer.Serialize(new
        {
            outcome = "requires-human",
            action = "effect:deploy.promote-prod",
            group = "deploy-control",
            autonomyLevel = 1,
            effectiveMinAutonomy = 99,
            enforced = true,
            source = "ActionOverride",
            reason = "always-human",
            authorizationId = "11111111-1111-1111-1111-111111111111",
        });
        var handler = new StubHandler(HttpStatusCode.OK, payload);
        var client = Build(handler);

        var result = await client.EvaluateGovernanceAsync(
            new GovernanceEvaluateRequest("effect:deploy.promote-prod", CorrelationId: "run-7"));

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/api/v1/governance/evaluate",
            "the engine cannot inject IAutonomyGate — Tamma.ElsaServer registers no repository — "
            + "so Seam E is an HTTP hop to this exact route");
        handler.LastBody.Should().Contain("run-7",
            "the correlation id must travel: it is the key one human grant covers, and without it "
            + "the ledger cannot tie a decision to a run");

        result.Should().NotBeNull();
        result!.Outcome.Should().Be(GovernanceEvaluateResponse.OutcomeRequiresHuman);
        result.Enforced.Should().BeTrue();
        result.AuthorizationId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "a requires-human answer with no authorization id tells the engine a person must "
            + "decide while giving nobody anything to decide");
    }

    [Test]
    public async Task EvaluateGovernanceAsync_returnsNull_onTransportFailure()
    {
        // The FAIL-OPEN input. It is safe only because Seam E's one v1 adoption
        // ORs its outcome into an existing predicate: a null contributes nothing
        // and the pipeline behaves exactly as it did before this story.
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable, "{}");
        var client = Build(handler);

        var result = await client.EvaluateGovernanceAsync(
            new GovernanceEvaluateRequest("effect:deploy.promote-prod"));

        result.Should().BeNull(
            "the client discriminates on IsSuccessStatusCode only; a 503 is a null, and the "
            + "CALLER decides what null means");
    }

    // ====================================================================
    // AC8 — why a Seam C denial is 409 and can never be 202
    // ====================================================================

    [Test]
    public async Task Client_treats_202_as_success()
    {
        // CHARACTERIZATION TEST — this is not a wish, it is the reason 409 was
        // chosen, ENCODED so a future author cannot "improve" the denial status
        // back to 202.
        //
        // TammaApiClient branches on nothing but IsSuccessStatusCode. 202 is
        // therefore indistinguishable from 200 on this client — and 202 is
        // ALREADY a real success on it (QueueSlackNotificationAsync →
        // POST /api/v1/notifications/slack → Results.Accepted). A 202 "escalated"
        // response would make the engine proceed as if the effect had happened:
        // the exact failure the gate exists to prevent, introduced by the gate.
        var accepted = new StubHandler(HttpStatusCode.Accepted, "{}");
        var acceptedIsSuccess = await Build(accepted).QueueSlackNotificationAsync(
            new Tamma.Activities.LlmCall.Models.SlackNotificationRequest());

        acceptedIsSuccess.Should().BeTrue(
            "202 IS success on this client. That is why a governance escalation cannot be "
            + "expressed as 202 — the engine would read it as 'done'.");

        // And the counterpart: 409 is NOT success, so the engine's caller falls
        // back / fails closed exactly as it does for any other refusal.
        var conflict = new StubHandler(HttpStatusCode.Conflict, "{}");
        var conflictIsSuccess = await Build(conflict).QueueSlackNotificationAsync(
            new Tamma.Activities.LlmCall.Models.SlackNotificationRequest());

        conflictIsSuccess.Should().BeFalse(
            "409 is the only shape that (a) is not success on this client and (b) says 'the "
            + "caller is authorized, the SYSTEM is not yet permitted' rather than 403's 'you may "
            + "not'");
    }
}
