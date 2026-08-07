using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Core.Actions;
using Tamma.Core.Documents;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// Count pins for every Action Catalog vocabulary (Story 43-2 AC14, D10). Every
/// count was RE-DERIVED from the tree on 2026-07-27 — the derivation command is
/// recorded beside each pin so the next person can re-run it. The design's
/// figures (22, 25) were hypotheses; they survived re-derivation unchanged.
/// Growing a vocabulary is a deliberate, reviewed diff: bump the pin AND add the
/// descriptor (BuildIndex refuses to boot otherwise).
/// </summary>
[TestFixture]
public class ActionVocabularyCountTests
{
    [Test]
    public void ActionNamespace_has_6_members()
    {
        Enum.GetValues<ActionNamespace>().Should().HaveCount(6);
    }

    [Test]
    public void AgentAction_plane_has_96_members()
    {
        // Derivation: grep -c '\[Wire(' src/Tamma.Core/Agents/AgentAction.cs → 96.
        // 80 → 96 (Story 41-1a): the 16 Epic 41 tokens (incl. the 41-8 Phase B
        // write-retro-narrative lockstep cell).
        Enum.GetValues<AgentAction>().Should().HaveCount(96);
    }

    [Test]
    public void DocumentType_plane_has_17_members()
    {
        // Derivation: grep -c '\[Wire(' src/Tamma.Core/Documents/DocumentTypeKey.cs → 17.
        // 10 → 16 (Story 41-1b): AcceptanceCriteria, BacklogOrdering, SprintPlan,
        // TestPlan, ThreatModel, UxSpec. 16 → 17 (Story 41-1c): Prose.
        Enum.GetValues<DocumentTypeKey>().Should().HaveCount(17);
    }

    [Test]
    public void ToolAction_has_8_members()
    {
        // Derivation: grep -rn ': IToolExecutor' src --include=*.cs | grep -v Registry
        // → 7 implementations (6 DI-registered + the deliberately-unregistered
        // GetAcceptanceRulesTool), with git_operations split read/write → 8.
        Enum.GetValues<ToolAction>().Should().HaveCount(8);
    }

    [Test]
    public void ExternalEffect_has_61_members()
    {
        // Derivation: grep 'RequireAuthorization("EngineServiceOnly")'
        // src/Tamma.Api/Program.cs → 26 routes, 17 MUTATING (5 engine-group
        // writes + 12 app-level writes; the 9 GETs are not catalogued), plus
        // mcp.tool.invoke, secret.reveal, process.spawn, deploy.promote-prod,
        // deploy.rollback → 22. 22 → 25 (Story 41-30): the schedule.create /
        // schedule.update / schedule.delete admin trio.
        // 25 → 35 (Story 44-2): the NATIVE tracker's ten mutating routes —
        // tracker.project.{create,update,delete},
        // tracker.work-item.{create,update,delete,assign,set-status} and
        // tracker.preferences.{set,delete}. Derivation: the four Map{Post,Patch,
        // Delete,Put} calls in Program.cs's `tracker` group (the eight GETs are
        // reads and are not catalogued, matching the EngineServiceOnly rule
        // above). These write Tamma's OWN system of record — distinct from
        // git.issue.patch / jira.ticket.patch, which mutate external trackers.
        // 35 -> 39 (Story 43-8 AC1 step 2, 2026-07-30): the four MENTORSHIP SESSION
        // LIFECYCLE mutations — mentorship.session.{start,pause,resume,cancel}.
        // Derivation: the four [HttpPost] actions on MentorshipController, the repo's
        // ONLY attribute-routed controller (`ls src/Tamma.Api/Controllers/` -> one
        // file; the four [HttpGet] actions are reads and are not catalogued, matching
        // the EngineServiceOnly rule above). They were baselined `no-catalog-member`
        // when 43-8's harnesses landed; they are catalogued now because
        // POST /api/Mentorship/start DISPATCHES the tamma-autonomous-mentorship Elsa
        // workflow rather than merely writing a row, which is the same kind of
        // consequence as effect:schedule.create and effect:agent-dispatch.run.
        // 39 -> 47 (Story 43-12): the per-target merge/deploy zone-ladder edit.
        // RETIRED the two coarse effects git.pull-request.merge + deploy.promote-prod
        // (-2); MINTED git.merge.{dev,qa,main} (the merge splits by PR base branch,
        // 55/60/65), deploy.{dev,qa,uat,staging,prod} (the deploy splits by target
        // env, 70/75/80/85/90 — dev+staging RESERVED, no pipeline stage exists),
        // git.checks.bypass (50, reserved) and git.webhook.register (85, reserved,
        // DUAL-dormant) (+10).
        // 47 -> 48 (Story 42-10): + effect:secret.read (level 90, manage-secrets
        // zone) — an LLM reading a secret VALUE into model context (43-11
        // Amendment 4). effect:secret.reveal is NOT removed (it stays as the
        // machinery plumbing fetch), so this is +1, not a swap.
        // 48 -> 59 (Story 31-13): +11 PR + issue operations. The 7 PR verbs
        // git.pull-request.{close,reopen,comment,review-comment,request-reviewers,
        // label,set-draft} (source-control-write) and the 4 issue callbacks
        // git.issue.{create,comment,labels.set,labels.remove} (issue-tracking).
        // Enforceable-but-unbound descriptors (no .Governs binding yet — the same
        // green pattern effect:secret.read used when first minted).
        // 59 -> 61 (Story 43-17 follow-up): the two /api/engine callbacks that had
        // NO OWNER — ci.workflow.dispatch (POST /api/engine/trigger-ci) and
        // llm.task.execute (POST /api/engine/execute-task). Both are DISTINCT from
        // their mediation-route twins (ci.tests.trigger, llm.call) because an effect
        // binds at exactly one site; same effect class, so same levels (30, 20).
        Enum.GetValues<ExternalEffect>().Should().HaveCount(61);
    }

