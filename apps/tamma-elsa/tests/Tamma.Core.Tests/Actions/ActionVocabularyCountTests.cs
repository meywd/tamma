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
    public void DocumentType_plane_has_16_members()
    {
        // Derivation: grep -c '\[Wire(' src/Tamma.Core/Documents/DocumentTypeKey.cs → 16.
        // 10 → 16 (Story 41-1b): AcceptanceCriteria, BacklogOrdering, SprintPlan,
        // TestPlan, ThreatModel, UxSpec.
        Enum.GetValues<DocumentTypeKey>().Should().HaveCount(16);
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
    public void ExternalEffect_has_25_members()
    {
        // Derivation: grep 'RequireAuthorization("EngineServiceOnly")'
        // src/Tamma.Api/Program.cs → 26 routes, 17 MUTATING (5 engine-group
        // writes + 12 app-level writes; the 9 GETs are not catalogued), plus
        // mcp.tool.invoke, secret.reveal, process.spawn, deploy.promote-prod,
        // deploy.rollback → 22. 22 → 25 (Story 41-30): the schedule.create /
        // schedule.update / schedule.delete admin trio.
        Enum.GetValues<ExternalEffect>().Should().HaveCount(25);
    }

    [Test]
    public void BackgroundActor_has_28_members()
    {
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
        Enum.GetValues<BackgroundActor>().Should().HaveCount(28);
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
    public void TotalCatalogMembers_is_181()
    {
        // 96 + 16 + 8 + 25 + 28 + 8 = 181 — was 180 (automation 27): Story 43-4
        // added automation:action-catalog-startup-validator. Earlier: was 154
        // (80 + 10 + 22 + 26 + …); the agent-action plane grew by 16 (Story
        // 41-1a), the document-type plane by 6 (Story 41-1b), and
        // effect/automation by 3 + 1 (Story 41-30).
        ActionCatalog.All.Should().HaveCount(181);
        ActionCatalog.ByKey.Should().HaveCount(181);
    }
}
