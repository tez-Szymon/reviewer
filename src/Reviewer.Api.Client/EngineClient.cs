using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Reviewer.Api;

/// <summary>A client of the engine's local API (<c>LocalApi</c> in Reviewer.Api), over its Unix domain socket.</summary>
public sealed class EngineClient : IEngineClient, IDisposable
{
    private readonly HttpClient _http;

    public EngineClient(string socketPath)
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        })
        {
            // The host is a placeholder: every connection goes to the socket.
            BaseAddress = new Uri("http://engine/"),
            Timeout = Timeout.InfiniteTimeSpan, // Event streams stay open; callers cancel instead.
        };
    }

    /// <summary>The client underneath, for requests this class has no method for yet.</summary>
    internal HttpClient Http => _http;

    /// <summary>The engine behind the socket, or null when none is listening.</summary>
    public async Task<EngineStatus?> GetStatusAsync(CancellationToken ct)
    {
        try
        {
            return await _http.GetFromJsonAsync<EngineStatus>("status", ApiJson.Options, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e) when (e.InnerException is SocketException or IOException)
        {
            return null;
        }
    }

    /// <exception cref="EngineApiException">The engine refused the request.</exception>
    /// <exception cref="HttpRequestException">No engine is listening.</exception>
    public Task<List<AgentStatus>> GetAgentsAsync(CancellationToken ct) => GetAsync<List<AgentStatus>>("agents", ct);

    /// <summary>Queued, running and throttled jobs, oldest first.</summary>
    public Task<List<JobDto>> GetQueueAsync(CancellationToken ct) => GetAsync<List<JobDto>>("queue", ct);

    /// <summary>Past and current jobs, newest first.</summary>
    /// <param name="repo"><c>owner/name</c> to list one repo only.</param>
    /// <param name="number">With <paramref name="repo"/>, one PR only.</param>
    public Task<List<JobSummaryDto>> ListJobsAsync(string? repo, int? number, int limit, CancellationToken ct) =>
        GetAsync<List<JobSummaryDto>>(Query("jobs", ("repo", repo), ("number", number?.ToString(CultureInfo.InvariantCulture)), ("limit", limit.ToString(CultureInfo.InvariantCulture))), ct);

    /// <summary>One job with its findings, by id or unique start; null when there's none.</summary>
    public async Task<JobDetailsDto?> GetJobAsync(string id, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"jobs/{Uri.EscapeDataString(id)}", ct).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await ReadAsync<JobDetailsDto>(response, ct).ConfigureAwait(false);
    }

    public async Task<CancelResult> CancelJobAsync(string id, CancellationToken ct)
    {
        using var response = await _http.PostAsync($"jobs/{Uri.EscapeDataString(id)}/cancel", null, ct).ConfigureAwait(false);
        return await ReadAsync<CancelResult>(response, ct).ConfigureAwait(false);
    }

    public async Task<JobDto> RetryJobAsync(string id, CancellationToken ct)
    {
        using var response = await _http.PostAsync($"jobs/{Uri.EscapeDataString(id)}/retry", null, ct).ConfigureAwait(false);
        return await ReadAsync<JobDto>(response, ct).ConfigureAwait(false);
    }

    /// <summary>Queues a review, or merges the request into one already queued for the PR.</summary>
    public async Task<QueueResult> QueueReviewAsync(QueueRequest request, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("jobs", request, ApiJson.Options, ct).ConfigureAwait(false);
        return await ReadAsync<QueueResult>(response, ct).ConfigureAwait(false);
    }

    /// <summary>Asks the engine to stop, as Ctrl+C does; it closes its API once a running review is requeued.</summary>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        using var response = await _http.PostAsync("shutdown", null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await EngineApiException.FromAsync(response, ct).ConfigureAwait(false);
        }
    }

    public Task<List<AccountDto>> GetAccountsAsync(CancellationToken ct) => GetAsync<List<AccountDto>>("accounts", ct);

    /// <summary>Open PRs in watched repos as last polled.</summary>
    public Task<List<WatchedPullRequestDto>> ListPullsAsync(string? repo, CancellationToken ct) =>
        GetAsync<List<WatchedPullRequestDto>>(Query("pulls", ("repo", repo)), ct);

    /// <summary>A PR's last published review, open findings and jobs.</summary>
    /// <param name="host">The PR's host; null is github.com.</param>
    public Task<PullRequestDto> GetPullAsync(string? host, string repo, int number, CancellationToken ct)
    {
        // A GitLab subgroup path is one escaped owner segment.
        var slash = repo.LastIndexOf('/');
        var path = $"pulls/{Uri.EscapeDataString(repo[..slash])}/{Uri.EscapeDataString(repo[(slash + 1)..])}/{number.ToString(CultureInfo.InvariantCulture)}";
        return GetAsync<PullRequestDto>(Query(path, ("host", host)), ct);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        return await ReadAsync<T>(response, ct).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await EngineApiException.FromAsync(response, ct).ConfigureAwait(false);
        }

        return await response.Content.ReadFromJsonAsync<T>(ApiJson.Options, ct).ConfigureAwait(false)
            ?? throw new EngineApiException(response.StatusCode, "The engine sent an empty response.");
    }

    private static string Query(string path, params (string Name, string? Value)[] parameters)
    {
        var set = parameters.Where(p => p.Value is not null).Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!)}").ToList();
        return set.Count == 0 ? path : $"{path}?{string.Join('&', set)}";
    }

    /// <summary>Job changes as the engine streams them, until <paramref name="ct"/> is cancelled or the engine stops.</summary>
    /// <param name="after">A <see cref="JobDto.Version"/> to resume after; null starts from now.</param>
    /// <param name="opened">
    /// Called once the stream is open: from then on no change is missed, so it's the moment to load the lists the
    /// changes apply to.
    /// </param>
    /// <exception cref="HttpRequestException">No engine is listening, or it refused the request.</exception>
    public async IAsyncEnumerable<JobDto> WatchJobsAsync(long? after, Action? opened, [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, after is { } version ? $"events?after={version}" : "events");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        opened?.Invoke();
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var parser = SseParser.Create(stream, (type, data) => type == "job" ? JsonSerializer.Deserialize<JobDto>(data, ApiJson.Options) : null);
            await foreach (var item in parser.EnumerateAsync(ct).ConfigureAwait(false))
            {
                if (item.Data is { } job)
                {
                    yield return job;
                }
            }
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>The engine answered with an error; <see cref="Exception.Message"/> is its problem detail.</summary>
public sealed class EngineApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    internal static async Task<EngineApiException> FromAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? detail = null;
        try
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            detail = problem.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
        }
        catch (JsonException)
        {
        }

        return new EngineApiException(response.StatusCode, detail ?? $"The engine answered {(int)response.StatusCode} {response.ReasonPhrase}.");
    }
}
