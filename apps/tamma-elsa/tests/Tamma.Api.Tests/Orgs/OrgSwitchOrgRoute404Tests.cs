using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using NUnit.Framework;

namespace Tamma.Api.Tests.Orgs;

/// <summary>
/// Regression suite guarding the deletion of <c>OrgEndpoints.SwitchOrg</c>.
/// The Story-18-3 handler (<c>POST /api/v1/orgs/switch-org</c>) called
/// <c>IUserRepository.UpdateActiveTenantAsync</c> directly, which would
/// have failed at runtime under the Phase-2 <c>prevent_tenant_id_change</c>
/// Postgres trigger for every user whose personal tenant had already been
/// bound (the uuid to uuid update path is blocked at the DB layer).
///
/// <para>Story 28-9's <c>AuthEndpoints.SwitchOrg</c>
/// (<c>POST /api/v1/auth/switch-org</c>) is the canonical replacement — it
/// stashes the active tenant in <c>users.Settings.activeTenantId</c> JSON
/// (avoiding the trigger) and rotates the refresh token alongside the new
/// JWT. The old route was never mapped in <c>Program.cs</c>, and the
/// handler itself has now been removed; these tests pin that fact so a
/// future regression that re-adds the broken code path would have to also
/// re-wire the route.</para>
/// </summary>
[TestFixture]
public class OrgSwitchOrgRoute404Tests
{
    [Test]
    public async Task Post_Api_V1_Orgs_SwitchOrg_Returns404()
    {
        using var client = ApiTestFixture.CreateClient();

        // Route must not resolve to any handler — the old
        // OrgEndpoints.SwitchOrg is gone and no /api/v1/orgs/switch-org
        // mapping exists.
        var response = await client.PostAsJsonAsync(
            "/api/v1/orgs/switch-org",
            new { tenantId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the Story-18-3 handler was deleted because it would have tripped "
            + "the Phase-2 prevent_tenant_id_change trigger; switch-org now "
            + "lives only at POST /api/v1/auth/switch-org (Story 28-9).");
    }

    [Test]
    public async Task Post_Api_V1_Auth_SwitchOrg_Exists_AndIsNotA404()
    {
        using var client = ApiTestFixture.CreateClient();

        // Companion assertion: the replacement route IS mapped. The call
        // here isn't authenticated against a real JWT (the fixture uses the
        // permissive-dev branch), so the exact success-vs-400/401/403 shape
        // depends on the handler body. The only thing this test asserts is
        // that the route is NOT a 404.
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/switch-org",
            new { tenantId = Guid.NewGuid() });

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound,
            "POST /api/v1/auth/switch-org must stay mapped — it's the "
            + "canonical switch-org endpoint (Story 28-9).");
    }
}
