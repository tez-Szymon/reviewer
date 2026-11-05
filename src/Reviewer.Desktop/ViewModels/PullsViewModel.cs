using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Reviewer.Api;
using Reviewer.Desktop.Services;

namespace Reviewer.Desktop.ViewModels;

/// <summary>An open PR in a watched repo, with where its reviews stand.</summary>
internal sealed class PullItem(WatchedPullRequestDto pr)
{
    public WatchedPullRequestDto Pr { get; } = pr;

    public string Name => Describe.Pr(Pr.Repo, Pr.Number);

    public string Title => Pr.Title;

    public string Author => Pr.Author;

    /// <summary>Where the last review left it, and whether commits came after.</summary>
    public string ReviewText => Pr.LastReview switch
    {
        null => Pr.Matched ? "not reviewed yet" : "not followed: no trigger matched",
        { } last when !string.Equals(last.HeadSha, Pr.HeadSha, StringComparison.OrdinalIgnoreCase) =>
            $"reviewed {Describe.Short(last.HeadSha)}, new commits since",
        { } last => $"reviewed {Describe.Short(last.HeadSha)} {Describe.Time(last.ReviewedAt)}",
    };

    /// <summary>Green when the last review covers the head, amber when commits came after it.</summary>
    public Tone ReviewTone => Pr.LastReview switch
    {
        null => Tone.Neutral,
        { } last when !string.Equals(last.HeadSha, Pr.HeadSha, StringComparison.OrdinalIgnoreCase) => Tone.Warning,
        _ => Tone.Success,
    };

    public Tone LatestJobTone => Pr.LatestJob is { } latest ? Describe.Tone(latest.Job) : Tone.Neutral;

    public string LatestJobText => Pr.LatestJob is { } latest ? Describe.State(latest.Job) : "";

    public bool HasJob => Pr.LatestJob is not null;
}

/// <summary>Open PRs in the watched repos as <c>reviewer serve</c> last polled them.</summary>
internal sealed partial class PullsViewModel(IEngineClient client, IShell shell) : PageViewModel
{
    private bool _reloading;
    private bool _stale;

    public override string Title => "Pull requests";

    public ObservableCollection<PullItem> Pulls { get; } = [];

    /// <summary>Reloads the whole list; a change while it loads makes it load once more after.</summary>
    public override async Task LoadAsync()
    {
        if (_reloading)
        {
            _stale = true;
            return;
        }

        _reloading = true;
        Loading = true;
        try
        {
            do
            {
                _stale = false;
                await TryAsync(async () =>
                {
                    var pulls = await client.ListPullsAsync(null, CancellationToken.None);
                    Pulls.Clear();
                    foreach (var pr in pulls)
                    {
                        Pulls.Add(new PullItem(pr));
                    }
                });
            }
            while (_stale);
        }
        finally
        {
            _reloading = false;
            Loading = false;
        }
    }

    /// <summary>A job of a listed PR changed: its last review or newest job may have too.</summary>
    public override void OnChanged(JobDto job)
    {
        if (Pulls.Any(p => p.Pr.Number == job.Number && p.Pr.Repo == job.Repo && p.Pr.Host == job.Host))
        {
            _ = LoadAsync();
        }
    }

    [RelayCommand]
    private void Open(PullItem pr)
    {
        if (pr.Pr.LatestJob is { } latest)
        {
            shell.OpenJob(latest.Job.Id);
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();
}
