using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets.Handlers;

namespace Tamma.Api.Tests.Secrets.Handlers;

/// <summary>
/// Story 29-7 AC8 — whitelist guard against rotating a role we don't
/// own.
/// </summary>
[TestFixture]
public class RoleWhitelistTests
{
    [TestCase("tamma_app")]
    [TestCase("tamma_engine")]
    [TestCase("tamma_provisioner")]
    public void Platform_Roles_AreAllowed(string role)
    {
        RoleWhitelist.IsAllowed(role, isTenantScope: false).Should().BeTrue();
    }

    [TestCase("postgres")]
    [TestCase("tamma_admin")]
    [TestCase("some_other_role")]
    [TestCase("TAMMA_APP")] // case-sensitive
    public void Platform_UnknownRoles_AreBlocked(string role)
    {
        RoleWhitelist.IsAllowed(role, isTenantScope: false).Should().BeFalse();
    }

    [Test]
    public void Tenant_ValidRolePattern_Allowed()
    {
        var role = "tamma_tenant_" + new string('a', 32);
        RoleWhitelist.IsAllowed(role, isTenantScope: true).Should().BeTrue();
    }

    [TestCase("tamma_tenant_short")]
    [TestCase("tamma_tenant_TOOLONGFORMATBROKENFAKE")]
    [TestCase("tamma_app")] // platform role in tenant scope = no
    [TestCase("postgres")]
    public void Tenant_InvalidRoles_AreBlocked(string role)
    {
        RoleWhitelist.IsAllowed(role, isTenantScope: true).Should().BeFalse();
    }

    [Test]
    public void Empty_Role_IsBlocked()
    {
        RoleWhitelist.IsAllowed("", true).Should().BeFalse();
        RoleWhitelist.IsAllowed("", false).Should().BeFalse();
    }
}
