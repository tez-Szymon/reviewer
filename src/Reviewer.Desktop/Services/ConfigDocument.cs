using System.Net;
using System.Text.Json.Nodes;
using Reviewer.Api;
using Reviewer.Engine.Config;

namespace Reviewer.Desktop.Services;

/// <summary>
/// <c>config.yaml</c> as the settings pages edit it: the text with its hash, what the file itself sets, and the merged
/// config. Both come from the one text, so they agree with the hash an edit names. Saves are edits to the text
/// (<c>PATCH /config</c>), never a rewrite, so the file's comments and layout stay.
/// </summary>
internal sealed class ConfigDocument(IEngineClient client)
{
    private ConfigText? _file;

    /// <summary>Raised after a load or save, on the caller's thread; pages rebuild their forms from the new file.</summary>
    public event Action? Changed;

    /// <summary>The merged config; null while the file is invalid, when only a hand edit can fix it.</summary>
    public ReviewerConfig? Config { get; private set; }

    /// <summary>What the file itself sets, without the built-in defaults.</summary>
    public JsonObject Settings { get; private set; } = [];

    /// <summary>Why the file can't be loaded; empty when it can.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    public bool Loaded => _file is not null;

    public async Task LoadAsync() => Apply(await client.GetConfigFileAsync(CancellationToken.None));

    /// <summary>Applies <paramref name="edits"/>, all or none.</summary>
    /// <exception cref="EngineApiException">
    /// The engine refused them. A file changed by hand since it was read is read again before this throws, so the page
    /// shows what's there now.
    /// </exception>
    public async Task SaveAsync(IReadOnlyList<ConfigEdit> edits)
    {
        if (edits.Count == 0)
        {
            return;
        }

        if (_file is null)
        {
            await LoadAsync();
        }

        try
        {
            Apply(await client.EditConfigAsync(new ConfigEdits(_file!.Hash, edits), CancellationToken.None));
        }
        catch (EngineApiException e) when (e.Status == HttpStatusCode.Conflict)
        {
            await LoadAsync();
            throw new EngineApiException(
                e.Status, "config.yaml was changed elsewhere since it was read, so nothing was saved. It's been read again: make the change once more.");
        }
    }

    private void Apply(ConfigText file)
    {
        _file = file;
        try
        {
            Settings = ConfigLoader.ReadSettings(file.Yaml);
            Config = ConfigLoader.Parse(file.Yaml);
            Problems = [];
        }
        catch (ConfigException e)
        {
            Config = null;
            Problems = e.Errors;
        }

        Changed?.Invoke();
    }
}
