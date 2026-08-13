using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Tamma.Platforms.GitHub.Tests;

/// <summary>
/// Test handler that scripts per-request responses without pulling in
/// WireMock — the Gitea driver tests' house pattern. Matches on
/// (method, URL-prefix); pops the next response per route on each
/// call. Falls back to 404 for unscripted routes so a test never
/// silently hits a real network.
///
/// <para>Records every request so tests can assert outbound shapes
/// (URL, body, headers).</para>
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>>> _routes = new();
    public List<RecordedRequest> Requests { get; } = new();

    public sealed record RecordedRequest(
        HttpMethod Method,
        string Url,
        string? Body,
        IReadOnlyDictionary<string, string> Headers);

    /// <summary>
    /// Enqueue a response for the next request matching method + URL
    /// prefix (exact-prefix on the request's AbsoluteUri).
    /// </summary>
    public void Enqueue(HttpMethod method, string urlPrefix,
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var key = Key(method, urlPrefix);
        var queue = _routes.GetOrAdd(key, _ => new ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>>());
        queue.Enqueue(responder);
    }

    public void EnqueueJson(HttpMethod method, string urlPrefix,
        HttpStatusCode status, string body, IDictionary<string, string>? headers = null)
    {
        Enqueue(method, urlPrefix, _ =>
        {
            var resp = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (headers is not null)
            {
                foreach (var (k, v) in headers)
                {
                    resp.Headers.TryAddWithoutValidation(k, v);
                }
            }
            return resp;
        });
    }

    /// <summary>Always-on responder for a route — never pops.</summary>
    public void EnqueueRepeating(HttpMethod method, string urlPrefix,
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var key = Key(method, urlPrefix);
        Func<HttpRequestMessage, HttpResponseMessage> wrapper = null!;
        wrapper = req =>
        {
            var resp = responder(req);
            _routes.GetOrAdd(key, _ => new()).Enqueue(wrapper);
            return resp;
        };
        _routes.GetOrAdd(key, _ => new()).Enqueue(wrapper);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var bodyStr = request.Content is null ? null
            : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var headerDict = request.Headers
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        lock (Requests)
        {
            Requests.Add(new RecordedRequest(
                request.Method, request.RequestUri!.AbsoluteUri, bodyStr, headerDict));
        }

        foreach (var key in _routes.Keys)
        {
            var (method, prefix) = ParseKey(key);
            if (request.Method == method
                && request.RequestUri!.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (_routes[key].TryDequeue(out var responder))
                {
                    return responder(request);
                }
            }
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                $"{{\"message\":\"unscripted route: {request.Method} {request.RequestUri}\"}}",
                Encoding.UTF8, "application/json"),
        };
    }

    private static string Key(HttpMethod m, string p) => $"{m.Method}|{p}";
    private static (HttpMethod, string) ParseKey(string k)
    {
        var i = k.IndexOf('|');
        return (new HttpMethod(k[..i]), k[(i + 1)..]);
    }
}
