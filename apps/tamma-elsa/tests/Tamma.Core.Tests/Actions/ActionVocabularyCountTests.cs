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
    public void AgentAction_plane_has_80_members()
    {
        // Derivation: grep -c '\[Wire(' src/Tamma.Core/Agents/AgentAction.cs → 80.
        Enum.GetValues<AgentAction>().Should().HaveCount(80);
    }

    [Test]
    public void DocumentType_plane_has_10_members()
    {
        // Derivation: grep -c '\[Wire(' src/Tamma.Core/Documents/DocumentTypeKey.cs → 10.
        Enum.GetValues<DocumentTypeKey>().Should().HaveCount(10);
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
    public void ExternalEffect_has_22_members()
    {
        // Derivation: grep 'RequireAuthorization("EngineServiceOnly")'
        // src/Tamma.Api/Program.cs → 26 routes, 17 MUTATING (5 engine-group
        // writes + 12 app-level writes; the 9 GETs are not catalogued), plus
        // mcp.tool.invoke, secret.reveal, process.spawn, deploy.promote-prod,
        // deploy.rollback → 22.
        Enum.GetValues<ExternalEffect>().Should().HaveCount(22);
    }

    [Test]
    public void BackgroundActor_has_26_members()
    {
        // Derivation: grep -rn 'AddHostedService' src --include=*.cs → 25
        // registrations (5 ElsaServer + 8 Api/Program.cs incl. one factory
        // overload and the Epic 46 review-F1 ProviderSettingsStorePrimingService
        // + 12 Api/Extensions) + PlatformTaskWorker (TryAddEnumerable
        // descriptor inside AddPlatformTaskWorker, no AddHostedService line)
        // → 26. Cross-checked: 26 non-abstract IHostedService classes exist
        // across both host assemblies (BackgroundActorCatalogSweepTests binds
        // them by type name).
        Enum.GetValues<BackgroundActor>().Should().HaveCount(26);
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
    public void TotalCatalogMembers_is_154()
    {
        // 80 + 10 + 8 + 22 + 26 + 8 = 154 — the design's working figure (153)
        // plus the Epic 46 review-F1 ProviderSettingsStorePrimingService.
        ActionCatalog.All.Should().HaveCount(154);
        ActionCatalog.ByKey.Should().HaveCount(154);
    }
}
