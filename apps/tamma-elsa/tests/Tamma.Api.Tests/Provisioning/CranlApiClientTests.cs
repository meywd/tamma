using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning.Cranl;

namespace Tamma.Api.Tests.Provisioning;

/// <summary>
/// Unit tests for <see cref="CranlApiClient"/>. All HTTP traffic is mocked
/// via a <see cref="HttpMessageHandler"/> stub — no live calls. Verifies:
/// request shapes (method, path, body, headers), response parsing, error
/// classification (404 / 500 / 429 → typed exceptions), and the in-client
/// 429 backoff retry.
/// </summary>
[TestFixture]
public class CranlApiClientTests
{
    private const string BaseUrl = "https://app.cranl.example/api";

    private Mock<HttpMessageHandler> _handler = null!;
    private List<HttpRequestMessage> _captured = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        _captured = new List<HttpRequestMessage>();
    }

    private CranlApiClient CreateClient()
    {
        var http = new HttpClient(_handler.Object)
        {
            BaseAddress = new Uri(BaseUrl + "/")
        };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "cranl_sk_TESTTESTTESTTESTTESTTESTTESTTEST");
        return new CranlApiClient(http, NullLogger<CranlApiClient>.Instance);
    }

    private void RespondOnce(HttpStatusCode status, string body, string contentType = "application/json")
    {
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => _captured.Add(Clone(req)))
            .ReturnsAsync(() => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            });
    }

    private void RespondInOrder(params (HttpStatusCode Status, string Body)[] responses)
    {
        var queue = new Queue<HttpResponseMessage>();
        foreach (var (status, body) in responses)
        {
            queue.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => _captured.Add(Clone(req)))
            .ReturnsAsync(() => queue.Count > 0 ? queue.Dequeue() : new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }

    /// <summary>
    /// Clone a request message so the captured copy survives after the
    /// SendAsync pipeline disposes the original.
    /// </summary>
    private static HttpRequestMessage Clone(HttpRequestMessage src)
    {
        var clone = new HttpRequestMessage(src.Method, src.RequestUri);
        if (src.Content is not null)
        {
            // Read synchronously — the handler delivers buffered StringContent
            // / JsonContent here, so this never blocks on a real socket.
            var bytes = src.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            clone.Content = new ByteArrayContent(bytes);
            foreach (var h in src.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        foreach (var h in src.Headers)
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return clone;
    }

    // ─── Projects ────────────────────────────────────────────────────────────

    [Test]
    public async Task CreateProjectAsync_PostsToProjectsWithExpectedBody()
    {
        RespondOnce(HttpStatusCode.OK,
            """{ "id": "proj-123", "name": "tamma-tenant-abcd1234", "organization_id": "org-1" }""");

        var client = CreateClient();
        var project = await client.CreateProjectAsync("tamma-tenant-abcd1234", "org-1");

        project.Id.Should().Be("proj-123");
        project.Name.Should().Be("tamma-tenant-abcd1234");
        project.OrganizationId.Should().Be("org-1");

        _captured.Should().HaveCount(1);
        var req = _captured[0];
        req.Method.Should().Be(HttpMethod.Post);
        req.RequestUri!.AbsolutePath.Should().Be("/api/projects");
        var body = await req.Content!.ReadAsStringAsync();
        body.Should().Contain("\"name\":\"tamma-tenant-abcd1234\"");
        body.Should().Contain("\"organizationId\":\"org-1\"");
    }

    [Test]
    public async Task DeleteProjectAsync_SendsDeleteToProjectsPath()
    {
        RespondOnce(HttpStatusCode.OK, """{ "success": true }""");

        var client = CreateClient();
        await client.DeleteProjectAsync("proj-123");

        _captured.Should().HaveCount(1);
        _captured[0].Method.Should().Be(HttpMethod.Delete);
        _captured[0].RequestUri!.AbsolutePath.Should().Be("/api/projects/proj-123");
    }

    // ─── Databases ───────────────────────────────────────────────────────────

    [Test]
    public async Task CreateDatabaseAsync_PostsRequestBodyWithProjectAndType()
    {
        RespondOnce(HttpStatusCode.OK,
            """{ "id": "db-1", "name": "tamma-x", "type": "postgresql", "status": "pending" }""");

        var client = CreateClient();
        var db = await client.CreateDatabaseAsync(new CreateDatabaseRequest
        {
            Name = "tamma-x",
            ProjectId = "proj-123",
            Type = "postgresql",
            ServerId = "germany-1"
        });

        db.Id.Should().Be("db-1");
        db.Status.Should().Be("pending");

        var body = await _captured.Single().Content!.ReadAsStringAsync();
        body.Should().Contain("\"projectId\":\"proj-123\"");
        body.Should().Contain("\"serverId\":\"germany-1\"");
        body.Should().Contain("\"type\":\"postgresql\"");
    }

    [Test]
    public async Task GetDatabaseAsync_ParsesConnectionFields()
    {
        RespondOnce(HttpStatusCode.OK, """
            {
              "id": "db-1",
              "name": "tamma-x",
              "type": "postgresql",
              "status": "running",
              "host": "tamma-x.cranl.internal",
              "port": 5432,
              "username": "admin",
              "password": "s3cret",
              "database": "tamma-x"
            }
            """);

        var client = CreateClient();
        var db = await client.GetDatabaseAsync("db-1");

        db.Status.Should().Be("running");
        db.Host.Should().Be("tamma-x.cranl.internal");
        db.Port.Should().Be(5432);
        db.BuildConnectionString().Should().Be("postgresql://admin:s3cret@tamma-x.cranl.internal:5432/tamma-x");
    }

    [Test]
    public async Task GetDatabaseAsync_FavorsExplicitConnectionStringWhenPresent()
    {
        RespondOnce(HttpStatusCode.OK, """
            {
              "id": "db-1",
              "name": "tamma-x",
              "type": "postgresql",
              "status": "running",
              "connection": "postgresql://u:p@h:5432/d"
            }
            """);

        var client = CreateClient();
        var db = await client.GetDatabaseAsync("db-1");
        db.BuildConnectionString().Should().Be("postgresql://u:p@h:5432/d");
    }

    [Test]
    public async Task DatabaseLifecycleAsync_PostsToActionPath()
    {
        RespondOnce(HttpStatusCode.OK, """{ "success": true, "action": "start" }""");

        var client = CreateClient();
        await client.DatabaseLifecycleAsync("db-1", "start");

        _captured.Single().Method.Should().Be(HttpMethod.Post);
        _captured.Single().RequestUri!.AbsolutePath.Should().Be("/api/databases/db-1/start");
    }

    // ─── Applications ────────────────────────────────────────────────────────

    [Test]
    public async Task CreateApplicationAsync_PostsAllOptionalFields()
    {
        RespondOnce(HttpStatusCode.OK,
            """{ "id": "app-1", "name": "tamma-engine-x", "status": "pending" }""");

        var client = CreateClient();
        var app = await client.CreateApplicationAsync(new CreateApplicationRequest
        {
            Name = "tamma-engine-x",
            ProjectId = "proj-1",
            RepositoryId = "repo-99",
            Branch = "main",
            BuildType = "dockerfile",
            ServerId = "germany-1",
            BuildPath = "/apps/tamma-elsa"
        });

        app.Id.Should().Be("app-1");
        var body = await _captured.Single().Content!.ReadAsStringAsync();
        body.Should().Contain("\"repositoryId\":\"repo-99\"");
        body.Should().Contain("\"buildType\":\"dockerfile\"");
        body.Should().Contain("\"buildPath\":\"/apps/tamma-elsa\"");
    }

    [Test]
    public async Task PutEnvironmentAsync_SendsEnvNewlineString()
    {
        RespondOnce(HttpStatusCode.OK, """{ "success": true }""");

        var client = CreateClient();
        var env = "DATABASE_URL=postgresql://u:p@h:5432/d\nTAMMA_TENANT_ID=abc";
        await client.PutEnvironmentAsync("app-1", env);

        var req = _captured.Single();
        req.Method.Should().Be(HttpMethod.Put);
        req.RequestUri!.AbsolutePath.Should().Be("/api/applications/app-1/environment");
        var body = await req.Content!.ReadAsStringAsync();
        body.Should().Contain("\"env\":");
        body.Should().Contain("DATABASE_URL=postgresql");
    }

    [Test]
    public async Task GetApplicationDomainsAsync_ParsesDefaultDomain()
    {
        RespondOnce(HttpStatusCode.OK, """
            {
              "domains": [
                { "domainId": "d1", "host": "tamma-engine-abc.cranl.net", "https": true,
                  "certificateType": "wildcard", "sslStatus": "active" }
              ],
              "defaultDomain": "tamma-engine-abc.cranl.net"
            }
            """);

        var client = CreateClient();
        var domains = await client.GetApplicationDomainsAsync("app-1");

        domains.DefaultDomain.Should().Be("tamma-engine-abc.cranl.net");
        domains.Domains.Should().HaveCount(1);
        domains.Domains[0].Host.Should().Be("tamma-engine-abc.cranl.net");
    }

    // ─── Error handling ──────────────────────────────────────────────────────

    [Test]
    public async Task GetDatabaseAsync_404_ThrowsCranlApiExceptionWithStatus()
    {
        RespondOnce(HttpStatusCode.NotFound, """{ "error": "Database not found" }""");

        var client = CreateClient();
        var act = async () => await client.GetDatabaseAsync("missing");

        var ex = await act.Should().ThrowAsync<CranlApiException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ex.Which.CranlError.Should().Be("Database not found");
        ex.Which.IsRetryable.Should().BeFalse();
    }

    [Test]
    public async Task GetDatabaseAsync_500_ThrowsCranlApiException()
    {
        RespondOnce(HttpStatusCode.InternalServerError, """{ "error": "boom" }""");

        var client = CreateClient();
        var act = async () => await client.GetDatabaseAsync("x");

        var ex = await act.Should().ThrowAsync<CranlApiException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task GetDatabaseAsync_TwoRateLimitsThenSuccess_RetriesAndSucceeds()
    {
        RespondInOrder(
            (HttpStatusCode.TooManyRequests, """{ "error": "rate limit" }"""),
            (HttpStatusCode.TooManyRequests, """{ "error": "rate limit" }"""),
            (HttpStatusCode.OK, """{ "id": "db-1", "name": "x", "type": "postgresql", "status": "running" }"""));

        var client = CreateClient();
        var db = await client.GetDatabaseAsync("db-1");

        db.Id.Should().Be("db-1");
        // 3 attempts: 2 retries + 1 success
        _captured.Should().HaveCount(3);
    }

    [Test]
    public async Task GetDatabaseAsync_PersistentRateLimit_ThrowsAfterRetriesExhausted()
    {
        // Always 429 — client retries up to 3 times then surfaces the last
        // response as an exception.
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => _captured.Add(Clone(req)))
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{ "error": "rate limit" }""", Encoding.UTF8, "application/json")
            });

        var client = CreateClient();
        var act = async () => await client.GetDatabaseAsync("db-1");

        var ex = await act.Should().ThrowAsync<CranlApiException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        ex.Which.IsRetryable.Should().BeTrue();
        // 3 attempts (initial + 2 retries) per the in-client schedule.
        _captured.Count.Should().Be(3);
    }

    [Test]
    public async Task ConfigureClient_SetsAuthorizationAndUserAgent()
    {
        var http = new HttpClient(_handler.Object);
        var options = new CranlOptions
        {
            BaseUrl = "https://app.cranl.example/api",
            ApiKey = "cranl_sk_THISISTHEKEY",
            UserAgent = "Tamma-Test/1.0"
        };

        CranlApiClient.ConfigureClient(http, options);

        http.BaseAddress!.ToString().Should().Be("https://app.cranl.example/api/");
        http.DefaultRequestHeaders.Authorization!.Scheme.Should().Be("Bearer");
        http.DefaultRequestHeaders.Authorization!.Parameter.Should().Be("cranl_sk_THISISTHEKEY");
        http.DefaultRequestHeaders.UserAgent.ToString().Should().Contain("Tamma-Test/1.0");
    }
}
