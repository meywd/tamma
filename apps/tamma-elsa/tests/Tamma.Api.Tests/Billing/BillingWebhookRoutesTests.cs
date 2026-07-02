using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using Tamma.Api.Tests.Infrastructure;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-5 (AC1, AC2, AC12, AC13) — route wiring under a real host. A SaaS
/// (Production) factory maps the webhook + admin routes; the webhook route is
/// anonymous + signature-gated (missing signature → 400; a signature present but
/// no cabinet secret → 503 fail-closed), and the admin routes are
/// <c>PlatformOwnerAccess</c>-gated. Boots a standalone factory against the shared
/// Postgres container, mirroring <c>PlatformOwnerAccessPolicyTests</c>.
/// </summary>
[TestFixture]
public class BillingWebhookRoutesTests
{
    private const string JwtSecret = "billing-webhook-test-secret-32-chars-min";
    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes(JwtSecret));

    private WebApplicationFactory<Program> _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Environment.SetEnvironmentVariable("Jwt__Secret", JwtSecret);
        Environment.SetEnvironmentVariable("Jwt__Issuer", "tamma");
        Environment.SetEnvironmentVariable("Jwt__Audience", "tamma-api");
        Environment.SetEnvironmentVariable("Cranl__ApiKey", null);
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection", ApiTestFixture.Postgres.GetConnectionString());
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__TammaDb", ApiTestFixture.Postgres.GetConnectionString());
        // Production boot supplies ConnectionStrings:ControlPlane → SaaS mode → the
        // billing webhook + admin routes are mapped.
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__ControlPlane", ApiTestFixture.Postgres.GetConnectionString());
        Environment.SetEnvironmentVariable(
            "Cranl__EncryptionKey", Convert.ToBase64String(new byte[32]));

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Production");
                b.DisableAlertHostedServices();
            });
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
        Environment.SetEnvironmentVariable("Jwt__Secret", null);
        Environment.SetEnvironmentVariable("Jwt__Issuer", null);
        Environment.SetEnvironmentVariable("Jwt__Audience", null);
        Environment.SetEnvironmentVariable("ConnectionStrings__ControlPlane", null);
        Environment.SetEnvironmentVariable("Cranl__EncryptionKey", null);
    }

    private static string MintToken(string role, string platformRole)
    {
        var jwt = new JwtSecurityToken(
            issuer: "tamma", audience: "tamma-api",
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
                new Claim("tenantId", Guid.NewGuid().ToString()),
                new Claim("role", role),
                new Claim("platformRole", platformRole),
                new Claim(JwtRegisteredClaimNames.Email, "actor@example.com"),
                new Claim("name", "Actor"),
                new Claim("authMethod", "email"),
                new Claim("tenants", "[]"),
            },
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    // ── AC1/AC3 — signature-gated, anonymous ──

    [Test]
    public async Task Webhook_Without_Signature_Is_Mapped_And_Returns_400()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync(
            "/api/v1/billing/stripe/webhook",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        // 400 (not 404) proves the route is mapped in SaaS AND anonymous (no 401)
        // AND signature-gated (missing Stripe-Signature → 400).
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── AC2 — unresolvable secret → 503 (fail closed) ──

    [Test]
    public async Task Webhook_With_Signature_But_No_Cabinet_Secret_Returns_503()
    {
        using var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/stripe/webhook")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Stripe-Signature", "t=1,v1=deadbeef");

        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "no signing secret in the cabinet → fail closed with 503, never open");
    }

    // ── AC12 — admin RBAC ──

    [Test]
    public async Task Admin_List_Rejects_NonPlatformAdmin()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintToken("owner", "user"));

        var resp = await client.GetAsync("/api/v1/admin/billing/webhook-events");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Admin_List_Admits_PlatformAdmin()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintToken("member", "platform_admin"));

        var resp = await client.GetAsync("/api/v1/admin/billing/webhook-events");
        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Admin_Replay_Rejects_NonPlatformAdmin()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintToken("owner", "user"));

        var resp = await client.PostAsync(
            $"/api/v1/admin/billing/webhook-events/{Guid.NewGuid()}/replay", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

/// <summary>
/// Story 35-5 (AC13) — in single-user mode the webhook + admin routes are NOT
/// mapped. The shared <see cref="ApiTestFixture"/> boots without a ControlPlane
/// connection string / TenantSharedSecret ⇒ SingleUser ⇒ the routes 404.
/// </summary>
[TestFixture]
public class BillingWebhookSingleUserModeTests
{
    [Test]
    public async Task Webhook_Route_Is_Unmapped_In_SingleUser()
    {
        using var client = ApiTestFixture.CreateClient();
        var resp = await client.PostAsync(
            "/api/v1/billing/stripe/webhook",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "single-user mode maps no Stripe surface (NullBillingProvider)");
    }

    [Test]
    public async Task Admin_List_Route_Is_Unmapped_In_SingleUser()
    {
        using var client = ApiTestFixture.CreateClient();
        var resp = await client.GetAsync("/api/v1/admin/billing/webhook-events");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
