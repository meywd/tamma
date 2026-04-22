using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Story 16-5 audit: validates the permission matrix in
/// <see cref="Permissions"/> matches the published role-action contract.
/// Each test case asserts a specific row in the AC matrix from
/// <c>docs/audit/rbac-coverage-2026-04-22.md</c>.
/// </summary>
[TestFixture]
public class PermissionsMatrixTests
{
    // ─── Member-level permissions (every authenticated user) ────────────────

    [TestCase("dashboard:view")]
    [TestCase("workflows:view")]
    public void Member_HasBasicReadPermissions(string permission)
    {
        Permissions.HasPermission("member", permission).Should().BeTrue();
    }

    // ─── Admin-required permissions: members denied ─────────────────────────

    [TestCase("workflows:manage")]
    [TestCase("users:view")]
    [TestCase("admin:access")]
    [TestCase("logs:access")]
    [TestCase("elsa:access")]
    [TestCase("settings:view")]
    [TestCase("apikeys:manage")]
    public void Member_DeniedAdminPermissions(string permission)
    {
        Permissions.HasPermission("member", permission).Should().BeFalse();
    }

    [TestCase("workflows:manage")]
    [TestCase("users:view")]
    [TestCase("admin:access")]
    [TestCase("logs:access")]
    [TestCase("elsa:access")]
    [TestCase("settings:view")]
    [TestCase("apikeys:manage")]
    public void Admin_HasAdminPermissions(string permission)
    {
        Permissions.HasPermission("admin", permission).Should().BeTrue();
    }

    // ─── Owner-only permissions ─────────────────────────────────────────────

    [TestCase("workflows:delete")]
    [TestCase("users:manage")]
    [TestCase("settings:manage")]
    public void Owner_HasOwnerOnlyPermissions(string permission)
    {
        Permissions.HasPermission("owner", permission).Should().BeTrue();
    }

    [TestCase("workflows:delete")]
    [TestCase("users:manage")]
    [TestCase("settings:manage")]
    public void Admin_DeniedOwnerOnlyPermissions(string permission)
    {
        Permissions.HasPermission("admin", permission).Should().BeFalse();
    }

    [TestCase("workflows:delete")]
    [TestCase("users:manage")]
    [TestCase("settings:manage")]
    public void Member_DeniedOwnerOnlyPermissions(string permission)
    {
        Permissions.HasPermission("member", permission).Should().BeFalse();
    }

    // ─── Hierarchy: owner inherits everything admin + member can do ─────────

    [Test]
    public void Owner_HasAllAdminPermissions()
    {
        var adminPerms = Permissions.GetRolePermissions("admin");
        foreach (var perm in adminPerms)
        {
            Permissions.HasPermission("owner", perm).Should()
                .BeTrue($"owner inherits admin permission '{perm}'");
        }
    }

    [Test]
    public void Admin_HasAllMemberPermissions()
    {
        var memberPerms = Permissions.GetRolePermissions("member");
        foreach (var perm in memberPerms)
        {
            Permissions.HasPermission("admin", perm).Should()
                .BeTrue($"admin inherits member permission '{perm}'");
        }
    }

    // ─── Edge cases ─────────────────────────────────────────────────────────

    [TestCase(null!)]
    [TestCase("")]
    [TestCase("guest")]
    [TestCase("Owner")] // case-sensitive
    public void UnknownRole_DeniedAllPermissions(string role)
    {
        Permissions.HasPermission(role, "dashboard:view").Should().BeFalse();
        Permissions.HasPermission(role, "workflows:delete").Should().BeFalse();
    }

    [Test]
    public void UnknownPermission_AlwaysDenied()
    {
        Permissions.HasPermission("owner", "no:such:perm").Should().BeFalse();
        Permissions.HasPermission("admin", "no:such:perm").Should().BeFalse();
        Permissions.HasPermission("member", "no:such:perm").Should().BeFalse();
    }

    // ─── GetRolePermissions returns proper subsets ──────────────────────────

    [Test]
    public void GetRolePermissions_Owner_ReturnsAllRules()
    {
        var ownerPerms = Permissions.GetRolePermissions("owner");
        ownerPerms.Should().Contain("workflows:delete");
        ownerPerms.Should().Contain("users:manage");
        ownerPerms.Should().Contain("settings:manage");
        ownerPerms.Should().Contain("admin:access");
        ownerPerms.Should().Contain("dashboard:view");
    }

    [Test]
    public void GetRolePermissions_Member_ExcludesPrivilegedActions()
    {
        var memberPerms = Permissions.GetRolePermissions("member");
        memberPerms.Should().NotContain("workflows:delete");
        memberPerms.Should().NotContain("users:manage");
        memberPerms.Should().NotContain("admin:access");
        memberPerms.Should().NotContain("elsa:access");
        memberPerms.Should().NotContain("logs:access");
        memberPerms.Should().Contain("dashboard:view");
        memberPerms.Should().Contain("workflows:view");
    }
}