    [Test]
    public void BackgroundActor_has_31_members()
    {
        // 29 → 31 (Epic 31 P2, 2026-08-07): + PlatformDriverCacheInvalidator
        // (Story 31-2's designed cache-invalidation subscriber, built in P2)
        // and + GitHubInstallationBridgeBackfill (the seam-14 registry-
        // unification startup sweep) — both IHostedServices in Tamma.Api.
        // 28 → 29 (Story 43-5): + GovernancePolicySnapshotPrimingService — the
        // action-assignments snapshot's cold-start primer is itself an
        // IHostedService, and the sweep binds the governance machinery too.
        // 27 → 28 (Story 43-4): + ActionCatalogStartupValidator — the boot-time
        // tool-vocabulary check is itself an IHostedService, and the sweep
        // deliberately binds the governance machinery too.
        // 26 → 27 (Story 41-30): + TenantScheduledTriggerService.
        // Derivation: grep -rn 'AddHostedService' src --include=*.cs → 25
        // registrations (5 ElsaServer + 8 Api/Program.cs incl. one factory
        // overload and the Epic 46 review-F1 ProviderSettingsStorePrimingService
        // + 12 Api/Extensions) + PlatformTaskWorker (TryAddEnumerable
        // descriptor inside AddPlatformTaskWorker, no AddHostedService line)
        // → 26. Cross-checked: 26 non-abstract IHostedService classes exist
        // across both host assemblies (BackgroundActorCatalogSweepTests binds
        // them by type name). +1 (Story 41-30): TenantScheduledTriggerService.
        Enum.GetValues<BackgroundActor>().Should().HaveCount(31);
    }

    [Test]
    public void PlatformTaskKind_has_8_members()
    {
        // Derivation: grep -rln ': IPlatformTaskHandler' src --include=*.cs → 9
        // types, one of which is the registry (implements
        // IPlatformTaskHandlerRegistry, not IPlatformTaskHandler — 43-2 C4) → 8.
        Enum.GetValues<PlatformTaskKind>().Should().HaveCount(8);
    }

    [Test]
    public void GitSubcommand_has_14_members()
    {
        // Derivation: GitOperationsTool.AllowedSubcommands literal (GitOperationsTool.cs).
        Enum.GetValues<GitSubcommand>().Should().HaveCount(14);
    }

    [Test]
    public void ActionGroup_has_16_members()
    {
        // SIXTEEN, not fifteen (43-3 C1/D2): the epic README and design.md both
        // NAME sixteen groups while asserting "15" — and merging two semantically
        // distinct groups to hit a round number is exactly the
        // wrong-but-consistent partition this vocabulary exists to avoid. Do NOT
        // "correct" this downward; these wires become persisted vocabulary at 43-5.
        Enum.GetValues<ActionGroup>().Should().HaveCount(16);
    }

    [Test]
    public void TotalCatalogMembers_is_221()
    {
        // 96 + 17 + 8 + 61 + 31 + 8 = 221 — was 219 (automation 29): Epic 31 P2
        // added the two platform-plane hosted services (driver-cache invalidator +
        // installation-bridge backfill) as automation:* machinery members.
        // 96 + 17 + 8 + 61 + 29 + 8 = 219 — was 217 (effect 59): the 43-17 follow-up
        // catalogued the two unowned engine callbacks (see ExternalEffect_has_61_members).
        // 96 + 17 + 8 + 59 + 29 + 8 = 217 — was 206 (effect 48): Story 31-13 added
        // the 11 PR + issue-callback effects (see ExternalEffect_has_59_members).
        // 96 + 17 + 8 + 48 + 29 + 8 = 206 — was 205 (effect 47): Story 42-10 minted
        // effect:secret.read (level 90) — an LLM reading a secret value into model
        // context (43-11 Amendment 4); secret.reveal stays as machinery, so +1.
        // 205 — was 197 (effect 39): Story 43-12
        // retired 2 coarse effects (git.pull-request.merge, deploy.promote-prod) and
        // minted 10 per-target merge/deploy zone-ladder keys (see
        // ExternalEffect_has_47_members). Earlier: was 193 (effect 35): Story 43-8
        // added the four mentorship-session effects (see
        // ExternalEffect_has_39_members). Earlier: was 183 (effect 25): Story 44-2
        // added the native tracker's ten mutating routes to the effect plane
        // (see ExternalEffect_has_35_members for the derivation). Earlier:
        // was 182 (automation 28): Story 43-5
        // added automation:governance-policy-snapshot-priming-service. Earlier:
        // was 181 (document-type 16): Story 41-1c added document-type:prose;
        // was 180 (automation 27, Story 43-4 added
        // automation:action-catalog-startup-validator); was 154
        // (80 + 10 + 22 + 26 + …); the agent-action plane grew by 16 (Story
        // 41-1a), the document-type plane by 6 (Story 41-1b), and
        // effect/automation by 3 + 1 (Story 41-30).
        ActionCatalog.All.Should().HaveCount(221);
        ActionCatalog.ByKey.Should().HaveCount(221);
    }
}
