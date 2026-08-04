using System.Net;
using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.Activities.Core;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 40-8 (AC1/AC2/AC3/AC5 — execution layer) — runs the REAL
/// <see cref="CreateIssuesWorkflow"/> through a real <see cref="IWorkflowRunner"/>
/// (the <c>TriageItemCycleApplyFaultExecutionTests</c> harness: Elsa runtime + the
/// DCB event drain + an injected <see cref="IIssueCreateClient"/> seam + a capturing
/// <c>TammaApiClient</c>), proving what the topology tests cannot:
///
/// <list type="number">
///   <item>the workflow COMPLETES with counts + created issue numbers on its output
///         surface (the defer/split branches' previously-dead dispatch now has a real,
///         finishing target — the bug's reproduction, inverted);</item>
///   <item>malformed input still reaches Finish — no fault, no incident, no pending
///         bookmark (the "never a hang" half of AC1);</item>
///   <item>a per-item failure emits the loud <c>ISSUES.CREATE_ITEM.FAILED</c> event
///         and the workflow STILL completes (Failure outcome routed, not faulted);</item>
///   <item>a re-run after a partial failure creates the input set exactly once
///         (AC3 at the workflow level);</item>
///   <item>the <c>tenantId</c> input tenant-tags the drained events — pinning the
///         invisible-at-compile-time <c>EventPersistenceMiddleware.ResolveTenantId</c>
///         contract that the workflow variable is literally named <c>TenantId</c>.</item>
/// </list>
///
/// <para>AC2's literal full-cycle pin (SingleIssueCycle driven end-to-end through a
/// real awaited dispatch) is not writable today — the only full-runtime dispatch
/// harness is <c>[Explicit]</c> and diagnosed broken
/// (<c>DocumentLifecycleExecutionTests</c>, 2026-07-29). The layered substitute is:
/// the existing Defer/Split routing pins + <c>DispatchTargetStructuralTests</c>'
/// resolution sweep + THIS fixture's real-runner execution of the child +
/// <c>SingleIssueCycleRoutingTests</c>' dispatch-input pins.</para>
/// </summary>
[TestFixture]
public class CreateIssuesWorkflowExecutionTests
{
    private const string Repo = "owner/repo";

    [Test]
    public async Task Completes_WithCounts_AndIssueNumbersOutput()
    {
        var capture = new CapturingHandler();
        var client = new ScriptedClient();
        await using var provider = BuildProvider(capture, client);

        var result = await RunWorkflowAsync(provider, new Dictionary<string, object>
        {
            ["repository"] = Repo,
            ["issuesJson"] = """[{"title":"deferred item 1"},{"title":"deferred item 2"}]""",
        });

        result.WorkflowState.Status.Should().Be(WorkflowStatus.Finished,
            "the create-issues child must COMPLETE so the waiting defer/split parent resumes");
        var output = result.WorkflowState.Output;
        output["success"].Should().Be(true);
        output["createdCount"].Should().Be(2);
        JsonSerializer.Deserialize<int[]>(output["issueNumbersJson"]!.ToString()!)
            .Should().HaveCount(2, "AC1: the result carries the created issue numbers");
        client.Created.Select(c => c.Title).Should().Equal("deferred item 1", "deferred item 2");
    }

    [Test]
    public async Task MalformedInput_StillReachesFinish_NoIncident_NoBookmark()
    {
        var capture = new CapturingHandler();
        var client = new ScriptedClient();
        await using var provider = BuildProvider(capture, client);

        var result = await RunWorkflowAsync(provider, new Dictionary<string, object>
        {
            ["repository"] = Repo,
            ["issuesJson"] = "this is not json",
        });

        result.WorkflowState.Status.Should().Be(WorkflowStatus.Finished,
            "AC1: malformed issuesJson must complete — never a fault, never a hang");
        result.WorkflowState.Incidents.Should().BeEmpty("no incident may be raised for malformed input");
        result.WorkflowState.Bookmarks.Should().BeEmpty("nothing may suspend");
        result.WorkflowState.Output["success"].Should().Be(true);
        result.WorkflowState.Output["createdCount"].Should().Be(0);
        client.CreateCalls.Should().Be(0);
    }

    [Test]
    public async Task PerItemFailure_EmitsLoudFailedEvent_AndStillCompletes()
    {
        var capture = new CapturingHandler();
        var client = new ScriptedClient { FailWith = t => t == "bad" ? 502 : 0 };
        await using var provider = BuildProvider(capture, client);

        var result = await RunWorkflowAsync(provider, new Dictionary<string, object>
        {
            ["repository"] = Repo,
            ["issuesJson"] = """[{"title":"good"},{"title":"bad"}]""",
        });

        result.WorkflowState.Status.Should().Be(WorkflowStatus.Finished,
            "a per-item failure routes the Failure outcome — the workflow still completes (D4)");
        result.WorkflowState.Output["success"].Should().Be(false, "the failure is honest, not swallowed");
        result.WorkflowState.Output["createdCount"].Should().Be(1);
        result.WorkflowState.Output["failedCount"].Should().Be(1);

        var types = CapturedEventTypes(capture);
        types.Should().Contain(IssuesCreateEvents.ItemFailed,
            "the per-item failure must drain as a loud ISSUES.CREATE_ITEM.FAILED event");
        types.Should().Contain(IssuesCreateEvents.BatchCompleted,
            "the batch terminal still fires on the failure path");
    }

