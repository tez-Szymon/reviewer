using System.Net;
using System.Text;
using System.Text.Json;

namespace Reviewer.Providers.GitHub.Tests;

/// <summary>Answers requests from canned routes and records what was sent.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body, string? Link, string? ETag)>> _routes = [];

    public List<(string Route, JsonElement? Body)> Requests { get; } = [];

    /// <summary>The <c>If-None-Match</c> header of each request, in order; null when it had none.</summary>
    public List<string?> IfNoneMatch { get; } = [];

    /// <param name="route">"METHOD path?query" relative to the API base.</param>
    public FakeGitHub On(string route, string body, HttpStatusCode status = HttpStatusCode.OK, string? nextPage = null, string? etag = null)
    {
        if (!_routes.TryGetValue(route, out var queue))
        {
            _routes[route] = queue = new();
        }

        queue.Enqueue((status, body, nextPage is null ? null : $"<https://api.github.com/{nextPage}>; rel=\"next\"", etag));
        return this;
    }

    public GitHubProvider Provider() => new(GitHubProvider.CreateHttpClient("test-token", this));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal("Bearer test-token", request.Headers.Authorization?.ToString());
        var route = $"{request.Method} {request.RequestUri!.PathAndQuery.TrimStart('/')}";
        JsonElement? body = request.Content is null
            ? null
            : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
        Requests.Add((route, body));
        IfNoneMatch.Add(request.Headers.TryGetValues("If-None-Match", out var tags) ? string.Join(",", tags) : null);

        if (!_routes.TryGetValue(route, out var queue) || queue.Count == 0)
        {
            throw new InvalidOperationException($"Unexpected request: {route}");
        }

        var (status, text, link, etag) = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
        var response = new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        if (link is not null)
        {
            response.Headers.Add("Link", link);
        }

        if (etag is not null)
        {
            response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag);
        }

        return response;
    }
}
