using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Messages;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.Core;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.ElsaServer.Endpoints;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 41-1c AC1/AC5 — a prose document (<c>kind=adr, audience=engineering</c>)
/// rides <see cref="DocumentLifecycleWorkflow"/> UNCHANGED: draft → validate →
/// review → accept-gate publish+suspend → Accept resume → Accepted+persisted.
/// The <c>DocumentLifecycleExecutionTests</c> harness shape (scripted stub
/// sub-workflows, capturing publisher, resume through the 39-8 seam), pointed at
/// <c>documentType = "prose"</c> with the eligible producing pair
/// <c>(devops, write-postmortem)</c>; the payload's <c>kind</c> (here <c>adr</c>)
/// is what names the prose family member — the cell and the kind are independent
/// axes.
///
/// <para>AC5 is asserted structurally too (fast, non-Explicit) in
/// <see cref="ProseLifecycleStructureTests"/>: the lifecycle graph has NO node
/// that mentions prose — no bespoke branch.</para>
/// </summary>
[TestFixture]
[Explicit("DOES NOT RUN ANYWHERE TODAY and FAILS when invoked: no CI job passes a filter that selects " +
    "[Explicit] fixtures, and under this bare-provider harness the lifecycle suspends forever on its first " +
    "Kind=ActivityKind.Task activity (ComputeReEntryPosition) — Elsa's background activity invoker defers it " +
    "but no BackgroundActivity bookmark is ever created, so nothing resumes it (diagnosed 2026-07-29; same " +
    "root cause as DocumentLifecycleExecutionTests). The ParentDocumentId/lifecycle assertions this fixture " +
    "would make are covered by EXECUTING tests in BuildReviewEnvelopeTests; keep this fixture as the " +
    "full-runtime target for a future working harness.")]
public class ProseLifecycleExecutionTests
{
    private const string TenantId = "";

    [SetUp]
    public void ResetScript() => Script.Reset();

    [Test]
    public async Task ProseAdr_FullCycle_ReviewedByTechWriter_ResumesToAccepted()
    {
        Script.Llm.Add(ValidProse());
        Script.Reviews.Add(ApproveReview());

        var capture = new CapturingHandler();
        var publisher = new CapturingPublisher();
        await using var provider = BuildProvider(capture, publisher);

        // Populate the Elsa registries so ByDefinitionId resolves the coded
        // workflows, and start the hosted services so the background workflow
        // dispatcher drains the mediated llm-call / document-review dispatches
        // (the runtime host normally does both at startup).
        await provider.GetRequiredService<Elsa.Workflows.Runtime.IRegistriesPopulator>()
            .PopulateAsync(CancellationToken.None);
        foreach (var hosted in provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        // Run to the accept-gate suspend — the USUAL AcceptanceRequest (AC5).
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync();
        var response = await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId("document-lifecycle"),
            Input = ProseInput(),
        });
        // The in-memory runtime may complete the mediated sub-workflow dispatches
        // asynchronously — wait (bounded) for the accept-gate publish to land.
        AcceptanceRequest? request = null;
        for (var i = 0; i < 100 && (request = publisher.Last) is null; i++)
            await Task.Delay(100);

        var runState = await (await runtime.CreateClientAsync(response.WorkflowInstanceId)).ExportStateAsync();
        var allBookmarks = await provider.GetRequiredService<IBookmarkStore>()
            .FindManyAsync(new Elsa.Workflows.Runtime.Filters.BookmarkFilter(), CancellationToken.None);
        request.Should().NotBeNull(
            "the lifecycle must publish an AcceptanceRequest and suspend on the gate " +
            $"(instance status: {response.Status}/{response.SubStatus}, " +
            $"output: {JsonSerializer.Serialize(runState.Output)}, " +
            $"bookmarks: [{string.Join("; ", allBookmarks.Select(b => $"{b.ActivityTypeName}:{b.Name}"))}], " +
            $"httpBodies: {capture.Bodies.Count})");

        // The prose subject document.
        request!.Document.Type.Should().Be("prose");
        request.Document.Audience.Should().Be("engineering",
            "the draft-mint path copies the payload audience onto the envelope (D2)");

        // AC5 — the Review is OVER the prose document: ParentDocumentId is the
        // prose document id, and the reviewer is the D6 default (tech_writer,
        // executable since 41-1a landed the TechWriter selector arm).
        request.Review.Type.Should().Be("review");
        request.Review.ParentDocumentId.Should().Be(request.Document.Id);
        request.Review.ProducedBy.Role.Should().Be(AgentRole.TechWriter.ToWire());
        request.Rules.Rules.ReviewerSelection.ReviewerRole.Should().Be(AgentRole.TechWriter.ToWire());

        // Resume through the 39-8 seam with Accept → Accepted, persisted.
        var result = await ResumeAsync(provider, request.DecisionSessionId, "{\"kind\":\"accept\"}");
        result.TryGetValue("status", out var status);
        status?.ToString().Should().Be(DocumentLifecycleResult.StatusAccepted);

