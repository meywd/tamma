using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Core.Actions;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// Story 43-8 (AC4) — BIDIRECTIONAL REFLECTION SWEEP over the engine's mediation
/// client, <see cref="TammaApiClient"/>. This is the only class through which the
/// Elsa engine reaches the outside world, so every <c>effect:*</c> member that a
/// workflow can cause is either one of its methods or is explicitly declared to be
/// performed somewhere else.
///
/// <para><b>code → catalog:</b> every public <c>Task</c>-returning method on the
/// client is either mapped to an <see cref="ExternalEffect"/> by
/// <see cref="EffectPerformingSites"/> or listed in the shrink-only, count-pinned
/// <see cref="KnownNonEffectClientMethods"/> baseline. A NEW mediation method
/// therefore fails the build until someone decides whether it is a governed
/// effect.</para>
///
/// <para><b>catalog → code:</b> every <see cref="ExternalEffect"/> member must be
/// classified in <see cref="EffectPerformingSites"/>, and a client-backed entry's
/// method name is resolved BY REFLECTION — renaming or deleting
/// <c>CreateBranchAsync</c> fails here. The failure message for an unclassified
/// member leads with <b>delete the member</b>, because the reflex of inventing a
/// call site to satisfy the test manufactures a phantom capability, which is worse
/// than the gap.</para>
///
/// <para><b>WHY A TABLE AND NOT ONLY THE ATTRIBUTE.</b>
/// <see cref="PerformsEffectAttribute"/> is the authoring shape 43-9 consumes, and
/// this sweep honours it (<see cref="EveryAttributedMethod_AgreesWithTheTable"/>):
/// an attributed method must name the effect the table maps it to, so attributes
/// can be applied incrementally without weakening anything. Applying the 17
/// attributes to <c>TammaApiClient</c> itself is a source edit to a file another
/// in-flight story is extending; the mapping is therefore carried here, where it is
/// checked by reflection today, and each entry GRADUATES to an attribute without
/// changing what is asserted.</para>
///
/// <para><b>WHAT THIS SWEEP CANNOT SEE</b> — stated so a green run is not read as a
/// stronger guarantee than it is:</para>
/// <list type="bullet">
///   <item><b>It binds a SITE, not an EFFECT</b> (43-8 AC10(a)/D6). Nothing checks
///   that <c>CreateBranchAsync</c> creates a branch. A second capability grown
///   inside an already-mapped method is invisible to every harness in this
///   epic — there is no <c>SiteKey</c>-style structural check available on the
///   method plane, because a C# method has no route pattern to compare
///   against.</item>
///   <item><b>It sees the client, not its callers.</b> An activity that reaches an
///   external system WITHOUT going through <see cref="TammaApiClient"/> (a raw
///   <c>HttpClient</c>, a shell-out) performs an ungoverned effect and appears
///   nowhere here.</item>
///   <item><b><c>effect:mcp.tool.invoke</c> has no drift signal at all.</b> Adding
///   an MCP server, or a tool on an existing server, changes nothing observable —
///   the member is one coarse row by construction.</item>
/// </list>
/// </summary>
[TestFixture]
public class MediationClientEffectSweepTests
{
    // ====================================================================
    // catalog → code: every effect member's performing site
    // ====================================================================

    /// <summary>Where an <see cref="ExternalEffect"/> is performed.</summary>
    private enum SiteKind
    {
        /// <summary>A public method on <see cref="TammaApiClient"/> (the engine mediation seam).</summary>
        MediationClient,

        /// <summary>An HTTP route reached by a caller that is NOT the engine (admin UI, dashboard).</summary>
        RouteOnly,

        /// <summary>An in-process site with no HTTP hop at all (a tool, a workflow branch).</summary>
        InProcess,
    }

