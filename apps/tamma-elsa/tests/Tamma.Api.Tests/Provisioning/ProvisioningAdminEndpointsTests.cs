using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Dtos.Admin;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Provisioning;

/// <summary>
/// Integration tests for the provisioning admin endpoints
/// (<c>POST /api/admin/tenants/{id}/provision</c>, <c>GET .../provisioning</c>,
/// <c>POST .../deprovision</c>). Uses the shared
/// <see cref="ApiTestFixture"/> WebApplicationFactory in Development mode,
/// which short-circuits authorization to permissive (every request passes the
/// OwnerAccess gate). Cranl is intentionally NOT configured in the test
/// environment so the Null provisioner wins — the endpoints still exercise
/// the routing + DTO + persistence path end-to-end.
/// </summary>
[TestFixture]
public class ProvisioningAdminEndpointsTests
{
    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
    }

    private static async Task<Guid> SeedTenantAsync()
    {
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Acme",
            Slug = "acme-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            ProvisioningState = "none",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    [Test]
    public async Task Provision_NewTenant_Returns202WithReadyStateFromNullProvisioner()
    {
        var tenantId = await SeedTenantAsync();
        using var client = ApiTestFixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/admin/tenants/{tenantId}/provision",
            new ProvisionTenantRequest("germany-1"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<TenantProvisioningResponse>();
        body.Should().NotBeNull();
        body!.TenantId.Should().Be(tenantId);
        // Null provisioner flips immediately to "ready" because Cranl is not
        // configured in the test environment.
        body.State.Should().Be("ready");
    }

    [Test]
    public async Task GetProvisioning_AfterProvision_ReturnsCurrentState()
    {
        var tenantId = await SeedTenantAsync();
        using var client = ApiTestFixture.Factory.CreateClient();

        await client.PostAsJsonAsync(
            $"/api/admin/tenants/{tenantId}/provision",
            new ProvisionTenantRequest("germany-1"));

        var response = await client.GetAsync($"/api/admin/tenants/{tenantId}/provisioning");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TenantProvisioningResponse>();
        body!.State.Should().Be("ready");
        body.TenantId.Should().Be(tenantId);
    }

    [Test]
    public async Task Deprovision_NullProvisioner_FlipsToDeprovisioned()
    {
        var tenantId = await SeedTenantAsync();
        using var client = ApiTestFixture.Factory.CreateClient();
        await client.PostAsJsonAsync(
            $"/api/admin/tenants/{tenantId}/provision",
            new ProvisionTenantRequest("germany-1"));

        var response = await client.PostAsync(
            $"/api/admin/tenants/{tenantId}/deprovision", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<TenantProvisioningResponse>();
        body!.State.Should().Be("deprovisioned");
    }
}