        CapturedEventTypes(capture).Should().ContainInOrder(
            DocumentEvents.ProducedSuccess,
            DocumentEvents.ValidatedSuccess,
            DocumentEvents.ReviewRequested,
            DocumentEvents.Reviewed,
            DocumentEvents.Accepted);

        // AC1 (persistence half) — the accepted prose envelope reached the
        // engine→API persist seam with its audience intact.
        var persisted = PersistedEnvelopes(capture);
        persisted.Should().Contain(e => e.Type == "prose" && e.Audience == "engineering");
    }

    // ── harness (the DocumentLifecycleExecutionTests shape) ────────────────

    private static Dictionary<string, object> ProseInput() => new()
    {
        // (devops, write-postmortem) is an ELIGIBLE prose producing pair today;
        // the payload's kind (adr) is what names the prose family member — the
        // producing cell and the kind are deliberately independent axes.
        ["producerRole"] = "devops",
        ["producerAction"] = "write-postmortem",
        ["producerVariablesJson"] = "{\"workItemJson\":\"x\"}",
        ["documentType"] = "prose",
        ["issueId"] = "issue-prose-1",
        ["correlationId"] = "corr-prose-1",
        ["acceptanceRulesJson"] = "",
        ["tenantId"] = TenantId,
    };

    private static string ValidProse() =>
        "{\"kind\":\"adr\",\"audience\":\"engineering\",\"title\":\"ADR-001: Prose rides the lifecycle\"," +
        "\"body\":\"## Decision\\nProse is a registered type whose body is unvalidated markdown.\"}";

    private static string ApproveReview() =>
        "{\"subject\":{\"kind\":\"document\",\"documentId\":\"0192a8b0-1111-7abc-8def-000000000001\",\"documentType\":\"prose\"}," +
        "\"decision\":\"approve\",\"summary\":\"clear, correctly tagged\",\"issues\":[]}";

    private static async Task<IDictionary<string, object>> ResumeAsync(
        IServiceProvider provider, Guid session, string decisionJson)
    {
        var bookmarkStore = provider.GetRequiredService<IBookmarkStore>();
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var loggerFactory = provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();

        var bookmarkName = WaitForDocumentDecisionActivity.DecisionBookmarkName(TenantId, session);
        var bookmarks = (await bookmarkStore.FindManyAsync(
            new Elsa.Workflows.Runtime.Filters.BookmarkFilter { Name = bookmarkName }, CancellationToken.None)).ToList();
        var instanceId = bookmarks.Count == 1 ? bookmarks[0].WorkflowInstanceId : string.Empty;

        await DocumentDecisionResumeEndpoint.Resume(
            new DocumentDecisionResumeEndpoint.ResumeRequest(
                SessionId: session,
                TenantId: TenantId,
                DecisionJson: decisionJson,
                Feedback: null,
                DeciderId: null,
                DeciderDisplay: null,
                Channel: "orchestrator",
                RulesReference: "system-default@1"),
            bookmarkStore, runtime, loggerFactory, CancellationToken.None);

        if (string.IsNullOrEmpty(instanceId)) return new Dictionary<string, object>();
        var client = await runtime.CreateClientAsync(instanceId);
        var state = await client.ExportStateAsync();
        return state.Output ?? new Dictionary<string, object>();
    }

    private static List<string?> CapturedEventTypes(CapturingHandler capture) =>
        capture.Bodies
            .Where(b => b.Contains("\"events\""))
            .SelectMany(b => JsonDocument.Parse(b).RootElement.GetProperty("events").EnumerateArray())
            .Select(e => e.GetProperty("eventType").GetString())
            .ToList();

    private static List<DocumentEnvelope> PersistedEnvelopes(CapturingHandler capture) =>
        capture.Bodies
            .Select(b => JsonDocument.Parse(b).RootElement)
            .Where(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("envelopeJson", out _))
            .Select(r => DocumentJson.Deserialize(r.GetProperty("envelopeJson").GetString()!))
            .ToList();

    private static ServiceProvider BuildProvider(CapturingHandler capture, CapturingPublisher publisher)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(publisher);
        services.AddSingleton<IAcceptanceRequestPublisher>(publisher);
        services.AddSingleton<ILifecycleReEntryService, NullLifecycleReEntryService>();

        services.AddElsa(elsa =>
        {
            elsa.AddActivitiesFrom<EmitDocumentEventActivity>();
            elsa.AddWorkflow<DocumentLifecycleWorkflow>();
            elsa.AddWorkflow<StubLlmCallWorkflow>();
            elsa.AddWorkflow<StubDocumentReviewWorkflow>();
            elsa.UseWorkflows(w => w.UseTammaEventPersistence());
        });

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tamma:ApiUrl"] = "http://tamma.test",
                ["Engine:CallbackUrl"] = "http://engine.test",
            })
            .Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(_ => new Tamma.Activities.LlmCall.TammaApiClient(
            new HttpClient(capture) { BaseAddress = null },
            NullLogger<Tamma.Activities.LlmCall.TammaApiClient>.Instance,
            config));

        return services.BuildServiceProvider();
    }

    private static class Script
    {
        public static readonly List<string> Llm = new();
        public static readonly List<string> Reviews = new();
        private static int s_llm;
        private static int s_review;

        public static void Reset() { Llm.Clear(); Reviews.Clear(); s_llm = 0; s_review = 0; }

        public static string NextLlm() => Next(Llm, ref s_llm);
        public static string NextReview() => Next(Reviews, ref s_review);

        private static string Next(List<string> list, ref int index)
        {
            if (list.Count == 0) return "{}";
            var i = Math.Min(Interlocked.Increment(ref index) - 1, list.Count - 1);
            return list[i];
        }
    }

    /// <summary>Stub <c>llm-call</c> — returns the next scripted payload.</summary>
    public class StubLlmCallWorkflow : WorkflowBase
    {
        protected override void Build(IWorkflowBuilder builder)
        {
            builder.DefinitionId = "llm-call";
            builder.Root = new Sequence
            {
                Activities =
                {
                    new SetOutput { Id = "OutSuccess", OutputName = new("success"), OutputValue = new(_ => (object)true) },
                    new SetOutput { Id = "OutResponse", OutputName = new("llmResponse"), OutputValue = new(_ => (object)Script.NextLlm()) },
                },
            };
        }
    }

    /// <summary>Stub <c>document-review</c> — returns the next scripted review.</summary>
    public class StubDocumentReviewWorkflow : WorkflowBase
    {
        protected override void Build(IWorkflowBuilder builder)
        {
            builder.DefinitionId = "document-review";
            builder.Root = new Sequence
            {
                Activities =
                {
                    new SetOutput { Id = "OutSuccess", OutputName = new("success"), OutputValue = new(_ => (object)true) },
                    new SetOutput { Id = "OutReview", OutputName = new("reviewJson"), OutputValue = new(_ => (object)Script.NextReview()) },
                },
            };
        }
    }

    private sealed class CapturingPublisher : IAcceptanceRequestPublisher
    {
        private readonly ConcurrentQueue<AcceptanceRequest> _requests = new();
        public AcceptanceRequest? Last => _requests.LastOrDefault();

        public Task PublishAsync(AcceptanceRequest request, CancellationToken ct)
        {
            _requests.Enqueue(request);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"ok\":true,\"persisted\":1}"),
            };
        }
    }
}

