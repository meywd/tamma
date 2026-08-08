using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Dtos.Engine;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Engine;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Tests.Engine;

/// <summary>
/// Epic 31 P3 (seam 5) — CONTRACT pins for the /api/engine/* git-proxy
/// handlers, written around the reroute off <c>IGitHubEngineCallbackService</c>
/// onto the platform-agnostic <see cref="IEngineGitCallbackService"/>. The
/// response SHAPES the deployed Elsa activities consume must survive the
/// reroute unchanged:
///
/// <list type="bullet">
///   <item>repo-config: 200 with the config JSON; graceful 200 <c>{}</c> when
///     no platform driver resolves (never a 5xx);</item>
///   <item>issues: 200 <c>{issues:[{id,number,title,state,body,html_url,labels:[{name}]}], total}</c>;</item>
///   <item>security-alerts: 200 <c>{dependabot:[], codeScanning:[]}</c>;</item>
///   <item>issue-comment: 200 <c>{id, htmlUrl}</c>; labels: 200 <c>{labels:[...]}</c> /
///     <c>{removed,label}</c>;</item>
///   <item>create-issue: 201 whose LOCATION is the platform's REAL issue URL
///     (the fabricated github.com URL is dead) and body <c>{number, htmlUrl, title}</c>;</item>
///   <item>no driver ⇒ the legacy 503 <c>github_client_not_configured</c>
///     envelope; platform failure ⇒ 502 <c>{error}</c>.</item>
/// </list>
///
/// <para>Also covers <see cref="PlatformEngineCallbackService"/> behavior over a
/// mocked driver: the .tamma config path fallback, the §4 security-alert
/// degradation (capability_unsupported ⇒ empty lists + ONE
/// ENGINE.SECURITY_ALERTS.SKIPPED audit event), and the real-URL issue create.</para>
/// </summary>
[TestFixture]
public class EngineCallbackContractTests
{
    private Mock<IEngineGitCallbackService> _service = null!;
    private StubTenantContext _tenant = null!;

    private sealed class StubTenantContext : ITenantContext
    {
        public Guid? TenantId { get; private set; } = Guid.NewGuid();
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    [SetUp]
    public void SetUp()
    {
        _service = new Mock<IEngineGitCallbackService>(MockBehavior.Loose);
        _tenant = new StubTenantContext();
    }

    private static async Task<(int Status, JsonElement Body, string? Location)> Exec(IResult result)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var raw = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var body = string.IsNullOrWhiteSpace(raw)
            ? default
            : JsonDocument.Parse(raw).RootElement.Clone();
        var location = ctx.Response.Headers.Location.ToString();
        return (ctx.Response.StatusCode, body, string.IsNullOrEmpty(location) ? null : location);
    }

    // ================================================================
    // Handler shapes (mocked service)
    // ================================================================

