using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-3 — the BYOK toggle mutations (<c>POST/DELETE
/// /api/pricing/providers/{provider}/byok</c>) are gated by the <c>PricingManage</c>
/// route policy → the <c>pricing:manage</c> permission. This pins the RBAC contract
/// the routes rely on: member → 403, tenant_admin / tenant_owner → allowed. (The spec
/// names <c>SettingsManage</c>, but that is owner-only and would 403 every
/// tenant_admin; per CLAUDE.md the prompt/convention/agent precedent grants
/// owner+admin via a dedicated gate — <c>PricingManage</c> here.)
/// </summary>
[TestFixture]
public class PricingByokRbacTests
{
    private const string Permission = "pricing:manage";

    [Test]
    public void Member_IsDenied()
    {
        Permissions.HasPermission("member", Permission).Should().BeFalse(
            "a member-role SaaS caller must hit 403 on the BYOK toggle mutations");
    }

    [TestCase("admin")]
    [TestCase("owner")]
    public void AdminAndOwner_AreAllowed(string role)
    {
        Permissions.HasPermission(role, Permission).Should().BeTrue(
            "tenant_admin and tenant_owner may enable/disable BYOK for their tenant");
    }
}
