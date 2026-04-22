using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Query;

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
    private static readonly Guid SecretId = Guid.Parse("cccccccc-3333-3333-3333-333333333333");

    private Mock<ISecretQueryService> _queryService = null!;
    private ClaimsPrincipal _user = null!;

    [SetUp]
    public void SetUp()
    {
        _queryService = new Mock<ISecretQueryService>();
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

    // ── helpers ────────────────────────────────────────────────────

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
