using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Reviewer.Api;

/// <summary>A client of the engine's <see cref="LocalApi"/>, over its Unix domain socket.</summary>
public sealed class EngineClient : IDisposable
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

    /// <summary>Job changes as the engine streams them, until <paramref name="ct"/> is cancelled or the engine stops.</summary>
    /// <param name="after">A <see cref="JobDto.Version"/> to resume after; null starts from now.</param>
    /// <exception cref="HttpRequestException">No engine is listening, or it refused the request.</exception>
    public async IAsyncEnumerable<JobDto> WatchJobsAsync(long? after, [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, after is { } version ? $"events?after={version}" : "events");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
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
