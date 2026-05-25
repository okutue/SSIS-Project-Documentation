namespace SsisLineage.UI.Services;

/// <summary>
/// Stable key for the current Blazor circuit / browser session.
/// </summary>
public sealed class LineageUserSession
{
    public string SessionKey { get; } = Guid.NewGuid().ToString("N");
}
