using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Tests.Documents;

/// <summary>
/// Story 39-11 (AC7/AC8, D6 — Activities half) — the FAIL-LOUD document persist
/// seam <see cref="PersistDocumentInstanceActivity"/> rides. Unlike the best-effort
/// event drain, <see cref="TammaApiClient.PersistDocumentAsync"/> /
/// <see cref="TammaApiClient.SetDocumentStatusAsync"/> THROW on a non-2xx or
/// transport failure — so the activity (which never catches to swallow) faults
/// loudly. The document is the lifecycle's product, not telemetry.
///
/// <para>Per the repo convention (Elsa's <c>ActivityExecutionContext</c> cannot be
/// cheaply constructed — see <c>CallLlmInlineActivityThinClientTests</c>), the seam
/// is proven at the client method the activity delegates to.</para>
/// </summary>
[TestFixture]
public class DocumentEngineWriteSeamTests
{
    private static TammaApiClient BuildClient(StubHandler handler)
    {
        var http = new HttpClient(handler);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Tamma:ApiUrl"] = "http://tamma.test" })
            .Build();
        return new TammaApiClient(http, NullLogger<TammaApiClient>.Instance, cfg);
    }

    [Test]
    public async Task PersistDocumentAsync_Faults_On5xx_NeverSwallows()
    {
        var client = BuildClient(new StubHandler(HttpStatusCode.InternalServerError, "boom"));

        var act = async () => await client.PersistDocumentAsync(
            new PersistDocumentRequest("{}", Guid.NewGuid()), "tenant");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Test]
    public async Task PersistDocumentAsync_Faults_On400_CodedBody()
    {
        var client = BuildClient(new StubHandler(HttpStatusCode.BadRequest,
            "{\"error\":\"DOCUMENT.STORE.INVALID_BODY\"}"));

        var act = async () => await client.PersistDocumentAsync(
            new PersistDocumentRequest("{}", null), "tenant");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Test]
    public async Task PersistDocumentAsync_Succeeds_On201_SendsTenantHeader()
    {
        var handler = new StubHandler(HttpStatusCode.Created, "{\"ok\":true}");
        var client = BuildClient(handler);

        await client.PersistDocumentAsync(new PersistDocumentRequest("{}", null), "tenant-1");

        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/engine/documents");
        handler.LastRequest.Headers.GetValues("X-Tenant-Id").Should().ContainSingle().Which.Should().Be("tenant-1");
    }

    [Test]
    public async Task SetDocumentStatusAsync_Faults_On5xx()
    {
        var client = BuildClient(new StubHandler(HttpStatusCode.InternalServerError, "boom"));

        var act = async () => await client.SetDocumentStatusAsync(
            Guid.NewGuid(), "accepted", Guid.NewGuid(), "tenant");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Test]
    public async Task SetDocumentStatusAsync_Faults_OnTransportException()
    {
        var client = BuildClient(new StubHandler(new HttpRequestException("connection refused")));

        var act = async () => await client.SetDocumentStatusAsync(
            Guid.NewGuid(), "accepted", null, "tenant");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Test]
    public async Task SetDocumentStatusAsync_Succeeds_On200_TargetsStatusRoute()
    {
        var docId = Guid.NewGuid();
        var handler = new StubHandler(HttpStatusCode.OK, "{\"ok\":true}");
        var client = BuildClient(handler);

        await client.SetDocumentStatusAsync(docId, "accepted", null, "tenant");

        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be($"/api/engine/documents/{docId}/status");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage? _response;
        private readonly Exception? _exception;
        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHandler(HttpStatusCode status, string body)
            => _response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

        public StubHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (_exception is not null) throw _exception;
            return Task.FromResult(_response!);
        }
    }
}
