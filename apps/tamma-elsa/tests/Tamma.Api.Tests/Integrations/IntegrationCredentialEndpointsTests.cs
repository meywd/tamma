using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Integrations;
using Tamma.Api.Services.Secrets;
using Tamma.Data;

namespace Tamma.Api.Tests.Integrations;

/// <summary>
/// Integration BYOK write endpoints — driven directly against the static handlers
/// with cabinet/resolver fakes (mirrors <c>ProviderCredentialEndpointsTests</c>).
/// Pins: valid set → Created + reveal-SAFE (no secret in the response) + the bundle
/// JSON handed to the cabinet DOES carry the secret; input validation; no-tenant
/// handling; duplicate → 409; delete existing → NoContent + invalidate; delete
/// missing → 404. (Member-role 403 is enforced by the PlatformsManage route policy
/// and pinned by <c>IntegrationCredentialRbacTests</c>.)
/// </summary>
[TestFixture]
public class IntegrationCredentialEndpointsTests
{
    private const string JiraToken = "fake-jira-api-token";
    private const string ResendKey = "fake-resend-key-value";
    private static readonly Guid Tenant = Guid.NewGuid();

    private FakeCabinet _cabinet = null!;
    private FakeJiraResolver _jiraResolver = null!;
    private FakeEmailResolver _emailResolver = null!;

    [SetUp]
    public void SetUp()
    {
        _cabinet = new FakeCabinet();
        _jiraResolver = new FakeJiraResolver();
        _emailResolver = new FakeEmailResolver();
    }

