using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Actions;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// Story 43-11 (the zone model) — the reviewable pin of every dial-governed
/// action's level, and the property that makes the dial mean something: the set
/// of automated actions strictly grows as the dial rises. Changing a level is a
/// two-file, reviewed diff (the descriptor AND <see cref="LevelTable"/>).
///
/// <para>The 42 machinery rows (Story 43-13 <c>IsMachinery</c>) are OFF the dial:
/// they carry no level semantics and are excluded from every quantifier here.</para>
/// </summary>
[TestFixture]
public class ActionCatalogLevelTests
{
    /// <summary>Shipped default dial (a fresh deployment). NOT AutonomyDial.Min.</summary>
    private const int DefaultDial = AcceptanceDefaults.DefaultAutonomyLevel; // 70

    private static IReadOnlyList<ActionDescriptor> DialRows =>
        ActionCatalog.All.Where(d => !d.IsMachinery).ToList();

    /// <summary>Actions automated at dial <paramref name="dial"/> (dial rows only).</summary>
    private static ISet<string> Automated(int dial) =>
        DialRows.Where(d => dial >= d.DefaultMinAutonomy).Select(d => d.Key.ToWire()).ToHashSet();

    /// <summary>
    /// THE explicit (actionKey → zone level) table for all 177 dial rows (was 175; 43-17 follow-up +2 engine-callback effect keys at 20/30; was 164; Story 31-13 +11 PR/issue effect keys, all dial rows at 35/40; was 163; Story 42-10 +1 effect:secret.read at 90; was 155; Story 43-12 +10 per-target merge/deploy keys −2 retired coarse keys)
    /// (Story 43-11 AC4, re-audit: 217 − 42 machinery). Transcribed from the
    /// zone-model derivation, NOT generated from the catalog — it is the
    /// independent pin the descriptor is compared against.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> LevelTable = new Dictionary<string, int>
    {
        // ── Level 5 — read-only (42) ──
        ["agent-action:analyze-assessment-response"] = 5,
        ["agent-action:analyze-security-incident"] = 5,
        ["agent-action:assess-capacity"] = 5,
        ["agent-action:assess-technical-risk"] = 5,
        ["agent-action:assess-vulnerability"] = 5,
        ["agent-action:audit-accessibility"] = 5,
        ["agent-action:audit-dependencies"] = 5,
        ["agent-action:clarify-requirements"] = 5,
        ["agent-action:context-scan"] = 5,
        ["agent-action:coordinate-release"] = 5,
        ["agent-action:create-tasks"] = 5,
        ["agent-action:debug-rootcause"] = 5,
        ["agent-action:decompose-issue"] = 5,
        ["agent-action:define-acceptance-criteria"] = 5,
        ["agent-action:diagnose-incident"] = 5,
        ["agent-action:facilitate-retro"] = 5,
        ["agent-action:generate-assessment-questions"] = 5,
        ["agent-action:incident-rootcause"] = 5,
        ["agent-action:manage-regression"] = 5,
        ["agent-action:monitor-health"] = 5,
        ["agent-action:plan-debugging"] = 5,
        ["agent-action:plan-deployment"] = 5,
        ["agent-action:plan-incident-response"] = 5,
        ["agent-action:plan-roadmap"] = 5,
        ["agent-action:plan-scope"] = 5,
        ["agent-action:plan-test-strategy"] = 5,
        ["agent-action:prioritize-backlog"] = 5,
        ["agent-action:research"] = 5,
        ["agent-action:resolve-blocker"] = 5,
        ["agent-action:score-ambiguity"] = 5,
        ["agent-action:threat-model"] = 5,
        ["agent-action:track-impediments"] = 5,
        ["agent-action:triage-context-scan"] = 5,
        ["agent-action:triage-defect"] = 5,
        ["agent-action:triage-intake"] = 5,
        ["agent-action:triage-pr"] = 5,
        ["agent-action:triage-tech-debt"] = 5,
        ["agent-action:triage-technical"] = 5,
        ["tool:file_read"] = 5,
        ["tool:get_acceptance_rules"] = 5,
        ["tool:git_operations.read"] = 5,
        ["tool:search_code"] = 5,
        // ── Level 10 — sensitive metadata reads (1) ──
        ["agent-action:audit-secrets"] = 10,
        // ── Level 15 — write documentation (13) ──
        ["agent-action:report-status"] = 15,
        ["agent-action:summarize-changes"] = 15,
        ["agent-action:summarize-stakeholder"] = 15,
        ["agent-action:summarize-technical"] = 15,
        ["agent-action:synthesize-standup"] = 15,
        ["agent-action:update-changelog"] = 15,
        ["agent-action:write-adr"] = 15,
        ["agent-action:write-api-docs"] = 15,
        ["agent-action:write-postmortem"] = 15,
        ["agent-action:write-release-notes"] = 15,
        ["agent-action:write-retro-narrative"] = 15,
        ["agent-action:write-runbook"] = 15,
        ["agent-action:write-user-docs"] = 15,
        // ── Level 20 — write Tamma's own records (16) ──
        ["effect:llm.call"] = 20,
        // 43-17 follow-up — the engine-callback twin (POST /api/engine/execute-task).
        // Same class as llm.call (it runs an LLM, and can enable TOOLS), same level.
        ["effect:llm.task.execute"] = 20,
        ["effect:mentorship.session.cancel"] = 20,
        ["effect:mentorship.session.pause"] = 20,
        ["effect:mentorship.session.resume"] = 20,
        ["effect:mentorship.session.start"] = 20,
        ["effect:schedule.create"] = 20,
        ["effect:schedule.delete"] = 20,
        ["effect:schedule.update"] = 20,
        ["effect:tracker.preferences.delete"] = 20,
        ["effect:tracker.preferences.set"] = 20,
        ["effect:tracker.project.create"] = 20,
        ["effect:tracker.project.update"] = 20,
        ["effect:tracker.work-item.assign"] = 20,
        ["effect:tracker.work-item.create"] = 20,
        ["effect:tracker.work-item.set-status"] = 20,
        ["effect:tracker.work-item.update"] = 20,
        // ── Level 25 — write code on a branch (24) ──
        ["agent-action:address-review-comments"] = 25,
        ["agent-action:author-ui-spec"] = 25,
        ["agent-action:design-api-contract"] = 25,
        ["agent-action:design-data-model"] = 25,
        ["agent-action:design-integration"] = 25,
        ["agent-action:design-system"] = 25,
        ["agent-action:draft-user-flow"] = 25,
        ["agent-action:implement-feature"] = 25,
        ["agent-action:implement-fix"] = 25,
        ["agent-action:implement-infrastructure"] = 25,
        ["agent-action:incorporate-answers"] = 25,
        ["agent-action:plan-fix"] = 25,
        ["agent-action:plan-implementation"] = 25,
        ["agent-action:plan-migration-strategy"] = 25,
        ["agent-action:plan-refactor"] = 25,
        ["agent-action:plan-sprint"] = 25,
        ["agent-action:plan-system-design"] = 25,
        ["agent-action:propose-design"] = 25,
        ["agent-action:refactor"] = 25,
        ["agent-action:write-regression-test"] = 25,
        ["agent-action:write-test-cases"] = 25,
        ["agent-action:write-tests"] = 25,
        ["tool:file_write"] = 25,
        ["tool:git_operations.write"] = 25,
        // ── Level 30 — run tests (4) ──
        ["agent-action:debug"] = 30,
        ["agent-action:exploratory-test"] = 30,
        ["effect:ci.tests.trigger"] = 30,
        // 43-17 follow-up — the engine-callback twin (POST /api/engine/trigger-ci).
        ["effect:ci.workflow.dispatch"] = 30,
        ["tool:run_tests"] = 30,
        // ── Level 35 — create branch / PR (15) ──
        ["effect:git.branch.create"] = 35,
        ["effect:git.issue.patch"] = 35,
        ["effect:git.pull-request.create"] = 35,
        ["effect:git.release.create"] = 35,
        ["effect:jira.ticket.patch"] = 35,
        // 31-13 — PR operation verbs + issue callbacks (review-comment is at 40).
        ["effect:git.pull-request.close"] = 35,
        ["effect:git.pull-request.reopen"] = 35,
        ["effect:git.pull-request.comment"] = 35,
        ["effect:git.pull-request.request-reviewers"] = 35,
        ["effect:git.pull-request.label"] = 35,
        ["effect:git.pull-request.set-draft"] = 35,
        ["effect:git.issue.create"] = 35,
        ["effect:git.issue.comment"] = 35,
        ["effect:git.issue.labels.set"] = 35,
        ["effect:git.issue.labels.remove"] = 35,
        // ── Level 40 — approve PRs / routine docs (28) ──
        // 31-13 — PR review output sits in the "Approve PRs" zone.
        ["effect:git.pull-request.review-comment"] = 40,
        ["agent-action:code-review"] = 40,
        ["agent-action:code-review-architecture"] = 40,
        ["agent-action:code-review-coverage"] = 40,
        ["agent-action:code-review-security"] = 40,
        ["agent-action:mentor-feedback"] = 40,
        ["agent-action:plan-review"] = 40,
        ["agent-action:plan-review-security"] = 40,
        ["agent-action:review-acceptance"] = 40,
        ["agent-action:review-compliance"] = 40,
        ["agent-action:review-design"] = 40,
        ["agent-action:review-docs"] = 40,
        ["agent-action:review-feasibility"] = 40,
        ["agent-action:review-operability"] = 40,
        ["agent-action:review-scope"] = 40,
        ["agent-action:review-testability"] = 40,
        ["agent-action:self-review"] = 40,
        ["agent-action:verify-acceptance"] = 40,
        ["document-type:ambiguity-assessment"] = 40,
        ["document-type:backlog-ordering"] = 40,
        ["document-type:clarification"] = 40,
        ["document-type:decomposition"] = 40,
        ["document-type:diagnosis"] = 40,
        ["document-type:findings"] = 40,
        ["document-type:prose"] = 40,
        ["document-type:test-plan"] = 40,
        ["document-type:test-spec"] = 40,
        ["document-type:triage-decision"] = 40,
        // ── Level 45 — approve binding docs (6) ──
        ["document-type:acceptance-criteria"] = 45,
        ["document-type:design"] = 45,
        ["document-type:plan"] = 45,
        ["document-type:review"] = 45,
        ["document-type:threat-model"] = 45,
        ["document-type:ux-spec"] = 45,
        // ── Level 50 — bypass PR checks (2) ──
        ["agent-action:configure-cicd"] = 50,
        // 43-12 — the reserved checks-bypass key (no performer in the tree yet).
        ["effect:git.checks.bypass"] = 50,
        // ── Level 55 — merge to dev (1) — 43-12 per-target merge split ──
        ["effect:git.merge.dev"] = 55,
        // ── Level 60 — merge to qa (1) — 43-12 ──
        ["effect:git.merge.qa"] = 60,
        // ── Level 65 — merge to main (1) — 43-12 (was the coarse git.pull-request.merge) ──
        ["effect:git.merge.main"] = 65,
        // ── Level 70 — deploy dev (1) — 43-12; RESERVED (no dev pipeline stage) ──
        ["effect:deploy.dev"] = 70,
        // ── Level 75 — external messages (3) + deploy qa (1, 43-12) ──
        ["effect:engine.channel-outbox.enqueue"] = 75,
        ["effect:notify.email.send"] = 75,
        ["effect:notify.slack.queue"] = 75,
        ["effect:deploy.qa"] = 75,
        // ── Level 80 — unbounded execution (4) + deploy uat (1, 43-12) ──
        ["effect:agent-dispatch.run"] = 80,
        ["effect:mcp.tool.invoke"] = 80,
        ["effect:process.spawn"] = 80,
        ["tool:shell_execute"] = 80,
        ["effect:deploy.uat"] = 80,
        // ── Level 85 — deploy staging + register webhook (2, 43-12) — both RESERVED /
        //    create-infrastructure zone (deploy.staging has no pipeline stage;
        //    git.webhook.register is DUAL-dormant) ──
        ["effect:deploy.staging"] = 85,
        ["effect:git.webhook.register"] = 85,
        // ── Level 90 — deploy prod (2) — 43-12 (was the coarse deploy.promote-prod) —
        //    plus manage-secrets read (1) — 42-10 ──
        ["agent-action:deploy"] = 90,
        ["effect:deploy.prod"] = 90,
        // 42-10 — an LLM reading a secret VALUE into model context (43-11 Amendment 4:
        // "secret read is ONE action at 90", manage-secrets zone).
        ["effect:secret.read"] = 90,
        // ── Level 95 — delete / rollback / hard deletes (6) ──
        ["agent-action:rollback"] = 95,
        ["document-type:sprint-plan"] = 95,
        ["effect:deploy.rollback"] = 95,
        ["effect:git.branch.delete"] = 95,
        ["effect:tracker.project.delete"] = 95,
        ["effect:tracker.work-item.delete"] = 95,
    };

