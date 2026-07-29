using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Providers;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 41-1a AC5, second half. The AC names TWO alias-iteration paths that
/// must keep working once the <c>scrum_master → product_owner</c> entry left
/// <see cref="RolePhaseMap.LegacyRoleAliases"/>: <c>AgentResolverService.cs:702</c>
/// (pinned by <see cref="AgentAliasMigrationTests"/>) and
/// <c>ProviderChainResolver.cs:264</c> — pinned here, and previously unasserted.
///
/// <para>Both walks share a shape: read the canonical key first, then iterate
/// the alias table for legacy keys whose canonical value is the requested role.
/// Removing an entry therefore changes the walk in both directions — the
/// promoted name stops being harvested for its old target, and the old target
/// stops inheriting it. These tests pin both directions plus the control that
/// the retained aliases still fold, so a future edit to the table cannot
/// silently re-point a stored provider chain at a different role.</para>
///
/// <para>The final region pins the <see cref="RolePhaseMap.NormalizeRole"/>
/// case-variant semantics that fall out of the same removal — recorded
/// deliberately, see the comment on that test.</para>
/// </summary>
[TestFixture]
public class ProviderChainAliasMigrationTests
{
    private static readonly Guid TenantId = Guid.Parse("41a1a41a-0000-4000-8000-000000000041");

    // The legacy TS (Story 9-5 / 9-8) config shape is the one the alias walk
    // serves: roles.<key>.providerChain, with defaults.providerChain last.
    private const string Action = "implement-feature";

    private Mock<IAgentConfigRepository> _configRepo = null!;
    private ProviderChainResolver _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _configRepo = new Mock<IAgentConfigRepository>();

        var breaker = new Mock<ICircuitBreakerService>();
        breaker
            .Setup(b => b.GetStateAsync(
                It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, Guid? _, CancellationToken __) =>
                new CircuitBreakerStatus(key, CircuitBreakerState.Closed, 0, null, null, null, false));

