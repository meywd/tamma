using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NUnit.Framework;
using Tamma.Api.Authorization;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Query;
using Tamma.Api.Services.Secrets.Reveal;

namespace Tamma.Api.Tests.Secrets.Query;

/// <summary>
/// Endpoint-layer tests for the Story 29-4 / 29-5 query + retire
/// handlers added to <see cref="SecretEndpoints"/>. Covers:
///
/// <list type="bullet">
///   <item><description>Platform list / get / versions / retire
///     happy paths.</description></item>
///   <item><description>Tenant variants with the membership filter's
///     stashed role item respected for write ops.</description></item>
///   <item><description>Route validation — empty Guid / bad version
///     number → 400.</description></item>
///   <item><description>Admin-role gate for tenant rotate / retire —
///     member gets 403, admin gets 200.</description></item>
///   <item><description>Cross-tenant isolation via the underlying
///     query service.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class SecretQueryEndpointsTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid SecretId = Guid.Parse("cccccccc-3333-3333-3333-333333333333");

    private Mock<ISecretQueryService> _queryService = null!;
    private Mock<ISecretRevealService> _revealService = null!;
    private Mock<Tamma.Data.Repositories.ITenantPlatformInstallationRepository> _installations = null!;
    private Mock<Tamma.Platforms.IPlatformInstallationEventEmitter> _installationEvents = null!;
    private ClaimsPrincipal _user = null!;

    [SetUp]
    public void SetUp()
    {
        _queryService = new Mock<ISecretQueryService>();
        _revealService = new Mock<ISecretRevealService>();
        _installations = new Mock<Tamma.Data.Repositories.ITenantPlatformInstallationRepository>();
        _installations
            .Setup(i => i.ListByTenantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Tamma.Data.Entities.TenantPlatformInstallation>());
        _installationEvents = new Mock<Tamma.Platforms.IPlatformInstallationEventEmitter>();
        _user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", "11111111-1111-1111-1111-111111111111"),
        }));
    }

    // ── List ───────────────────────────────────────────────────────

    [Test]
    public async Task ListPlatform_ReturnsRows()
    {
        var meta = FakeMetadata("db/role", SecretScope.Platform);
        _queryService.Setup(q => q.ListAsync(
                SecretScope.Platform, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { meta });

        var result = await SecretEndpoints.ListPlatformSecrets(
            _queryService.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(StatusCodes.Status200OK);
        var json = SerializeBody(result);
        json.Should().Contain("\"name\":\"db/role\"");
        json.Should().Contain("\"scope\":\"platform\"");
    }

    [Test]
    public async Task ListTenant_EmptyGuid_Returns400()
    {
        var result = await SecretEndpoints.ListTenantSecrets(
            Guid.Empty, _queryService.Object, new DefaultHttpContext());
        ExtractStatusCode(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task ListTenant_ScopesByTenantId()
    {
        var meta = FakeMetadata("db/role", SecretScope.Tenant, TenantA);
        _queryService.Setup(q => q.ListAsync(
                SecretScope.Tenant, TenantA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { meta });

        var result = await SecretEndpoints.ListTenantSecrets(
            TenantA, _queryService.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(StatusCodes.Status200OK);
        _queryService.Verify(q => q.ListAsync(
            SecretScope.Tenant, TenantA, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Get ────────────────────────────────────────────────────────

    [Test]
    public async Task GetPlatform_NotFound_Returns404()
    {
        _queryService.Setup(q => q.GetAsync(
                SecretId, SecretScope.Platform, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SecretMetadata?)null);

        var result = await SecretEndpoints.GetPlatformSecret(
            SecretId, _queryService.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task GetTenant_CrossTenantReturns404_NotLeaking()
    {
        // Query service returns null on cross-tenant because it does
        // the scope check — but we pin the endpoint behaviour: null
        // maps to 404 (not 403) so existence does not leak.
        _queryService.Setup(q => q.GetAsync(
                SecretId, SecretScope.Tenant, TenantB, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SecretMetadata?)null);

        var result = await SecretEndpoints.GetTenantSecret(
            TenantB, SecretId, _queryService.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task GetPlatform_ResponseHasNoPlaintext()
    {
        var meta = FakeMetadata("db/role", SecretScope.Platform);
        _queryService.Setup(q => q.GetAsync(
                SecretId, SecretScope.Platform, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(meta);

        var result = await SecretEndpoints.GetPlatformSecret(
            SecretId, _queryService.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(StatusCodes.Status200OK);
        var json = SerializeBody(result);
        json.Should().NotContain("plaintext");
        json.Should().NotContain("cipher");
    }

    // ── Versions ───────────────────────────────────────────────────

    [Test]
    public async Task ListPlatformVersions_ReturnsRows()
    {
        var v = new SecretVersion(
            SecretId, 1, SecretVersionStatus.Active,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, Guid.Empty);
        _queryService.Setup(q => q.ListVersionsAsync(
                SecretId, SecretScope.Platform, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { v });

        var result = await SecretEndpoints.ListPlatformVersions(
            SecretId, _queryService.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(StatusCodes.Status200OK);
        var json = SerializeBody(result);
        json.Should().Contain("\"versionNumber\":1");
        json.Should().Contain("\"status\":\"Active\"");
    }

    // ── Retire ─────────────────────────────────────────────────────

    [Test]
    public async Task RetirePlatform_ActiveVersion_Returns409()
    {
        _queryService.Setup(q => q.RetireVersionAsync(
                SecretId, 2, SecretScope.Platform, null, It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cannot retire active"));

        var result = await SecretEndpoints.RetirePlatformVersion(
            SecretId, 2, _user, _queryService.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(409);
    }

    [Test]
    public async Task RetirePlatform_NotFound_Returns404()
    {
        _queryService.Setup(q => q.RetireVersionAsync(
                SecretId, 1, SecretScope.Platform, null, It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await SecretEndpoints.RetirePlatformVersion(
            SecretId, 1, _user, _queryService.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task RetirePlatform_HappyPath_Returns200()
    {
        _queryService.Setup(q => q.RetireVersionAsync(
                SecretId, 1, SecretScope.Platform, null, It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SecretVersionStatus.Revoked);

        var result = await SecretEndpoints.RetirePlatformVersion(
            SecretId, 1, _user, _queryService.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(StatusCodes.Status200OK);
        var json = SerializeBody(result);
        json.Should().Contain("\"status\":\"Revoked\"");
    }

    [Test]
    public async Task RetirePlatform_BadVersionNumber_Returns400()
    {
        var result = await SecretEndpoints.RetirePlatformVersion(
            SecretId, 0, _user, _queryService.Object, new DefaultHttpContext());
        ExtractStatusCode(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    // ── Tenant retire — role gating ────────────────────────────────

    [Test]
    public async Task RetireTenant_NoRoleItem_Returns500()
    {
        // Simulates misconfigured filter chain — membership filter
        // should always run first. The handler defends anyway.
        var http = new DefaultHttpContext();
        var result = await SecretEndpoints.RetireTenantVersion(
            TenantA, SecretId, 1, _user, _queryService.Object, http);
        ExtractStatusCode(result).Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Test]
    public async Task RetireTenant_MemberRole_Returns403()
    {
        var http = WithTenantRole(TenantRoleHierarchy.Member);

        var result = await SecretEndpoints.RetireTenantVersion(
            TenantA, SecretId, 1, _user, _queryService.Object, http);

        ExtractStatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task RetireTenant_AdminRole_HappyPath()
    {
        var http = WithTenantRole(TenantRoleHierarchy.Admin);
        _queryService.Setup(q => q.RetireVersionAsync(
                SecretId, 1, SecretScope.Tenant, TenantA, It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SecretVersionStatus.Revoked);

        var result = await SecretEndpoints.RetireTenantVersion(
            TenantA, SecretId, 1, _user, _queryService.Object, http);

        ExtractStatusCode(result).Should().Be(StatusCodes.Status200OK);
    }

    // ── Tenant rotate — scope isolation ────────────────────────────

    [Test]
    public async Task RotateTenant_MemberRole_Returns403()
    {
        var http = WithTenantRole(TenantRoleHierarchy.Member);
        var body = new SecretEndpoints.RotateSecretRequestBody("valid-new-value");

        var result = await SecretEndpoints.RotateTenantSecret(
            TenantA, SecretId, body, _user,
            _revealService.Object, _queryService.Object,
            _installations.Object, _installationEvents.Object, http);

        ExtractStatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
        _revealService.VerifyNoOtherCalls();
    }

    [Test]
    public async Task RotateTenant_CrossTenantAttempt_Returns404()
    {
        // Attacker forges a tenant-id in the route pointing at a
        // secret that belongs to a DIFFERENT tenant. The scope check
        // via query service returns null, so the rotate never reaches
        // the reveal service.
        var http = WithTenantRole(TenantRoleHierarchy.Admin);
        _queryService.Setup(q => q.GetAsync(
                SecretId, SecretScope.Tenant, TenantA, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SecretMetadata?)null);

        var body = new SecretEndpoints.RotateSecretRequestBody("valid-new-value");

        var result = await SecretEndpoints.RotateTenantSecret(
            TenantA, SecretId, body, _user,
            _revealService.Object, _queryService.Object,
            _installations.Object, _installationEvents.Object, http);

        ExtractStatusCode(result).Should().Be(StatusCodes.Status404NotFound);
        _revealService.Verify(r => r.IssueRotateAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task RotateTenant_HappyPath_ReturnsRevealToken()
    {
        var http = WithTenantRole(TenantRoleHierarchy.Admin);
        var existing = FakeMetadata("db/role", SecretScope.Tenant, TenantA);
        _queryService.Setup(q => q.GetAsync(
                SecretId, SecretScope.Tenant, TenantA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _revealService.Setup(r => r.IssueRotateAsync(
                SecretId, It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RevealTokenIssueResult(
                existing, "NEW-TOK", DateTimeOffset.UtcNow.AddSeconds(60)));

        var body = new SecretEndpoints.RotateSecretRequestBody("valid-new-value");

        var result = await SecretEndpoints.RotateTenantSecret(
            TenantA, SecretId, body, _user,
            _revealService.Object, _queryService.Object,
            _installations.Object, _installationEvents.Object, http);

        ExtractStatusCode(result).Should().Be(StatusCodes.Status200OK);
        var json = SerializeBody(result);
        json.Should().Contain("\"revealToken\":\"NEW-TOK\"");
        json.Should().NotContain("valid-new-value");
    }

    // ── Epic 31 review (F-medium) — rotating a tenant secret that backs a
    //    platform installation credential must emit CREDENTIAL_ROTATED so
    //    the driver-cache invalidator evicts the stale composed driver. ──

    [Test]
    public async Task RotateTenant_SecretBacksInstallationCredential_EmitsCredentialRotated()
    {
        var http = WithTenantRole(TenantRoleHierarchy.Admin);
        var existing = FakeMetadata("gitea/install-20260101000000", SecretScope.Tenant, TenantA);
        _queryService.Setup(q => q.GetAsync(
                SecretId, SecretScope.Tenant, TenantA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _revealService.Setup(r => r.IssueRotateAsync(
                SecretId, It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RevealTokenIssueResult(
                existing, "NEW-TOK", DateTimeOffset.UtcNow.AddSeconds(60)));

        var backedRow = Guid.NewGuid();
        _installations.Setup(i => i.ListByTenantAsync(TenantA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new Tamma.Data.Entities.TenantPlatformInstallation
                {
                    Id = backedRow,
                    TenantId = TenantA,
                    PlatformKind = "gitea",
                    CredentialSecretScope = "tenant",
                    CredentialSecretName = "gitea/install-20260101000000",
                },
                // A row backed by a DIFFERENT secret must not be touched.
                new Tamma.Data.Entities.TenantPlatformInstallation
                {
                    Id = Guid.NewGuid(),
                    TenantId = TenantA,
                    PlatformKind = "github",
                    CredentialSecretScope = "tenant",
                    CredentialSecretName = "github/install-20250101000000",
                },
            });

        var body = new SecretEndpoints.RotateSecretRequestBody("valid-new-value");
        var result = await SecretEndpoints.RotateTenantSecret(
            TenantA, SecretId, body, _user,
            _revealService.Object, _queryService.Object,
            _installations.Object, _installationEvents.Object, http);

        ExtractStatusCode(result).Should().Be(StatusCodes.Status200OK);
        _installationEvents.Verify(e => e.EmitCredentialRotatedAsync(
            TenantA, Tamma.Platforms.Abstractions.PlatformKind.Gitea, backedRow,
            It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once,
            "the driver cache is serving a driver composed from the OLD plaintext — "
            + "without this event nothing evicts it before the absolute TTL");
        _installationEvents.Verify(e => e.EmitCredentialRotatedAsync(
            It.IsAny<Guid>(), It.IsAny<Tamma.Platforms.Abstractions.PlatformKind>(),
            It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task RotateTenant_UnrelatedSecret_EmitsNoInstallationEvent()
    {
        var http = WithTenantRole(TenantRoleHierarchy.Admin);
        var existing = FakeMetadata("db/role", SecretScope.Tenant, TenantA);
        _queryService.Setup(q => q.GetAsync(
                SecretId, SecretScope.Tenant, TenantA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _revealService.Setup(r => r.IssueRotateAsync(
                SecretId, It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RevealTokenIssueResult(
                existing, "NEW-TOK", DateTimeOffset.UtcNow.AddSeconds(60)));

        var body = new SecretEndpoints.RotateSecretRequestBody("valid-new-value");
        var result = await SecretEndpoints.RotateTenantSecret(
            TenantA, SecretId, body, _user,
            _revealService.Object, _queryService.Object,
            _installations.Object, _installationEvents.Object, http);

        ExtractStatusCode(result).Should().Be(StatusCodes.Status200OK);
        _installationEvents.VerifyNoOtherCalls();
    }

    [Test]
    public async Task RotateTenant_InstallationLookupFails_RotationStillSucceeds()
    {
        // The bridge is best-effort by design: the rotation already
        // happened, and the driver cache's absolute TTL bounds staleness.
        var http = WithTenantRole(TenantRoleHierarchy.Admin);
        var existing = FakeMetadata("gitea/install-20260101000000", SecretScope.Tenant, TenantA);
        _queryService.Setup(q => q.GetAsync(
                SecretId, SecretScope.Tenant, TenantA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _revealService.Setup(r => r.IssueRotateAsync(
                SecretId, It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RevealTokenIssueResult(
                existing, "NEW-TOK", DateTimeOffset.UtcNow.AddSeconds(60)));
        _installations.Setup(i => i.ListByTenantAsync(TenantA, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var body = new SecretEndpoints.RotateSecretRequestBody("valid-new-value");
        var result = await SecretEndpoints.RotateTenantSecret(
            TenantA, SecretId, body, _user,
            _revealService.Object, _queryService.Object,
            _installations.Object, _installationEvents.Object, http);

        ExtractStatusCode(result).Should().Be(StatusCodes.Status200OK,
            "a failed cache-invalidation bridge must never fail the completed rotation");
    }

    // ── helpers ────────────────────────────────────────────────────

    private static DefaultHttpContext WithTenantRole(string role)
    {
        var http = new DefaultHttpContext();
        http.Items[RequireTenantMembershipFilter.TenantRoleItemKey] = role;
        return http;
    }

    private static SecretMetadata FakeMetadata(
        string name, SecretScope scope, Guid? tenantId = null) =>
        SecretMetadataFactory.Create(
            name: name,
            scope: scope,
            tenantId: tenantId,
            purpose: SecretPurpose.DbCredential,
            consumerRefs: null,
            ownerUserId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            rotationSchedule: null,
            now: DateTimeOffset.UtcNow);

    private static int ExtractStatusCode(IResult result)
    {
        if (result is IStatusCodeHttpResult withStatus)
        {
            return withStatus.StatusCode ?? 0;
        }
        return 0;
    }

    private static string SerializeBody(IResult result)
    {
        if (result is IValueHttpResult valued)
        {
            return System.Text.Json.JsonSerializer.Serialize(valued.Value);
        }
        return "";
    }
}
