using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Reveal;

namespace Tamma.Api.Tests.Secrets.Reveal;

/// <summary>
/// Unit tests for <see cref="SecretEndpoints"/> covering:
///
/// <list type="bullet">
///   <item><description>Request-body validation (name, plaintext
///     bounds, purpose enum).</description></item>
///   <item><description>Response envelope shape — no plaintext in
///     the body.</description></item>
///   <item><description>Reveal outcomes map onto the correct HTTP
///     status.</description></item>
/// </list>
///
/// <para>The endpoints are minimal APIs — we call their handler
/// methods directly with a mocked <see cref="ISecretRevealService"/>
/// and a fake <see cref="ClaimsPrincipal"/>, then poke at the
/// resulting <see cref="IResult"/> via the
/// <see cref="Microsoft.AspNetCore.Http.HttpResults"/> probes. This
/// keeps the suite lightweight vs a full WebApplicationFactory.</para>
/// </summary>
[TestFixture]
public class SecretEndpointsTests
{
    private Mock<ISecretRevealService> _service = null!;
    private ClaimsPrincipal _user = null!;

    [SetUp]
    public void SetUp()
    {
        _service = new Mock<ISecretRevealService>();
        _user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", "11111111-1111-1111-1111-111111111111"),
        }));
    }

    // ── Create — body validation ────────────────────────────────────

    [TestCase("", "valid-plaintext", "DbCredential")]
    [TestCase(null, "valid-plaintext", "DbCredential")]
    [TestCase("db/app-role", "", "DbCredential")]
    [TestCase("db/app-role", null, "DbCredential")]
    [TestCase("db/app-role", "short", "DbCredential")]           // under min
    [TestCase("db/app-role", "valid-plaintext", "")]
    [TestCase("db/app-role", "valid-plaintext", "NotAPurpose")]
    public async Task Create_InvalidBody_Returns400(
        string? name, string? plaintext, string purpose)
    {
        var body = new SecretEndpoints.CreateSecretRequestBody(
            Name: name!,
            Purpose: purpose,
            Plaintext: plaintext,
            ConsumerRefs: null,
            RotationDays: null);

        var result = await SecretEndpoints.CreatePlatformSecret(
            body, _user, _service.Object, new DefaultHttpContext());

        var statusCode = ExtractStatusCode(result);
        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        _service.VerifyNoOtherCalls();
    }

    [Test]
    public async Task CreatePlatform_HappyPath_Returns201WithRevealToken()
    {
        var metadata = FakeMetadata("db/app-role", SecretScope.Platform);
        _service.Setup(s => s.IssueCreateAsync(
                "db/app-role",
                SecretScope.Platform,
                null,
                SecretPurpose.DbCredential,
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ConsumerRef>?>(),
                It.IsAny<Guid>(),
                It.IsAny<RotationSchedule?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RevealTokenIssueResult(
                metadata, "TOKEN-123", DateTimeOffset.UtcNow.AddSeconds(60)));

        var body = new SecretEndpoints.CreateSecretRequestBody(
            Name: "db/app-role",
            Purpose: "DbCredential",
            Plaintext: "valid-plaintext",
            ConsumerRefs: null,
            RotationDays: null);

        var result = await SecretEndpoints.CreatePlatformSecret(
            body, _user, _service.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(StatusCodes.Status201Created);

        var bodyObj = ExtractBody(result);
        bodyObj.Should().NotBeNull();
        var json = System.Text.Json.JsonSerializer.Serialize(bodyObj);
        json.Should().Contain("\"revealToken\":\"TOKEN-123\"");
        json.Should().Contain("revealUrl");
        // The plaintext must NOT be in the response body.
        json.Should().NotContain("valid-plaintext");
    }

    [Test]
    public async Task CreateTenant_NonEmptyTenantId_Passes()
    {
        var tenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var metadata = FakeMetadata(
            "db/role", SecretScope.Tenant, tenantId);
        _service.Setup(s => s.IssueCreateAsync(
                It.IsAny<string>(),
                SecretScope.Tenant,
                tenantId,
                It.IsAny<SecretPurpose>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ConsumerRef>?>(),
                It.IsAny<Guid>(),
                It.IsAny<RotationSchedule?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RevealTokenIssueResult(
                metadata, "TENANT-TOKEN",
                DateTimeOffset.UtcNow.AddSeconds(60)));

        var body = new SecretEndpoints.CreateSecretRequestBody(
            Name: "db/role",
            Purpose: "DbCredential",
            Plaintext: "valid-plaintext",
            ConsumerRefs: null,
            RotationDays: null);

        var result = await SecretEndpoints.CreateTenantSecret(
            tenantId, body, _user, _service.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(StatusCodes.Status201Created);
    }

    [Test]
    public async Task CreateTenant_EmptyTenantId_Returns400()
    {
        var body = new SecretEndpoints.CreateSecretRequestBody(
            Name: "db/role",
            Purpose: "DbCredential",
            Plaintext: "valid-plaintext",
            ConsumerRefs: null,
            RotationDays: null);

        var result = await SecretEndpoints.CreateTenantSecret(
            Guid.Empty, body, _user, _service.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    // ── Rotate ──────────────────────────────────────────────────────

    [Test]
    public async Task Rotate_HappyPath_Returns200()
    {
        var secretId = Guid.NewGuid();
        var metadata = FakeMetadata("db/role", SecretScope.Platform) with
        {
            Id = secretId,
            ActiveVersionNumber = 2,
        };
        _service.Setup(s => s.IssueRotateAsync(
                secretId, It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RevealTokenIssueResult(
                metadata, "NEW-TOKEN",
                DateTimeOffset.UtcNow.AddSeconds(60)));

        var body = new SecretEndpoints.RotateSecretRequestBody(
            NewPlaintext: "rotated-value");

        var result = await SecretEndpoints.RotateSecret(
            secretId, body, _user, _service.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(StatusCodes.Status200OK);
        var json = System.Text.Json.JsonSerializer.Serialize(ExtractBody(result));
        json.Should().Contain("\"revealToken\":\"NEW-TOKEN\"");
    }

    [Test]
    public async Task Rotate_NotFound_Returns404()
    {
        var secretId = Guid.NewGuid();
        _service.Setup(s => s.IssueRotateAsync(
                secretId, It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var body = new SecretEndpoints.RotateSecretRequestBody("valid-new-value");

        var result = await SecretEndpoints.RotateSecret(
            secretId, body, _user, _service.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task Rotate_MissingBody_Returns400()
    {
        var body = new SecretEndpoints.RotateSecretRequestBody(NewPlaintext: "");

        var result = await SecretEndpoints.RotateSecret(
            Guid.NewGuid(), body, _user, _service.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    // ── Reveal ──────────────────────────────────────────────────────

    [Test]
    public async Task Reveal_Success_Returns200WithPlaintext()
    {
        _service.Setup(s => s.ConsumeAsync(
                "TOKEN", It.IsAny<RevealCallerContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RevealTokenConsumeResult(
                RevealTokenConsumeOutcome.Success,
                SecretId: Guid.NewGuid(),
                VersionNumber: 1,
                SecretName: "db/role",
                Plaintext: "revealed-plaintext",
                ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(30)));

        var result = await SecretEndpoints.RevealSecret(
            "TOKEN", _service.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(StatusCodes.Status200OK);
        var json = System.Text.Json.JsonSerializer.Serialize(ExtractBody(result));
        json.Should().Contain("\"plaintext\":\"revealed-plaintext\"");
    }

    [Test]
    public async Task Reveal_AlreadyConsumed_Returns410()
    {
        _service.Setup(s => s.ConsumeAsync(
                "TOKEN", It.IsAny<RevealCallerContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RevealTokenConsumeResult(
                RevealTokenConsumeOutcome.AlreadyConsumed,
                Guid.NewGuid(), 1, null, null,
                DateTimeOffset.UtcNow));

        var result = await SecretEndpoints.RevealSecret(
            "TOKEN", _service.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(StatusCodes.Status410Gone);
    }

    [Test]
    public async Task Reveal_Expired_Returns410()
    {
        _service.Setup(s => s.ConsumeAsync(
                "TOKEN", It.IsAny<RevealCallerContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RevealTokenConsumeResult(
                RevealTokenConsumeOutcome.Expired,
                Guid.NewGuid(), 1, null, null,
                DateTimeOffset.UtcNow));

        var result = await SecretEndpoints.RevealSecret(
            "TOKEN", _service.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(StatusCodes.Status410Gone);
    }

    [Test]
    public async Task Reveal_NotFound_Returns404()
    {
        _service.Setup(s => s.ConsumeAsync(
                "TOKEN", It.IsAny<RevealCallerContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RevealTokenConsumeResult(
                RevealTokenConsumeOutcome.NotFound,
                null, null, null, null, null));

        var result = await SecretEndpoints.RevealSecret(
            "TOKEN", _service.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task Reveal_EmptyToken_Returns400()
    {
        var result = await SecretEndpoints.RevealSecret(
            "   ", _service.Object, new DefaultHttpContext());

        ExtractStatusCode(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    // ── Helpers ─────────────────────────────────────────────────────

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
        // The ASP.NET minimal-API result objects expose StatusCode via
        // either IStatusCodeHttpResult or the typed record properties.
        if (result is IStatusCodeHttpResult withStatus)
        {
            return withStatus.StatusCode ?? 0;
        }
        return 0;
    }

    private static object? ExtractBody(IResult result)
    {
        // Reach into common typed result shapes for the response
        // payload. Covers Results.Ok / Results.Created / Results.Json
        // which all implement IValueHttpResult.
        if (result is IValueHttpResult valued)
        {
            return valued.Value;
        }
        return null;
    }
}