        _sut = new ProviderChainResolver(_configRepo.Object, breaker.Object);
    }

    private void SetupConfig(string json) =>
        _configRepo
            .Setup(r => r.ResolveAsync(TenantId))
            .ReturnsAsync((new AgentConfig { TenantId = TenantId, Config = json }, "tenant"));

    /// <summary>A legacy-shape config carrying one provider chain under <paramref name="roleKey"/>.</summary>
    private static string LegacyChain(string roleKey, string provider) =>
        $$"""
        {
          "roles": {
            "{{roleKey}}": {
              "providerChain": [{"provider": "{{provider}}", "model": "m-1"}]
            }
          }
        }
        """;

    // -----------------------------------------------------------------------
    // scrum_master is now first-class on this path — not an alias fold
    // -----------------------------------------------------------------------

    [Test]
    public async Task ScrumMasterChain_IsFoundUnderItsOwnCanonicalKey()
    {
        // Post-41-1a the key is served by the CANONICAL branch
        // (ProviderChainResolver.cs:259, TryReadLegacy(root, role)). It cannot
        // have come from the alias walk at :264 — the table has no
        // scrum_master entry left to iterate.
        SetupConfig(LegacyChain("scrum_master", "openai"));

        var result = await _sut.ResolveAsync(TenantId, "scrum_master", Action);

        result.ErrorCode.Should().BeNull();
        result.Ordered.Should().ContainSingle()
            .Which.Provider.Provider.Should().Be("openai");
    }

    [Test]
    public async Task ScrumMasterAndProductOwnerChains_DoNotCrossContaminate()
    {
        // The sharpest form of "resolves as a first-class role, not via an
        // alias fold": with BOTH keys present each role gets its own chain.
        // Before the removal, requesting product_owner could be served the
        // scrum_master chain by the alias walk.
        SetupConfig("""
        {
          "roles": {
            "scrum_master":  {"providerChain": [{"provider": "openai",    "model": "m-1"}]},
            "product_owner": {"providerChain": [{"provider": "anthropic", "model": "m-2"}]}
          }
        }
        """);

        var scrumMaster = await _sut.ResolveAsync(TenantId, "scrum_master", Action);
        var productOwner = await _sut.ResolveAsync(TenantId, "product_owner", Action);

        scrumMaster.Ordered.Should().ContainSingle()
            .Which.Provider.Provider.Should().Be("openai");
        productOwner.Ordered.Should().ContainSingle()
            .Which.Provider.Provider.Should().Be("anthropic");
    }

    [Test]
    public async Task ProductOwnerRequest_NoLongerHarvests_TheScrumMasterChain()
    {
        // Only scrum_master is configured. product_owner's alias walk now
        // iterates analyst/researcher only, finds neither, and reports an
        // empty chain rather than borrowing another role's providers.
        SetupConfig(LegacyChain("scrum_master", "openai"));

        var result = await _sut.ResolveAsync(TenantId, "product_owner", Action);

        result.Ordered.Should().BeEmpty();
        result.ErrorCode.Should().Be("EMPTY_PROVIDER_CHAIN");
        result.AllExhausted.Should().BeTrue();
    }

    [Test]
    public async Task ProductOwnerRequest_FallsThroughToDefaults_NotTheScrumMasterChain()
    {
        // Same removal, observed against the final TS fallback
        // (defaults.providerChain): the tail wins because the alias walk no
        // longer matches scrum_master on the way past.
        SetupConfig("""
        {
          "roles": {
            "scrum_master": {"providerChain": [{"provider": "openai", "model": "m-1"}]}
          },
          "defaults": {"providerChain": [{"provider": "anthropic", "model": "m-2"}]}
        }
        """);

        var result = await _sut.ResolveAsync(TenantId, "product_owner", Action);

        result.Ordered.Should().ContainSingle()
            .Which.Provider.Provider.Should().Be("anthropic");
    }

    [Test]
    public async Task ScrumMasterRequest_DoesNotInherit_TheProductOwnerChain()
    {
        // The reverse direction of the same removal. No alias maps TO
        // scrum_master, so the walk contributes nothing and the request does
        // not fall sideways into product_owner's providers.
        SetupConfig(LegacyChain("product_owner", "anthropic"));

        var result = await _sut.ResolveAsync(TenantId, "scrum_master", Action);

        result.Ordered.Should().BeEmpty();
        result.ErrorCode.Should().Be("EMPTY_PROVIDER_CHAIN");
    }

    // -----------------------------------------------------------------------
    // Controls — the retained aliases still fold through the same walk
    // -----------------------------------------------------------------------

    [Test]
    public async Task AnalystChain_StillFoldsTo_ProductOwner_ThroughTheAliasWalk()
    {
        // The named control from AC5: only scrum_master was promoted. analyst
        // is not a canonical role, so this chain can ONLY have been reached by
        // the alias walk at :264 — proving the removal did not damage the walk.
        SetupConfig(LegacyChain("analyst", "openai"));

        var result = await _sut.ResolveAsync(TenantId, "product_owner", Action);

        result.ErrorCode.Should().BeNull();
        result.Ordered.Should().ContainSingle()
            .Which.Provider.Provider.Should().Be("openai");
        result.Ordered[0].Provider.Model.Should().Be("m-1");
    }

    [Test]
    public async Task EveryRetainedAlias_StillFoldsToItsCanonicalRole_ThroughTheAliasWalk()
    {
        // Driven off the live table rather than a literal list, so a future
        // promotion (the next scrum_master) is covered without editing this
        // test — it either keeps folding or the entry is gone.
        foreach (var (legacy, canonical) in RolePhaseMap.LegacyRoleAliases)
        {
            SetupConfig(LegacyChain(legacy, "openai"));

            var result = await _sut.ResolveAsync(TenantId, canonical, Action);

            result.Ordered.Should().ContainSingle(
                    $"a stored chain keyed '{legacy}' must still resolve for role '{canonical}'")
                .Which.Provider.Provider.Should().Be("openai");
        }
    }

    [Test]
    public void NoAliasEntry_ShadowsACanonicalRole()
    {
        // The invariant the scrum_master entry violated: an alias key that is
        // ALSO a canonical role must map to itself. Otherwise this walk hands
        // role X's stored chain to role Y — silently, since the canonical
        // branch is tried first only for the REQUESTED role, not for the keys
        // the walk consumes.
        foreach (var (legacy, canonical) in RolePhaseMap.LegacyRoleAliases)
        {
            if (RolePhaseMap.ValidRoles.Contains(legacy))
            {
                canonical.Should().Be(
                    legacy,
                    $"'{legacy}' is a canonical role, so the alias table must not re-point it at '{canonical}'");
            }

            RolePhaseMap.ValidRoles.Should().Contain(
                canonical,
                $"alias '{legacy}' must fold onto a real role");
        }

        RolePhaseMap.LegacyRoleAliases.Should().NotContainKey(
            "scrum_master",
            "Story 41-1a removed it — scrum_master is a first-class AgentRole now");
    }

    // -----------------------------------------------------------------------
    // Case-variant semantics — PINNING current behaviour, not endorsing it
    // -----------------------------------------------------------------------

    [Test]
    public void NormalizeRole_UppercaseScrumMaster_PassesThroughAndThenThrows()
    {
        // RECORDED, NOT ACCIDENTAL. NormalizeRole (RolePhaseMap.cs:320) checks
        // the ORDINAL ValidRoles set before the OrdinalIgnoreCase alias table.
        // A stored case variant "SCRUM_MASTER" therefore misses ValidRoles
        // (ordinal), misses the alias table (entry removed by 41-1a), and is
        // returned unchanged — so the next AssertValidRole throws.
        //
        // Before 41-1a the same input FOLDED silently to product_owner via the
        // case-insensitive alias table. The change from fold to throw is the
        // intended posture: a loud "Unknown role" beats silently running the
        // wrong agent with the wrong provider chain and prompt cells. Any
        // future decision to make ValidRoles case-insensitive must flip this
        // test deliberately.
        RolePhaseMap.NormalizeRole("SCRUM_MASTER").Should().Be("SCRUM_MASTER");

        var assert = () => RolePhaseMap.AssertValidRole(RolePhaseMap.NormalizeRole("SCRUM_MASTER"));
        assert.Should().Throw<ArgumentException>()
            .WithMessage("Unknown role: 'SCRUM_MASTER'.*");

        // Same verdict at the typed-parse boundary (EnumWire is ordinal too).
        var parse = () => AgentRoleExtensions.Parse("SCRUM_MASTER");
        parse.Should().Throw<ArgumentException>()
            .WithMessage("Unknown role: 'SCRUM_MASTER'.*");
    }

    [Test]
    public void NormalizeRole_UppercaseRetainedAlias_StillFolds()
    {
        // The asymmetry the test above records: a RETAINED alias still folds
        // case-insensitively, because it is matched by the alias table rather
        // than the ordinal ValidRoles set. Only names promoted into ValidRoles
        // lose their case tolerance.
        RolePhaseMap.NormalizeRole("ANALYST").Should().Be("product_owner");
        RolePhaseMap.NormalizeRole("Researcher").Should().Be("product_owner");
    }

    [Test]
    public async Task ChainLookup_IsOrdinal_UppercaseRoleKeyFindsNoChain()
    {
        // The same case sensitivity on the chain path: ProviderChainResolver
        // never normalises, and JSON property lookup is ordinal, so an
        // uppercase request finds neither the canonical key nor an alias.
        // Fail-loud (EMPTY_PROVIDER_CHAIN) rather than a wrong-role chain.
        SetupConfig(LegacyChain("scrum_master", "openai"));

        var result = await _sut.ResolveAsync(TenantId, "SCRUM_MASTER", Action);

        result.Ordered.Should().BeEmpty();
        result.ErrorCode.Should().Be("EMPTY_PROVIDER_CHAIN");
    }
}