    [TestCase(true, ShellExecutionProfile.SandboxedLevel)]
    [TestCase(false, ShellExecutionProfile.UnsandboxedLevel)]
    public void ShellAndProcessSpawn_ShippedLevel_IsProfileDependent(bool sandboxed, int expected)
    {
        // Story 42-10 (AC3, D9) — both profile arms in ONE run via the
        // BuildDescriptors(int) seam (the static catalog freezes at the shipped
        // 80 in a test process, so the sandboxed 40 arm cannot be read from it).
        var descriptors = ActionCatalog.BuildDescriptors(
            sandboxed ? ShellExecutionProfile.SandboxedLevel : ShellExecutionProfile.UnsandboxedLevel);

        int LevelOf(string wire) =>
            descriptors.Single(d => d.Key.ToWire() == wire).DefaultMinAutonomy;

        LevelOf("tool:shell_execute").Should().Be(expected);
        LevelOf("effect:process.spawn").Should().Be(expected,
            "process.spawn shares the shell executor, so it earns the same profile-dependent level");
    }

    [Test]
    public void LevelTable_PinsTheShippedUnsandboxedShellLevel()
    {
        // The static LevelTable above pins the SHIPPED (unsandboxed) 80 for the two
        // shell rows — the sandboxed 40 arm is a deployment opt-in, exercised by the
        // parameterized test above, never the frozen shipped catalog.
        LevelTable["tool:shell_execute"].Should().Be(ShellExecutionProfile.UnsandboxedLevel);
        LevelTable["effect:process.spawn"].Should().Be(ShellExecutionProfile.UnsandboxedLevel);
    }