    [Test]
    public async Task ReRunAfterPartialFailure_CreatesTheInputSetExactlyOnce()
    {
        var capture = new CapturingHandler();
        var client = new ScriptedClient { FailWith = t => t is "t2" or "t3" ? 500 : 0 };
        await using var provider = BuildProvider(capture, client);

        var input = new Dictionary<string, object>
        {
            ["repository"] = Repo,
            ["issuesJson"] = """[{"title":"t1"},{"title":"t2"},{"title":"t3"}]""",
        };

        var run1 = await RunWorkflowAsync(provider, input);
        run1.WorkflowState.Output["createdCount"].Should().Be(1);

        client.FailWith = null; // healthy again — the crash-re-run shape
        var run2 = await RunWorkflowAsync(provider, input);

        run2.WorkflowState.Status.Should().Be(WorkflowStatus.Finished);
        client.Created.Select(c => c.Title).Should().BeEquivalentTo(new[] { "t1", "t2", "t3" },
            "AC3: the created set on the platform is exactly the input list, once");
        run2.WorkflowState.Output["skippedCount"].Should().Be(1,
            "run 1's creation is skipped by the platform-side dedupe, not re-created");
    }

    [Test]
    public async Task TenantId_Input_TagsTheDrainedEvents()
    {
        // Pins the EventPersistenceMiddleware.ResolveTenantId contract: the drain reads
        // the workflow variable literally named "TenantId" and sends it as the
        // X-Tenant-Id header on the event append. RED if the variable is renamed.
        var capture = new CapturingHandler();
        var client = new ScriptedClient();
        await using var provider = BuildProvider(capture, client);

        var tenant = Guid.NewGuid();
        await RunWorkflowAsync(provider, new Dictionary<string, object>
        {
            ["repository"] = Repo,
            ["issuesJson"] = """[{"title":"tenant-scoped"}]""",
            ["tenantId"] = tenant.ToString(),
        });

        capture.TenantHeaders.Should().NotBeEmpty("the ISSUES.CREATE* events must drain");
        capture.TenantHeaders.Should().AllBe(tenant.ToString(),
            "AC5: the drained events must be tenant-tagged from the workflow's TenantId variable");
    }

    [Test]
    public async Task NoCallbackUrl_MockShortCircuit_StillCompletes()
    {
        // Parity with the ApplyTriageResultActivity mock path: an installation with no
        // Engine:CallbackUrl must not hang or fault the defer/split tail.
        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, client: null, callbackUrl: null);

        var result = await RunWorkflowAsync(provider, new Dictionary<string, object>
        {
            ["repository"] = Repo,
            ["issuesJson"] = """[{"title":"t1"}]""",
        });

        result.WorkflowState.Status.Should().Be(WorkflowStatus.Finished);
        result.WorkflowState.Output["success"].Should().Be(true);
        result.WorkflowState.Output["createdCount"].Should().Be(0, "the mock path creates nothing, loudly");
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static async Task<RunWorkflowResult> RunWorkflowAsync(
        IServiceProvider rootProvider, IDictionary<string, object> input)
    {
        using var scope = rootProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
        return await runner.RunAsync(
            new CreateIssuesWorkflow(), new RunWorkflowOptions { Input = input }, CancellationToken.None);
    }

    private static ServiceProvider BuildProvider(
        CapturingHandler capture, IIssueCreateClient? client, string? callbackUrl = "http://engine.test")
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddElsa(elsa =>
        {
            elsa.AddActivity<CreateIssuesActivity>();
            elsa.UseWorkflows(w => w.UseTammaEventPersistence());
        });

        var settings = new Dictionary<string, string?> { ["Tamma:ApiUrl"] = "http://tamma.test" };
        if (callbackUrl is not null)
            settings["Engine:CallbackUrl"] = callbackUrl;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        services.AddSingleton<IConfiguration>(config);
        if (client is not null)
            services.AddSingleton(client);

        services.AddSingleton(_ => new Tamma.Activities.LlmCall.TammaApiClient(
            new HttpClient(capture) { BaseAddress = null },
            NullLogger<Tamma.Activities.LlmCall.TammaApiClient>.Instance,
            config));

        return services.BuildServiceProvider();
    }

    private static List<string?> CapturedEventTypes(CapturingHandler capture) =>
        capture.Bodies
            .SelectMany(b => JsonDocument.Parse(b).RootElement.GetProperty("events").EnumerateArray())
            .Select(e => e.GetProperty("eventType").GetString())
            .ToList();

    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class ScriptedClient : IIssueCreateClient
    {
        private int _nextNumber = 500;

        public List<(string Title, string Body, IReadOnlyList<string> Labels)> Created { get; } = new();
        public List<ExistingIssueRef> Existing { get; } = new();
        public int CreateCalls { get; private set; }
        public Func<string, int>? FailWith { get; set; }

        public Task<IssueCreateResult> CreateIssueAsync(
            string repository, string title, string body, IReadOnlyList<string> labels, CancellationToken ct)
        {
            CreateCalls++;
            var status = FailWith?.Invoke(title) ?? 0;
            if (status != 0)
                return Task.FromResult(IssueCreateResult.Fail(status));
            var number = _nextNumber++;
            Created.Add((title, body, labels));
            Existing.Add(new ExistingIssueRef(number, title, "open"));
            return Task.FromResult(IssueCreateResult.Ok(number));
        }

        public Task<IReadOnlyList<ExistingIssueRef>> ListIssuesAsync(
            string repository, int page, int perPage, CancellationToken ct)
        {
            IReadOnlyList<ExistingIssueRef> slice =
                Existing.Skip((page - 1) * perPage).Take(perPage).ToList();
            return Task.FromResult(slice);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        public List<string?> TenantHeaders { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
                if (request.Headers.TryGetValues("X-Tenant-Id", out var values))
                    TenantHeaders.Add(values.FirstOrDefault());
            }
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"ok\":true,\"persisted\":1}"),
            };
        }
    }
}
