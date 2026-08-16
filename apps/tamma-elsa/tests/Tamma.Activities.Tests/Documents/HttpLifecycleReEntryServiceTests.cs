using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.Documents;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Resume;

namespace Tamma.Activities.Tests.Documents;

/// <summary>
/// 2026-08-13 (engine-driven E2E follow-up) — pins the ENGINE-HOST re-entry
/// implementation: <see cref="HttpLifecycleReEntryService"/>, the latest-accepted
/// read over the API's 39-11 HTTP surface. Before it existed, the engine could only
/// register the Null seam (the REAL service's store dependencies are API-host-only),
/// which made the plan-review shim's <c>FetchLatestAcceptedDocumentActivity</c>
/// structurally blind — every engine-driven cycle terminated needs-human even after
/// the plan was accepted (E2E run 29's root cause).
/// </summary>
[TestFixture]
public class HttpLifecycleReEntryServiceTests
{
    private const string BaseUrl = "http://api.test:3000";

    // ── ReconstructAsync ──────────────────────────────────────────────────

    [Test]
    public async Task Reconstruct_AcceptedDocumentOfTheType_YieldsComplete()
    {
        var docId = Guid.NewGuid();
        var handler = new StubHandler((req, _) =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/api/documents/issues/owner%2Frepo%231/latest");
            req.Headers.Authorization!.Scheme.Should().Be("Bearer");
            req.Headers.Authorization.Parameter.Should().Be("test-token");
            return Json(HttpStatusCode.OK, $$"""
                {"issueId":"owner/repo#1","documents":[
                  {{Entry(docId, "plan", revision: 3)}}
                ]}
                """);
        });

        var position = await Service(handler).ReconstructAsync(
            null, "owner/repo#1", "plan", CancellationToken.None);

        position.ResumeAt.Should().Be(LifecycleResumeStage.Complete);
        position.ExistingDocumentId.Should().Be(docId);
        position.ExistingRevision.Should().Be(3);
    }

    [Test]
    public async Task Reconstruct_NoAcceptedDocumentOfTheType_YieldsFresh()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, $$"""
            {"issueId":"i-1","documents":[{{Entry(Guid.NewGuid(), "decomposition", 1)}}]}
            """));

        var position = await Service(handler).ReconstructAsync(
            null, "i-1", "plan", CancellationToken.None);

        position.ResumeAt.Should().Be(LifecycleResumeStage.Produce,
            "an accepted document of a DIFFERENT type must not read as this type's Complete");
        position.ExistingDocumentId.Should().BeNull();
    }

    [Test]
    public async Task Reconstruct_BlankIssueId_IsFresh_WithoutAnyHttpCall()
    {
        var handler = new StubHandler((_, _) =>
            throw new InvalidOperationException("no HTTP call expected"));

        var position = await Service(handler).ReconstructAsync(
            null, "  ", "plan", CancellationToken.None);

        position.ResumeAt.Should().Be(LifecycleResumeStage.Produce);
    }

    [Test]
    public async Task Reconstruct_NonSuccessStatus_ThrowsRetryable_NeverSilentFresh()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.InternalServerError, "{}"));

        var act = () => Service(handler).ReconstructAsync(
            null, "i-1", "plan", CancellationToken.None);

        (await act.Should().ThrowAsync<TammaError>(
                "a failed read yielding Fresh would re-produce work that already exists"))
            .Which.Code.Should().Be("DOCUMENT.REENTRY.HTTP_READ_FAILED");
    }

    [Test]
    public async Task Reconstruct_TransportFailure_ThrowsRetryable()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("boom"));

        var act = () => Service(handler).ReconstructAsync(
            null, "i-1", "plan", CancellationToken.None);

        (await act.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("DOCUMENT.REENTRY.HTTP_READ_FAILED");
    }

    [Test]
    public async Task Reconstruct_ExplicitTenant_TravelsAsTheTenantHeader()
    {
        var tenant = Guid.NewGuid();
        string? seenHeader = "unset";
        var handler = new StubHandler((req, _) =>
        {
            seenHeader = req.Headers.TryGetValues("X-Tenant-Id", out var v)
                ? v.Single()
                : null;
            return Json(HttpStatusCode.OK, """{"issueId":"i-1","documents":[]}""");
        });

        await Service(handler).ReconstructAsync(tenant, "i-1", "plan", CancellationToken.None);
        seenHeader.Should().Be(tenant.ToString());

        // Single-user gates dispatch tenantless — no header, the API's
        // service-plane binding resolves the personal tenant.
        await Service(handler).ReconstructAsync(null, "i-1", "plan", CancellationToken.None);
        seenHeader.Should().BeNull();
    }

    // ── GetDocumentBodyAsync ──────────────────────────────────────────────

    [Test]
    public async Task GetDocumentBody_MapsTheWireEntryOntoAnEnvelope()
    {
        var docId = Guid.NewGuid();
        var handler = new StubHandler((req, _) =>
        {
            req.RequestUri!.AbsolutePath.Should().Be($"/api/documents/{docId}");
            return Json(HttpStatusCode.OK, Entry(docId, "plan", revision: 2));
        });

        var envelope = await Service(handler).GetDocumentBodyAsync(
            null, docId, CancellationToken.None);

        envelope.Should().NotBeNull();
        envelope!.Id.Should().Be(docId);
        envelope.Type.Should().Be("plan");
        envelope.State.Should().Be(DocumentState.Accepted);
        envelope.IssueId.Should().Be("owner/repo#1");
        envelope.ProducedBy.Role.Should().Be("architect");
        envelope.Payload.GetProperty("goal").GetString().Should().Be("scripted",
            "the payload body must round-trip — it is the whole point of the read");
    }

    [Test]
    public async Task GetDocumentBody_NotFound_IsNull_NotAThrow()
    {
        var handler = new StubHandler((_, _) =>
            Json(HttpStatusCode.NotFound, """{"error":"document_not_found"}"""));

        var envelope = await Service(handler).GetDocumentBodyAsync(
            null, Guid.NewGuid(), CancellationToken.None);

        envelope.Should().BeNull();
    }

    [Test]
    public async Task GetDocumentBody_EmptyGuid_IsNull_WithoutAnyHttpCall()
    {
        var handler = new StubHandler((_, _) =>
            throw new InvalidOperationException("no HTTP call expected"));

        var envelope = await Service(handler).GetDocumentBodyAsync(
            null, Guid.Empty, CancellationToken.None);

        envelope.Should().BeNull();
    }

    // ── plumbing ──────────────────────────────────────────────────────────

    private static HttpLifecycleReEntryService Service(HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Tamma:ApiUrl"] = BaseUrl,
                ["Tamma:ApiToken"] = "test-token",
            }).Build();
        return new HttpLifecycleReEntryService(
            new StubFactory(handler), config,
            NullLogger<HttpLifecycleReEntryService>.Instance);
    }

    /// <summary>A wire LineageDocumentEntry as the API serializes it (39-11 DTOs).</summary>
    private static string Entry(Guid id, string type, int revision) => $$"""
        {"id":"{{id}}","documentType":"{{type}}","issueId":"owner/repo#1",
         "producedByRole":"architect","producedByAction":"plan-system-design",
         "revision":{{revision}},"status":"accepted","audience":null,
         "supersedesDocumentId":null,"parentDocumentId":null,"correlatingEventId":null,
         "createdAt":"2026-08-13T10:00:00.000Z","updatedAt":"2026-08-13T10:00:01.000Z",
         "body":{"goal":"scripted"},"reviews":[]}
        """;

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request, cancellationToken));
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
