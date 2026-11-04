using CommunityToolkit.Mvvm.ComponentModel;
using Reviewer.Api;

namespace Reviewer.Desktop.ViewModels;

/// <summary>A page of the main window: loaded when the event stream opens, kept current by its events.</summary>
internal abstract partial class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>What went wrong with the last load or action, until the next one succeeds.</summary>
    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _loading;

    public virtual Task LoadAsync() => Task.CompletedTask;

    public virtual void OnChanged(JobDto job)
    {
    }

    /// <summary>Runs <paramref name="action"/>, showing what failed instead of throwing.</summary>
    protected async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            Error = null;
        }
        catch (Exception e) when (e is EngineApiException or HttpRequestException or IOException)
        {
            Error = e is EngineApiException ? e.Message : $"Couldn't reach the engine: {e.Message}";
        }
    }
}
