using System.Net;

namespace Tamma.Api.Tests.Platforms;

/// <summary>
/// Story 31-8 — programmable <see cref="HttpMessageHandler"/> used by
/// the per-platform provisioner test fixtures. Records every request
/// (method, URL, body) and replies with the per-URL response queue.
///
/// <para>The recorder is rich enough to cover the per-target error
/// isolation tests (return 500 for one target's PUT, 200 for the
/// next target's PUT, assert the per-target results carry the right
/// shape) without standing up WireMock.</para>
/// </summary>
public sealed class CiSecretsProvisionerTestHandler : HttpMessageHandler
{
    public sealed record RecordedRequest(
        string Method,
        string Url,
        string? Body);

    public List<RecordedRequest> Requests { get; } = new();

    /// <summary>
    /// Per-URL response queue. Key is the request URL (path + query);
    /// value is a list of responses returned in order. Use
    /// <c>"GET /repos/o/r/actions/secrets/public-key"</c> shape.
    /// </summary>
    public Dictionary<string, Queue<HttpResponseMessage>> Responses { get; } = new();

    /// <summary>
    /// Default response when no entry matches (for tests that don't
    /// care about a specific call). Default = 204.
    /// </summary>
    public HttpStatusCode DefaultStatus { get; set; } = HttpStatusCode.NoContent;
    public string? DefaultBody { get; set; }

    public void EnqueueJson(string method, string url, HttpStatusCode status, string body)
    {
        var key = $"{method} {url}";
        if (!Responses.TryGetValue(key, out var q))
        {
            q = new Queue<HttpResponseMessage>();
            Responses[key] = q;
        }
        q.Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });
    }

    public void EnqueueStatus(string method, string url, HttpStatusCode status)
    {
        var key = $"{method} {url}";
        if (!Responses.TryGetValue(key, out var q))
        {
            q = new Queue<HttpResponseMessage>();
            Responses[key] = q;
        }
        q.Enqueue(new HttpResponseMessage(status));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.PathAndQuery;
        string? body = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }
        Requests.Add(new RecordedRequest(request.Method.Method, url, body));

        var key = $"{request.Method.Method} {url}";
        if (Responses.TryGetValue(key, out var q) && q.Count > 0)
        {
            return q.Dequeue();
        }

        var def = new HttpResponseMessage(DefaultStatus);
        if (DefaultBody is not null)
        {
            def.Content = new StringContent(DefaultBody, System.Text.Encoding.UTF8, "application/json");
        }
        return def;
    }
}
