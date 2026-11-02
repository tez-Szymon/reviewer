using System.Net;
using System.Text;
using System.Text.Json;

namespace Reviewer.Providers.GitLab.Tests;

/// <summary>Answers requests from canned routes and records what was sent. Serves a self-hosted GitLab under a path.</summary>
internal sealed class FakeGitLab : HttpMessageHandler
{
    public static readonly Uri Host = new("https://git.acme.test/gitlab");

    private const string ApiPath = "/gitlab/api/v4/";

    private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body, string? Link, string? ETag)>> _routes = [];

    public List<(string Route, JsonElement? Body)> Requests { get; } = [];

    /// <summary>The <c>If-None-Match</c> header of each request, in order; null when it had none.</summary>
    public List<string?> IfNoneMatch { get; } = [];

    /// <param name="route">"METHOD path?query" relative to the API root, escaped as sent.</param>
    public FakeGitLab On(string route, string body, HttpStatusCode status = HttpStatusCode.OK, string? nextPage = null, string? etag = null)
    {
        if (!_routes.TryGetValue(route, out var queue))
        {
            _routes[route] = queue = new();
        }

        queue.Enqueue((status, body, nextPage is null ? null : $"<https://git.acme.test{ApiPath}{nextPage}>; rel=\"next\"", etag));
        return this;
    }

    public GitLabProvider Provider() => new(GitLabProvider.CreateHttpClient(Host, "test-token", this));

    public IEnumerable<string> Routes => Requests.Select(r => r.Route);

    public JsonElement Body(string route) => Requests.Single(r => r.Route == route).Body!.Value;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal("test-token", Assert.Single(request.Headers.GetValues("PRIVATE-TOKEN")));
        var path = request.RequestUri!.PathAndQuery;
        Assert.StartsWith(ApiPath, path, StringComparison.Ordinal);
        var route = $"{request.Method} {path[ApiPath.Length..]}";
        var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((route, text.Length == 0 ? null : JsonDocument.Parse(text).RootElement.Clone()));
        IfNoneMatch.Add(request.Headers.TryGetValues("If-None-Match", out var tags) ? string.Join(",", tags) : null);

        if (!_routes.TryGetValue(route, out var queue) || queue.Count == 0)
        {
            throw new InvalidOperationException($"Unexpected request: {route}");
        }

        var (status, body, link, etag) = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
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
