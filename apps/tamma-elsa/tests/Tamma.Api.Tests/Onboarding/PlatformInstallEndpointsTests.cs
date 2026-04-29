using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Onboarding;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Tests.Onboarding;

/// <summary>
/// Story 31-9 — direct-handler tests for the platform-install
/// endpoints. Auth gating (PlatformsManage policy) is enforced at the
/// route mapping site in <c>Program.cs</c>; the handler itself trusts
/// the principal it receives. These tests verify:
/// <list type="bullet">
///   <item>The picker endpoint lists every PlatformKind with the
///         right available/coming-soon flag.</item>
///   <item>The install endpoint requires a tenant claim.</item>
///   <item>Body validation surfaces the right 400.</item>
///   <item>Successful install round-trips through the
///         <see cref="IPlatformConnectService"/>.</item>
/// </list>
/// </summary>
[TestFixture]
public class PlatformInstallEndpointsTests
{
    private FakeConnectService _service = null!;
#pragma warning disable NUnit1032
    private ServiceProvider _services = null!;
#pragma warning restore NUnit1032

    [SetUp]
    public void Setup()
    {
        _service = new FakeConnectService();
        var collection = new ServiceCollection();
        collection.AddSingleton<IPlatformConnectService>(_service);
        // ILoggerFactory + the logging stack are required by ASP.NET
        // Core's IResult.ExecuteAsync (it logs every response). Add a
        // null-only logger so the in-process result execution can run.
        collection.AddLogging();
        // No driver factory registered → ListPlatforms reports every
        // kind as "coming soon". Tests that need an "available" kind
        // attach the keyed registration in their own setup.
        _services = collection.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown() => _services?.Dispose();

    [Test]
    public void ListPlatforms_ReturnsAllSixKinds()
    {
        var result = PlatformInstallEndpoints.ListPlatforms(_services);
        var status = ResultStatus(result);
        status.Should().Be(StatusCodes.Status200OK);

        var body = ResultBody(result);
        // All 6 kinds present.
        foreach (var kind in Enum.GetValues<PlatformKind>())
        {
            body.Should().Contain(kind.ToString());
        }
    }

    [Test]
    public void ListPlatforms_MarksKindWithFactoryAsAvailable()
    {
        // Re-register the service collection with a fake Gitea factory
        // so the endpoint reports available=true for that kind. Replaces
        // _services (the new collection includes everything the helpers
        // need to execute IResults).
        _services.Dispose();
        var collection = new ServiceCollection();
        collection.AddSingleton<IPlatformConnectService>(_service);
        collection.AddLogging();
        collection.AddKeyedSingleton<IGitPlatformDriverFactory>(
            PlatformKind.Gitea, new FakeFactory(PlatformKind.Gitea));
        _services = collection.BuildServiceProvider();

        var result = PlatformInstallEndpoints.ListPlatforms(_services);
        var body = ResultBody(result);

        // Gitea is available, Bitbucket is not.
        body.Should().MatchRegex("\"kind\"\\s*:\\s*\"Gitea\".*\"available\"\\s*:\\s*true");
        body.Should().MatchRegex("\"kind\"\\s*:\\s*\"Bitbucket\".*\"available\"\\s*:\\s*false");
    }

    [Test]
    public async Task Install_Returns401_WhenNoUserClaim()
    {
        var http = new DefaultHttpContext();
        var principal = new ClaimsPrincipal(new ClaimsIdentity()); // anonymous

        var body = new PlatformInstallRequestBody(
            "Gitea", "https://gitea.example.com", null, "tok");
        var result = await PlatformInstallEndpoints.Install(
            body, principal, _service, http);

        ResultStatus(result).Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Test]
    public async Task Install_Returns400_WhenNoTenantClaim()
    {
        var principal = WithUser(Guid.NewGuid(), tenantId: null);
        var body = new PlatformInstallRequestBody(
            "Gitea", "https://gitea.example.com", null, "tok");
        var result = await PlatformInstallEndpoints.Install(
            body, principal, _service, new DefaultHttpContext());

        ResultStatus(result).Should().Be(StatusCodes.Status400BadRequest);
        ResultBody(result).Should().Contain("no tenant in JWT");
    }

    [Test]
    public async Task Install_Returns400_WhenKindIsUnknown()
    {
        var principal = WithUser(Guid.NewGuid(), tenantId: Guid.NewGuid());
        var body = new PlatformInstallRequestBody(
            "Slack", "https://slack.com", null, "tok");
        var result = await PlatformInstallEndpoints.Install(
            body, principal, _service, new DefaultHttpContext());

        ResultStatus(result).Should().Be(StatusCodes.Status400BadRequest);
        ResultBody(result).Should().Contain("unknown platform kind");
    }

    [Test]
    public async Task Install_PassesRequestToConnectService_OnHappyPath()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var principal = WithUser(userId, tenantId);

        _service.NextResult = PlatformConnectResult.Success(
            installationId: Guid.NewGuid(),
            kind: PlatformKind.Gitea,
            baseUrl: "https://gitea.example.com",
            externalId: null,
            secretName: "gitea/install-x");

        var body = new PlatformInstallRequestBody(
            "Gitea", "https://gitea.example.com", null, "tok-1");
        var result = await PlatformInstallEndpoints.Install(
            body, principal, _service, new DefaultHttpContext());

        ResultStatus(result).Should().Be(StatusCodes.Status200OK);
        _service.LastRequest.Should().NotBeNull();
        _service.LastRequest!.TenantId.Should().Be(tenantId);
        _service.LastRequest.ActorUserId.Should().Be(userId);
        _service.LastRequest.Kind.Should().Be(PlatformKind.Gitea);
        _service.LastRequest.CredentialPlaintext.Should().Be("tok-1");
    }

