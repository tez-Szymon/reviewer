using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Reviewer.Desktop.ViewModels;

namespace Reviewer.Desktop.Views;

/// <summary>Each page's icon in the sidebar, from <c>Themes/Icons.axaml</c>.</summary>
internal static class PageIcons
{
    public static readonly IValueConverter ForPage = new FuncValueConverter<PageViewModel?, Geometry?>(page => Find(page switch
    {
        QueueViewModel => "Icon.ListTodo",
        PullsViewModel => "Icon.GitPullRequest",
        HistoryViewModel => "Icon.History",
        ReposViewModel => "Icon.FolderGit2",
        SettingsViewModel => "Icon.Settings",
        _ => "Icon.CircleDot",
    }));

    private static Geometry? Find(string key) =>
        Application.Current?.TryGetResource(key, null, out var value) == true ? value as Geometry : null;
}