    [Test]
    public void EveryDialAction_HasItsAssignedLevel()
    {
        var actual = DialRows.ToDictionary(d => d.Key.ToWire(), d => d.DefaultMinAutonomy);

        var missing = LevelTable.Keys.Except(actual.Keys).ToList();
        var extra = actual.Keys.Except(LevelTable.Keys).ToList();
        var mismatched = LevelTable
            .Where(kv => actual.TryGetValue(kv.Key, out var v) && v != kv.Value)
            .Select(kv => $"{kv.Key}: table={kv.Value} catalog={actual[kv.Key]}")
            .ToList();

        missing.Should().BeEmpty("keys in the table with no catalog dial row");
        extra.Should().BeEmpty("catalog dial rows absent from the table");
        mismatched.Should().BeEmpty("a level moved without updating BOTH the descriptor and this table");
        actual.Should().HaveCount(177, "177 = 219 catalog rows − 42 machinery (43-17 follow-up: +2 engine-callback keys, ci.workflow.dispatch 30 and llm.task.execute 20; Story 31-13: +11 PR/issue effect keys, all dial rows at 35/40; Story 42-10: +1 effect:secret.read at 90; Story 43-12: +10 per-target merge/deploy keys − 2 retired coarse keys)");
    }

    [Test]
    public void MachineryRows_AreOffTheDial()
    {
        // The 42 machinery rows are excluded from the dial by IsMachinery (43-13);
        // none appears in the level table, and none is a dial row here.
        ActionCatalog.All.Count(d => d.IsMachinery).Should().Be(42);
        ActionCatalog.All.Where(d => d.IsMachinery).Select(d => d.Key.ToWire())
            .Should().OnlyContain(k => !LevelTable.ContainsKey(k));
    }

