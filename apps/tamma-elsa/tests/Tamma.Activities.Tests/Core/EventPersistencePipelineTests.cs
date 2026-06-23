using System.Net;
using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Pipelines.ActivityExecution;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.Core;

namespace Tamma.Activities.Tests.Core;

/// <summary>
/// End-to-end pipeline coverage (C1). Runs a REAL <see cref="TammaActivity"/>
/// through the Elsa activity-execution pipeline with the engine's
/// event-persistence drain installed and asserts BOTH:
/// <list type="number">
///   <item>the activity actually executed (its side effect happened), and</item>
///   <item>its emitted event reached the append channel (a fake
///         <c>TammaApiClient</c> captured the POST body).</item>
/// </list>
///
/// <para>This is the test the review demanded — the one that catches the
/// pipeline-registration bug. The original wiring used
/// <c>app.Services.ConfigureDefaultActivityExecutionPipeline(p =&gt; p.Use(...))</c>.
/// In Elsa 3.5.3 that calls <c>IActivityExecutionPipeline.Setup</c>, which
/// builds a FRESH pipeline from only the supplied middleware and discards the
/// framework defaults (the activity invoker). It also mutates only the
/// ROOT-scope pipeline instance, while the service is registered <b>scoped</b>
/// and rebuilt per run — so the drain is installed on a pipeline nobody uses
/// and NEVER fires. Either way the audit trail is silently never persisted.
/// <see cref="UsingBrokenRegistration_DrainNeverFires_NothingPersisted"/> pins
/// that broken behaviour; <see cref="UsingFixedRegistration_ActivityRuns_AndEventReachesAppendChannel"/>
/// pins the fix (drain APPENDED to the full default pipeline at build time).</para>
/// </summary>
[TestFixture]
public class EventPersistencePipelineTests
{
    /// <summary>
    /// THE C1 FIX. With <see cref="EventPersistencePipelineExtensions.UseTammaEventPersistence"/>
    /// the activity executes AND its event reaches the append channel.
    /// </summary>
    [Test]
    public async Task UsingFixedRegistration_ActivityRuns_AndEventReachesAppendChannel()
    {
        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, useBroken: false);

        SideEffectActivity.Reset();
        await RunActivityAsync(provider);

        SideEffectActivity.Ran.Should().BeTrue(
            "the fixed registration appends the drain to the default pipeline, so the activity invoker still runs the activity");

        capture.Bodies.Should().NotBeEmpty("the activity's emitted events must reach POST /api/engine/events");
        var allTypes = capture.Bodies
            .SelectMany(b => JsonDocument.Parse(b).RootElement.GetProperty("events").EnumerateArray())
            .Select(e => e.GetProperty("eventType").GetString())
            .ToList();
        allTypes.Should().Contain("TEST.SIDE_EFFECT.COMPLETED",
            "the activity's COMPLETED event must land in domain_events via the drain");
    }

    /// <summary>
    /// Pins the C1 bug: the ORIGINAL registration
    /// (<c>app.Services.ConfigureDefaultActivityExecutionPipeline(p =&gt; p.Use(...))</c>)
    /// mutates only the ROOT-scope <see cref="IActivityExecutionPipeline"/>
    /// instance via <c>Setup</c>. But that service is registered <b>scoped</b>
    /// and rebuilt per workflow run from the feature delegate, so the real
    /// per-run pipeline never sees the drain. Net effect: the activity still
    /// runs (the untouched default per-scope pipeline keeps its invoker) but
    /// the drain NEVER fires — the audit trail is silently never persisted.
    /// </summary>
    [Test]
    public async Task UsingBrokenRegistration_DrainNeverFires_NothingPersisted()
    {
        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, useBroken: true);

        SideEffectActivity.Reset();
        await RunActivityAsync(provider);

        // The activity itself still runs (the default per-scope pipeline keeps
        // its invoker) — but that only makes the silent data-loss worse:
        SideEffectActivity.Ran.Should().BeTrue(
            "the root-scope Setup mutation never reaches the scoped per-run pipeline, so the default invoker is untouched and the activity runs");
        capture.Bodies.Should().BeEmpty(
            "the drain middleware was installed on an unused root-scope pipeline instance, so it never fires and NO event is appended — this is the C1 data-loss bug");
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static async Task RunActivityAsync(IServiceProvider rootProvider)
    {
        using var scope = rootProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
        await runner.RunAsync(new SideEffectActivity());
    }

    private static ServiceProvider BuildProvider(CapturingHandler capture, bool useBroken)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddElsa(elsa =>
        {
            elsa.AddActivity<SideEffectActivity>();
            if (!useBroken)
                elsa.UseWorkflows(w => w.UseTammaEventPersistence());
        });

        // A real TammaApiClient over a capturing handler — the drain resolves
        // TammaApiClient from the activity scope, projects the events, and POSTs.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Tamma:ApiUrl"] = "http://tamma.test" })
            .Build();
        services.AddSingleton(_ => new Tamma.Activities.LlmCall.TammaApiClient(
            new HttpClient(capture) { BaseAddress = null },
            NullLogger<Tamma.Activities.LlmCall.TammaApiClient>.Instance,
            config));

        var provider = services.BuildServiceProvider();

        if (useBroken)
        {
            // Reproduce the ORIGINAL Program.cs registration verbatim: this
            // calls IActivityExecutionPipeline.Setup, replacing the default
            // pipeline with ONLY the drain middleware (no activity invoker).
            provider.ConfigureDefaultActivityExecutionPipeline(pipeline =>
                pipeline.Use(EventPersistenceMiddleware.Create()));
        }

        return provider;
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
