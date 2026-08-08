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
/// <para><b>code → catalog:</b> every public instance method on the client —
/// <b>whatever it returns</b> — is either mapped to an <see cref="ExternalEffect"/>
/// by <see cref="EffectPerformingSites"/> or listed in the shrink-only, count-pinned
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
/// <para><b>WHY BOTH A TABLE AND THE ATTRIBUTE (updated 2026-07-30).</b>
/// <see cref="PerformsEffectAttribute"/> is the authoring shape Story 43-9 consumes,
/// and <b>all 17 attributes are now applied</b> to <see cref="TammaApiClient"/>
/// (43-8 AC4 step 7, carve-out §A1 #3). The table is KEPT, not retired, and the two
/// are held in agreement by <see cref="EveryAttributedMethod_AgreesWithTheTable"/> —
/// because they catch different things:</para>
/// <list type="bullet">
///   <item>the <b>attribute</b> is readable by PRODUCTION code (43-9's enforcement
///   seam and <c>ActionEnforcementSites</c> live in <c>src/</c> and cannot see a
///   table declared in a test assembly — story 43-8 §A2);</item>
///   <item>the <b>table</b> is what makes a RENAME fail: its method names are
///   resolved by reflection, whereas an attribute travels with the method it
///   decorates and survives any rename silently.</item>
/// </list>
/// <para>Keeping both is therefore not duplication; deleting either loses a distinct
/// guarantee. Story 43-8 §A1 carve-out #3 recorded the original justification for
/// table-only ("a file another in-flight story is extending") — §A2 established that
/// justification was factually wrong, and this is its closure.</para>
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
///   <item><b>It sees INSTANCE methods declared on the type, and nothing else.</b>
///   Discovery no longer filters on return type (review F12 — see
///   <see cref="ClientMethods"/>), but it still cannot see: a <c>static</c> mediation
///   helper; a non-public method reached through an internal seam; an extension
///   method over the client; or a method inherited from a base class the client might
///   grow (<c>DeclaredOnly</c>). Each of those is a way to add mediation surface this
///   sweep would not report, and none of them exists on
///   <see cref="TammaApiClient"/> today.</item>
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

        /// <summary>Story 43-12 — a RESERVED key: a real catalog row minted at its zone
        /// level with NO performer in the tree yet (git.checks.bypass, git.webhook.register,
        /// deploy.dev, deploy.staging). Named so the first caller cannot ship ungoverned.</summary>
        Reserved,
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
            // ── The 24 mutating mediation-client methods (AC4's enumerated set;
            //    17 + Story 31-13's 7 PR-lifecycle verbs) ──
            [ExternalEffect.LlmCall] = new(SiteKind.MediationClient, "CallLlmAsync",
                "the engine's only path to a model call"),
            [ExternalEffect.GitBranchCreate] = new(SiteKind.MediationClient, "CreateBranchAsync",
                "engine-mediated git write"),
            [ExternalEffect.GitBranchDelete] = new(SiteKind.MediationClient, "DeleteBranchAsync",
                "engine-mediated git write"),
            [ExternalEffect.GitPullRequestCreate] = new(SiteKind.MediationClient, "CreatePullRequestAsync",
                "engine-mediated git write"),
            // Story 43-12 — the coarse git.pull-request.merge is retired; the per-target
            // trio all map to the ONE method MergePullRequestAsync (it carries three
            // [PerformsEffect] attributes; the gate picks the key by the PR base branch).
            [ExternalEffect.GitMergeDev] = new(SiteKind.MediationClient, "MergePullRequestAsync",
                "engine-mediated git write (PR base 'dev')"),
            [ExternalEffect.GitMergeQa] = new(SiteKind.MediationClient, "MergePullRequestAsync",
                "engine-mediated git write (PR base 'qa')"),
            [ExternalEffect.GitMergeMain] = new(SiteKind.MediationClient, "MergePullRequestAsync",
                "engine-mediated git write (PR base 'main'; fail-closed default)"),
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
                "GET /api/v1/secrets/reveal/{token}; machinery plumbing fetch (off the dial), so no "
                + "mediation-client method exists to attribute"),
            // 42-10 — an LLM reading a secret value into context. Enforced at the reveal
            // route for LLM callers; no engine-side mediation-client method (the engine
            // has no reveal caller), so RouteOnly like secret.reveal.
            [ExternalEffect.SecretRead] = new(SiteKind.RouteOnly, null,
                "GET /api/v1/secrets/reveal/{token} — LLM-caller value read into model context; "
                + "enforced for LLM callers, no engine mediation-client method"),
            // Story 43-17 follow-up — the two /api/engine callbacks that had NO
            // OWNER. RouteOnly: they are reached by the ENGINE over HTTP, but
            // neither has a TammaApiClient method (the engine calls them through
            // its own callback clients), so there is no [PerformsEffect] site.
            [ExternalEffect.CiWorkflowDispatch] = new(SiteKind.RouteOnly, null,
                "POST /api/engine/trigger-ci — engine callback; no TammaApiClient method performs it"),
            [ExternalEffect.LlmTaskExecute] = new(SiteKind.RouteOnly, null,
                "POST /api/engine/execute-task — engine callback; no TammaApiClient method performs it"),
            [ExternalEffect.McpToolInvoke] = new(SiteKind.RouteOnly, null,
                "the C# surface is the KB proxy route POST /api/kb/mcp/tools/invoke (SiteKey corrected "
                + "2026-07-29, review F16 — it previously named a start|stop alternation that is not a "
                + "route pattern); the invocation it proxies runs in the TypeScript intelligence sidecar, "
                + "so the tool SET has NO drift signal in this repo"),
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

            // ── Story 31-13 — PR operations (step 8: now client-backed) ──
            // The 7 PR verbs are performed by the engine's mediation client — each is
            // a governed git write on the /api/v1/git/{owner}/{repo}/pull-requests/{n}/…
            // plane, called by a TammaApiClient method carrying [PerformsEffect].
            [ExternalEffect.GitPullRequestClose] = new(SiteKind.MediationClient, "ClosePullRequestAsync",
                "engine-mediated git write"),
            [ExternalEffect.GitPullRequestReopen] = new(SiteKind.MediationClient, "ReopenPullRequestAsync",
                "engine-mediated git write"),
            [ExternalEffect.GitPullRequestComment] = new(SiteKind.MediationClient, "CommentOnPullRequestAsync",
                "engine-mediated git write"),
            [ExternalEffect.GitPullRequestReviewComment] = new(SiteKind.MediationClient, "ReviewCommentOnPullRequestAsync",
                "engine-mediated git write (review output)"),
            [ExternalEffect.GitPullRequestRequestReviewers] = new(SiteKind.MediationClient, "RequestPullRequestReviewersAsync",
                "engine-mediated git write"),
            [ExternalEffect.GitPullRequestLabel] = new(SiteKind.MediationClient, "SetPullRequestLabelsAsync",
                "engine-mediated git write"),
            [ExternalEffect.GitPullRequestSetDraft] = new(SiteKind.MediationClient, "SetPullRequestDraftAsync",
                "engine-mediated git write"),

            // ── Story 31-13 — the formerly-ungoverned issue callbacks. RouteOnly:
            // called by IssueCallbackClient / HttpTriageApplyClient, NOT the engine's
            // TammaApiClient — no mediation-client method exists (or should).
            [ExternalEffect.GitIssueCreate] = new(SiteKind.RouteOnly, null,
                "Story 31-13 issue callback; reached via IssueCallbackClient / HttpTriageApplyClient, not the engine mediation client"),
            [ExternalEffect.GitIssueComment] = new(SiteKind.RouteOnly, null,
                "Story 31-13 issue callback; reached via IssueCallbackClient / HttpTriageApplyClient, not the engine mediation client"),
            [ExternalEffect.GitIssueLabelsSet] = new(SiteKind.RouteOnly, null,
                "Story 31-13 issue callback; reached via IssueCallbackClient / HttpTriageApplyClient, not the engine mediation client"),
            [ExternalEffect.GitIssueLabelsRemove] = new(SiteKind.RouteOnly, null,
                "Story 31-13 issue callback; reached via IssueCallbackClient / HttpTriageApplyClient, not the engine mediation client"),

            // ── Story 43-8 AC1 step 2 (carve-out §A1 #1, closed 2026-07-30) ──
            // The four MentorshipController [HttpPost] actions. RouteOnly: they are
            // reached from a UI, never through the engine's mediation client, so no
            // TammaApiClient method exists (or should) to attribute.
            [ExternalEffect.MentorshipSessionStart] = new(SiteKind.RouteOnly, null,
                "POST /api/Mentorship/start — the only [Governs]-attributed controller action family; "
                + "it dispatches the tamma-autonomous-mentorship Elsa workflow, so an agent run is "
                + "under way when it completes"),
            [ExternalEffect.MentorshipSessionPause] = new(SiteKind.RouteOnly, null,
                "POST /api/Mentorship/sessions/{sessionId:guid}/pause — control over an in-flight "
                + "mentorship run, reached from the UI"),
            [ExternalEffect.MentorshipSessionResume] = new(SiteKind.RouteOnly, null,
                "POST /api/Mentorship/sessions/{sessionId:guid}/resume — control over an in-flight "
                + "mentorship run, reached from the UI"),
            [ExternalEffect.MentorshipSessionCancel] = new(SiteKind.RouteOnly, null,
                "POST /api/Mentorship/sessions/{sessionId:guid}/cancel — terminates the mentorship "
                + "workflow instance; reached from the UI, never through the mediation client"),

            // ── In-process: no HTTP hop, so no route sweep can ever see them ──
            [ExternalEffect.ProcessSpawn] = new(SiteKind.InProcess, null,
                "ShellExecuteTool's ProcessStartInfo, inside the tool loop — invisible to any route sweep"),
            // Story 43-12 — the coarse deploy.promote-prod is retired; the shipped
            // pipeline is QA -> UAT -> Prod ONLY, so only these three env keys have a
            // performing site (the stage transition). Seam E gates deploy.prod.
            [ExternalEffect.DeployQa] = new(SiteKind.InProcess, null,
                "DeploymentPipelineWorkflow's QA stage transition; the deploy itself runs inside the LLM tool loop"),
            [ExternalEffect.DeployUat] = new(SiteKind.InProcess, null,
                "DeploymentPipelineWorkflow's UAT stage transition; the deploy itself runs inside the LLM tool loop"),
            [ExternalEffect.DeployProd] = new(SiteKind.InProcess, null,
                "DeploymentPipelineWorkflow's production stage transition (Seam E gates it); the deploy "
                + "itself runs inside the LLM tool loop, so this gates the TRANSITION only"),
            [ExternalEffect.DeployRollback] = new(SiteKind.InProcess, null,
                "DeploymentPipelineWorkflow's RollbackProduction branch; same tool-loop limitation as promote"),
            // Story 43-12 — RESERVED keys: real catalog rows at their zone levels with
            // no performer in the tree yet. deploy.dev / deploy.staging have no pipeline
            // stage (QA -> UAT -> Prod only); git.checks.bypass has nothing that bypasses
            // checks.
            [ExternalEffect.DeployDev] = new(SiteKind.Reserved, null,
                "RESERVED (Story 43-12): no dev stage exists in DeploymentPipelineWorkflow (QA -> UAT -> Prod only)"),
            [ExternalEffect.DeployStaging] = new(SiteKind.Reserved, null,
                "RESERVED (Story 43-12): no staging stage exists in DeploymentPipelineWorkflow (QA -> UAT -> Prod only)"),
            [ExternalEffect.GitChecksBypass] = new(SiteKind.Reserved, null,
                "RESERVED (Story 43-12): nothing in the tree bypasses required checks yet"),
            // Epic 31 P4 M3 (2026-08-08) — RESERVED → LIVE, as machinery: the first
            // caller of IGitPlatformClient.RegisterWebhookAsync is the server-initiated
            // WebhookRegistrationService (platform connect + single-user startup
            // validation) — provisioning plumbing, so per the row's own 43-12 note it
            // moved to the machinery inventory instead of binding an LLM route. No HTTP
            // hop of its own → InProcess.
            [ExternalEffect.GitWebhookRegister] = new(SiteKind.InProcess, null,
                "WebhookRegistrationService (Tamma.Api) — server-initiated at platform connect / startup "
                + "validation; machinery (Epic 31 P4 M3), audited via GIT.WEBHOOK_REGISTER.* events"),
        };

    // ====================================================================
    // code → catalog: the read-only / non-effect client methods
    // ====================================================================

    /// <summary>
    /// Every public instance <see cref="TammaApiClient"/> method (any return type — see
    /// <see cref="ClientMethods"/> and review finding F12) that
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
    // The EXCEPTION classifier — strictly stronger than RatchetClassifies.
    // Review finding F3 (2026-08-01), PROVED BY MUTATION on the sibling
    // ratchet (KnownUngovernedEndpoints) and reproduced here.
    // ====================================================================

    /// <summary>
    /// The vocabulary a <see cref="ReviewedNonEffectExceptions"/> entry must use ON
    /// TOP OF <see cref="JustificationKeywords"/>.
    ///
    /// <para><b>Why a second vocabulary exists</b> (review F3). D17's claim that the
    /// exception set "cannot become a blanket escape hatch" rested on each entry
    /// passing <see cref="RatchetClassifies"/> — which is precisely what all 19
    /// baseline entries do by construction. A reviewer proved the sibling case on
    /// <c>KnownUngovernedEndpoints</c>: move an ordinary baseline entry into the
    /// exception set with its justification COPIED VERBATIM, re-seed the exception
    /// pin, drop the baseline pin so the laundering reads as PROGRESS, everything
    /// green. The identical edit works here — <c>GetBudgetAsync</c> out of
    /// <see cref="KnownNonEffectClientMethods"/>, into
    /// <see cref="ReviewedNonEffectExceptions"/> verbatim, pin 19 → 18,
    /// <see cref="NonEffectExceptionPinHistory"/> <c>[1]</c> → <c>[2]</c>: 23 of 23
    /// tests passed.</para>
    ///
    /// <para><b>What separates the two sets.</b> The baseline is a LABEL — "read-only:
    /// reads commits" — for a method nobody has had to argue about. An exception is
    /// the claim that cataloguing this method as an effect would be WRONG, and the
    /// reason is the same circularity the endpoint side records: the gate-evaluation
    /// call is the question, not the act, so gating it would require a grant in
    /// order to ask whether a grant is needed. An exception must say that, in words.
    /// This vocabulary occurs ZERO times across all 19 baseline justifications
    /// (asserted by <see cref="Discrimination_noBaselineJustification_wouldSatisfyTheExceptionClassifier"/>).</para>
    /// </summary>
    private static readonly string[] ExceptionCircularityKeywords =
    [
        "circular",
        "circularity",
        "deadlock",
    ];

    /// <summary>
    /// Floor on an exception justification's length. An exception is an ARGUMENT,
    /// not a label: the seeded entry runs 345 characters, while the longest of the
    /// 19 baseline justifications is 167.
    /// </summary>
    private const int MinExceptionJustificationLength = 200;

    /// <summary>
    /// Whether a justification is strong enough to buy an entry in
    /// <see cref="ReviewedNonEffectExceptions"/> — MATERIALLY stronger than
    /// <see cref="RatchetClassifies"/>, which is all the baseline requires.
    /// </summary>
    internal static bool ExceptionRatchetClassifies(string justification)
    {
        if (!RatchetClassifies(justification)) return false;

        var text = justification.Trim();

        if (!ExceptionCircularityKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (text.Length < MinExceptionJustificationLength) return false;

        // Never a VERBATIM copy of a live baseline justification — the exact
        // laundering the reviewer performed on the sibling ratchet.
        return !KnownNonEffectClientMethods.Values.Any(v => string.Equals(
            v.Trim(), text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// THE PIN HISTORY RULE for this assembly's ratchets, as a pure predicate so the
    /// live assertions and the discrimination proofs drive the SAME rule. A history
    /// is legal iff it is non-empty, STARTS AT ITS DECLARED SEED, and strictly
    /// decreases thereafter.
    ///
    /// <para><b>Review F3.</b> The head-binding is the new part. Every one of this
    /// assembly's four pin histories has LENGTH 1, so the strictly-decreasing loop
    /// they all ran was vacuous and nothing at all constrained
    /// <c>[1]</c> → <c>[2]</c>.</para>
    /// </summary>
    internal static IReadOnlyList<string> PinHistoryProblems(string name, int seed, int[] history)
    {
        var problems = new List<string>();

        if (history.Length == 0)
        {
            problems.Add($"  {name}: has no pin history — the pin is a bare literal.");
            return problems;
        }

        if (history[0] != seed)
        {
            problems.Add(
                $"  {name}: its history STARTS at {history[0]} but the recorded seed is {seed}. "
                + "A history whose head can move is not a record — it can be silently RE-SEEDED "
                + "at a larger value, and at length 1 the strictly-decreasing check never "
                + "executes, so nothing else would notice.");
        }

        for (var i = 1; i < history.Length; i++)
        {
            if (history[i] >= history[i - 1])
            {
                problems.Add(
                    $"  {name}: pin history {history[i - 1]} → {history[i]} is not a decrease. "
                    + "A ratchet that turns both ways is not a ratchet.");
            }
        }

        return problems;
    }

    // ====================================================================
    // Story 43-9 DECISION D17 — the NAMED, DATED, REVIEWED exception set
    // ====================================================================

    /// <summary>One reviewed exception to the shrink-only non-effect baseline.</summary>
    /// <param name="Method">Exact method name on <see cref="TammaApiClient"/>.</param>
    /// <param name="AddedOn">ISO date the exception was reviewed.</param>
    /// <param name="Story">The story that reviewed it.</param>
    /// <param name="Justification">Why it performs no catalogued effect; must classify.</param>
    internal sealed record ReviewedException(
        string Method, string AddedOn, string Story, string Justification);

    /// <summary>
    /// <b>Story 43-9 D17.</b> Genuinely non-effect client methods added AFTER
    /// <see cref="KnownNonEffectClientMethods"/> became shrink-only.
    ///
    /// <para><b>Why an exception set rather than a bigger pin.</b>
    /// <see cref="NonEffectPinHistory"/> is strictly decreasing and its last
    /// element IS the pin, asserted here and again from the registry by
    /// <c>RatchetDisciplineTests</c>. So appending 20 is red by design and editing
    /// 19 in place is the undeclared re-widening the ratchet exists to catch. That
    /// is right for a baseline of methods that SHOULD eventually be classified as
    /// effects — but it makes a genuinely read-only NEW method unrepresentable,
    /// and a rule that forbids the honest answer eventually forces a dishonest
    /// one: classify a read as an effect, or move it to a base class this sweep's
    /// <c>DeclaredOnly</c> discovery cannot see (a hole this fixture's own
    /// doc-comment already names). Both are worse than an exception a reviewer
    /// sees in the diff.</para>
    ///
    /// <para><b>Why it cannot become a blanket escape hatch.</b> It is keyed by
    /// EXACT METHOD NAME, so a different new method still goes red; each entry
    /// carries a date, the reviewing story and a justification that must pass
    /// <see cref="ExceptionRatchetClassifies"/>; its MEMBERSHIP is pinned by method
    /// name in <see cref="ExceptionSet_membershipIsPinnedByMethod"/>; the set is
    /// count-pinned by a history whose HEAD is bound to its seed and whose tail must
    /// strictly decrease; it is declared in <c>RatchetDisciplineTests.Ratchets()</c>;
    /// and staleness applies both ways — an entry whose method no longer exists, or
    /// which becomes mapped to an <see cref="ExternalEffect"/>, fails until
    /// deleted. The rejected alternative was the count-level "name the index that
    /// may rise" precedent, which is ANONYMOUS: any future method could occupy the
    /// widened slot.</para>
    ///
    /// <para><b>CORRECTION 2026-08-01 (review F3) — the paragraph above used to say
    /// "a justification that must pass <c>RatchetClassifies</c>", and the set was
    /// pinned by COUNT ALONE with a one-element history. Both were the escape hatch,
    /// not the guard against it: all 19 baseline entries pass
    /// <c>RatchetClassifies</c> by construction, and
    /// <c>for (i = 1; i &lt; Length; i++)</c> over a one-element array asserts
    /// nothing. Moving <c>GetBudgetAsync</c> here with its justification COPIED
    /// VERBATIM, re-seeding <see cref="NonEffectExceptionPinHistory"/> to
    /// <c>[2]</c> and dropping the baseline pin 19 → 18 left 23 of 23 tests
    /// GREEN.</b></para>
    ///
    /// <para><see cref="KnownNonEffectClientMethods"/> stays at 19 and its history
    /// stays <c>[19]</c>. The exception set is unioned into the classifier's
    /// "is this method accounted for" check and EXCLUDED from that count pin, so
    /// unreviewed growth of the baseline is still impossible.</para>
    /// </summary>
    internal static readonly IReadOnlyList<ReviewedException> ReviewedNonEffectExceptions =
    [
        new("EvaluateGovernanceAsync", "2026-08-01", "Story 43-9",
            "read-only: Seam E's gate-evaluation call (POST /api/v1/governance/evaluate). It ASKS "
            + "whether the system may perform a catalogued action by itself and changes nothing "
            + "outside Tamma; the effect it asks ABOUT is separately catalogued and separately "
            + "bound. Mapping this to an ExternalEffect would catalogue the question as though it "
            + "were the answer, and the result would be circular: CheckActionGateActivity would "
            + "have to clear the gate before it were allowed to ask the gate anything, so the "
            + "first denial would deadlock every later evaluation. Same circularity the endpoint "
            + "side records for POST /api/v1/governance/evaluate itself, stated at the client "
            + "seam. (Circularity clause added 2026-08-01 under review F3: the argument was always "
            + "the reason for this entry, but until F3 nothing required an exception to WRITE it, "
            + "so a copied baseline label bought an exception just as well.)"),

        new("GetGitPlatformCapabilitiesAsync", "2026-08-07", "Epic 31 P2",
            "read-only: the §4 is-supported CHECK STEP's probe (GET /api/v1/git/{owner}/{repo}/"
            + "capabilities). It ASKS what the tenant's resolved platform driver supports so the "
            + "workflow can choose between the action step and its defined alternative step BEFORE "
            + "the action runs, and it changes nothing outside Tamma; the action it asks ABOUT is "
            + "separately catalogued and separately bound (e.g. effect:git.pull-request.set-draft). "
            + "Mapping the probe to an ExternalEffect would catalogue the question as though it "
            + "were the answer, and the result would be circular in exactly the "
            + "EvaluateGovernanceAsync direction: the check step exists so the governed action is "
            + "never even ATTEMPTED on a platform that cannot perform it — gating the check on the "
            + "action's own governance would require clearing the gate to ask whether the gated "
            + "step should run at all, so the first denial would wedge every capability-degraded "
            + "workflow at its check node instead of routing to the alternative step."),
    ];

    /// <summary>The exception set's own count pin — RE-SEEDED 2026-08-07 (Epic 31
    /// P2) at 2 when GetGitPlatformCapabilitiesAsync was admitted; a re-seed is a
    /// two-file edit (this seed + the RatchetDisciplineTests registry) made
    /// deliberately visible in the diff, per the D17 admission rule.</summary>
    internal static readonly int[] NonEffectExceptionPinHistory = [2];

    /// <summary>
    /// The value <see cref="NonEffectExceptionPinHistory"/> was SEEDED at, and the
    /// value <see cref="NonEffectPinHistory"/> was seeded at. Restated as named
    /// constants that the pin-history rule checks, so a re-seed is a change to a
    /// named seed rather than an invisible edit to the array's first element
    /// (review F3).
    /// </summary>
    internal const int NonEffectExceptionPinSeed = 2;

    /// <summary>See <see cref="NonEffectExceptionPinSeed"/>.</summary>
    internal const int NonEffectPinSeed = 19;

    /// <summary>Exception method names, for the union the classifier consumes.</summary>
    private static IReadOnlyDictionary<string, string> ExceptionsAsBaseline() =>
        ReviewedNonEffectExceptions.ToDictionary(
            e => e.Method, e => e.Justification, StringComparer.Ordinal);

    /// <summary>
    /// The baseline UNIONED with the reviewed exceptions — what "accounted for"
    /// means. The COUNT PINS deliberately see the two collections separately.
    /// </summary>
    private static IReadOnlyDictionary<string, string> AccountedForClientMethods()
    {
        var union = new Dictionary<string, string>(KnownNonEffectClientMethods, StringComparer.Ordinal);
        foreach (var (name, justification) in ExceptionsAsBaseline()) union[name] = justification;
        return union;
    }

    // ====================================================================
    // Discovery + the classifier, as pure functions over their inputs
    // ====================================================================

    /// <summary>
    /// EVERY public instance method declared on a client type — not only the
    /// <c>Task</c>-returning ones.
    ///
    /// <para><b>Review finding F12 (2026-07-29), proved by mutation.</b> This filter
    /// used to read <c>typeof(Task).IsAssignableFrom(m.ReturnType)</c>. A reviewer
    /// added a real <c>public ValueTask&lt;bool&gt; ZzNukeProductionAsync()</c> to
    /// <see cref="TammaApiClient"/> and ALL THIRTEEN TESTS IN THIS FIXTURE STAYED
    /// GREEN: the method was invisible to discovery, so no governance decision was
    /// demanded, and neither count pin moved (the anti-no-op tripwire was a
    /// <c>&gt;25</c> lower bound, and the baseline pin counts baseline entries). A
    /// harness that reads as coverage while covering nothing is exactly the failure
    /// this story exists to prevent, so <b>return type is no longer a discovery
    /// filter</b> — <c>ValueTask</c>, <c>IAsyncEnumerable</c>, <c>void</c> and plain
    /// synchronous methods all reach the classifier.</para>
    ///
    /// <para>Two exclusions remain, both structural rather than discretionary, and
    /// both pinned by
    /// <see cref="Discrimination_aNonTaskReturningClientMethodIsReported"/>:
    /// compiler-generated accessors (<c>IsSpecialName</c> — property getters such as
    /// <c>BaseUrl</c>, operators, event add/remove), and overrides of
    /// <see cref="object"/> members (<c>ToString</c>/<c>Equals</c>/<c>GetHashCode</c>),
    /// which are formatting and identity, not mediation. What that still cannot see is
    /// listed in the fixture doc-comment's "WHAT THIS SWEEP CANNOT SEE".</para>
    /// </summary>
    internal static IReadOnlyList<MethodInfo> ClientMethods(Type clientType) =>
        clientType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.GetBaseDefinition().DeclaringType != typeof(object))
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

        // catalog → code (c): two effects may not claim the same method — EXCEPT the
        // per-target merge trio (Story 43-12), which legitimately share
        // MergePullRequestAsync (that one method carries three [PerformsEffect]
        // attributes and the gate picks the key by the PR base branch).
        var duplicated = sites.Values
            .Where(s => s.ClientMethod is not null && s.ClientMethod != "MergePullRequestAsync")
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
        var discovered = ClientMethods(typeof(TammaApiClient));

        // ANTI-NO-OP TRIPWIRE (a): if the reflection filter ever stops matching (a
        // base-class extraction, an interface split), every assertion below would
        // pass vacuously on a tiny list.
        discovered.Should().HaveCountGreaterThan(25,
            "TammaApiClient exposes dozens of public mediation methods; a tiny result means the "
            + "discovery filter broke, not that the client shrank");

        // ANTI-NO-OP TRIPWIRE (b) — added for review finding F12. A `>25` lower bound
        // cannot catch a SINGLE addition, which is precisely what the reviewer's
        // ValueTask mutation was. The discovered surface is therefore pinned EXACTLY,
        // so adding one method of ANY return shape moves a number in this file.
        //
        // MEASURED 2026-07-29, before and after the F12 widening: 36 both times. The
        // widening surfaced ZERO new methods on TammaApiClient (its whole public
        // instance surface is Task-returning today, plus the `BaseUrl` property
        // getter, which is IsSpecialName), so nothing needed reclassifying into
        // EffectPerformingSites or KnownNonEffectClientMethods and neither of the
        // other two pins moved. That is why 36/19/17 are unchanged by a change that
        // genuinely widened the lens.
        // 36 → 37 (Story 43-9, 2026-08-01): + EvaluateGovernanceAsync, Seam E's
        // gate-evaluation read. This pin is a LEGITIMATE bump-with-review — its
        // own message has always said "move this number in the same commit" — and
        // it is NOT a ratchet, unlike NonEffectClientMethods_countIsPinned, which
        // stays at 19 because the new method is accounted for by the D17 reviewed
        // exception set rather than by widening the baseline.
        // 37 → 44 (Story 31-13, step 8): + the 7 PR-lifecycle verb methods
        // (Close/Reopen/CommentOn/ReviewCommentOn/RequestReviewers/SetLabels/SetDraft),
        // each carrying [PerformsEffect] and mapped in EffectPerformingSites. This pin
        // is a bump-with-review, not a ratchet — "move this number in the same commit".
        // 44 → 45 (Epic 31 P2, 2026-08-07): + GetGitPlatformCapabilitiesAsync, the §4
        // check-step's READ-ONLY capability probe. Accounted for by a second D17
        // reviewed exception (see ReviewedNonEffectExceptions — the probe is the
        // QUESTION whose answer decides which workflow branch runs; cataloguing the
        // question as an effect would be the EvaluateGovernanceAsync circularity again).
        discovered.Should().HaveCount(45,
            "the mediation surface is pinned exactly: 24 effect-performing + 19 baselined "
            + "non-effect + 2 reviewed-exception methods. A change here is a new (or removed) "
            + "mediation method — decide whether it is a governed effect, then move this number "
            + "in the same commit.");
    }

    [Test]
    public void EveryEffectMember_AndEveryClientMethod_IsClassified()
    {
        var problems = Classify(
            typeof(TammaApiClient),
            Enum.GetValues<ExternalEffect>(),
            EffectPerformingSites,
            // UNIONED with the D17 reviewed exceptions: a method is "accounted
            // for" if it is mapped, baselined, OR carries a named exception.
            AccountedForClientMethods());

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
        // [PerformsEffect] is the shape Story 43-9 and ActionEnforcementSites consume;
        // the table is what makes a rename fail. This test is what keeps the two from
        // ever disagreeing.
        //
        // ANTI-VACUITY TRIPWIRE (2026-07-30). Until the attributes landed, this test
        // iterated zero attributed methods and passed unconditionally — it read as a
        // guarantee while guaranteeing nothing, which is the failure mode this whole
        // story exists to prevent. Assert the attributed count FIRST, so the loop
        // below can never silently become a no-op again.
        // Story 43-12 — GetCustomAttributes (plural): MergePullRequestAsync now carries
        // THREE [PerformsEffect] attributes, and GetCustomAttribute<T> throws
        // AmbiguousMatchException on multiples. The attributed-method COUNT is still 17
        // (17 distinct methods; the merge method is one of them).
        var attributed = ClientMethods(typeof(TammaApiClient))
            .Where(m => m.GetCustomAttributes<PerformsEffectAttribute>(inherit: false).Any())
            .ToList();

        attributed.Should().HaveCount(24,
            "all 24 mutating TammaApiClient methods carry [PerformsEffect] (17 + Story 31-13's 7 "
            + "PR-lifecycle verbs; the merge method carries three since Story 43-12's per-target "
            + "split, but it is still ONE method). If this is 0 the attributes were stripped and the "
            + "agreement check below is vacuous; if it is higher, a method was attributed without a "
            + "corresponding EffectPerformingSites entry — decide which effect it performs, in the "
            + "table, first.");

        var problems = new List<string>();

        foreach (var method in ClientMethods(typeof(TammaApiClient)))
        {
            foreach (var attribute in method.GetCustomAttributes<PerformsEffectAttribute>(inherit: false))
            {
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
        }

        problems.Should().BeEmpty(
            "an applied [PerformsEffect] must agree with the declared performing site:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void MediationClientSites_countIsPinned()
    {
        EffectPerformingSites.Values.Count(s => s.Kind == SiteKind.MediationClient)
            .Should().Be(26,
                "17 -> 19 (Story 43-12): the coarse git.pull-request.merge (1 MediationClient entry) is "
                + "retired and replaced by the per-target trio git.merge.{dev,qa,main} (3 entries, all "
                + "sharing MergePullRequestAsync). 19 -> 26 (Story 31-13, step 8): the 7 PR-lifecycle "
                + "verbs flip from RouteOnly to MediationClient as their TammaApiClient methods land. A "
                + "change here means a mediation method became (or stopped being) a governed effect — "
                + "that is a governance decision, not a refactor.");
    }

    /// <summary>
    /// The <see cref="KnownNonEffectClientMethods"/> pin's recorded high-water
    /// history, oldest first; every element must be strictly LESS than its
    /// predecessor (asserted by
    /// <see cref="TheRatchetPin_IsMechanicallyShrinkOnly"/>). Seeded at 19 on
    /// 2026-07-29 and unmoved since — applying the 17 <c>[PerformsEffect]</c>
    /// attributes on 2026-07-30 reclassified nothing, because the table already
    /// mapped exactly those 17 methods.
    /// </summary>
    internal static readonly int[] NonEffectPinHistory = [19];

    [Test]
    public void TheRatchetPin_IsMechanicallyShrinkOnly()
    {
        // Adopted 2026-07-30 from TemplateExampleConformanceTests for all four 43-8
        // ratchets: a bare const compared with HaveCount makes "shrink-only" prose an
        // author can defeat by editing one literal.
        NonEffectPinHistory.Should().NotBeEmpty();
        NonEffectPinHistory[^1].Should().Be(19,
            "the pin IS the last recorded high-water value; changing one without the other is the "
            + "shape of an undeclared re-widening");

        // Review F3 (2026-08-01): BIND THE HEAD. The loop below constrains the TAIL
        // of the history and never its first element, and this history has LENGTH 1
        // so the loop never runs at all.
        var problems = PinHistoryProblems(
            nameof(NonEffectPinHistory), NonEffectPinSeed, NonEffectPinHistory);

        problems.Should().BeEmpty(
            "A method leaves this baseline by becoming a governed effect, never by the list "
            + "growing:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void NonEffectClientMethods_countIsPinned()
    {
        // (c) of the three ratchet properties. Without it an ADDITION is undetectable.
        // Arithmetic restated after the F12 widening: the numerator is now every public
        // INSTANCE method (any return type), not just the Task-returning ones. It is
        // still 36, because TammaApiClient happens to expose no non-Task method today —
        // see The_sweep_actually_sees_the_client_surface for the measurement.
        KnownNonEffectClientMethods.Should().HaveCount(19,
            "44 public instance mediation methods − 24 effect-performing − 1 D17 reviewed "
            + "exception = 19. If this fails because a new mediation method was added, that is the "
            + "ratchet working: decide whether it is a governed effect. Do NOT bump this number — "
            + "it is shrink-only. A genuinely non-effect new method goes in "
            + "ReviewedNonEffectExceptions, named and dated.");
    }

    // ====================================================================
    // Story 43-9 D17 — the reviewed exception set's own three properties
    // ====================================================================

    [Test]
    public void ExceptionSet_countIsPinned_andIsMechanicallyShrinkOnly()
    {
        NonEffectExceptionPinHistory.Should().NotBeEmpty();

        ReviewedNonEffectExceptions.Should().HaveCount(NonEffectExceptionPinHistory[^1],
            "the exception set is count-pinned. Seeded at 1 on 2026-08-01 by Story 43-9 "
            + "(EvaluateGovernanceAsync). A second entry must be argued for — the whole point of "
            + "keying exceptions BY METHOD NAME is that one exception does not buy the next.");

        // Review F3: head-binding is what makes shrink-only mean anything at
        // length 1. Before it, [1] → [2] was a ONE-LITERAL edit and all 23 tests
        // in this fixture and the meta-test stayed green.
        var problems = PinHistoryProblems(
            nameof(NonEffectExceptionPinHistory),
            NonEffectExceptionPinSeed,
            NonEffectExceptionPinHistory);

        problems.Should().BeEmpty(
            "an exception set that can grow without a decrease is an escape hatch:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void ExceptionSet_membershipIsPinnedByMethod()
    {
        // Review F3. A COUNT pin cannot tell "an exception was added" from "a
        // baseline entry was relabelled an exception while the baseline pin
        // dropped" — the second reads as governance PROGRESS in the diff. Pinning
        // the MEMBERSHIP makes admitting a method a sentence a reviewer reads.
        ReviewedNonEffectExceptions.Select(e => e.Method)
            .Should().BeEquivalentTo(
                new[] { "EvaluateGovernanceAsync", "GetGitPlatformCapabilitiesAsync" },
                "these methods, and only these methods, are D17 reviewed exceptions "
                + "(EvaluateGovernanceAsync: Story 43-9; GetGitPlatformCapabilitiesAsync: Epic 31 "
                + "P2's §4 capability probe). Admitting another is a governance decision: name it "
                + "here, argue the circularity in its justification, and do NOT pay for it by "
                + "dropping the baseline pin.");
    }

    [Test]
    public void Discrimination_noBaselineJustification_wouldSatisfyTheExceptionClassifier()
    {
        // THE F3 PROOF for this ratchet. Every baseline justification must be
        // REJECTED as an exception justification — otherwise "reviewed exception"
        // is a second name for "baseline entry" and the two pins can be traded
        // against each other while the diff reads as progress.
        var launderable = KnownNonEffectClientMethods
            .Where(kv => ExceptionRatchetClassifies(kv.Value))
            .Select(kv => $"  {kv.Key}: '{kv.Value}'")
            .ToList();

        launderable.Should().BeEmpty(
            $"{launderable.Count} of {KnownNonEffectClientMethods.Count} baseline justifications "
            + "would buy a D17 reviewed exception verbatim. An exception is the claim that "
            + "cataloguing the method as an effect would be WRONG because doing so is circular; a "
            + "baseline entry is a label for a method nobody has argued about:"
            + Environment.NewLine + string.Join(Environment.NewLine, launderable));
    }

    [Test]
    public void Discrimination_theExceptionClassifierIsStrictlyStronger_notMerelyDifferent()
    {
        foreach (var e in ReviewedNonEffectExceptions)
        {
            RatchetClassifies(e.Justification).Should().BeTrue(
                "an exception justification is a baseline justification PLUS an argument, never "
                + $"something outside the vocabulary: '{e.Justification}'");
        }

        foreach (var placeholder in new[] { "", "   ", "TODO", "n/a", "circular" })
        {
            ExceptionRatchetClassifies(placeholder).Should().BeFalse(
                $"'{placeholder}' must not buy an exception — in particular the bare word "
                + "'circular' must not, or the new requirement degenerates into a magic word");
        }
    }

    [Test]
    public void Discrimination_aReSeededPinHistoryIsReported()
    {
        // Drive the REAL pin rule with the sibling reviewer's exact edit shape.
        PinHistoryProblems("fixture", 1, [2]).Should().ContainSingle()
            .Which.Should().Contain("RE-SEEDED",
                "a length-1 history re-seeded upward is the F3 mutation; if the rule stays silent "
                + "here the shrink-only property is vacuous");

        PinHistoryProblems("fixture", 1, [1]).Should().BeEmpty(
            "the complement: the seeded history itself must be legal, or the rule is always red "
            + "and proves nothing");

        PinHistoryProblems("fixture", 19, [19, 18]).Should().BeEmpty(
            "shrinking is the direction the ratchet is FOR");

        PinHistoryProblems("fixture", 19, [19, 20]).Should().ContainSingle()
            .Which.Should().Contain("is not a decrease");

        PinHistoryProblems("fixture", 1, []).Should().ContainSingle()
            .Which.Should().Contain("bare literal");
    }

    [Test]
    public void ExceptionSet_entriesAreDatedAndAttributed_andStillExist()
    {
        var live = ClientMethods(typeof(TammaApiClient))
            .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var mapped = EffectPerformingSites.Values
            .Where(s => s.ClientMethod is not null)
            .Select(s => s.ClientMethod!)
            .ToHashSet(StringComparer.Ordinal);

        var problems = new List<string>();
        foreach (var e in ReviewedNonEffectExceptions)
        {
            if (!DateOnly.TryParse(e.AddedOn, out _) || string.IsNullOrWhiteSpace(e.Story))
                problems.Add($"  {e.Method}: addedOn='{e.AddedOn}' story='{e.Story}' — a named, "
                    + "dated, reviewed exception must actually carry all three.");
            // Review F3: the STRICTER exception classifier, not RatchetClassifies.
            // Requiring only what the baseline requires is what made this set a
            // blanket escape hatch.
            if (!ExceptionRatchetClassifies(e.Justification))
                problems.Add($"  {e.Method}: unclassified justification '{e.Justification}'.");
            // Staleness, both ways (D17(5)).
            if (!live.Contains(e.Method))
                problems.Add($"  {e.Method}: no such method on TammaApiClient any more — DELETE the exception.");
            if (mapped.Contains(e.Method))
                problems.Add($"  {e.Method}: is now mapped to an ExternalEffect — DELETE the exception.");
            if (KnownNonEffectClientMethods.ContainsKey(e.Method))
                problems.Add($"  {e.Method}: is in BOTH the baseline and the exception set — pick one.");
        }

        problems.Should().BeEmpty(
            "the D17 exception set must be as disciplined as the ratchet it relieves:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// The exception set's justifications / classifier / count / staleness probe,
    /// for the meta-test in <c>RatchetDisciplineTests</c>.
    /// </summary>
    internal static IReadOnlyList<string> ExceptionRatchetJustifications() =>
        ReviewedNonEffectExceptions.Select(e => e.Justification).ToArray();

    /// <summary>Live entry count of the exception set, for the meta-test.</summary>
    internal static int ExceptionRatchetCount() => ReviewedNonEffectExceptions.Count;

    /// <summary>
    /// Drives the REAL <see cref="Classify"/> with an EXCEPTION-shaped entry whose
    /// method is now mapped to an effect — the stale case, proving the exception
    /// set drains rather than accumulating.
    /// </summary>
    internal static IReadOnlyList<string> ExceptionRatchetStalenessProbe() =>
        Classify(
            typeof(FixtureClient),
            [ExternalEffect.LlmCall],
            FixtureSites,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ReadSomethingAsync"] = "read-only: fixture",
                // Mapped by FixtureSites AND present here: the stale shape.
                ["DeleteTheUniverseAsync"] = "read-only: fixture exception",
            })
        .Where(p => p.Contains("DELETE its KnownNonEffectClientMethods entry", StringComparison.Ordinal))
        .ToArray();

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
    // Ratchet-discipline surface (Story 43-8 AC8, carve-out §A1 #5) — the seam
    // RatchetDisciplineTests reads, so the meta-test can assert all three
    // properties without KnownNonEffectClientMethods becoming public API.
    // ====================================================================

    /// <summary>The ratchet's justification strings, for the meta-test.</summary>
    internal static IReadOnlyList<string> RatchetJustifications() =>
        KnownNonEffectClientMethods.Values.ToArray();

    /// <summary>The ratchet's justification classifier, for the meta-test.</summary>
    internal static bool RatchetClassifies(string justification) =>
        !string.IsNullOrWhiteSpace(justification)
        && JustificationKeywords.Any(k => justification.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>The ratchet's live entry count, for the meta-test.</summary>
    internal static int RatchetCount() => KnownNonEffectClientMethods.Count;

    /// <summary>
    /// Drives the REAL <see cref="Classify"/> with a baselined method that is now
    /// mapped to an effect — the stale case. Non-empty output is the proof that this
    /// ratchet's staleness arm fires.
    /// </summary>
    internal static IReadOnlyList<string> RatchetStalenessProbe() =>
        Classify(
            typeof(FixtureClient),
            [ExternalEffect.LlmCall],
            FixtureSites,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ReadSomethingAsync"] = "read-only: fixture",
                ["DeleteTheUniverseAsync"] = "read-only: fixture",
            })
        .Where(p => p.Contains("DELETE its KnownNonEffectClientMethods entry", StringComparison.Ordinal))
        .ToArray();

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

    /// <summary>
    /// A second stand-in, also declared in the TEST assembly, whose methods return
    /// things that are NOT <c>Task</c>. It is the PERMANENT regression proof for
    /// review finding F12: under the old
    /// <c>typeof(Task).IsAssignableFrom(ReturnType)</c> discovery filter EVERY method
    /// on this type was invisible, so a mediation method could be added with no
    /// governance decision and no pin moving. The proof lives here rather than on
    /// <see cref="TammaApiClient"/> deliberately — a harness must never require a
    /// production edit to demonstrate that it works.
    /// </summary>
    private sealed class NonTaskFixtureClient
    {
        /// <summary>The reviewer's exact mutation shape: a generic <c>ValueTask</c>.</summary>
        public ValueTask<bool> ZzNukeProductionAsync() => ValueTask.FromResult(true);

        /// <summary>Non-generic <c>ValueTask</c> — a different runtime type, same hole.</summary>
        public ValueTask BareValueTaskAsync() => ValueTask.CompletedTask;

        /// <summary>A streaming return; assignable to neither <c>Task</c> nor <c>ValueTask</c>.</summary>
        public IAsyncEnumerable<string> StreamAsync() => throw new NotSupportedException();

        /// <summary>A blocking mediation call — synchronous code performs effects too.</summary>
        public bool SynchronousEffect() => true;

        /// <summary><c>void</c>: fire-and-forget is the easiest effect of all to hide.</summary>
        public void FireAndForget() { }

        /// <summary>MUST NOT be reported: an <see cref="object"/> override is formatting.</summary>
        public override string ToString() => nameof(NonTaskFixtureClient);
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
    public void Discrimination_aNonTaskReturningClientMethodIsReported()
    {
        // F12 REGRESSION PIN (2026-07-29). Drives the REAL discovery + the REAL
        // classifier over a fixture whose entire surface the old filter dropped.
        var discovered = ClientMethods(typeof(NonTaskFixtureClient)).Select(m => m.Name).ToList();

        discovered.Should().BeEquivalentTo(
            new[]
            {
                "BareValueTaskAsync", "FireAndForget", "StreamAsync",
                "SynchronousEffect", "ZzNukeProductionAsync",
            },
            "return type is NOT a governance property — a ValueTask, IAsyncEnumerable, bool or void "
            + "mediation method performs effects exactly as a Task-returning one does, and under the "
            + "pre-F12 filter every one of these was invisible to the sweep");

        discovered.Should().NotContain("ToString",
            "an object override is formatting, not mediation; the two structural exclusions "
            + "(IsSpecialName accessors, object overrides) are the ONLY ones");

        var problems = Classify(
            typeof(NonTaskFixtureClient),
            [],
            new Dictionary<ExternalEffect, EffectSite>(),
            new Dictionary<string, string>(StringComparer.Ordinal));

        problems.Should().HaveCount(5,
            "each unmapped, unbaselined method must produce exactly one governance-decision demand");
        problems.Should().Contain(p => p.Contains("ZzNukeProductionAsync", StringComparison.Ordinal),
            "this is the reviewer's mutation, verbatim: adding it to the real client used to leave "
            + "all thirteen tests green");
        problems.Should().Contain(p => p.Contains("FireAndForget", StringComparison.Ordinal));
        problems.Should().NotContain(p => p.Contains("ToString", StringComparison.Ordinal));
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