    [Test]
    public void RaisingTheDial_AutomatesStrictlyMore()
    {
        // THE load-bearing property (43-11 AC5): the dial demonstrably does
        // something. FALSE before the remap (every action was at Min → the set was
        // constant across [70,100]); true only after it.

        // Automated(default dial) is a STRICT subset of Automated(max):
        // a subset, AND strictly smaller.
        var atDefault = Automated(DefaultDial);
        var atMax = Automated(AutonomyDial.Max);
        atDefault.Should().BeSubsetOf(atMax);
        atDefault.Count.Should().BeLessThan(atMax.Count,
            "raising the dial from 70 to 100 automates strictly more (false before the remap)");

        // At Min nothing is automated; at Max the whole dial catalog is.
        Automated(AutonomyDial.Min).Should().BeEmpty("the lowest assigned level is 5, above Min=1");
        Automated(AutonomyDial.Max).Should().HaveCount(DialRows.Count, "at 100 everything automates");

        // Monotone: no dial position ever automates LESS than the one below it.
        var levels = AutonomyDial.ValidLevels().ToList();
        var growthPoints = 0;
        for (var i = 1; i < levels.Count; i++)
        {
            var lower = Automated(levels[i - 1]);
            var higher = Automated(levels[i]);
            lower.Should().BeSubsetOf(higher, $"dial {levels[i]} must not automate less than {levels[i - 1]}");
            if (higher.Count > lower.Count) growthPoints++;
        }

        // The number of dial positions at which the automated set STRICTLY grows —
        // one per distinct assigned level — is a coarse anti-collapse guard.
        growthPoints.Should().BeGreaterThanOrEqualTo(10,
            "collapsing the table back toward one value would drop this below 10");
    }

