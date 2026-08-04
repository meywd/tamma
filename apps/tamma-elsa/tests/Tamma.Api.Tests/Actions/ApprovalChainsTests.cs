using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Actions;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-14 (AC6/AC7) — the five-chain fixture and the chain-monotonicity
/// build-time test. Pure (no infra): the fixture ↔ catalog and the monotonicity
/// checker's red-capability are asserted directly.
/// </summary>
[TestFixture]
public class ApprovalChainsTests
{
    [Test]
    public void FiveChainsExist_WithTheExpectedNames()
    {
        ApprovalChains.All.Select(c => c.Name).Should().BeEquivalentTo(new[]
        {
            ApprovalChains.MergeComposite, ApprovalChains.DeployTail,
            ApprovalChains.Rotation, ApprovalChains.TenantMove, ApprovalChains.TenantDelete,
        });
    }

    [Test]
    public void MergeComposite_MintsThePerTargetMergeKeys_IssuePatch_AndBranchDelete()
    {
        var chain = ApprovalChains.Find(ApprovalChains.MergeComposite)!;
        chain.MintedTargetKeys.Should().BeEquivalentTo(new[]
        {
            "effect:git.merge.dev", "effect:git.merge.qa", "effect:git.merge.main",
            "effect:git.issue.patch", "effect:git.branch.delete",
        });
    }

    [Test]
    public void DeployTail_MintsProdDeployAndReleaseCreate()
    {
        var chain = ApprovalChains.Find(ApprovalChains.DeployTail)!;
        chain.MintedTargetKeys.Should().BeEquivalentTo(new[]
        {
            "effect:deploy.prod", "effect:git.release.create",
        });
    }

    [Test]
    public void MachineryChains_MintNothing_ButKeepTheSeam()
    {
        // Amendment 4 / caller-kind re-audit: rotation, tenant-move, tenant-delete
        // are entirely machinery — the gated-target set is EMPTY, with a
        // justification recorded so a future reclassification is a fixture edit.
        foreach (var name in new[]
                 { ApprovalChains.Rotation, ApprovalChains.TenantMove, ApprovalChains.TenantDelete })
        {
            var chain = ApprovalChains.Find(name)!;
            chain.MintedTargetKeys.Should().BeEmpty($"{name} is a machinery chain");
            chain.Justification.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Test]
    public void EveryMintedTargetResolvesInTheCatalog()
    {
        // Staleness guard: a minted key that no longer exists in the catalog (a
        // 43-12 re-point that missed the fixture) fails here.
        foreach (var chain in ApprovalChains.All)
        {
            foreach (var target in chain.MintedTargetKeys)
            {
                ApprovalChains.CatalogLevelOf(target).Should().NotBeNull(
                    $"minted target '{target}' of chain '{chain.Name}' must resolve to a "
                    + "non-machinery catalog action");
            }
        }
    }

    [Test]
    public void ProductionCatalog_HasNoMonotonicityViolations()
    {
        // AC7 — over the shipped catalog levels, no chain link exceeds its entry
        // approval unless covered by the head's grant or its own resumable wait.
        var violations = ApprovalChains.FindMonotonicityViolations();
        violations.Should().BeEmpty(
            "a level edit that breaks a chain's monotonicity must fail the build with the "
            + "chain named; violations: " + string.Join(" | ", violations));
    }

    [Test]
    public void Checker_SelfTest_FlagsASyntheticViolation()
    {
        // The red-capability proof: a synthetic chain with a level-95 link under a
        // level-65 entry, NOT in the minted set and with NO own resumable wait,
        // MUST be flagged. This member fails if the checker is a stub.
        var synthetic = new[]
        {
            new ApprovalChains.Chain(
                Name: "synthetic-bad",
                EntryApprovalActionWire: "effect:entry",
                Links: new[]
                {
                    new ApprovalChains.Link("effect:tail-95", HasOwnResumableHumanWait: false),
                },
                MintedTargetKeys: System.Array.Empty<string>(),
                Justification: "synthetic"),
        };

        int? Levels(string wire) => wire switch
        {
            "effect:entry" => 65,
            "effect:tail-95" => 95,
            _ => null,
        };

        var violations = ApprovalChains.FindMonotonicityViolations(synthetic, Levels);
        violations.Should().ContainSingle()
            .Which.Should().Contain("synthetic-bad").And.Contain("effect:tail-95");
    }

    [Test]
    public void Checker_AllowsAnAboveEntryLink_WhenCoveredByTheHeadGrant()
    {
        var chain = new[]
        {
            new ApprovalChains.Chain(
                Name: "covered",
                EntryApprovalActionWire: "effect:entry",
                Links: new[] { new ApprovalChains.Link("effect:tail-95") },
                MintedTargetKeys: new[] { "effect:tail-95" }, // covered by head grant
                Justification: "covered"),
        };
        int? Levels(string w) => w == "effect:entry" ? 65 : w == "effect:tail-95" ? 95 : (int?)null;
        ApprovalChains.FindMonotonicityViolations(chain, Levels).Should().BeEmpty();
    }

    [Test]
    public void Checker_AllowsAnAboveEntryLink_WhenItHasItsOwnResumableWait()
    {
        var chain = new[]
        {
            new ApprovalChains.Chain(
                Name: "own-wait",
                EntryApprovalActionWire: "effect:entry",
                Links: new[] { new ApprovalChains.Link("effect:tail-95", HasOwnResumableHumanWait: true) },
                MintedTargetKeys: System.Array.Empty<string>(),
                Justification: "own wait"),
        };
        int? Levels(string w) => w == "effect:entry" ? 65 : w == "effect:tail-95" ? 95 : (int?)null;
        ApprovalChains.FindMonotonicityViolations(chain, Levels).Should().BeEmpty();
    }
}
