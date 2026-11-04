namespace Reviewer.Desktop.Services;

/// <summary>What view models ask of the window: opening links and folders, and moving between pages.</summary>
internal interface IShell
{
    Task OpenUriAsync(Uri uri);

    Task OpenFolderAsync(string path);

    /// <summary>Shows a job's review detail, with a way back to the current page.</summary>
    void OpenJob(string id);

    void Back();
}
