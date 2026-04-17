using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using NUnit.Framework;
using Tamma.Api.Services.Diagnostics;
using Tamma.Api.Services.Diagnostics.Models;
using Tamma.Api.Services.SaaS;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.SaaS;

/// <summary>
/// Unit tests for <see cref="LlmProxyService"/>. Exercises the happy path
/// (a canned anthropic response is parsed + diagnostics recorded), plus budget
/// enforcement and HTTP error propagation.
/// </summary>
[TestFixture]
public class LlmProxyServiceTests
{
    private Mock<IHttpClientFactory> _httpFactory = null!;
    private Mock<IDiagnosticsService> _diagnostics = null!;
    private Mock<ILogger<LlmProxyService>> _logger = null!;
    private Mock<HttpMessageHandler> _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _httpFactory = new Mock<IHttpClientFactory>();
        _diagnostics = new Mock<IDiagnosticsService>();
        _logger = new Mock<ILogger<LlmProxyService>>();
        _handler = new Mock<HttpMessageHandler>();

        // Default: no budget (unlimited).
        _diagnostics.Setup(d => d.GetBudgetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Budget(spent: 0m, limit: 1_000_000m, over: false));
    }

    private LlmProxyService CreateService()
    {
        var client = new HttpClient(_handler.Object)
        {
            BaseAddress = new Uri("https://api.anthropic.com")
        };
        _httpFactory.Setup(f => f.CreateClient("anthropic")).Returns(client);

        return new LlmProxyService(_httpFactory.Object, _diagnostics.Object, _logger.Object);
    }

    // ─── Happy path ────────────────────────────────────────────────────────

    [Test]
    public async Task ChatAsync_HappyPath_ReturnsParsedResponseAndRecordsDiagnostic()
    {
        var tenantId = Guid.NewGuid();
        var cannedResponse = """
            {
              "id": "msg_abc123",
              "model": "claude-sonnet-4.5",
              "content": [ { "type": "text", "text": "hello from canned" } ],
              "stop_reason": "end_turn",
              "usage": { "input_tokens": 12, "output_tokens": 34 }
            }
            """;

        SetupHttp(HttpStatusCode.OK, cannedResponse);

        var service = CreateService();
        var request = new ChatRequest(
            Model: "claude-sonnet-4.5",
            Messages: new[] { new ChatMessage("user", "Say hi") },
            MaxTokens: 64,
            Temperature: 0.2);

        var resp = await service.ChatAsync(request, tenantId);

        resp.Success.Should().BeTrue();
        resp.Text.Should().Be("hello from canned");
        resp.Model.Should().Be("claude-sonnet-4.5");
        resp.PromptTokens.Should().Be(12);
        resp.CompletionTokens.Should().Be(34);
        resp.TotalTokens.Should().Be(46);

        _diagnostics.Verify(d => d.RecordEventAsync(
            It.Is<ProviderDiagnostic>(p =>
                p.TenantId == tenantId &&
                p.ProviderKey == "anthropic-claude" &&
                p.TokensUsed == 46 &&
                p.Cost > 0m &&
                p.Success == true &&
                p.Model == "claude-sonnet-4.5"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task ChatAsync_ForwardsPromptToAnthropic()
    {
        HttpRequestMessage? captured = null;
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"m","model":"m","content":[{"type":"text","text":"x"}],"usage":{"input_tokens":1,"output_tokens":1}}""",
                    Encoding.UTF8, "application/json")
            });

        var service = CreateService();
        var req = new ChatRequest(
            Model: "claude-opus-4.7",
            Messages: new[]
            {
                new ChatMessage("system", "be brief"),
                new ChatMessage("user", "hi"),
            },
            MaxTokens: 100,
            Temperature: null);

        await service.ChatAsync(req, null);

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().Be("/v1/messages");
        var body = await captured.Content!.ReadAsStringAsync();
        var root = JsonDocument.Parse(body).RootElement;
        root.GetProperty("model").GetString().Should().Be("claude-opus-4.7");
        root.GetProperty("max_tokens").GetInt32().Should().Be(100);
    }

    // ─── Budget enforcement ────────────────────────────────────────────────

    [Test]
    public async Task ChatAsync_OverBudget_RejectsRequestBeforeCallingUpstream()
    {
        var tenantId = Guid.NewGuid();
        _diagnostics.Setup(d => d.GetBudgetAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Budget(spent: 120m, limit: 100m, over: true));

        var service = CreateService();
        var req = new ChatRequest("m", new[] { new ChatMessage("user", "x") }, null, null);

        var resp = await service.ChatAsync(req, tenantId);

        resp.Success.Should().BeFalse();
        resp.ErrorReason.Should().Be("budget_exceeded");
        _handler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
        _diagnostics.Verify(d => d.RecordEventAsync(
            It.IsAny<ProviderDiagnostic>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ChatAsync_NoTenant_SkipsBudgetCheck()
    {
        SetupHttp(HttpStatusCode.OK,
            """{"id":"m","model":"m","content":[{"type":"text","text":"x"}],"usage":{"input_tokens":1,"output_tokens":1}}""");

        var service = CreateService();
        var req = new ChatRequest("m", new[] { new ChatMessage("user", "x") }, null, null);

        var resp = await service.ChatAsync(req, null);

        resp.Success.Should().BeTrue();
        _diagnostics.Verify(
            d => d.GetBudgetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─── Upstream errors ───────────────────────────────────────────────────

    [Test]
    public async Task ChatAsync_UpstreamReturnsError_ReturnsFailureAndRecordsFailureDiagnostic()
    {
        var tenantId = Guid.NewGuid();
        SetupHttp(HttpStatusCode.InternalServerError, "{\"error\":\"upstream blew up\"}");

        var service = CreateService();
        var req = new ChatRequest("m", new[] { new ChatMessage("user", "x") }, 50, null);

        var resp = await service.ChatAsync(req, tenantId);

        resp.Success.Should().BeFalse();
        resp.ErrorReason.Should().Be("upstream_error");

        _diagnostics.Verify(d => d.RecordEventAsync(
            It.Is<ProviderDiagnostic>(p =>
                p.TenantId == tenantId &&
                p.ProviderKey == "anthropic-claude" &&
                p.Success == false &&
                !string.IsNullOrEmpty(p.ErrorMessage)),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task ChatAsync_UpstreamThrows_ReturnsFailureAndRecordsFailureDiagnostic()
    {
        var tenantId = Guid.NewGuid();
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network down"));

        var service = CreateService();
        var req = new ChatRequest("m", new[] { new ChatMessage("user", "x") }, 50, null);

        var resp = await service.ChatAsync(req, tenantId);

        resp.Success.Should().BeFalse();
        resp.ErrorReason.Should().Be("upstream_error");
        _diagnostics.Verify(d => d.RecordEventAsync(
            It.Is<ProviderDiagnostic>(p => p.Success == false),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── Validation ────────────────────────────────────────────────────────

    [Test]
    public async Task ChatAsync_EmptyMessages_ReturnsValidationError()
    {
        var service = CreateService();
        var req = new ChatRequest("m", Array.Empty<ChatMessage>(), null, null);

        var resp = await service.ChatAsync(req, null);

        resp.Success.Should().BeFalse();
        resp.ErrorReason.Should().Be("invalid_request");
        _handler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private void SetupHttp(HttpStatusCode status, string body)
    {
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private static BudgetStatus Budget(decimal spent, decimal limit, bool over) => new(
        AccountId: Guid.Empty,
        PeriodStart: DateTime.UtcNow.AddDays(-1),
        PeriodEnd: DateTime.UtcNow.AddDays(1),
        Spent: spent,
        Limit: limit,
        Remaining: limit - spent,
        PercentUsed: limit == 0 ? 0 : (double)(spent / limit * 100),
        AlertThreshold: 0.8,
        ShouldAlert: false,
        IsOverBudget: over);
}
