using Photino.NET;
using SsisLineage.UI.Services;

namespace SsisLineage.Desktop;

/// <summary>Holds the native window so the folder picker can open OS dialogs.</summary>
internal static class DesktopWindowHolder
{
    public static PhotinoWindow? Window { get; set; }
}

/// <summary>Native folder picker backed by the Photino window's OS dialog.</summary>
internal sealed class PhotinoFolderPicker : IFolderPicker
{
    public bool IsSupported => DesktopWindowHolder.Window != null;

    public Task<string?> PickFolderAsync(string? startingPath = null)
    {
        var window = DesktopWindowHolder.Window;
        if (window == null) return Task.FromResult<string?>(null);

        try
        {
            var selected = window.ShowOpenFolder("Select SSIS project folder", startingPath, multiSelect: false);
            var path = selected is { Length: > 0 } ? selected[0] : null;
            return Task.FromResult(path);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }
}
