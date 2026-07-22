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
using Tamma.Core.Documents.Policy;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-7 — full-runtime execution coverage for the review producers (Test Plan
/// step 10, scenarios (a)–(g)). Drives the REAL producers through the Elsa runtime
/// with a role-aware SCRIPTED <c>llm-call</c> stub and the durable event drain.
///
/// <para>CI-only: the class name contains <c>Execution</c> so the fast local filter
/// (<c>FullyQualifiedName!~Execution</c>) skips it, and <c>[Explicit]</c> keeps it out
/// of the default gate; the Postgres CI jobs run it.</para>
/// </summary>
[TestFixture]
[Explicit("Full Elsa workflow-runtime integration — runs in the CI Postgres jobs, skipped in the fast local gate")]
public class ReviewProducerExecutionTests
{
    [SetUp]
    public void Reset() => ScriptedResponder.Reset();

    // ── (a) single-reviewer, document subject ──

    [Test]
    public async Task SingleReviewer_DocumentSubject_LegacyReply_YieldsValidatedReviewEnvelope()
    {
        ScriptedResponder.SetRole("architect", LegacyApprove());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture);

        var output = await RunAsync(provider, "review-single-reviewer", SingleInput("architect", DocumentSubject()));

        Bool(output, "success").Should().BeTrue();
        Str(output, "reviewJson").Should().Contain("\"decision\"");
        Str(output, "reviewDocumentId").Should().NotBeNullOrEmpty();

        var types = CapturedTypes(capture);
        types.Should().Contain(DocumentEvents.ProducedSuccess);
        types.Should().Contain(DocumentEvents.ValidatedSuccess);
        HasReviewDocumentTypeTag(capture).Should().BeTrue("the DOCUMENT.* events tag documentType=review");
    }

    // ── (b) single-reviewer, diff subject ──

    [Test]
    public async Task SingleReviewer_DiffSubject_CanonicalReply_YieldsDiffReview()
    {
        ScriptedResponder.SetRole("senior_developer", CanonicalDiffApprove());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture);

        var output = await RunAsync(provider, "review-single-reviewer", SingleInput("senior_developer", DiffSubject()));

        Bool(output, "success").Should().BeTrue();
        Str(output, "reviewJson").Should().Contain("\"kind\":\"diff\"");
    }

    // ── (c) invalid → repair → valid within bounds ──

    [Test]
    public async Task SingleReviewer_InvalidThenValid_RepairsWithinBounds()
    {
        ScriptedResponder.SetRole("architect", "garbage not a review", CanonicalApprove());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture);

        var output = await RunAsync(provider, "review-single-reviewer", SingleInput("architect", DocumentSubject()));

        Bool(output, "success").Should().BeTrue("the second (valid) attempt succeeds within the repair bound");
    }

    // ── (d) always-garbage → validation-exhausted, NO review envelope ──

    [Test]
    public async Task SingleReviewer_AlwaysGarbage_ExhaustsValidation_NoReviewEnvelope()
    {
        ScriptedResponder.SetRole("architect", "garbage", "still garbage", "more garbage", "and more");

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture);

        var output = await RunAsync(provider, "review-single-reviewer", SingleInput("architect", DocumentSubject()));

        Bool(output, "success").Should().BeFalse();
        Str(output, "failureKind").Should().Be("validation-exhausted");
        CapturedTypes(capture).Should().Contain(DocumentEvents.ValidatedFailed);
        CapturedTypes(capture).Should().NotContain(DocumentEvents.ValidatedSuccess,
            "no valid review envelope may exist anywhere in the stream");
    }

    // ── (e) panel of 3 → 3 members + aggregate with AggregatedFrom ──

    [Test]
    public async Task Panel_ThreeApprovingRoles_AggregatesWithAggregatedFrom()
    {
        ScriptedResponder.SetRole("architect", CanonicalApprove());
        ScriptedResponder.SetRole("developer", CanonicalApprove());
        ScriptedResponder.SetRole("security", CanonicalApprove());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture);

        var output = await RunAsync(provider, "review-panel", PanelInput(new[] { "architect", "developer", "security" }, "unanimous"));

        Bool(output, "success").Should().BeTrue();
        Str(output, "reviewJson").Should().Contain("\"aggregatedFrom\"");
        var members = JsonDocument.Parse(Str(output, "memberReviewsJson")).RootElement;
        members.GetArrayLength().Should().Be(3);

        var types = CapturedTypes(capture);
        types.Should().Contain(DocumentEvents.ReviewPanelStarted);
        types.Should().Contain(DocumentEvents.ReviewPanelCompleted);
    }

    // ── (f) panel undecidable → PANEL_UNDECIDABLE, no aggregate ──

    [Test]
    public async Task Panel_OneMemberExhausted_UnderFullRosterMinimum_IsUndecidable()
    {
        ScriptedResponder.SetRole("architect", CanonicalApprove());
        ScriptedResponder.SetRole("developer", CanonicalApprove());
        ScriptedResponder.SetRole("security", "garbage", "garbage", "garbage", "garbage");

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture);

        var output = await RunAsync(provider, "review-panel", PanelInput(new[] { "architect", "developer", "security" }, "unanimous"));

        Bool(output, "success").Should().BeFalse();
        Str(output, "undecidableReason").Should().Be("BelowQuorum");
        CapturedTypes(capture).Should().Contain(DocumentEvents.ReviewPanelUndecidable);
    }

    // ── (g) router honors the 39-6 D10 contract in both modes ──

    [Test]
    public async Task Router_SingleMode_HonorsContract()
    {
        ScriptedResponder.SetRole("architect", CanonicalApprove());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture);

        var rules = AcceptanceRulesJson.Serialize(AcceptanceDefaults.Rules); // single architect
        var output = await RunAsync(provider, "document-review", RouterInput(rules));

        Bool(output, "success").Should().BeTrue();
        Str(output, "reviewJson").Should().NotBeNullOrEmpty();
        Str(output, "reviewDocumentId").Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task Router_PanelMode_HonorsContract()
    {
        ScriptedResponder.SetRole("architect", CanonicalApprove());
        ScriptedResponder.SetRole("developer", CanonicalApprove());
        ScriptedResponder.SetRole("security", CanonicalApprove());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture);

        var rules = AcceptanceRulesJson.Serialize(ThreeRolePanelRules());
        var output = await RunAsync(provider, "document-review", RouterInput(rules));

        Bool(output, "success").Should().BeTrue();
        Str(output, "memberReviewsJson").Should().Contain("reviewDocumentId");
    }

    // ════════════════════════════════════════════════════════════════════
    // Harness
    // ════════════════════════════════════════════════════════════════════

    private static async Task<IDictionary<string, object>> RunAsync(
        IServiceProvider provider, string definitionId, Dictionary<string, object> input)
    {
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync();
        await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId(definitionId),
            Input = input,
        });
        var state = await client.ExportStateAsync();
        return state.Output ?? new Dictionary<string, object>();
    }

    private static Dictionary<string, object> SingleInput(string role, string subjectJson) => new()
    {
        ["reviewerRole"] = role,
        ["subjectJson"] = subjectJson,
        ["variablesJson"] = "{\"planJson\":\"x\"}",
        ["issueId"] = "issue-1",
        ["correlationId"] = "corr-1",
        ["tenantId"] = "",
    };

    private static Dictionary<string, object> PanelInput(string[] roles, string rule) => new()
    {
        ["subjectJson"] = DocumentSubject(),
        ["variablesJson"] = "{\"planJson\":\"x\"}",
        ["issueId"] = "issue-1",
        ["correlationId"] = "corr-1",
        ["tenantId"] = "",
        ["acceptanceRulesJson"] = AcceptanceRulesJson.Serialize(PanelRules(roles, rule)),
        ["panelDecisionRule"] = rule,
    };

    private static Dictionary<string, object> RouterInput(string rulesJson) => new()
    {
        ["documentJson"] = "{\"id\":\"0192a8b0-1111-7abc-8def-000000000001\",\"payload\":{\"summary\":\"x\"}}",
        ["documentType"] = "plan",
        ["issueId"] = "issue-1",
        ["correlationId"] = "corr-1",
        ["tenantId"] = "",
        ["acceptanceRulesJson"] = rulesJson,
    };

    private static AcceptanceRules PanelRules(string[] roles, string rule) => AcceptanceDefaults.Rules with
    {
        ReviewerSelection = new ReviewerSelection(
            Mode: ReviewerMode.Panel,
            ReviewerRole: null,
            PanelRoles: roles,
            Quorum: null,
            DecisionRule: rule == "majority" ? ReviewDecisionRule.Majority : ReviewDecisionRule.Unanimous),
    };

    private static AcceptanceRules ThreeRolePanelRules() => PanelRules(new[] { "architect", "developer", "security" }, "unanimous");

    private static string DocumentSubject() =>
        "{\"kind\":\"document\",\"documentId\":\"0192a8b0-1111-7abc-8def-000000000001\",\"documentType\":\"plan\"}";

    private static string DiffSubject() =>
        "{\"kind\":\"diff\",\"repository\":\"meywd/tamma\",\"prNumber\":42}";

    private static string LegacyApprove() =>
        "{\"issues\":[],\"verdict\":{\"decision\":\"APPROVE\",\"summary\":\"lgtm\",\"blockingIssues\":[]}}";

    private static string CanonicalApprove() =>
        "{\"subject\":{\"kind\":\"document\",\"documentId\":\"0192a8b0-1111-7abc-8def-000000000001\",\"documentType\":\"plan\"}," +
        "\"decision\":\"approve\",\"summary\":\"looks good\",\"issues\":[]}";

    private static string CanonicalDiffApprove() =>
        "{\"subject\":{\"kind\":\"diff\",\"repository\":\"meywd/tamma\",\"prNumber\":42}," +
        "\"decision\":\"approve\",\"summary\":\"clean\",\"issues\":[]}";

    private static bool Bool(IDictionary<string, object> o, string key)
        => o.TryGetValue(key, out var v) && (v is true || (v is string s && bool.TryParse(s, out var b) && b));

    private static string Str(IDictionary<string, object> o, string key)
        => o.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private static List<string?> CapturedTypes(CapturingHandler capture) =>
        CapturedEvents(capture).Select(e => e.GetProperty("eventType").GetString()).ToList();

    private static List<JsonElement> CapturedEvents(CapturingHandler capture) =>
        capture.Bodies
            .SelectMany(b => JsonDocument.Parse(b).RootElement.GetProperty("events").EnumerateArray())
            .ToList();

    private static bool HasReviewDocumentTypeTag(CapturingHandler capture) =>
        CapturedEvents(capture).Any(e =>
            e.TryGetProperty("tags", out var t) &&
            t.TryGetProperty("documentType", out var dt) &&
            dt.GetString() == "review");

    private static ServiceProvider BuildProvider(CapturingHandler capture)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddElsa(elsa =>
        {
            elsa.AddActivitiesFrom<EmitDocumentEventActivity>();
            elsa.AddWorkflow<DocumentReviewWorkflow>();
            elsa.AddWorkflow<SingleReviewerWorkflow>();
            elsa.AddWorkflow<PanelReviewWorkflow>();
            elsa.AddWorkflow<StubLlmCallWorkflow>();
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

    // ── role-aware scripted stub llm-call ──

    private static class ScriptedResponder
    {
        private static readonly Dictionary<string, List<string>> s_scripts = new();
        private static readonly Dictionary<string, int> s_counters = new();

        public static void Reset() { s_scripts.Clear(); s_counters.Clear(); }

        public static void SetRole(string role, params string[] replies) => s_scripts[role] = replies.ToList();

        public static string ForRole(string role)
        {
            if (!s_scripts.TryGetValue(role, out var list) || list.Count == 0) return "{}";
            var i = s_counters.TryGetValue(role, out var c) ? c : 0;
            s_counters[role] = i + 1;
            return list[Math.Min(i, list.Count - 1)];
        }
    }

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
                    new SetOutput
                    {
                        Id = "OutResponse", OutputName = new("llmResponse"),
                        OutputValue = new(ctx => (object)ScriptedResponder.ForRole(ctx.GetInput<string>("agentRole") ?? "")),
                    },
                },
            };
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
