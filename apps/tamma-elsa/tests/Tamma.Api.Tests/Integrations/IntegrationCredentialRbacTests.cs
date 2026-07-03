using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;

namespace Tamma.Api.Tests.Integrations;

/// <summary>
/// Integration BYOK write endpoints (<c>POST/DELETE /api/v1/integrations/*/credential</c>)
/// are gated by the <c>PlatformsManage</c> route policy → the <c>platforms:manage</c>
/// permission. This pins the RBAC contract the routes rely on: member → 403,
/// tenant_admin / tenant_owner → allowed (200/2xx). (Enforcement is the route
/// policy; this asserts the underlying authorization decision, like the provider
/// BYOK endpoints rely on <c>PermissionsMatrixTests</c> for <c>agents:manage</c>.)
/// </summary>
[TestFixture]
public class IntegrationCredentialRbacTests
{
    private const string Permission = "platforms:manage";

    [Test]
    public void Member_IsDenied()
    {
        Permissions.HasPermission("member", Permission).Should().BeFalse(
            "a member-role SaaS caller must hit 403 on the integration BYOK write endpoints");
    }

    [TestCase("admin")]
    [TestCase("owner")]
    public void AdminAndOwner_AreAllowed(string role)
    {
        Permissions.HasPermission(role, Permission).Should().BeTrue(
            "tenant_admin and tenant_owner may set/remove the tenant's integration credential");
    }
}
