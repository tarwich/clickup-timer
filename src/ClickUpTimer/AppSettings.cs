using System.IO;
using System.Text.Json;

namespace ClickUpTimer;

internal sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public string Mode { get; init; } = "Taskbar";
    public string? Monitor { get; init; }
    public double TaskbarX { get; init; }
    public double FloatingX { get; init; } = 0.05;
    public double FloatingY { get; init; } = 0.85;
    public bool LaunchAtSignIn { get; init; }
    public string? UserId { get; init; }
    public string? UserName { get; init; }
    public string? WorkspaceId { get; init; }
    public string? WorkspaceName { get; init; }
    public string? PreferredListId { get; init; }
    public string? PreferredListName { get; init; }
    public List<RecentTask> RecentTasks { get; init; } = [];
    public Dictionary<string, List<string>> IgnoredStatuses { get; init; } = [];
    public bool IsConfigured => UserId is not null && WorkspaceId is not null && PreferredListId is not null;
}

internal sealed record TaskSummary(string Id, string Name, string Status, string? ListId = null, string? StatusType = null);
internal sealed record RecentTask(string UserId, string WorkspaceId, TaskSummary Task);
internal sealed record TaskCache(string UserId, string WorkspaceId, string ListId, DateTimeOffset FetchedAt, List<TaskSummary> Tasks);

internal sealed class SettingsStore
{
    internal static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClickUpTimer");
    private readonly string directory;
    internal string? Warning { get; private set; }
    internal SettingsStore(string? directory = null) => this.directory = directory ?? DefaultDirectory;
    internal AppSettings Load()
    {
        try
        {
            var file = Path.Combine(directory, "settings.json");
            if (File.Exists(file))
            {
                var result = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file)) ?? throw new JsonException();
                if (result.Version != 1) throw new JsonException();
                static double Fraction(double v, double fallback) => double.IsFinite(v) && v >= 0 && v <= 1 ? v : fallback;
                return result with { Mode = result.Mode == "Floating" ? "Floating" : "Taskbar", TaskbarX = Fraction(result.TaskbarX, 0), FloatingX = Fraction(result.FloatingX, .05), FloatingY = Fraction(result.FloatingY, .85) };
            }
            var legacy = Path.Combine(directory, "prototype", "placement.json");
            if (File.Exists(legacy))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(legacy));
                var root = json.RootElement;
                var x = root.GetProperty("HorizontalFraction").GetDouble();
                return new AppSettings { Monitor = root.GetProperty("Device").GetString(), TaskbarX = double.IsFinite(x) ? Math.Clamp(x, 0, 1) : 0 };
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        { Warning = "Saved settings could not be read. Defaults are shown; saving will replace the settings file."; }
        return new();
    }
    internal void Save(AppSettings settings) { Write("settings.json", settings); Warning = null; }
    internal void SaveCache(TaskCache cache) => Write("task-cache.json", cache);
    internal TaskCache? LoadCache(string user, string workspace, string list)
    {
        try
        {
            var path = Path.Combine(directory, "task-cache.json");
            if (!File.Exists(path)) return null;
            var cache = JsonSerializer.Deserialize<TaskCache>(File.ReadAllText(path));
            return cache is not null && cache.UserId == user && cache.WorkspaceId == workspace && cache.ListId == list ? cache : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    private void Write(string name, object value)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}