/// <summary>
/// Story 41-1c AC5 (the structural half — FAST, runs in the default gate):
/// <see cref="DocumentLifecycleWorkflow"/> contains no bespoke prose branch. A
/// graph walk of the built workflow finds no node whose Id, Name, or type
/// mentions prose — the prose document type rides the exact same nodes every
/// other type does.
/// </summary>
[TestFixture]
public class ProseLifecycleStructureTests
{
    [Test]
    public void DocumentLifecycleWorkflow_HasNoBespokeProseBranch()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new DocumentLifecycleWorkflow());
        var root = builder.Object.Root;
        root.Should().NotBeNull();

        var offenders = new List<string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<IActivity>();
        stack.Push(root!);
        while (stack.Count > 0)
        {
            var activity = stack.Pop();
            if (activity is null || !seen.Add(activity)) continue;

            var id = activity.Id ?? "";
            var name = activity.GetType().GetProperty("Name")?.GetValue(activity) as string ?? "";
            if (id.Contains("prose", StringComparison.OrdinalIgnoreCase)
                || name.Contains("prose", StringComparison.OrdinalIgnoreCase)
                || activity.GetType().Name.Contains("prose", StringComparison.OrdinalIgnoreCase))
                offenders.Add($"{activity.GetType().Name} (Id='{id}', Name='{name}')");

            foreach (var child in Children(activity)) stack.Push(child);
        }

        seen.Count.Should().BeGreaterThan(10, "the walk must actually traverse the lifecycle graph");
        offenders.Should().BeEmpty(
            "prose must ride the generic lifecycle — a node naming prose is a bespoke branch (41-1c AC5)");
    }

    private static IEnumerable<IActivity> Children(IActivity activity)
    {
        var type = activity.GetType();
        var members = type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Cast<System.Reflection.MemberInfo>()
            .Concat(type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic));
        foreach (var member in members)
        {
            object? value;
            try
            {
                value = member switch
                {
                    System.Reflection.PropertyInfo p when p.CanRead && p.GetIndexParameters().Length == 0 => p.GetValue(activity),
                    System.Reflection.FieldInfo f => f.GetValue(activity),
                    _ => null,
                };
            }
            catch { continue; }

            if (value is IActivity child) yield return child;
            else if (value is System.Collections.IEnumerable en and not string)
                foreach (var item in en) if (item is IActivity nested) yield return nested;
        }
    }
}
