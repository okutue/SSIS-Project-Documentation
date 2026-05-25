using System.Collections.Concurrent;

namespace SsisLineage.UI.Services;

/// <summary>
/// Holds generated reports keyed by browser session so navigation between pages
/// does not lose state when scoped services or prerender runs reset the circuit scope.
/// </summary>
public sealed class LineageReportStore
{
    private readonly ConcurrentDictionary<string, LineageReportData> _reports = new(StringComparer.Ordinal);

    public void Set(string sessionKey, LineageReportData report)
    {
        _reports[sessionKey] = report;
    }

    public LineageReportData? Get(string sessionKey)
    {
        return _reports.TryGetValue(sessionKey, out var report) ? report : null;
    }

    public void Clear(string sessionKey)
    {
        _reports.TryRemove(sessionKey, out _);
    }
}
