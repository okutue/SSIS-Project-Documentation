namespace SsisLineage.UI.Services;

/// <summary>
/// Host-specific folder picker. The desktop (Photino) host implements a native
/// dialog; the web host has no picker (browsers cannot expose a folder path),
/// so it falls back to the text field + recent-projects list.
/// </summary>
public interface IFolderPicker
{
    bool IsSupported { get; }
    Task<string?> PickFolderAsync(string? startingPath = null);
}

/// <summary>Default no-op picker used by the web host.</summary>
public sealed class NoopFolderPicker : IFolderPicker
{
    public bool IsSupported => false;
    public Task<string?> PickFolderAsync(string? startingPath = null) => Task.FromResult<string?>(null);
}