    [Test]
    public void LevelDistribution_IsNotCollapsedTowardOneValue()
    {
        // 43-11 AC3's coarse guard (replacing EveryOtherMember_DefaultsToMin): the
        // catalog uses many distinct levels and no single value dominates — a loud
        // failure if someone reverts the table toward one number while leaving the
        // levels in place.
        var byLevel = DialRows.GroupBy(d => d.DefaultMinAutonomy).ToList();
        byLevel.Count.Should().BeGreaterThanOrEqualTo(10, "at least 10 distinct dial levels");
        byLevel.Max(g => g.Count()).Should().BeLessThan((int)(DialRows.Count * 0.40),
            "no single level may cover more than 40% of the dial catalog");
    }

    // ── AC9 — the migration decision table (upward moves above the default dial) ──

    private enum MoveDecision { Accept, Rebase }

    private static readonly IReadOnlyDictionary<string, (MoveDecision Decision, string Reason)> UpwardMoveDecisions =
        new Dictionary<string, (MoveDecision, string)>
        {
            ["effect:notify.slack.queue"] = (MoveDecision.Accept, "external message leaves the deployment — honest gate at 75"),
            ["effect:notify.email.send"] = (MoveDecision.Accept, "a sent email cannot be unsent — honest gate at 75"),
            ["effect:engine.channel-outbox.enqueue"] = (MoveDecision.Accept, "enqueue becomes a sent message once the sweeper drains — same 75 as notify"),
            ["tool:shell_execute"] = (MoveDecision.Accept, "arbitrary shell holding the deployment's secrets (OQ1/Amendment-2D) — 80; a working autonomous run raises its dial or sandboxes"),
            ["effect:process.spawn"] = (MoveDecision.Accept, "same executor as shell_execute — 80"),
            ["effect:agent-dispatch.run"] = (MoveDecision.Accept, "external agent run, unbounded reach — 80"),
            ["effect:mcp.tool.invoke"] = (MoveDecision.Accept, "unbounded reach + no CI drift signal (2026-07-30 MCP decision survives in substance) — 80"),
            ["agent-action:deploy"] = (MoveDecision.Accept, "production/tenant, irreversibly — 90"),
            // 43-12 — the coarse deploy.promote-prod split into per-env keys. Only the
            // envs ABOVE the default dial (70) carry a decision here: qa 75, uat 80,
            // staging 85, prod 90 (dev 70 sits AT the default, not above it).
            ["effect:deploy.qa"] = (MoveDecision.Accept, "qa stage transition — 75; a working autonomous run raises its dial"),
            ["effect:deploy.uat"] = (MoveDecision.Accept, "uat stage transition — 80"),
            ["effect:deploy.staging"] = (MoveDecision.Accept, "staging deploy — 85 (RESERVED: no staging pipeline stage exists yet)"),
            ["effect:deploy.prod"] = (MoveDecision.Accept, "production promotion — 90; the business-mode gate is untouched and joins by OR"),
            ["effect:git.webhook.register"] = (MoveDecision.Accept, "registers a durable ingress path (create-infrastructure zone) — 85 (RESERVED / DUAL-dormant)"),
            // 42-10 — an LLM reading a secret value into model context (manage-secrets zone).
            ["effect:secret.read"] = (MoveDecision.Accept, "a secret value in a model transcript can leak, and cannot be un-read — 90"),
            ["agent-action:rollback"] = (MoveDecision.Accept, "production rollback — 95"),
            ["effect:deploy.rollback"] = (MoveDecision.Accept, "production rollback branch — 95"),
            ["effect:git.branch.delete"] = (MoveDecision.Accept, "destroys something outside the deployment — 95"),
            ["effect:tracker.project.delete"] = (MoveDecision.Accept, "irreversible destroy of user work — 95"),
            ["effect:tracker.work-item.delete"] = (MoveDecision.Accept, "irreversible destroy of user work — 95"),
            ["document-type:sprint-plan"] = (MoveDecision.Rebase, "product owner 2026-08-03: sprint-plan acceptance is 95 — never orchestrator-approved below a near-max dial"),
        };