    /// <summary>One effect member's declared performing site.</summary>
    /// <param name="Kind">Which plane performs it.</param>
    /// <param name="ClientMethod">
    /// For <see cref="SiteKind.MediationClient"/>: the method name on
    /// <see cref="TammaApiClient"/>, RESOLVED BY REFLECTION — a rename fails the build.
    /// Null for every other kind.
    /// </param>
    /// <param name="Justification">Why the site is where it is.</param>
    private sealed record EffectSite(SiteKind Kind, string? ClientMethod, string Justification);

    /// <summary>
    /// THE catalog→code map. Totality is asserted against
    /// <c>Enum.GetValues&lt;ExternalEffect&gt;()</c>, so a new effect member fails
    /// the build until someone says where it is performed.
    /// </summary>
    private static readonly IReadOnlyDictionary<ExternalEffect, EffectSite> EffectPerformingSites =
        new Dictionary<ExternalEffect, EffectSite>
        {
            // ── The 17 mutating mediation-client methods (AC4's enumerated set) ──
            [ExternalEffect.LlmCall] = new(SiteKind.MediationClient, "CallLlmAsync",
                "the engine's only path to a model call"),
            [ExternalEffect.GitBranchCreate] = new(SiteKind.MediationClient, "CreateBranchAsync",
                "engine-mediated git write"),
            [ExternalEffect.GitBranchDelete] = new(SiteKind.MediationClient, "DeleteBranchAsync",
                "engine-mediated git write"),
            [ExternalEffect.GitPullRequestCreate] = new(SiteKind.MediationClient, "CreatePullRequestAsync",
                "engine-mediated git write"),
            [ExternalEffect.GitPullRequestMerge] = new(SiteKind.MediationClient, "MergePullRequestAsync",
                "engine-mediated git write"),
            [ExternalEffect.GitReleaseCreate] = new(SiteKind.MediationClient, "CreateReleaseAsync",
                "engine-mediated git write"),
            [ExternalEffect.GitIssuePatch] = new(SiteKind.MediationClient, "UpdateIssueStatusAsync",
                "engine-mediated issue write"),
            [ExternalEffect.JiraTicketPatch] = new(SiteKind.MediationClient, "UpdateJiraTicketAsync",
                "engine-mediated issue write"),
            [ExternalEffect.CiTestsTrigger] = new(SiteKind.MediationClient, "TriggerTestsAsync",
                "engine-mediated CI trigger"),
            [ExternalEffect.AgentDispatchRun] = new(SiteKind.MediationClient, "DispatchAgentRunAsync",
                "engine-mediated external agent dispatch"),
            [ExternalEffect.NotifySlackQueue] = new(SiteKind.MediationClient, "QueueSlackNotificationAsync",
                "engine-mediated outbound comms"),
            [ExternalEffect.NotifyEmailSend] = new(SiteKind.MediationClient, "SendEmailAsync",
                "engine-mediated outbound comms"),
            [ExternalEffect.EngineEventsAppend] = new(SiteKind.MediationClient, "AppendEventsAsync",
                "engine-mediated event-store write"),
            [ExternalEffect.EnginePlatformEventsAppend] = new(SiteKind.MediationClient, "AppendPlatformEventsAsync",
                "engine-mediated event-store write"),
            [ExternalEffect.EngineDocumentPersist] = new(SiteKind.MediationClient, "PersistDocumentAsync",
                "engine-mediated document write"),
            [ExternalEffect.EngineDocumentSetStatus] = new(SiteKind.MediationClient, "SetDocumentStatusAsync",
                "engine-mediated document write"),
            [ExternalEffect.EngineChannelOutboxEnqueue] = new(SiteKind.MediationClient, "PostChannelOutboxAsync",
                "engine-mediated channel write"),

            // ── Route-only: performed by a human-facing caller, never by the engine ──
            [ExternalEffect.SecretReveal] = new(SiteKind.RouteOnly, null,
                "GET /api/v1/secrets/reveal/{token}; informational-only and never enforceable, so no "
                + "mediation-client method exists to attribute"),
            [ExternalEffect.McpToolInvoke] = new(SiteKind.RouteOnly, null,
                "the C# surface is the KB proxy start/stop pair; invocation itself happens in the "
                + "TypeScript intelligence sidecar and has NO drift signal in this repo"),
            [ExternalEffect.ScheduleCreate] = new(SiteKind.RouteOnly, null,
                "admin-only scheduled-trigger route (Story 41-30); reached from the dashboard, not the engine"),
            [ExternalEffect.ScheduleUpdate] = new(SiteKind.RouteOnly, null,
                "admin-only scheduled-trigger route (Story 41-30); reached from the dashboard, not the engine"),
            [ExternalEffect.ScheduleDelete] = new(SiteKind.RouteOnly, null,
                "admin-only scheduled-trigger route (Story 41-30); reached from the dashboard, not the engine"),
            [ExternalEffect.TrackerProjectCreate] = new(SiteKind.RouteOnly, null,
                "native tracker route (Story 44-2); a tracker write is reached from the UI, not the mediation client"),
            [ExternalEffect.TrackerProjectUpdate] = new(SiteKind.RouteOnly, null,
                "native tracker route (Story 44-2); a tracker write is reached from the UI, not the mediation client"),
            [ExternalEffect.TrackerProjectDelete] = new(SiteKind.RouteOnly, null,
                "native tracker route (Story 44-2); a tracker write is reached from the UI, not the mediation client"),
            [ExternalEffect.TrackerWorkItemCreate] = new(SiteKind.RouteOnly, null,
                "native tracker route (Story 44-2); a tracker write is reached from the UI, not the mediation client"),
            [ExternalEffect.TrackerWorkItemUpdate] = new(SiteKind.RouteOnly, null,
                "native tracker route (Story 44-2); a tracker write is reached from the UI, not the mediation client"),
            [ExternalEffect.TrackerWorkItemDelete] = new(SiteKind.RouteOnly, null,
                "native tracker route (Story 44-2); a tracker write is reached from the UI, not the mediation client"),
            [ExternalEffect.TrackerWorkItemAssign] = new(SiteKind.RouteOnly, null,
                "native tracker route (Story 44-2); a tracker write is reached from the UI, not the mediation client"),
            [ExternalEffect.TrackerWorkItemSetStatus] = new(SiteKind.RouteOnly, null,
                "native tracker route (Story 44-2); a tracker write is reached from the UI, not the mediation client"),
            [ExternalEffect.TrackerPreferencesSet] = new(SiteKind.RouteOnly, null,
                "native tracker route (Story 44-2); tenant tracker configuration written from the UI"),
            [ExternalEffect.TrackerPreferencesDelete] = new(SiteKind.RouteOnly, null,
                "native tracker route (Story 44-2); tenant tracker configuration written from the UI"),

            // ── In-process: no HTTP hop, so no route sweep can ever see them ──
            [ExternalEffect.ProcessSpawn] = new(SiteKind.InProcess, null,
                "ShellExecuteTool's ProcessStartInfo, inside the tool loop — invisible to any route sweep"),
            [ExternalEffect.DeployPromoteProd] = new(SiteKind.InProcess, null,
                "DeploymentPipelineWorkflow's production stage transition; the deploy itself runs inside "
                + "the LLM tool loop, so this gates the TRANSITION only"),
            [ExternalEffect.DeployRollback] = new(SiteKind.InProcess, null,
                "DeploymentPipelineWorkflow's RollbackProduction branch; same tool-loop limitation as promote"),
        };

