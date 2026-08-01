using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;
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
        public int Calls { get; private set; }

        /// <summary>Status for the Nth call, when the test needs a sequence.</summary>
        public Queue<(HttpStatusCode Status, string Body)> Scripted { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var (s, b) = Scripted.Count > 0 ? Scripted.Dequeue() : (status, body);
            return new HttpResponseMessage(s)
            {
                Content = new StringContent(b, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>The exact envelope <c>AutonomyGateEnforcement.Denial</c> writes.</summary>
    private static string DenialBody(
        string code = "ACTION.GATE.REQUIRES_HUMAN", Guid? authorizationId = null) =>
        JsonSerializer.Serialize(new
        {
            code,
            action = "effect:git.pull-request.merge",
            group = "merge-control",
            effectiveMinAutonomy = 99,
            autonomyLevel = 1,
            authorizationId,
            correlationId = "PUT /api/v1/git/o/r/pull-requests/7/merge",
            reason = "always-human",
            assignmentSource = "PlatformCeiling",
            error = "The autonomy policy for this action does not permit the system to perform it "
                + "without a person.",
        });

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
            new SlackNotificationRequest());

        acceptedIsSuccess.Should().BeTrue(
            "202 IS success on this client. That is why a governance escalation cannot be "
            + "expressed as 202 — the engine would read it as 'done'.");

        // And the counterpart: 409 is NOT success, so the engine's caller falls
        // back / fails closed exactly as it does for any other refusal.
        var conflict = new StubHandler(HttpStatusCode.Conflict, "{}");
        var conflictIsSuccess = await Build(conflict).QueueSlackNotificationAsync(
            new SlackNotificationRequest());

        conflictIsSuccess.Should().BeFalse(
            "409 is the only shape that (a) is not success on this client and (b) says 'the "
            + "caller is authorized, the SYSTEM is not yet permitted' rather than 403's 'you may "
            + "not'");
    }

    // ====================================================================
    // 2026-08-01 review finding F5 (client half) — a governance denial must
    // not arrive looking exactly like an outage
    // ====================================================================

    [Test]
    public async Task AGovernanceDenial_isDistinguishableFromAnOutage()
    {
        // THE DEFECT. `Client_treats_202_as_success` above records that this client
        // "discriminates on nothing but IsSuccessStatusCode" — which is exactly why
        // D7 chose 409 over 202, and exactly why the 409 then arrived as a null
        // indistinguishable from a 503 or a socket reset. The two are opposites: an
        // outage clears itself, a denial repeats identically for ever until a person
        // decides or an admin lowers the threshold. An activity that retries a
        // denial burns its whole budget and then reports a platform failure for what
        // is a policy decision.
        var authorizationId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var denied = new StubHandler(HttpStatusCode.Conflict, DenialBody(authorizationId: authorizationId));
        var deniedClient = Build(denied);
        var deniedResult = await deniedClient.MergePullRequestAsync(
            "o/r", 7, new GitMergePrRequest());

        deniedResult.Should().BeNull(
            "ADDITIVE ONLY: existing callers keep seeing null on a 409 and none of them changes "
            + "shape. The distinction is carried alongside, not instead.");

        deniedClient.LastGovernanceDenial.Should().NotBeNull(
            "the whole point of F5 — the caller can now tell a policy refusal from an outage");
        deniedClient.LastGovernanceDenial!.Code.Should().Be(
            TammaApiGovernanceDenial.RequiresHumanCode);
        deniedClient.LastGovernanceDenial.Action.Should().Be("effect:git.pull-request.merge");
        deniedClient.LastGovernanceDenial.AuthorizationId.Should().Be(authorizationId);
        deniedClient.LastGovernanceDenial.IsClearableByAHuman.Should().BeTrue(
            "a requires-human denial carrying a pending row IS clearable — that is the one case "
            + "where waiting for a person is the right response rather than retrying");

        // The control: a real outage on the same call must stay indistinguishable
        // from nothing-in-particular, i.e. it must NOT set the denial.
        var down = new StubHandler(HttpStatusCode.ServiceUnavailable, "{}");
        var downClient = Build(down);
        var downResult = await downClient.MergePullRequestAsync(
            "o/r", 7, new GitMergePrRequest());

        downResult.Should().BeNull();
        downClient.LastGovernanceDenial.Should().BeNull(
            "THE ANTI-NO-OP HALF: if a 503 also set the denial, 'distinguishable' would be "
            + "satisfied by a field that is always populated");
    }

    [Test]
    public async Task AnUnrelated409_isNotMistakenForAGovernanceDenial()
    {
        // 409 is not owned by governance. An optimistic-concurrency conflict or a
        // duplicate-resource refusal must keep reading as an ordinary non-2xx, or
        // the engine would start waiting for a human who has nothing to decide.
        var handler = new StubHandler(HttpStatusCode.Conflict,
            JsonSerializer.Serialize(new { code = "BRANCH_ALREADY_EXISTS", error = "already there" }));
        var client = Build(handler);

        await client.CreateBranchAsync("o/r", new GitCreateBranchRequest());

        client.LastGovernanceDenial.Should().BeNull();

        // And a 409 whose body is not JSON at all must not throw out of the error path.
        var garbage = new StubHandler(HttpStatusCode.Conflict, "<html>nope</html>");
        var garbageClient = Build(garbage);
        var act = async () => await garbageClient.CreateBranchAsync("o/r", new GitCreateBranchRequest());

        await act.Should().NotThrowAsync();
        garbageClient.LastGovernanceDenial.Should().BeNull();
    }

    [Test]
    public async Task TheDenialSlot_isClearedByTheNextCall_soItCanNeverBeReadStale()
    {
        // A "last denial" that outlives its call is worse than none: the caller
        // would attribute an old refusal to a call that succeeded, and start waiting
        // for a human over a completed effect.
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        handler.Scripted.Enqueue((HttpStatusCode.Conflict, DenialBody()));
        handler.Scripted.Enqueue((HttpStatusCode.OK, "{\"success\":true}"));
        handler.Scripted.Enqueue((HttpStatusCode.ServiceUnavailable, "{}"));
        var client = Build(handler);

        await client.CreateBranchAsync("o/r", new GitCreateBranchRequest());
        client.LastGovernanceDenial.Should().NotBeNull("call 1 was denied");

        await client.CreateBranchAsync("o/r", new GitCreateBranchRequest());
        client.LastGovernanceDenial.Should().BeNull("call 2 SUCCEEDED — the denial is not sticky");

        await client.CreateBranchAsync("o/r", new GitCreateBranchRequest());
        client.LastGovernanceDenial.Should().BeNull(
            "call 3 was a genuine outage, which is precisely the thing a denial must not be "
            + "confused with");
    }

    [Test]
    public async Task TheDenialIsRecognised_onEveryVerbTheEnforcedRoutesUse()
    {
        // The opted-in set spans POST, PUT, PATCH and DELETE, and this client sends
        // each through a DIFFERENT private helper (PostAsync / SendJsonAsync /
        // the inline DELETE) plus five bespoke senders. Recognising the denial in
        // only one of them would leave most enforced routes still reading as
        // outages, so every path is exercised.
        async Task Assert409IsSeen(string label, Func<TammaApiClient, Task> call)
        {
            var handler = new StubHandler(HttpStatusCode.Conflict, DenialBody());
            var client = Build(handler);
            try { await call(client); }
            catch (HttpRequestException) { /* the two fail-LOUD document senders throw */ }
            client.LastGovernanceDenial.Should().NotBeNull(
                $"{label} is an enforced route and can answer 409");
        }

        await Assert409IsSeen("POST git branches",
            c => c.CreateBranchAsync("o/r", new GitCreateBranchRequest()));
        await Assert409IsSeen("PUT git merge",
            c => c.MergePullRequestAsync("o/r", 7, new GitMergePrRequest()));
        await Assert409IsSeen("PATCH git issue",
            c => c.UpdateIssueStatusAsync("o/r", 7, new GitUpdateIssueRequest()));
        await Assert409IsSeen("DELETE git branch",
            c => c.DeleteBranchAsync("o/r", "feature/x", "run-1"));
        await Assert409IsSeen("POST notifications/slack",
            c => c.QueueSlackNotificationAsync(new SlackNotificationRequest()));
        await Assert409IsSeen("POST engine/events",
            c => c.AppendEventsAsync(new[] { new EngineEventRecord(Guid.NewGuid(), "TEST.EVENT", null, null, null, null, null, null, null, null, null, null) }));
        await Assert409IsSeen("POST engine/platform-events",
            c => c.AppendPlatformEventsAsync(new[] { new PlatformEventRecord(Guid.NewGuid(), "TEST.PLATFORM.EVENT", null, null, null, null, null, null) }));
        await Assert409IsSeen("POST engine/channel/outbox",
            c => c.PostChannelOutboxAsync("{}"));
        await Assert409IsSeen("POST engine/documents",
            c => c.PersistDocumentAsync(new PersistDocumentRequest("{}", null)));
        await Assert409IsSeen("POST engine/documents/{id}/status",
            c => c.SetDocumentStatusAsync(Guid.NewGuid(), "accepted", null));
    }

    // ====================================================================
    // F5's other question — does ANY opted-in route send a correlation id
    // where the server-side filter can see it?
    // ====================================================================

    [Test]
    public async Task WhichEnforcedRoutes_sendACorrelationIdTheGateFilterCanSee()
    {
        // AutonomyGateEnforcement.ResolveCorrelationId reads the
        // X-Tamma-Correlation-Id HEADER, else a ?correlationId= QUERY value. It
        // deliberately does NOT read the body (that would consume the stream the
        // handler binds from). So a request whose correlation travels only in the
        // JSON body is, to the gate, a request with NO correlation — which means no
        // pending authorization row can be minted for it and the 409's
        // authorizationId is null.
        //
        // This enumerates the opted-in routes BY EXECUTION and records exactly which
        // ones satisfy the filter today. It is a characterization pin: if a future
        // change starts (or stops) sending one, this goes red and the author has to
        // say which.
        var visible = new List<string>();
        var invisible = new List<string>();

        async Task Probe(string label, Func<TammaApiClient, Task> call)
        {
            var handler = new StubHandler(HttpStatusCode.OK, "{}");
            var client = Build(handler);
            try { await call(client); } catch (HttpRequestException) { }

            var uri = handler.LastRequest!.RequestUri!;
            var hasQuery = uri.Query.Contains("correlationId=", StringComparison.OrdinalIgnoreCase);
            var hasHeader = handler.LastRequest.Headers.Contains("X-Tamma-Correlation-Id");
            (hasQuery || hasHeader ? visible : invisible).Add(label);
        }

        await Probe("POST /api/v1/git/{o}/{r}/branches",
            c => c.CreateBranchAsync("o/r", new GitCreateBranchRequest()));
        await Probe("POST /api/v1/git/{o}/{r}/pull-requests",
            c => c.CreatePullRequestAsync("o/r", new GitCreatePrRequest()));
        await Probe("PUT /api/v1/git/{o}/{r}/pull-requests/{n}/merge",
            c => c.MergePullRequestAsync("o/r", 7, new GitMergePrRequest { CorrelationId = "run-1" }));
        await Probe("PATCH /api/v1/git/{o}/{r}/issues/{n}",
            c => c.UpdateIssueStatusAsync("o/r", 7, new GitUpdateIssueRequest()));
        await Probe("POST /api/v1/git/{o}/{r}/releases",
            c => c.CreateReleaseAsync("o/r", new GitCreateReleaseRequest()));
        await Probe("DELETE /api/v1/git/{o}/{r}/branches",
            c => c.DeleteBranchAsync("o/r", "feature/x", "run-1"));
        await Probe("POST /api/v1/ci/{o}/{r}/test-runs",
            c => c.TriggerTestsAsync("o/r", new CiTriggerTestsRequest()));
        await Probe("PATCH /api/v1/jira/tickets/{id}",
            c => c.UpdateJiraTicketAsync("T-1", new JiraUpdateTicketRequest()));
        await Probe("POST /api/v1/agent-dispatch/{o}/{r}/runs",
            c => c.DispatchAgentRunAsync("o/r", new AgentDispatchRunApiRequest()));
        await Probe("POST /api/v1/notifications/slack",
            c => c.QueueSlackNotificationAsync(new SlackNotificationRequest()));
        await Probe("POST /api/v1/notifications/email",
            c => c.SendEmailAsync(new EmailSendRequest()));
        await Probe("POST /api/engine/events",
            c => c.AppendEventsAsync(new[] { new EngineEventRecord(Guid.NewGuid(), "TEST.EVENT", null, null, null, null, null, null, null, null, null, null) }));
        await Probe("POST /api/engine/platform-events",
            c => c.AppendPlatformEventsAsync(new[] { new PlatformEventRecord(Guid.NewGuid(), "TEST.PLATFORM.EVENT", null, null, null, null, null, null) }));
        await Probe("POST /api/engine/channel/outbox",
            c => c.PostChannelOutboxAsync("{}"));
        await Probe("POST /api/engine/documents",
            c => c.PersistDocumentAsync(new PersistDocumentRequest("{}", null)));
        await Probe("POST /api/engine/documents/{id}/status",
            c => c.SetDocumentStatusAsync(Guid.NewGuid(), "accepted", null));

        TestContext.Out.WriteLine("Correlation VISIBLE to the gate filter:");
        foreach (var v in visible) TestContext.Out.WriteLine("  " + v);
        TestContext.Out.WriteLine("Correlation INVISIBLE to the gate filter:");
        foreach (var i in invisible) TestContext.Out.WriteLine("  " + i);

        visible.Should().BeEquivalentTo(new[] { "DELETE /api/v1/git/{o}/{r}/branches" },
            "EXACTLY ONE opted-in route puts a correlation where the filter looks — the branch "
            + "delete, whose caller (MergeAndCompleteReviewActivity) passes the workflow "
            + "instance id as ?correlationId=. Every other enforced route sends none, and the "
            + "PUT merge is the sharpest case: it DOES carry a CorrelationId, but in the JSON "
            + "BODY, which ResolveCorrelationId deliberately never reads.");

        invisible.Should().HaveCount(15,
            "the other fifteen opted-in routes give the gate nothing to key a pending "
            + "authorization on");
    }
}