    [Test]
    public void NoUpwardMove_IsUndecided()
    {
        // 43-11 AC9 (ContractBindingTests ratchet): every dial action whose level
        // exceeds the shipped default dial must carry a recorded ACCEPT/REBASE; a
        // row naming an action that no longer moves up is stale and fails.
        var actualUpward = DialRows
            .Where(d => d.DefaultMinAutonomy > DefaultDial)
            .Select(d => d.Key.ToWire())
            .ToHashSet();

        actualUpward.Should().BeEquivalentTo(UpwardMoveDecisions.Keys,
            "every action above the default dial (70) must have a decision, and no decision may be stale");
    }

    // ── 43-16 AC7 — the acceptance day-one-loosening decisions (the three
    //    formerly human-pinned document types) ──

    private static readonly IReadOnlyDictionary<string, (MoveDecision Decision, string Signer, string Reason)>
        AcceptanceDayOneLooseningDecisions = new Dictionary<string, (MoveDecision, string, string)>
        {
            ["document-type:design"] = (MoveDecision.Accept, "product owner 2026-08-03",
                "45 automates design acceptance at dial ≥45 (loosens at the default 70) — resolved-by-default, the zone model's answer"),
            ["document-type:threat-model"] = (MoveDecision.Accept, "product owner 2026-08-03",
                "45 automates threat-model acceptance at dial ≥45 (loosens at the default 70) — resolved-by-default"),
            ["document-type:sprint-plan"] = (MoveDecision.Rebase, "product owner 2026-08-03",
                "rebased to 95 (above the default 70) — sprint acceptance stays human until a near-max dial"),
        };

    [Test]
    public void AcceptanceDayOneLoosening_IsDecided_AndNotStale()
    {
        // 43-16 AC7 / D4: the three formerly-human-pinned document types each carry
        // a signed ACCEPT (level ≤ default dial — the loosening is intended) or
        // REBASE (level > default dial — day-one behaviour preserved). A missing,
        // undecided, or stale row fails the build.
        var formerlyHumanPinned = new[]
        {
            "document-type:design", "document-type:sprint-plan", "document-type:threat-model",
        };
        AcceptanceDayOneLooseningDecisions.Keys.Should().BeEquivalentTo(formerlyHumanPinned);

        foreach (var (key, (decision, signer, _)) in AcceptanceDayOneLooseningDecisions)
        {
            signer.Should().NotBeNullOrWhiteSpace("an undecided row has no signer");
            var level = ActionCatalog.Get(ActionKey.Parse(key)).DefaultMinAutonomy;
            AutonomyDial.IsValidLevel(level).Should().BeTrue($"{key} must sit at a valid level");
            if (decision == MoveDecision.Accept)
                level.Should().BeLessThanOrEqualTo(DefaultDial,
                    $"{key} is ACCEPT — it must automate at the default dial (level ≤ {DefaultDial})");
            else
                level.Should().BeGreaterThan(DefaultDial,
                    $"{key} is REBASE — its level must be above the default dial ({DefaultDial})");
        }
    }
}