    // ====================================================================
    // code → catalog: the read-only / non-effect client methods
    // ====================================================================

    /// <summary>
    /// Every public <c>Task</c>-returning <see cref="TammaApiClient"/> method that
    /// performs NO catalogued external effect, with the reason. A RATCHET: an entry
    /// that now maps to an effect fails as stale, justifications are keyword-classified,
    /// and the count is pinned so an ADDITION fails the build.
    ///
    /// <para>Note the five entries classified
    /// <c>internal-session-lifecycle-no-external-effect</c>: they DO mutate provider
    /// session and telemetry state. They are not read-only, and calling them that
    /// would make this list the place mutating methods hide. Each says exactly what
    /// it mutates and why that is not an external effect.</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> KnownNonEffectClientMethods =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ResolveAgentAsync"] = "read-only: resolves the agent config for a role",
            ["ResolveForPhaseAsync"] = "read-only: resolves the agent config for a phase",
            ["GetPullRequestCommentsAsync"] = "read-only: reads PR review comments",
            ["GetCommitsAsync"] = "read-only: reads commits",
            ["GetFileChangesAsync"] = "read-only: reads a diff",
            ["GetBuildStatusAsync"] = "read-only: polls CI status",
            ["GetJiraTicketAsync"] = "read-only: reads a Jira ticket",
            ["DiscoverAgentRunAsync"] = "read-only: correlates a dispatched run to its provider run id",
            ["GetAgentRunAsync"] = "read-only: polls an agent run's status",
            ["CollectAgentResultsAsync"] = "read-only: reads a finished agent run's artifacts",
            ["ResolveAgentInstallationIdAsync"] = "read-only: resolves a platform app installation id",
            ["GetProviderHealthAsync"] = "read-only: reads provider health",
            ["GetBudgetAsync"] = "read-only: reads the remaining budget",