    [Test]
    public async Task RepoConfig_NoDriver_GracefulEmptyObject_Never5xx()
    {
        _service
            .Setup(s => s.ReadRepoConfigAsync(It.IsAny<Guid?>(), "acme", "widgets", "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(GitHubCallbackResult<JsonElement>.NotConfigured());

        var (status, body, _) = await Exec(await EngineEndpoints.GetRepoConfig(
            _service.Object, _tenant, "acme/widgets", null));

        status.Should().Be(200, "the deployed activity falls through to its empty-conventions path on {}");
        body.ValueKind.Should().Be(JsonValueKind.Object);
        body.EnumerateObject().Should().BeEmpty();
    }

    [Test]
    public async Task Issues_ProjectsTheLegacyRowShape()
    {
        var row = JsonDocument.Parse(
            "{\"id\":7,\"number\":7,\"title\":\"t\",\"state\":\"open\",\"body\":\"b\",\"html_url\":\"https://p/7\",\"labels\":[{\"name\":\"bug\"}]}")
            .RootElement.Clone();
        _service
            .Setup(s => s.ListIssuesAsync(It.IsAny<Guid?>(), "acme", "widgets", "open", null, 30, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GitHubCallbackResult<IssueListResult>.Ok(new IssueListResult(new[] { row }, 1)));

        var (status, body, _) = await Exec(await EngineEndpoints.GetIssues(
            _service.Object, _tenant, "acme/widgets", null, null, null, null));

        status.Should().Be(200);
        body.GetProperty("total").GetInt32().Should().Be(1);
        var issue = body.GetProperty("issues").EnumerateArray().Single();
        issue.GetProperty("number").GetInt32().Should().Be(7);
        issue.GetProperty("html_url").GetString().Should().Be("https://p/7");
        issue.GetProperty("labels").EnumerateArray().Single().GetProperty("name").GetString().Should().Be("bug");
    }

    [Test]
    public async Task Issues_NoDriver_Returns503_GithubClientNotConfiguredEnvelope()
    {
        _service
            .Setup(s => s.ListIssuesAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GitHubCallbackResult<IssueListResult>.NotConfigured());

        var (status, body, _) = await Exec(await EngineEndpoints.GetIssues(
            _service.Object, _tenant, "acme/widgets", null, null, null, null));

        status.Should().Be(503);
        body.GetProperty("error").GetString().Should().Be("github_client_not_configured",
            "the deployed activities branch on this exact legacy error code");
    }

    [Test]
    public async Task Issues_PlatformFailure_Returns502_ErrorShape()
    {
        _service
            .Setup(s => s.ListIssuesAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GitHubCallbackResult<IssueListResult>.Failed("404: not found"));

        var (status, body, _) = await Exec(await EngineEndpoints.GetIssues(
            _service.Object, _tenant, "acme/widgets", null, null, null, null));

        status.Should().Be(502);
        body.GetProperty("error").GetString().Should().Be("404: not found");
    }

    [Test]
    public async Task SecurityAlerts_ShapeIsDependabotPlusCodeScanning()
    {
        _service
            .Setup(s => s.ListSecurityAlertsAsync(It.IsAny<Guid?>(), "acme", "widgets", "all", It.IsAny<CancellationToken>()))
            .ReturnsAsync(GitHubCallbackResult<SecurityAlertResult>.Ok(
                new SecurityAlertResult(Array.Empty<JsonElement>(), Array.Empty<JsonElement>())));

        var (status, body, _) = await Exec(await EngineEndpoints.GetSecurityAlerts(
            _service.Object, _tenant, "acme/widgets", null));

        status.Should().Be(200);
        body.GetProperty("dependabot").ValueKind.Should().Be(JsonValueKind.Array);
        body.GetProperty("codeScanning").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Test]
    public async Task IssueComment_ShapeIsIdPlusHtmlUrl()
    {
        _service
            .Setup(s => s.PostIssueCommentAsync(It.IsAny<Guid?>(), "acme", "widgets", 7, "hello", It.IsAny<CancellationToken>()))
            .ReturnsAsync(GitHubCallbackResult<IssueCommentResult>.Ok(new IssueCommentResult(123, "")));

        var (status, body, _) = await Exec(await EngineEndpoints.PostIssueComment(
            new IssueCommentRequest("acme/widgets", 7, "hello"), _service.Object, _tenant));

        status.Should().Be(200);
        body.GetProperty("id").GetInt64().Should().Be(123);
        body.TryGetProperty("htmlUrl", out _).Should().BeTrue();
    }

    [Test]
    public async Task CreateIssue_LocationIsThePlatformsRealUrl_NotAFabricatedGitHubOne()
    {
        _service
            .Setup(s => s.CreateIssueAsync(It.IsAny<Guid?>(), "acme", "widgets", "T", null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GitHubCallbackResult<CreatedIssueResult>.Ok(
                new CreatedIssueResult(9, "https://gitea.example.com/acme/widgets/issues/9", "T")));

        var (status, body, location) = await Exec(await EngineEndpoints.CreateIssue(
            new Tamma.Api.Dtos.Engine.CreateIssueRequest("acme/widgets", "T", null, null, null),
            _service.Object, _tenant));

        status.Should().Be(201);
        location.Should().Be("https://gitea.example.com/acme/widgets/issues/9",
            "the fabricated https://github.com/... Location is dead — the platform's REAL URL travels");
        body.GetProperty("htmlUrl").GetString().Should().Be("https://gitea.example.com/acme/widgets/issues/9");
        body.GetProperty("number").GetInt32().Should().Be(9);
    }

    // ================================================================
    // PlatformEngineCallbackService behavior (mocked driver)
    // ================================================================

    private sealed class RecordingEventRepository : IEventRepository
    {
        public List<DomainEvent> Appended { get; } = new();
        public Task<DomainEvent> AppendAsync(DomainEvent evt) { Appended.Add(evt); return Task.FromResult(evt); }
        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    }

    private static (PlatformEngineCallbackService Service, Mock<IGitPlatformClient> Client, RecordingEventRepository Events)
        ServiceOverMockedDriver()
    {
        var client = new Mock<IGitPlatformClient>(MockBehavior.Loose);
        var resolver = new Mock<IPlatformResolver>();
        resolver
            .Setup(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediationDriverResolution(
                new FakeDriver(client.Object), MediationCredentialSource.PlatformDefault));
        var events = new RecordingEventRepository();
        var service = new PlatformEngineCallbackService(
            resolver.Object, events, NullLogger<PlatformEngineCallbackService>.Instance);
        return (service, client, events);
    }

    private sealed class FakeDriver : IGitPlatformDriver
    {
        public FakeDriver(IGitPlatformClient client) => Client = client;
        public PlatformKind Kind => PlatformKind.Gitea;
        public IGitPlatformClient Client { get; }
        public IGitPlatformActionsClient? Actions => null;
        public IReadOnlySet<PlatformCapability> Capabilities { get; } = new HashSet<PlatformCapability>();
    }

    [Test]
    public async Task RepoConfig_TriesYamlThenYmlThenJson_AndWrapsYaml()
    {
        var (service, client, _) = ServiceOverMockedDriver();
        client
            .Setup(c => c.GetFileContentAsync(It.IsAny<GetFileContentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GetFileContentRequest r, CancellationToken _) =>
                r.Path == ".tamma/config.yml"
                    ? PlatformResult<byte[]>.FromOk(System.Text.Encoding.UTF8.GetBytes("conventions: x"))
                    : PlatformResult<byte[]>.FromError(new PlatformError.NotFound()));

        var result = await service.ReadRepoConfigAsync(null, "acme", "widgets", "main");

        result.ServiceUnavailable.Should().BeFalse();
        result.Result.GetProperty("rawYaml").GetString().Should().Be("conventions: x");
    }

    [Test]
    public async Task RepoConfig_NoConfigFile_ReturnsEmptyObject()
    {
        var (service, client, _) = ServiceOverMockedDriver();
        client
            .Setup(c => c.GetFileContentAsync(It.IsAny<GetFileContentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<byte[]>.FromError(new PlatformError.NotFound()));

        var result = await service.ReadRepoConfigAsync(null, "acme", "widgets", "main");

        result.Result.ValueKind.Should().Be(JsonValueKind.Object);
        result.Result.EnumerateObject().Should().BeEmpty();
    }

    [Test]
    public async Task SecurityAlerts_CapabilityUnsupported_DegradesToEmpty_WithOneSkipAuditEvent()
    {
        // §4 — skip-with-audit, never silent, never a hard failure.
        var (service, client, events) = ServiceOverMockedDriver();
        client
            .Setup(c => c.ListSecurityAlertsAsync("acme", "widgets", "all", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<SecurityAlerts>.FromError(
                new PlatformError.InvalidRequest(PlatformErrorText.CapabilityUnsupportedCode, "no alerts API")));

        var result = await service.ListSecurityAlertsAsync(null, "acme", "widgets", "all");

        result.ServiceUnavailable.Should().BeFalse();
        result.Result!.Dependabot.Should().BeEmpty();
        result.Result.CodeScanning.Should().BeEmpty();

        var evt = events.Appended.Should().ContainSingle().Subject;
        evt.Type.Should().Be(PlatformEngineCallbackService.SecurityAlertsSkippedEventType);
        evt.Tags.Should().Contain("capability_unsupported");
    }

    [Test]
    public async Task CreateIssue_ReturnsThePlatformsRealHtmlUrl()
    {
        var (service, client, _) = ServiceOverMockedDriver();
        client
            .Setup(c => c.CreateIssueAsync(It.IsAny<Tamma.Platforms.Abstractions.Models.CreateIssueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<Issue>.FromOk(new Issue(
                "9", "T", null, IssueState.Open, "https://gitea.example.com/acme/widgets/issues/9",
                Array.Empty<string>())));

        var result = await service.CreateIssueAsync(null, "acme", "widgets", "T", null, null, null);

        result.Result!.HtmlUrl.Should().Be("https://gitea.example.com/acme/widgets/issues/9");
        result.Result.Number.Should().Be(9);
    }

    [Test]
    public async Task AnyOp_NoDriverResolved_AnswersTheLegacyNotConfiguredEnvelope()
    {
        var resolver = new Mock<IPlatformResolver>();
        resolver
            .Setup(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MediationDriverResolution?)null);
        var service = new PlatformEngineCallbackService(
            resolver.Object, new RecordingEventRepository(), NullLogger<PlatformEngineCallbackService>.Instance);

        (await service.ListIssuesAsync(null, "a", "b", "open", null, 30, 1)).ServiceUnavailable.Should().BeTrue();
        (await service.CreateIssueAsync(null, "a", "b", "T", null, null, null)).ServiceUnavailable.Should().BeTrue();
        (await service.ReadRepoConfigAsync(null, "a", "b", "main")).ServiceUnavailable.Should().BeTrue();
    }
}
