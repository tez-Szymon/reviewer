using Reviewer.Api;

namespace Reviewer.Desktop.Services;

/// <summary>
/// The engine's job events, raised on the UI thread through <c>post</c>. <see cref="Opened"/> comes first, once the
/// stream is open: lists loaded from then on miss no change. A reconnect resumes after the last version seen.
/// </summary>
internal sealed class JobFeed(IEngineClient client, Action<Action> post)
{
    private long? _last;

    public event Action? Opened;

    public event Action<JobDto>? Changed;

    /// <summary>The stream ended without being cancelled.</summary>
    public event Action? Lost;

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in client.WatchJobsAsync(_last, () => post(() => Opened?.Invoke()), ct).ConfigureAwait(false))
            {
                _last = Math.Max(_last ?? 0, job.Version);
                post(() => Changed?.Invoke(job));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or EngineApiException)
        {
        }

        if (!ct.IsCancellationRequested)
        {
            post(() => Lost?.Invoke());
        }
    }
}
