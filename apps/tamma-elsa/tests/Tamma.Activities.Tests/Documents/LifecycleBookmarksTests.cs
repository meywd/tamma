using Elsa.Workflows;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Documents;

namespace Tamma.Activities.Tests.Documents;

/// <summary>
/// Story 39-10 (AC4/AC8) — the ONE canonical tenant-folded bookmark builder
/// (<see cref="LifecycleBookmarks"/>): determinism, tenant folding, hostile-character
/// normalization, the registry contract, and the byte-parity pin that keeps
/// <see cref="WaitForDocumentDecisionActivity.DecisionBookmarkName"/> byte-identical to
/// its 39-8 output after delegating here.
/// </summary>
[TestFixture]
public class LifecycleBookmarksTests
{
    [Test]
    public void ForStageGate_IsDeterministic_AndFoldsTenant()
    {
        var tenantA = Guid.NewGuid().ToString();
        var tenantB = Guid.NewGuid().ToString();

        var a1 = LifecycleBookmarks.ForStageGate(tenantA, "issue-1", "decomposition", "accept-gate");
        var a2 = LifecycleBookmarks.ForStageGate(tenantA, "issue-1", "decomposition", "accept-gate");
        var b1 = LifecycleBookmarks.ForStageGate(tenantB, "issue-1", "decomposition", "accept-gate");

        a1.Should().Be(a2, "same inputs → byte-identical name (suspend/resume parity)");
        a1.Should().NotBe(b1, "folding the tenant is the IDOR guard — different tenants get disjoint names");
        a1.Should().StartWith("accept-gate-");
    }

    [Test]
    public void Compose_NullTenant_UsesStablePlaceholder()
    {
        LifecycleBookmarks.Compose("gate", null, "seg")
            .Should().Be("gate-none-seg");
    }

    [Test]
    public void Compose_NormalizesHostileSegments()
    {
        // '/', '-', spaces and upper-case all fold through NormalizeSegment so a segment
        // cannot break the '-' delimiter scheme or smuggle a collision.
        var name = LifecycleBookmarks.Compose("gate", "Tenant/A", "Owner/Repo", "ISSUE 1");
        name.Should().Be("gate-tenant_a-owner_repo-issue_1");
    }

    [Test]
    public void ForDecisionSession_IsByteIdenticalTo_DecisionBookmarkName()
    {
        var session = Guid.NewGuid();
        foreach (var tenant in new[] { null, "", "Tenant-A", Guid.NewGuid().ToString() })
        {
            LifecycleBookmarks.ForDecisionSession(tenant, session)
                .Should().Be(WaitForDocumentDecisionActivity.DecisionBookmarkName(tenant, session),
                    "the 39-8 gate builder must delegate here byte-for-byte");
        }
    }

    [Test]
    public void ForDecisionSession_MatchesLegacyFormat()
    {
        var session = Guid.Parse("0192a8b0-2222-7abc-8def-000000000002");
        LifecycleBookmarks.ForDecisionSession(null, session)
            .Should().Be($"document-decision-none-{session}");
    }

    [Test]
    public void CanonicalSuspendActivities_IsNonEmpty_AndEveryTypeIsAnElsaActivity()
    {
        LifecycleBookmarks.CanonicalSuspendActivities.Should().NotBeEmpty();
        LifecycleBookmarks.CanonicalSuspendActivities.Keys
            .Should().OnlyContain(t => typeof(IActivity).IsAssignableFrom(t),
                "every canonical suspend type must be an Elsa activity");
        LifecycleBookmarks.CanonicalSuspendActivities.Should()
            .ContainKey(typeof(WaitForDocumentDecisionActivity));
    }
}
