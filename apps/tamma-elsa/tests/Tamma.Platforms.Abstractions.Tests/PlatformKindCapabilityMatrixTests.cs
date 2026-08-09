using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.Abstractions.Tests;

/// <summary>
/// Story 31-1 AC4: every <see cref="PlatformKind"/> has a non-empty
/// matrix entry, and the values match the brief's table. Asserting
/// by enum members (not by hard-coded count) survives future
/// additions.
/// </summary>
[TestFixture]
public sealed class PlatformKindCapabilityMatrixTests
{
    [Test]
    public void Every_platform_kind_has_a_matrix_entry()
    {
        foreach (var kind in Enum.GetValues<PlatformKind>())
        {
            Action act = () => PlatformKindCapabilityMatrix.DefaultsFor(kind);
            act.Should().NotThrow($"PlatformKind.{kind} must be in the matrix");
        }
    }

    [Test]
    public void All_returns_one_entry_per_kind()
    {
        PlatformKindCapabilityMatrix.All.Keys
            .Should().BeEquivalentTo(Enum.GetValues<PlatformKind>());
    }

    [Test]
    public void Every_matrix_entry_is_non_empty()
    {
        foreach (var kind in Enum.GetValues<PlatformKind>())
        {
            PlatformKindCapabilityMatrix.DefaultsFor(kind)
                .Should().NotBeEmpty($"PlatformKind.{kind} must advertise capabilities");
        }
    }

    [Test]
    public void GitHub_has_libsodium_secrets()
    {
        PlatformKindCapabilityMatrix
            .Supports(PlatformKind.GitHub, PlatformCapability.LibsodiumSecrets)
            .Should().BeTrue("only GitHub uses libsodium sealed-box for secret writes");
    }

    [Test]
    public void Only_GitHub_has_libsodium_secrets()
    {
        foreach (var kind in Enum.GetValues<PlatformKind>())
        {
            if (kind == PlatformKind.GitHub) continue;
            PlatformKindCapabilityMatrix
                .Supports(kind, PlatformCapability.LibsodiumSecrets)
                .Should().BeFalse($"PlatformKind.{kind} does not use libsodium");
        }
    }

    [Test]
    public void Only_GitLab_has_webhook_static_token()
    {
        foreach (var kind in Enum.GetValues<PlatformKind>())
        {
            var expected = kind == PlatformKind.GitLab;
            PlatformKindCapabilityMatrix
                .Supports(kind, PlatformCapability.WebhookStaticToken)
                .Should().Be(expected,
                    $"PlatformKind.{kind} {(expected ? "uses" : "does not use")} static-token webhooks");
        }
    }

    [Test]
    public void All_platforms_advertise_actions_capability()
    {
        foreach (var kind in Enum.GetValues<PlatformKind>())
        {
            PlatformKindCapabilityMatrix
                .Supports(kind, PlatformCapability.Actions)
                .Should().BeTrue($"every platform in the matrix has CI dispatch (PlatformKind.{kind})");
        }
    }

    [Test]
    public void All_platforms_advertise_list_accessible_repos()
    {
        // Onboarding picker (31-9) depends on this for every kind.
        foreach (var kind in Enum.GetValues<PlatformKind>())
        {
            PlatformKindCapabilityMatrix
                .Supports(kind, PlatformCapability.ListAccessibleRepos)
                .Should().BeTrue();
        }
    }

    [Test]
    public void PrLifecycle_IsAdvertisedByExactlyTheDriversThatPerformIt()
    {
        // Story 31-13 shipped GitHub; Epic 31 P5 M1 made Gitea real (PATCH
        // state / requested_reviewers / issue-side labels / WIP-title draft
        // toggle) with Forgejo riding the Gitea shim; Epic 31 P6 M1 made
        // GitLab real (state_event / reviewer_ids with in-driver username
        // resolution / add_labels+remove_labels / "Draft: " title toggle).
        // Only the deferred kinds (Bitbucket, AzureDevOps) have no driver.
        // The driver-level narrowing (version floors: 1.14 on Gitea/Forgejo,
        // 13.9 on GitLab) is asserted in the drivers' own
        // ComputeCapabilities tests; the matrix rows here are the
        // optimistic per-kind defaults.
        var advertising = new[]
        {
            PlatformKind.GitHub, PlatformKind.Gitea, PlatformKind.Forgejo,
            PlatformKind.GitLab,
        };

        foreach (var kind in advertising)
        {
            PlatformKindCapabilityMatrix.DefaultsFor(kind)
                .Should().Contain(PlatformCapability.PrLifecycle,
                    $"{kind}'s driver performs the six lifecycle verbs for real");
        }

        foreach (var kind in Enum.GetValues<PlatformKind>().Except(advertising))
        {
            PlatformKindCapabilityMatrix.DefaultsFor(kind)
                .Should().NotContain(PlatformCapability.PrLifecycle,
                    $"{kind} does not yet perform the PR lifecycle verbs");
        }
    }

    [Test]
    public void Returned_set_is_read_only()
    {
        var set = PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.GitHub);
        set.Should().BeAssignableTo<IReadOnlySet<PlatformCapability>>();
    }

    [Test]
    public void DefaultsFor_throws_on_undefined_value()
    {
        var bogus = (PlatformKind)9999;
        Action act = () => PlatformKindCapabilityMatrix.DefaultsFor(bogus);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
