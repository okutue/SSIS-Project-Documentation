using System.Text.Json;

namespace SsisLineage.UI.Services;

public sealed class RecentProject
{
    public string ProjectPath { get; set; } = "";
    public string StartPackage { get; set; } = "";
    public DateTime LastUsedUtc { get; set; }
}

/// <summary>
/// Persists the most recently used project/package pairs to a local JSON file
/// under %APPDATA%\SsisLineage. Fully local — nothing leaves the machine.
/// </summary>
public sealed class RecentProjectsService
{
    private const int MaxEntries = 8;
    private readonly string _filePath;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public RecentProjectsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SsisLineage");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "recent-projects.json");
    }

    public IReadOnlyList<RecentProject> Get()
    {
        try
        {
            if (!File.Exists(_filePath)) return Array.Empty<RecentProject>();
            var json = File.ReadAllText(_filePath);
            var items = JsonSerializer.Deserialize<List<RecentProject>>(json) ?? new();
            return items.OrderByDescending(i => i.LastUsedUtc).ToList();
        }
        catch
        {
            return Array.Empty<RecentProject>();
        }
    }

    public void Add(string projectPath, string startPackage)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return;

        try
        {
            var items = Get().ToList();
            items.RemoveAll(i =>
                string.Equals(i.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(i.StartPackage, startPackage, StringComparison.OrdinalIgnoreCase));

            items.Insert(0, new RecentProject
            {
                ProjectPath = projectPath,
                StartPackage = startPackage,
                LastUsedUtc = DateTime.UtcNow
            });

            var trimmed = items.Take(MaxEntries).ToList();
            File.WriteAllText(_filePath, JsonSerializer.Serialize(trimmed, _json));
        }
        catch
        {
            // best-effort persistence; ignore IO errors
        }
    }
}
