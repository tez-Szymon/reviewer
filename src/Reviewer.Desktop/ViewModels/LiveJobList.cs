using System.Collections.ObjectModel;
using Reviewer.Api;

namespace Reviewer.Desktop.ViewModels;

/// <summary>
/// Jobs loaded from a list endpoint and kept current by events. Events that arrive while a load is in flight are held
/// and applied over it, newest version winning, so the list never goes back in time.
/// </summary>
internal sealed class LiveJobList(Func<JobDto, bool> include, Comparison<JobItem> order)
{
    private readonly Dictionary<string, JobItem> _byId = new(StringComparer.Ordinal);
    private List<JobDto>? _held;

    public ObservableCollection<JobItem> Items { get; } = [];

    public JobItem? Find(string id) => _byId.GetValueOrDefault(id);

    public async Task LoadAsync(Func<Task<IReadOnlyList<(JobDto Job, (int, int, int)? Counts)>>> load)
    {
        _held = [];
        IReadOnlyList<(JobDto Job, (int, int, int)? Counts)> loaded;
        try
        {
            loaded = await load();
        }
        catch
        {
            _held = null;
            throw;
        }

        var held = _held;
        _held = null;
        _byId.Clear();
        Items.Clear();
        foreach (var (job, counts) in loaded)
        {
            Apply(job)?.Counts = counts;
        }

        foreach (var job in held)
        {
            Apply(job);
        }
    }

    public void OnChanged(JobDto job)
    {
        if (_held is not null)
        {
            _held.Add(job);
            return;
        }

        Apply(job);
    }

    private JobItem? Apply(JobDto job)
    {
        if (_byId.TryGetValue(job.Id, out var item))
        {
            if (!item.Update(job))
            {
                return item;
            }

            if (!include(item.Job))
            {
                _byId.Remove(job.Id);
                Items.Remove(item);
                return null;
            }

            Items.Remove(item);
            Insert(item);
            return item;
        }

        if (!include(job))
        {
            return null;
        }

        item = new JobItem(job);
        _byId[job.Id] = item;
        Insert(item);
        return item;
    }

    private void Insert(JobItem item)
    {
        var at = 0;
        while (at < Items.Count && order(Items[at], item) <= 0)
        {
            at++;
        }

        Items.Insert(at, item);
    }
}