    [Test]
    public async Task Install_Returns400WithHint_WhenServiceFails()
    {
        var principal = WithUser(Guid.NewGuid(), tenantId: Guid.NewGuid());
        _service.NextResult = PlatformConnectResult.Failure(
            "auth_probe_failed", "bad token");

        var body = new PlatformInstallRequestBody(
            "Gitea", "https://gitea.example.com", null, "tok");
        var result = await PlatformInstallEndpoints.Install(
            body, principal, _service, new DefaultHttpContext());

        ResultStatus(result).Should().Be(StatusCodes.Status400BadRequest);
        var responseBody = ResultBody(result);
        responseBody.Should().Contain("auth_probe_failed");
        responseBody.Should().Contain("bad token");
    }

    [Test]
    public async Task ListInstallations_ReturnsEmpty_WhenNoTenantClaim()
    {
        var principal = WithUser(Guid.NewGuid(), tenantId: null);
        var result = await PlatformInstallEndpoints.ListInstallations(
            principal, _service, new DefaultHttpContext());

        ResultStatus(result).Should().Be(StatusCodes.Status200OK);
        ResultBody(result).Should().Contain("\"count\":0");
    }

    [Test]
    public async Task ListInstallations_ReturnsServiceRows()
    {
        var tenantId = Guid.NewGuid();
        var principal = WithUser(Guid.NewGuid(), tenantId);
        _service.NextList = new List<PlatformConnectionDto>
        {
            new(
                InstallationId: Guid.NewGuid(),
                Kind: PlatformKind.Gitea,
                BaseUrl: "https://gitea.example.com",
                ExternalId: "ext-1",
                Status: "connected",
                IsPrimary: true,
                CreatedAt: DateTime.UtcNow),
        };

        var result = await PlatformInstallEndpoints.ListInstallations(
            principal, _service, new DefaultHttpContext());

        ResultStatus(result).Should().Be(StatusCodes.Status200OK);
        ResultBody(result).Should().Contain("https://gitea.example.com");
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static ClaimsPrincipal WithUser(Guid userId, Guid? tenantId)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
        };
        if (tenantId is { } tid)
            claims.Add(new Claim("tenantId", tid.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private int ResultStatus(IResult result)
    {
        var ctx = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
            RequestServices = _services,
        };
        result.ExecuteAsync(ctx).GetAwaiter().GetResult();
        return ctx.Response.StatusCode;
    }

    private string ResultBody(IResult result)
    {
        var ctx = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
            RequestServices = _services,
        };
        result.ExecuteAsync(ctx).GetAwaiter().GetResult();
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ctx.Response.Body);
        return reader.ReadToEnd();
    }

    private sealed class FakeConnectService : IPlatformConnectService
    {
        public PlatformConnectRequest? LastRequest { get; private set; }
        public PlatformConnectResult NextResult { get; set; } =
            PlatformConnectResult.Failure("not_set", "test default");
        public IReadOnlyList<PlatformConnectionDto> NextList { get; set; } =
            Array.Empty<PlatformConnectionDto>();

        public Task<PlatformConnectResult> ConnectAsync(
            PlatformConnectRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(NextResult);
        }

        public Task<IReadOnlyList<PlatformConnectionDto>> ListForTenantAsync(
            Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult(NextList);
    }

    private sealed class FakeFactory : IGitPlatformDriverFactory
    {
        public FakeFactory(PlatformKind kind) => Kind = kind;
        public PlatformKind Kind { get; }
        public Task<IGitPlatformDriver> CreateAsync(
            Tamma.Platforms.Abstractions.Models.PlatformInstallation installation,
            string credentialPlaintext, CancellationToken ct = default) =>
            Task.FromResult<IGitPlatformDriver>(new NullGitPlatformDriver { Kind = Kind });
    }
}
