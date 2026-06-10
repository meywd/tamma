using System.Net;

namespace Tamma.Platforms.GitLab.Tests.Support;

/// <summary>
/// Minimal scriptable HTTP handler used to mock GitLab API responses
/// without taking a WireMock dependency.
///
/// <para>Tests register one or more handlers keyed by
/// (method, path-prefix). The handler captures every request for
/// later assertions (path, body, headers, sequence).</para>
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public List<CapturedRequest> Requests { get; } = new();

    private readonly List<Route> _routes = new();
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _queue = new();

    /// <summary>
    /// Register a route that matches by HTTP method + URL substring.
    /// First matching route wins.
    /// </summary>
    public FakeHttpMessageHandler AddRoute(
        HttpMethod method,
        string urlContains,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes.Add(new Route(method, urlContains, respond));
        return this;
    }

    /// <summary>
    /// Register a sequence of responses that are consumed in order.
    /// Useful for testing pagination.
    /// </summary>
    public FakeHttpMessageHandler EnqueueResponse(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _queue.Enqueue(respond);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var captured = new CapturedRequest(
            request.Method,
            request.RequestUri!,
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false),
            request.Headers
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase));
        Requests.Add(captured);

        if (_queue.Count > 0)
        {
            var next = _queue.Dequeue();
            return next(request);
        }
        foreach (var route in _routes)
        {
            if (route.Method == request.Method &&
                request.RequestUri!.ToString().Contains(route.UrlContains, StringComparison.Ordinal))
            {
                return route.Respond(request);
            }
        }
        return new HttpResponseMessage(HttpStatusCode.NotImplemented)
        {
            Content = new StringContent($"no route for {request.Method} {request.RequestUri}"),
        };
    }

    private sealed record Route(HttpMethod Method, string UrlContains, Func<HttpRequestMessage, HttpResponseMessage> Respond);

    public sealed record CapturedRequest(
        HttpMethod Method,
        Uri RequestUri,
        string? Body,
        IReadOnlyDictionary<string, string> Headers);

    public static HttpResponseMessage Json(HttpStatusCode status, string body)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
    }

    public static HttpResponseMessage JsonWithHeader(
        HttpStatusCode status, string body, string headerName, string headerValue)
    {
        var resp = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        resp.Headers.TryAddWithoutValidation(headerName, headerValue);
        return resp;
    }
}
