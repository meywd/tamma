using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NUnit.Framework;
using Tamma.Api.Services.Integrations;
using Tamma.Core.Interfaces;

namespace Tamma.Api.Tests.Integrations;

/// <summary>
/// <see cref="JiraApiClient"/> use-time SSRF hardening. Pins that a hostile
/// per-tenant <c>baseUrl</c> (private/loopback/metadata) or a path-traversal
/// <c>ticketId</c> is refused BEFORE any HTTP call is made, that a redirect is not
/// followed, and that a legitimate allowlisted host still works.
/// </summary>
[TestFixture]
public class JiraApiClientSsrfTests
{
    // Allowlist atlassian so the legitimate-host tests need no DNS.
    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jira:AllowedHostSuffixes"] = ".atlassian.net",
        })
        .Build();

    private static (JiraApiClient client, Mock<HttpMessageHandler> handler) Build(
        HttpStatusCode status = HttpStatusCode.OK, string? body = null)
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(status)
            {
                Content = new StringContent(body ?? "{}", System.Text.Encoding.UTF8, "application/json"),
            });

        var http = new HttpClient(handler.Object);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(JiraApiClient.HttpClientName)).Returns(http);

        return (new JiraApiClient(factory.Object, Config(), NullLogger<JiraApiClient>.Instance), handler);
    }

    private static void VerifyNoHttpCall(Mock<HttpMessageHandler> handler) =>
        handler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());

    [TestCase("http://169.254.169.254")]
    [TestCase("https://169.254.169.254")]
    [TestCase("https://127.0.0.1")]
    [TestCase("https://10.0.0.1")]
    [TestCase("http://acme.atlassian.net")]   // http scheme rejected even when host allowlisted
    public async Task GetTicket_HostileBaseUrl_RefusedWithoutHttpCall(string baseUrl)
    {
        var (client, handler) = Build();
        var cred = new JiraCredential(baseUrl, "bot@example.com", "token");

        var result = await client.GetTicketAsync(cred, "PROJ-1");

        result.Success.Should().BeFalse();
        VerifyNoHttpCall(handler);
    }

    [TestCase("../../../etc/passwd")]
    [TestCase("PROJ/1/../../admin")]
    [TestCase("..")]
    public async Task GetTicket_PathTraversalTicketId_RefusedWithoutHttpCall(string ticketId)
    {
        var (client, handler) = Build();
        var cred = new JiraCredential("https://acme.atlassian.net", "bot@example.com", "token");

        var result = await client.GetTicketAsync(cred, ticketId);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("invalid ticket id");
        VerifyNoHttpCall(handler);
    }

    [Test]
    public async Task GetTicket_RedirectResponse_RefusedNotFollowed()
    {
        var (client, _) = Build(HttpStatusCode.Redirect);
        var cred = new JiraCredential("https://acme.atlassian.net", "bot@example.com", "token");

        var result = await client.GetTicketAsync(cred, "PROJ-1");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("refused redirect");
    }

    [Test]
    public async Task GetTicket_AllowlistedHost_ValidResponse_Succeeds()
    {
        const string body = """
        {"id":"1001","key":"PROJ-1","fields":{"summary":"do it","status":{"name":"To Do"}}}
        """;
        var (client, _) = Build(HttpStatusCode.OK, body);
        var cred = new JiraCredential("https://acme.atlassian.net", "bot@example.com", "token");

        var result = await client.GetTicketAsync(cred, "PROJ-1");

        result.Success.Should().BeTrue();
        result.Data!.Key.Should().Be("PROJ-1");
    }

    [Test]
    public async Task UpdateTicket_HostileBaseUrl_RefusedWithoutHttpCall()
    {
        var (client, handler) = Build();
        var cred = new JiraCredential("https://192.168.1.10", "bot@example.com", "token");

        var result = await client.UpdateTicketAsync(cred, "PROJ-1", new JiraTicketUpdate { Comment = "hi" });

        result.Success.Should().BeFalse();
        VerifyNoHttpCall(handler);
    }
}
