using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using NUnit.Framework;
using Tamma.Api.Infrastructure;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-9 <b>AC7 / Decision D15</b> — the enforcement-opt-in sweep.
///
/// <para>D15 makes "which routes are gated" a thing that must be WRITTEN DOWN:
/// <c>.Governs(key)</c> binds, <c>.EnforcesGovernance()</c> enforces, and the two
/// are separate lines precisely so that turning enforcement on for a route is a
/// per-route review rather than a side effect of a helper. A written-down list
/// with nothing asserting it is a comment, so this fixture pins the opted-in set
/// EXACTLY: an accidental addition and an accidental omission both go red.</para>
///
/// <para><b>Why exact rather than a lower bound.</b> The failure this guards is
/// asymmetric and both directions are real: adding an opt-in silently converts a
/// route into a hard 409 the moment an admin tightens its action (a behaviour
/// change nobody reviewed), and REMOVING one silently ungoverns a route while
/// every binding harness stays green, because the binding is still there.</para>
///
/// <para><b>The MISCONFIGURATION arm matters as much as the list.</b>
/// <see cref="EveryEnforcedEndpoint_AlsoCarriesABinding"/> is what makes the
/// enforcement core's fail-CLOSED branch unreachable in a shipped build: an
/// enforced route with no <c>IActionGateMetadata</c> would 409 permanently, and
/// this is the test that stops that reaching a release.</para>
/// </summary>
[TestFixture]
public class GovernedEndpointEnforcementSweepTests
{
    /// <summary>
    /// <b>THE OPT-IN LIST.</b> Every route where Story 43-9 turned enforcement on,
    /// with the reason the set is exactly this and nothing else.
    ///
    /// <para><b>What is IN:</b> the 16 mutating <c>EngineServiceOnly</c> mediation
    /// routes. These are the surfaces an autonomous engine reaches to change the
    /// outside world — create a branch, merge a PR, cut a release, trigger CI,
    /// patch an issue, dispatch an agent run, send mail — which is exactly what the
    /// admin's autonomy dial is a control for. All 16 ship at
    /// <c>AutonomyDial.Min</c>, so day-one control flow through every one of them is
    /// byte-identical to before this story (AC2).</para>
    ///
    /// <para><b>What is deliberately OUT:</b></para>
    /// <list type="bullet">
    ///   <item><c>POST /api/v1/llm/call</c> — <b>Seam A, never in any version</b>
    ///   (AC3 / epic D2). It is BOUND (so the harnesses and the admin UI can see
    ///   what it performs) and never opts in. A RequiresHuman here would reach a
    ///   DispatchWorkflow whose calling workflow has no human route in 44 of 45
    ///   cases, and blocking here as well as at Seam E would double-gate deploy,
    ///   since the deployment pipeline reaches the model through this route.
    ///   Pinned separately and explicitly by
    ///   <see cref="LlmCallRoute_IsBound_ButNotEnforced"/>.</item>
    ///   <item>the four <c>[Governs]</c> <c>MentorshipController</c> actions — the
    ///   controller-plane opt-in MECHANISM ships in this story
    ///   (<see cref="EnforcesGovernanceAttribute"/>, exercised directly by
    ///   <c>EnforcesGovernanceAttributeTests</c>), but no controller action opts in
    ///   here. Two reasons: they are reached from a UI by a person, which the
    ///   epic's own general rule says must not be gated; and
    ///   <c>MentorshipController.cs</c> is outside this story's file scope, so
    ///   opting them in would have been an unreviewed edit in someone else's
    ///   lane.</item>
    ///   <item><c>POST /api/kb/mcp/tools/invoke</c> — carries no binding at all,
    ///   and Story 43-9 D16 decided it stays that way. <b>NOTE (43-17 follow-up):</b>
    ///   the ORIGINAL justification recorded here — "<c>effect:mcp.tool.invoke</c>
    ///   ships <c>AlwaysHuman</c>" — is STALE and was false by the time it was read:
    ///   43-11 M6 moved all four AlwaysHuman rows onto real levels, and
    ///   <c>ActionCatalogDefaultsTests.NoShippedDescriptor_CarriesAlwaysHuman</c>
    ///   pins that set EMPTY. The D16 decision itself is untouched here — only the
    ///   dead reasoning is corrected, because that sentence has already misled one
    ///   reader into treating the key as human-gated.</item>
    /// </list>
    /// </summary>
    private static readonly IReadOnlySet<string> EnforcementOptedInRoutes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            // engine → API callbacks
            "POST /api/engine/events",
            "POST /api/engine/platform-events",
            "POST /api/engine/documents",
            "POST /api/engine/documents/{documentId:guid}/status",
            "POST /api/engine/channel/outbox",
            // git platform mediation
            "POST /api/v1/git/{owner}/{repo}/branches",
            "DELETE /api/v1/git/{owner}/{repo}/branches",
            "POST /api/v1/git/{owner}/{repo}/pull-requests",
            "PUT /api/v1/git/{owner}/{repo}/pull-requests/{n:int}/merge",
            "PATCH /api/v1/git/{owner}/{repo}/issues/{n:int}",
            "POST /api/v1/git/{owner}/{repo}/releases",
            // CI / Jira / agent dispatch
            "POST /api/v1/ci/{owner}/{repo}/test-runs",
            "PATCH /api/v1/jira/tickets/{ticketId}",
            "POST /api/v1/agent-dispatch/{owner}/{repo}/runs",
            // outbound comms
            "POST /api/v1/notifications/slack",
            "POST /api/v1/notifications/email",
            // Story 42-10 — an LLM reading a secret value into context (secret.read
            // at 90). Anonymous grades as LLM (fail-closed), so the tool-loop curl of
            // the reveal URL is gated; an authenticated human passes.
            "GET /api/v1/secrets/reveal/{token}",
            // Story 31-13 — the 7 PR-lifecycle verbs (35, review-comment 40). Born
            // bound + enforcing; behaviour-preserving at the shipped dial (< 70).
            "POST /api/v1/git/{owner}/{repo}/pull-requests/{n:int}/close",
            "POST /api/v1/git/{owner}/{repo}/pull-requests/{n:int}/reopen",
            "POST /api/v1/git/{owner}/{repo}/pull-requests/{n:int}/comments",
            "POST /api/v1/git/{owner}/{repo}/pull-requests/{n:int}/review-comments",
            "POST /api/v1/git/{owner}/{repo}/pull-requests/{n:int}/reviewers",
            "PUT /api/v1/git/{owner}/{repo}/pull-requests/{n:int}/labels",
            "PUT /api/v1/git/{owner}/{repo}/pull-requests/{n:int}/draft",
            // Story 31-13 — the 4 formerly-ungoverned issue callbacks, now bound +
            // enforcing (their KnownUngovernedEndpoints baseline entries are deleted
            // in the same commit). Auth unchanged (WorkflowsManage); level 35 < 70.
            "POST /api/engine/create-issue",
            "POST /api/engine/issue-comment",
            "POST /api/engine/issue-labels",
            "DELETE /api/engine/issue-labels/{repo}/{issueNumber}/{label}",
            // 43-17 follow-up — the last two ungoverned /api/engine routes, which
            // 43-17 flagged as having NO OWNER. ci.workflow.dispatch is 30,
            // llm.task.execute is 20; both < 70, so behaviour-preserving.
            "POST /api/engine/trigger-ci",
            "POST /api/engine/execute-task",
        };

    /// <summary>Endpoints carrying the D15 enforcement opt-in, off the booted host.</summary>
    private static IReadOnlyList<GovernanceHostFixture.EndpointFact> EnforcedEndpoints() =>
        GovernanceHostFixture.Endpoints.Where(f => f.EnforcesGovernance).ToArray();

    [Test]
    public void The_sweep_actually_sees_enforced_endpoints()
    {
        // ANTI-VACUITY. Every assertion below filters on EnforcesGovernance; if the
        // metadata lookup ever stopped matching (a rename, an ASP.NET Core upgrade
        // that drops custom metadata from RouteEndpoint.Metadata), the exact-set
        // assertion would fail loudly — but the "no route is enforced without a
        // binding" arm would pass vacuously. This is the tripwire for that.
        EnforcedEndpoints().Should().NotBeEmpty(
            "Story 43-9 opts 16 mediation routes into enforcement; an empty result means the "
            + "IGovernanceEnforcementMetadata lookup broke, not that enforcement was removed");
    }

    [Test]
    public void TheEnforcementOptInSet_isExactlyTheWrittenDownList()
    {
        var live = EnforcedEndpoints()
            .Select(f => f.SiteKey)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var unexpected = live.Except(EnforcementOptedInRoutes, StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        var missing = EnforcementOptedInRoutes.Except(live, StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        unexpected.Should().BeEmpty(
            "a route gained .EnforcesGovernance() without being added to this list. Under D15 that "
            + "is a BEHAVIOUR CHANGE — the moment an admin tightens the bound action, this route "
            + "hard-409s — and it must be a reviewed line, not an inherited one:"
            + Environment.NewLine + string.Join(Environment.NewLine, unexpected));

        missing.Should().BeEmpty(
            "a route in this list no longer carries .EnforcesGovernance(). Removing an opt-in "
            + "silently UNGOVERNS the route while every binding harness stays green, because the "
            + "binding is still there — which is exactly why this set is pinned in both "
            + "directions:" + Environment.NewLine + string.Join(Environment.NewLine, missing));

        live.Should().HaveCount(30,
            "16 mediation routes (Story 43-9) + the reveal route (Story 42-10, secret.read) "
            + "+ the 11 PR/issue verbs (Story 31-13: 7 PR-lifecycle routes + 4 issue callbacks) "
            + "+ the 2 formerly-unowned engine callbacks (43-17 follow-up: trigger-ci, execute-task) "
            + "opt into enforcement.");
    }

    [Test]
    public void EveryEnforcedEndpoint_AlsoCarriesABinding()
    {
        // The enforcement core FAILS CLOSED on this shape — an enforced route with
        // no IActionGateMetadata 409s every request with ACTION.GATE.MISCONFIGURED,
        // because "enforce this, but I cannot tell what it does" must not answer
        // "proceed". This test is what keeps that branch unreachable in a shipped
        // build: a wiring typo fails here, not in production.
        var unbound = EnforcedEndpoints()
            .Where(f => !f.IsGoverned)
            .Select(f => $"  {f.SiteKey}")
            .ToList();

        unbound.Should().BeEmpty(
            ".EnforcesGovernance() without .Governs(actionKey) is a permanent 409. Add the binding "
            + "or remove the opt-in:" + Environment.NewLine + string.Join(Environment.NewLine, unbound));
    }

    [Test]
    public void LlmCallRoute_IsBound_ButNotEnforced()
    {
        // AC3 arm (b) — the STRUCTURAL half of "Seam A never blocks".
        //
        // Arm (a) is a behaviour test (LlmCallSeam_NeverBlocks_EvenUnderEnforce):
        // set effect:llm.call to AlwaysHuman at every scope and the route still
        // returns 200. But a behaviour test alone survives a future author adding
        // the opt-in only if the filter happens to keep letting it through — it
        // pins the CONSEQUENCE, not the WIRING. This arm pins the wiring, so
        // "completing" Seam A goes red on the line that was actually written.
        var llmCall = GovernanceHostFixture.Endpoints
            .Where(f => f.SiteKey == "POST /api/v1/llm/call")
            .ToList();

        llmCall.Should().ContainSingle(
            "POST /api/v1/llm/call must exist — if this route moved, both arms of AC3 need "
            + "re-siting, not deleting");

        llmCall[0].IsGoverned.Should().BeTrue(
            "Seam A is BOUND: .Governs(effect:llm.call) is what lets the drift harnesses and the "
            + "admin UI see what this route performs");

        llmCall[0].EnforcesGovernance.Should().BeFalse(
            "SEAM A MUST NEVER BLOCK, IN ANY VERSION (AC3 / epic D2). A RequiresHuman here reaches "
            + "a DispatchWorkflow whose calling workflow has no human route in 44 of 45 cases — it "
            + "would suspend with nobody able to resume it — and blocking here AND at Seam E would "
            + "double-gate deploy, because the deployment pipeline reaches the model through this "
            + "very route while Seam E gates the prod-approval decision. Agent-action enforcement "
            + "lives ONLY at Seam E. If you added .EnforcesGovernance() here, remove it; the "
            + "handler already OBSERVES and audits.");
    }

    [Test]
    public void TheControllerPlane_hasAnOptInMechanism_evenThoughNoControllerOptsInYet()
    {
        // D15 reasoning #4: the two authoring shapes do not share a mechanism. An
        // IEndpointFilter does not run for an MVC endpoint, so a filter attached
        // inside Governs() would have enforced 17 routes and SILENTLY SKIPPED the
        // four [Governs] controller actions while reading as "all bindings are
        // enforced". The controller-plane opt-in therefore exists as an attribute.
        //
        // No controller opts in yet (see EnforcementOptedInRoutes' doc), so this
        // asserts the MECHANISM rather than a usage — deliberately, because a
        // mechanism nobody asserts is how the 17-vs-4 gap reappears.
        typeof(EnforcesGovernanceAttribute).Should()
            .Implement<IGovernanceEnforcementMetadata>(
                "the attribute must be discoverable through the SAME metadata lookup as the "
                + "minimal-API marker, or the sweep above cannot see a controller opt-in at all");

        typeof(Microsoft.AspNetCore.Mvc.Filters.IAsyncActionFilter)
            .IsAssignableFrom(typeof(EnforcesGovernanceAttribute)).Should().BeTrue(
                "an IEndpointFilter does not run for controller endpoints, so the controller "
                + "plane's opt-in must be an MVC filter. If this stops being true the attribute "
                + "becomes inert metadata: it would READ as enforcement and enforce nothing.");

        var controllerOptIns = GovernanceHostFixture.Endpoints
            .Where(f => f.Kind == GovernanceHostFixture.EndpointKind.ControllerAction
                        && f.EnforcesGovernance)
            .Select(f => $"  {f.SiteKey}")
            .ToList();

        controllerOptIns.Should().BeEmpty(
            "no controller action opts into enforcement in Story 43-9. If you added one, it is a "
            + "behaviour change on a human-operated UI surface: add it to "
            + "EnforcementOptedInRoutes with the reasoning, in the same commit:"
            + Environment.NewLine + string.Join(Environment.NewLine, controllerOptIns));
    }

    [Test]
    public void EnforcedRoutes_areASubsetOfBoundRoutes_andStrictlySmaller()
    {
        // The population relationship D15 creates, asserted rather than described:
        // enforcement is opt-in ON TOP of binding, and the two sets are NOT equal.
        // If they ever became equal, the most likely cause is that someone
        // re-attached the filter inside Governs() — the design this story
        // overturned — and Seam A would be blockable again.
        var bound = GovernanceHostFixture.Endpoints
            .Where(f => f.IsGoverned).Select(f => f.SiteKey)
            .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var enforced = EnforcedEndpoints().Select(f => f.SiteKey)
            .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

        enforced.Should().BeSubsetOf(bound,
            "an enforced route must be a bound route (the filter reads the binding to know which "
            + "action to evaluate)");

        enforced.Count.Should().BeLessThan(bound.Count,
            "binding and enforcing must stay two different populations. Equality is the signature "
            + "of enforcement having been folded back into .Governs(), which would make Seam A "
            + "blockable — the exact outcome epic decision D2 forbids.");
    }
}