            // NOT read-only — and said so out loud (43-8 implementation-plan Correction 3).
            ["RecordProviderFailureAsync"] =
                "internal-session-lifecycle-no-external-effect: writes the provider circuit-breaker "
                + "counter inside Tamma; nothing outside Tamma observes it",
            ["RecordProviderSuccessAsync"] =
                "internal-session-lifecycle-no-external-effect: clears the provider circuit-breaker "
                + "counter inside Tamma; nothing outside Tamma observes it",
            ["RecordDiagnosticsAsync"] =
                "internal-session-lifecycle-no-external-effect: appends Tamma's own diagnostics rows",
            ["CreateProviderAsync"] =
                "internal-session-lifecycle-no-external-effect: opens a provider SESSION handle; the "
                + "consequential act is the llm.call that follows, which IS catalogued",
            ["ExecuteProviderAsync"] =
                "internal-session-lifecycle-no-external-effect: drives an already-open provider session; "
                + "the model invocation it performs is governed as effect:llm.call at CallLlmAsync",
            ["DisposeProviderAsync"] =
                "internal-session-lifecycle-no-external-effect: releases a provider session handle",
        };

    /// <summary>
    /// Keyword classifier (the <c>ContractBindingTests.UniversalPin_*</c> idiom) — a
    /// non-empty string is not a justification.
    /// </summary>
    private static readonly string[] JustificationKeywords =
    [
        "read-only",
        "internal-session-lifecycle-no-external-effect",
    ];

    // ====================================================================
    // Discovery + the classifier, as pure functions over their inputs
    // ====================================================================

    /// <summary>Every public instance <c>Task</c>-returning method declared on a client type.</summary>
    internal static IReadOnlyList<MethodInfo> ClientMethods(Type clientType) =>
        clientType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && typeof(Task).IsAssignableFrom(m.ReturnType))
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// THE SWEEP. Pure over its inputs so the discrimination tests below drive the
    /// REAL classifier with synthetic input rather than a re-implementation of it.
    /// </summary>
    private static List<string> Classify(
        Type clientType,
        IReadOnlyList<ExternalEffect> effects,
        IReadOnlyDictionary<ExternalEffect, EffectSite> sites,
        IReadOnlyDictionary<string, string> nonEffectMethods)
    {
        var problems = new List<string>();
        var methods = ClientMethods(clientType);
        var methodsByName = methods.ToLookup(m => m.Name, StringComparer.Ordinal);

        // catalog → code (a): totality. A member with no declared site fails, and the
        // message leads with DELETE — inventing a site manufactures a phantom capability.
        foreach (var effect in effects)
        {
            if (sites.ContainsKey(effect)) continue;
            problems.Add(
                $"  effect:{effect.ToWire()}: no performing site is declared. If nothing performs it, "
                + "DELETE the ExternalEffect member and its catalog descriptor — a catalogued action "
                + "with no site renders in the admin UI as governed and governs nothing. If something "
                + "does perform it, add an EffectPerformingSites entry naming the site.");
        }

        // catalog → code (b): a declared client method must actually exist.
        foreach (var (effect, site) in sites)
        {
            if (!effects.Contains(effect))
            {
                problems.Add(
                    $"  effect:{effect.ToWire()}: EffectPerformingSites entry for an effect member that "
                    + "no longer exists — delete the entry.");
                continue;
            }

            if (site.Kind == SiteKind.MediationClient)
            {
                if (string.IsNullOrWhiteSpace(site.ClientMethod))
                    problems.Add($"  effect:{effect.ToWire()}: MediationClient site with no method name.");
                else if (!methodsByName[site.ClientMethod!].Any())
                    problems.Add(
                        $"  effect:{effect.ToWire()}: declares mediation-client method "
                        + $"'{site.ClientMethod}', which does not exist on {clientType.Name} — it was "
                        + "renamed or deleted. Update the entry, or DELETE the effect member if the "
                        + "capability is gone.");
            }
            else if (site.ClientMethod is not null)
            {
                problems.Add(
                    $"  effect:{effect.ToWire()}: {site.Kind} sites must not name a client method.");
            }

            if (string.IsNullOrWhiteSpace(site.Justification))
                problems.Add($"  effect:{effect.ToWire()}: empty site justification.");
        }

        // catalog → code (c): two effects may not claim the same method.
        var duplicated = sites.Values
            .Where(s => s.ClientMethod is not null)
            .GroupBy(s => s.ClientMethod!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);
        problems.AddRange(duplicated.Select(g =>
            $"  client method '{g.Key}' is claimed by {g.Count()} effect members — one site, one effect."));

        // code → catalog: every client method is mapped or baselined.
        var mapped = sites.Values
            .Where(s => s.ClientMethod is not null)
            .Select(s => s.ClientMethod!)
            .ToHashSet(StringComparer.Ordinal);

        problems.AddRange(methods
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !mapped.Contains(name) && !nonEffectMethods.ContainsKey(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(name =>
                $"  {clientType.Name}.{name}: a new mediation method with no governance decision. "
                + "Either map it to an ExternalEffect in EffectPerformingSites (adding the member and "
                + "its catalog descriptor if it is a new capability), or add a justified "
                + "KnownNonEffectClientMethods entry AND bump the count pin in the same commit."));

        // Staleness both ways.
        problems.AddRange(nonEffectMethods.Keys
            .Where(name => mapped.Contains(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(name =>
                $"  {name}: listed as non-effect but now mapped to an effect — DELETE its "
                + "KnownNonEffectClientMethods entry (the ratchet only turns one way)."));

        problems.AddRange(nonEffectMethods.Keys
            .Where(name => !methodsByName[name].Any())
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(name =>
                $"  {name}: listed in KnownNonEffectClientMethods but no such method exists on "
                + $"{clientType.Name} — DELETE the entry."));

        return problems;
    }

    // ====================================================================
    // The sweep against reality
    // ====================================================================

    [Test]
    public void The_sweep_actually_sees_the_client_surface()
    {
        // ANTI-NO-OP TRIPWIRE: if the reflection filter ever stops matching (a base
        // class extraction, an interface split, ValueTask), every assertion below
        // would pass vacuously.
        ClientMethods(typeof(TammaApiClient)).Should().HaveCountGreaterThan(25,
            "TammaApiClient exposes dozens of public Task-returning methods; a tiny result means the "
            + "discovery filter broke, not that the client shrank");
    }

    [Test]
    public void EveryEffectMember_AndEveryClientMethod_IsClassified()
    {
        var problems = Classify(
            typeof(TammaApiClient),
            Enum.GetValues<ExternalEffect>(),
            EffectPerformingSites,
            KnownNonEffectClientMethods);

        problems.Should().BeEmpty(
            "the mediation seam and the effect:* plane must agree in BOTH directions:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryEffectKey_ResolvesInTheCatalog()
    {
        var unresolved = Enum.GetValues<ExternalEffect>()
            .Select(e => new ActionKey(ActionNamespace.Effect, e.ToWire()))
            .Where(k => !ActionCatalog.ByKey.ContainsKey(k))
            .Select(k => $"  {k.ToWire()}")
            .ToList();

        unresolved.Should().BeEmpty(
            "every effect member this sweep binds must be a catalogued action:"
            + Environment.NewLine + string.Join(Environment.NewLine, unresolved));
    }

    [Test]
    public void EveryAttributedMethod_AgreesWithTheTable()
    {
        // The GRADUATION path. [PerformsEffect] is the shape Story 43-9 consumes; as
        // attributes are applied to TammaApiClient they must name the effect the
        // table already maps — so the two can never disagree, and the table can be
        // retired member by member.
        var problems = new List<string>();

        foreach (var method in ClientMethods(typeof(TammaApiClient)))
        {
            var attribute = method.GetCustomAttribute<PerformsEffectAttribute>(inherit: false);
            if (attribute is null) continue;

            if (!EffectPerformingSites.TryGetValue(attribute.Effect, out var site))
            {
                problems.Add(
                    $"  {method.Name}: [PerformsEffect({attribute.Effect})] names an effect with no "
                    + "EffectPerformingSites entry.");
                continue;
            }

            if (site.ClientMethod != method.Name)
                problems.Add(
                    $"  {method.Name}: [PerformsEffect({attribute.Effect})] disagrees with the table, "
                    + $"which maps that effect to '{site.ClientMethod ?? "(no client method)"}'.");
        }

        problems.Should().BeEmpty(
            "an applied [PerformsEffect] must agree with the declared performing site:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void MediationClientSites_countIsPinned()
    {
        EffectPerformingSites.Values.Count(s => s.Kind == SiteKind.MediationClient)
            .Should().Be(17,
                "AC4 enumerates exactly 17 mutating TammaApiClient methods. A change here means a "
                + "mediation method became (or stopped being) a governed effect — that is a "
                + "governance decision, not a refactor.");
    }

    [Test]
    public void NonEffectClientMethods_countIsPinned()
    {
        // (c) of the three ratchet properties. Without it an ADDITION is undetectable.
        KnownNonEffectClientMethods.Should().HaveCount(19,
            "36 public Task-returning methods − 17 effect-performing = 19. If this fails because a new "
            + "mediation method was added, that is the ratchet working: decide whether it is a "
            + "governed effect before bumping the number.");
    }

    [Test]
    public void NonEffectClientMethods_justificationsAreClassified()
    {
        var unclassified = KnownNonEffectClientMethods
            .Where(kv => string.IsNullOrWhiteSpace(kv.Value)
                         || !JustificationKeywords.Any(k => kv.Value.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => $"  {kv.Key}: {kv.Value}")
            .ToList();

        unclassified.Should().BeEmpty(
            "every KnownNonEffectClientMethods justification must classify as ["
            + string.Join(", ", JustificationKeywords) + "] — a mutating method may not hide behind "
            + "the word 'internal' without saying what it mutates:"
            + Environment.NewLine + string.Join(Environment.NewLine, unclassified));
    }

    [Test]
    public void SessionLifecycleMethods_carryTheExplicitClassification()
    {
        // Implementation-plan Correction 3: these five are NOT read-only. Pin the
        // exact wording so a later edit cannot quietly reclassify them.
        string[] sessionMethods =
        [
            "RecordProviderFailureAsync", "RecordProviderSuccessAsync",
            "CreateProviderAsync", "ExecuteProviderAsync", "DisposeProviderAsync",
        ];

        foreach (var name in sessionMethods)
        {
            KnownNonEffectClientMethods.Should().ContainKey(name);
            KnownNonEffectClientMethods[name].Should().Contain(
                "internal-session-lifecycle-no-external-effect",
                $"{name} mutates provider session/telemetry state; calling it 'read-only' would make "
                + "this list the place mutating methods hide");
        }
    }

    // ====================================================================
    // DISCRIMINATION PROOFS — the sweep must FAIL on ungoverned input
    // ====================================================================

    /// <summary>
    /// A stand-in mediation client, declared in the TEST assembly, used to drive the
    /// real classifier with surface that does not exist in production.
    /// </summary>
    private sealed class FixtureClient
    {
        public Task<bool> DeleteTheUniverseAsync() => Task.FromResult(true);

        public Task<bool> ReadSomethingAsync() => Task.FromResult(true);
    }

    private static readonly IReadOnlyDictionary<ExternalEffect, EffectSite> FixtureSites =
        new Dictionary<ExternalEffect, EffectSite>
        {
            [ExternalEffect.LlmCall] = new(SiteKind.MediationClient, "DeleteTheUniverseAsync", "fixture"),
        };

    [Test]
    public void Discrimination_anUnclassifiedClientMethodIsReported()
    {
        var problems = Classify(
            typeof(FixtureClient),
            [ExternalEffect.LlmCall],
            FixtureSites,
            new Dictionary<string, string>(StringComparer.Ordinal));

        problems.Should().ContainSingle()
            .Which.Should().Contain("ReadSomethingAsync",
                "an unmapped, unbaselined mediation method must fail — if it does not, a new "
                + "capability can ship through the seam with no governance decision");
    }

    [Test]
    public void Discrimination_anEffectWithNoDeclaredSiteIsReported()
    {
        var problems = Classify(
            typeof(FixtureClient),
            [ExternalEffect.LlmCall, ExternalEffect.GitBranchDelete],
            FixtureSites,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ReadSomethingAsync"] = "read-only: fixture" });

        problems.Should().ContainSingle()
            .Which.Should().Contain("DELETE the ExternalEffect member",
                "the catalog→code direction must lead with DELETE, not invite a fake call site");
    }

    [Test]
    public void Discrimination_aRenamedClientMethodIsReported()
    {
        var problems = Classify(
            typeof(FixtureClient),
            [ExternalEffect.LlmCall],
            new Dictionary<ExternalEffect, EffectSite>
            {
                [ExternalEffect.LlmCall] = new(SiteKind.MediationClient, "MethodThatWasRenamedAsync", "fixture"),
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ReadSomethingAsync"] = "read-only: fixture",
                ["DeleteTheUniverseAsync"] = "read-only: fixture",
            });

        problems.Should().ContainSingle()
            .Which.Should().Contain("does not exist",
                "the table's method names are resolved by reflection — a rename must fail, otherwise "
                + "the mapping rots into a comment");
    }

    [Test]
    public void Discrimination_aStaleBaselineEntryIsReported()
    {
        var problems = Classify(
            typeof(FixtureClient),
            [ExternalEffect.LlmCall],
            FixtureSites,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ReadSomethingAsync"] = "read-only: fixture",
                ["DeleteTheUniverseAsync"] = "read-only: fixture",
            });

        problems.Should().ContainSingle()
            .Which.Should().Contain("DELETE its KnownNonEffectClientMethods entry",
                "a baselined method that is now mapped must fail as stale, so the baseline drains");
    }

    [Test]
    public void Discrimination_afullyClassifiedSurfaceIsClean()
    {
        // The complement: prove the classifier is not simply always-red.
        var problems = Classify(
            typeof(FixtureClient),
            [ExternalEffect.LlmCall],
            FixtureSites,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ReadSomethingAsync"] = "read-only: fixture" });

        problems.Should().BeEmpty();
    }
}
