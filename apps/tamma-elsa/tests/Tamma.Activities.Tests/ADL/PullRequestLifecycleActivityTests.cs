using System.Net;
using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.Activities.Core;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// Story 31-13 (AC4) — the workflow plane can CLOSE then REOPEN a pull request
/// through the mediated, governed git plane.
///
/// <para><b>Harness choice (stated per the plan's fallback).</b> A full
/// <c>WebApplicationFactory</c> host that runs the real <c>GitMediationService</c>
/// against a canned GitHub REST handler would need Tamma.Api's DI + a Postgres event
/// store (Testcontainers). This fixture takes the plan's sanctioned lighter path: it
/// runs a REAL Elsa workflow graph (<c>Sequence[Close → Reopen]</c>) through the real
/// <see cref="IWorkflowRunner"/> and the durable event drain, against a canned-HTTP
/// <c>TammaApiClient</c> (the <c>CreateIssuesWorkflowExecutionTests</c> harness). The
/// canned handler stands in for the API: it answers <c>.../close</c> with
/// <c>prState=closed</c> and <c>.../reopen</c> with <c>prState=open</c>, and 200s the
/// event append so the drain captures the emitted DCB events.</para>
///
/// <para>The activities emit the headline <c>GIT.PR_CLOSED.SUCCESS</c> /
/// <c>GIT.PR_REOPENED.SUCCESS</c> DCB events on the workflow event stream (the
/// live-surface <c>GitMediationService</c> also emits the same audit terminals under
/// D1 — here, with no server running, the activity plane is the observed emitter).</para>
/// </summary>
[TestFixture]
public class PullRequestLifecycleActivityTests
{
    private const string Repo = "owner/repo";
    private const int PrNumber = 42;

    [Test]
    public async Task Workflow_CanCloseAndReopen_APullRequest()
    {
        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture);

        var result = await RunWorkflowAsync(provider);

        result.WorkflowState.Status.Should().Be(WorkflowStatus.Finished,
            "the close→reopen sequence must complete end to end");
        result.WorkflowState.Incidents.Should().BeEmpty();

        // ── The PR state goes closed then open (from the mediation responses) ──
        capture.GitCalls.Select(c => c.Path).Should().ContainInOrder(
            $"/api/v1/git/owner/repo/pull-requests/{PrNumber}/close",
            $"/api/v1/git/owner/repo/pull-requests/{PrNumber}/reopen");

        // ── Both success events are emitted, in order, carrying the new state ──
        var events = CapturedEvents(capture);
        var closed = events.SingleOrDefault(e => e.GetProperty("eventType").GetString() == "GIT.PR_CLOSED.SUCCESS");
        closed.ValueKind.Should().NotBe(JsonValueKind.Undefined, "the close must emit GIT.PR_CLOSED.SUCCESS");
        closed.GetProperty("data").GetProperty("prState").GetString().Should().Be("closed");

        var reopened = events.SingleOrDefault(e => e.GetProperty("eventType").GetString() == "GIT.PR_REOPENED.SUCCESS");
        reopened.ValueKind.Should().NotBe(JsonValueKind.Undefined, "the reopen must emit GIT.PR_REOPENED.SUCCESS");
        reopened.GetProperty("data").GetProperty("prState").GetString().Should().Be("open");
    }

    /// <summary>
    /// Structural half of AC4: both activities exist and are <c>[Activity]</c>-discoverable.
    /// </summary>
    [Test]
    public void CloseAndReopenActivities_AreActivityDiscoverable()
    {
        foreach (var type in new[] { typeof(ClosePullRequestActivity), typeof(ReopenPullRequestActivity) })
        {
            typeof(IActivity).IsAssignableFrom(type).Should().BeTrue($"{type.Name} must be an IActivity");
            type.GetCustomAttributes(typeof(ActivityAttribute), inherit: false)
                .Should().NotBeEmpty($"{type.Name} must carry [Activity] so the engine can discover it");
        }
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static async Task<RunWorkflowResult> RunWorkflowAsync(IServiceProvider rootProvider)
    {
        using var scope = rootProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
        return await runner.RunAsync(
            new CloseReopenWorkflow(), new RunWorkflowOptions(), CancellationToken.None);
    }

    /// <summary>A minimal graph: close the PR, then reopen it.</summary>
    private sealed class CloseReopenWorkflow : WorkflowBase
    {
        protected override void Build(IWorkflowBuilder builder)
        {
            builder.Root = new Sequence
            {
                Activities =
                {
                    new ClosePullRequestActivity
                    {
                        Repository = new Input<string>(Repo),
                        PrNumber = new Input<int>(PrNumber),
                        TenantId = new Input<string?>(string.Empty),
                    },
                    new ReopenPullRequestActivity
                    {
                        Repository = new Input<string>(Repo),
                        PrNumber = new Input<int>(PrNumber),
                        TenantId = new Input<string?>(string.Empty),
                    },
                },
            };
        }
    }

    private static ServiceProvider BuildProvider(CapturingHandler capture)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddElsa(elsa =>
        {
            elsa.AddActivity<ClosePullRequestActivity>();
            elsa.AddActivity<ReopenPullRequestActivity>();
            elsa.UseWorkflows(w => w.UseTammaEventPersistence());
        });

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Tamma:ApiUrl"] = "http://tamma.test" })
            .Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(_ => new Tamma.Activities.LlmCall.TammaApiClient(
            new HttpClient(capture) { BaseAddress = null },
            NullLogger<Tamma.Activities.LlmCall.TammaApiClient>.Instance,
            config));

        return services.BuildServiceProvider();
    }

    private static List<JsonElement> CapturedEvents(CapturingHandler capture) =>
        capture.EventBodies
            .SelectMany(b => JsonDocument.Parse(b).RootElement.GetProperty("events").EnumerateArray())
            .ToList();

    // ── Canned-HTTP handler: the API stand-in ────────────────────────────────

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(string Path, string Body)> GitCalls { get; } = new();
        public List<string> EventBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

            if (path.EndsWith("/close", StringComparison.Ordinal))
            {
                GitCalls.Add((path, body));
                return Json("{\"success\":true,\"prState\":\"closed\"}");
            }
            if (path.EndsWith("/reopen", StringComparison.Ordinal))
            {
                GitCalls.Add((path, body));
                return Json("{\"success\":true,\"prState\":\"open\"}");
            }
            if (path.EndsWith("/api/engine/events", StringComparison.Ordinal))
            {
                EventBodies.Add(body);
                return Json("{\"ok\":true,\"persisted\":1}");
            }
            return Json("{}");
        }

        private static HttpResponseMessage Json(string content) =>
            new(HttpStatusCode.OK) { Content = new StringContent(content) };
    }
}
