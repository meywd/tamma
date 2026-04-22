using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Authorization;

namespace Tamma.Api.Tests.Orgs;

[TestFixture]
public class TenantRoleHierarchyTests
{
    [TestCase("owner", true)]
    [TestCase("admin", true)]
    [TestCase("member", true)]
    [TestCase("Owner", false)]
    [TestCase("root", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void IsValid_AcceptsKnownRoles_RejectsOthers(string? role, bool expected)
    {
        TenantRoleHierarchy.IsValid(role).Should().Be(expected);
    }

    [TestCase("owner", 2)]
    [TestCase("admin", 1)]
    [TestCase("member", 0)]
    [TestCase("root", -1)]
    [TestCase(null, -1)]
    public void Level_AssignsExpectedRanks(string? role, int expected)
    {
        TenantRoleHierarchy.Level(role).Should().Be(expected);
    }

    [TestCase("owner", "admin", true)]
    [TestCase("owner", "owner", true)]
    [TestCase("admin", "owner", false)]
    [TestCase("admin", "admin", true)]
    [TestCase("member", "admin", false)]
    [TestCase("member", "member", true)]
    [TestCase("root", "member", false)]
    public void IsAtLeast_ComparesByRank(string role, string min, bool expected)
    {
        TenantRoleHierarchy.IsAtLeast(role, min).Should().Be(expected);
    }
}