    private static ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) }, "test"));

    private static HttpContext Http() => new DefaultHttpContext();
    private static ITenantContext TenantCtx(Guid? id) => new StubTenant(id);

    // ── JIRA set ──────────────────────────────────────────────────────────

    [Test]
    public async Task SetJira_Valid_Created_RevealSafe_StoresBundleWithSecret_Invalidates()
    {
        var body = new SetJiraCredentialRequest("https://jira.example.com", "bot@example.com", JiraToken);

        var result = await IntegrationCredentialEndpoints.SetJiraCredential(
            body, Principal(), TenantCtx(Tenant), _cabinet, _jiraResolver, Http());

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Created<SetIntegrationCredentialResponse>>();
        var value = ((Microsoft.AspNetCore.Http.HttpResults.Created<SetIntegrationCredentialResponse>)result).Value!;
        value.Integration.Should().Be("jira");
        value.Version.Should().Be(1);
        // Reveal-safe: the token must NEVER round-trip in the response.
        JsonSerializer.Serialize(value).Should().NotContain(JiraToken);

        _cabinet.SetName.Should().Be(IntegrationCabinetNames.JiraConfig);
        _cabinet.SetTenant.Should().Be(Tenant);
        _cabinet.SetConsumer.Should().Be("jira");
        // The bundle handed to the cabinet DOES carry the secret (it's what gets stored).
        _cabinet.SetJson.Should().Contain(JiraToken);
        _jiraResolver.Invalidated.Should().ContainSingle().Which.Should().Be(Tenant);
    }

    [Test]
    public async Task SetJira_InvalidBaseUrl_BadRequest()
    {
        var body = new SetJiraCredentialRequest("not-a-url", "bot@example.com", JiraToken);
        var result = await IntegrationCredentialEndpoints.SetJiraCredential(
            body, Principal(), TenantCtx(Tenant), _cabinet, _jiraResolver, Http());
        result.GetType().Name.Should().Contain("BadRequest");
        _cabinet.SetName.Should().BeNull();
    }

    [Test]
    public async Task SetJira_MissingToken_BadRequest()
    {
        var body = new SetJiraCredentialRequest("https://jira.example.com", "bot@example.com", "  ");
        var result = await IntegrationCredentialEndpoints.SetJiraCredential(
            body, Principal(), TenantCtx(Tenant), _cabinet, _jiraResolver, Http());
        result.GetType().Name.Should().Contain("BadRequest");
    }

    [Test]
    public async Task SetJira_NoTenantContext_BadRequest()
    {
        var body = new SetJiraCredentialRequest("https://jira.example.com", "bot@example.com", JiraToken);
        var result = await IntegrationCredentialEndpoints.SetJiraCredential(
            body, Principal(), TenantCtx(null), _cabinet, _jiraResolver, Http());
        result.GetType().Name.Should().Contain("BadRequest");
        _cabinet.SetName.Should().BeNull();
    }

    [Test]
    public async Task SetJira_Duplicate_Conflict()
    {
        _cabinet.ThrowDuplicate = true;
        var body = new SetJiraCredentialRequest("https://jira.example.com", "bot@example.com", JiraToken);
        var result = await IntegrationCredentialEndpoints.SetJiraCredential(
            body, Principal(), TenantCtx(Tenant), _cabinet, _jiraResolver, Http());
        result.GetType().Name.Should().Contain("Conflict");
    }

    // ── JIRA delete ───────────────────────────────────────────────────────

    [Test]
    public async Task DeleteJira_Existing_NoContent_Invalidates()
    {
        _cabinet.RemoveResult = true;
        var result = await IntegrationCredentialEndpoints.DeleteJiraCredential(
            Principal(), TenantCtx(Tenant), _cabinet, _jiraResolver, Http());
        result.GetType().Name.Should().Contain("NoContent");
        _cabinet.RemovedName.Should().Be(IntegrationCabinetNames.JiraConfig);
        _jiraResolver.Invalidated.Should().Contain(Tenant);
    }

    [Test]
    public async Task DeleteJira_Missing_NotFound()
    {
        _cabinet.RemoveResult = false;
        var result = await IntegrationCredentialEndpoints.DeleteJiraCredential(
            Principal(), TenantCtx(Tenant), _cabinet, _jiraResolver, Http());
        result.GetType().Name.Should().Contain("NotFound");
        _jiraResolver.Invalidated.Should().BeEmpty();
    }

    // ── EMAIL set ─────────────────────────────────────────────────────────

    [Test]
    public async Task SetEmail_Resend_Valid_Created_RevealSafe_StoresSecret()
    {
        var body = new SetEmailCredentialRequest("resend", "team@tenant.example.com", ResendApiKey: ResendKey);
        var result = await IntegrationCredentialEndpoints.SetEmailCredential(
            body, Principal(), TenantCtx(Tenant), _cabinet, _emailResolver, Http());

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Created<SetIntegrationCredentialResponse>>();
        var value = ((Microsoft.AspNetCore.Http.HttpResults.Created<SetIntegrationCredentialResponse>)result).Value!;
        value.Integration.Should().Be("email");
        JsonSerializer.Serialize(value).Should().NotContain(ResendKey);
        _cabinet.SetName.Should().Be(IntegrationCabinetNames.EmailConfig);
        _cabinet.SetJson.Should().Contain(ResendKey);
        _emailResolver.Invalidated.Should().Contain(Tenant);
    }

    [Test]
    public async Task SetEmail_Smtp_Valid_Created()
    {
        var body = new SetEmailCredentialRequest("smtp", "team@tenant.example.com", SmtpHost: "smtp.example.com", SmtpPort: 587);
        var result = await IntegrationCredentialEndpoints.SetEmailCredential(
            body, Principal(), TenantCtx(Tenant), _cabinet, _emailResolver, Http());
        result.GetType().Name.Should().Contain("Created");
    }

    [Test]
    public async Task SetEmail_UnknownTransport_BadRequest()
    {
        var body = new SetEmailCredentialRequest("carrier-pigeon", "team@tenant.example.com");
        var result = await IntegrationCredentialEndpoints.SetEmailCredential(
            body, Principal(), TenantCtx(Tenant), _cabinet, _emailResolver, Http());
        result.GetType().Name.Should().Contain("BadRequest");
    }

    [Test]
    public async Task SetEmail_ResendWithoutKey_BadRequest()
    {
        var body = new SetEmailCredentialRequest("resend", "team@tenant.example.com", ResendApiKey: null);
        var result = await IntegrationCredentialEndpoints.SetEmailCredential(
            body, Principal(), TenantCtx(Tenant), _cabinet, _emailResolver, Http());
        result.GetType().Name.Should().Contain("BadRequest");
    }

    [Test]
    public async Task SetEmail_SmtpWithoutHost_BadRequest()
    {
        var body = new SetEmailCredentialRequest("smtp", "team@tenant.example.com", SmtpHost: null);
        var result = await IntegrationCredentialEndpoints.SetEmailCredential(
            body, Principal(), TenantCtx(Tenant), _cabinet, _emailResolver, Http());
        result.GetType().Name.Should().Contain("BadRequest");
    }

    // ── fakes ─────────────────────────────────────────────────────────────

    private sealed class StubTenant(Guid? id) : ITenantContext
    {
        public Guid? TenantId { get; private set; } = id;
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    private sealed class FakeCabinet : IIntegrationCredentialCabinet
    {
        public string? SetName { get; private set; }
        public Guid? SetTenant { get; private set; }
        public string? SetConsumer { get; private set; }
        public string? SetJson { get; private set; }
        public string? RemovedName { get; private set; }
        public bool ThrowDuplicate { get; set; }
        public bool RemoveResult { get; set; }

        public Task<SecretMetadata> SetAsync(Guid tenantId, string cabinetName, string consumerSystem, string bundleJson, Guid ownerUserId, CancellationToken ct = default)
        {
            if (ThrowDuplicate)
            {
                throw new InvalidOperationException("already exists");
            }
            SetName = cabinetName;
            SetTenant = tenantId;
            SetConsumer = consumerSystem;
            SetJson = bundleJson;
            return Task.FromResult(new SecretMetadata(
                Guid.NewGuid(), cabinetName, SecretScope.Tenant, tenantId, SecretPurpose.ApiKey,
                Array.Empty<ConsumerRef>(), ownerUserId, RotationSchedule.None,
                LastRotatedAt: null, NextRotationDueAt: null, ActiveVersionNumber: 1,
                CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow));
        }

        public Task<bool> RemoveAsync(Guid tenantId, string cabinetName, CancellationToken ct = default)
        {
            RemovedName = cabinetName;
            return Task.FromResult(RemoveResult);
        }
    }

    private sealed class FakeJiraResolver : IJiraCredentialResolver
    {
        public List<Guid?> Invalidated { get; } = new();
        public Task<JiraCredentialResolution?> ResolveAsync(Guid? tenantId, CancellationToken ct = default)
            => Task.FromResult<JiraCredentialResolution?>(null);
        public void Invalidate(Guid? tenantId) => Invalidated.Add(tenantId);
    }

    private sealed class FakeEmailResolver : IEmailCredentialResolver
    {
        public List<Guid?> Invalidated { get; } = new();
        public Task<EmailCredentialResolution?> ResolveAsync(Guid? tenantId, CancellationToken ct = default)
            => Task.FromResult<EmailCredentialResolution?>(null);
        public void Invalidate(Guid? tenantId) => Invalidated.Add(tenantId);
    }
}
