using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClickUpTimer;

internal sealed record SearchItem(string Id, string Name, string Type, string? ListId = null, string? Status = null, string? StatusType = null, string? Url = null, string? Path = null)
{
    internal TaskSummary Task => new(Id, Name, Status ?? "", ListId, StatusType);
    internal Choice Choice => new(Id, string.IsNullOrWhiteSpace(Path) ? Name : Path);
    internal bool Matches(string query) => query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .All(term => Name.Contains(term, StringComparison.OrdinalIgnoreCase) || Id.Contains(term, StringComparison.OrdinalIgnoreCase) || Path?.Contains(term, StringComparison.OrdinalIgnoreCase) == true);
}
internal sealed record SearchPage(List<SearchItem> Items, string? Cursor = null, bool FilterLocally = false);
internal sealed record SearchSnapshot(string UserId, string WorkspaceId, List<SearchItem> Items);

internal sealed class SearchCache(string directory)
{
    private readonly Dictionary<string, SearchSnapshot> memory = [];
    private readonly object gate = new();
    private static string Key(string user, string workspace) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { user, workspace }))));
    internal List<SearchItem> Read(string user, string workspace)
    {
        lock (gate)
        {
            var key = Key(user, workspace);
            if (!memory.TryGetValue(key, out var snapshot))
            {
                try
                {
                    var path = System.IO.Path.Combine(directory, "search-" + key + ".json");
                    snapshot = File.Exists(path) ? JsonSerializer.Deserialize<SearchSnapshot>(File.ReadAllText(path)) : null;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { snapshot = null; }
                if (snapshot?.UserId != user || snapshot.WorkspaceId != workspace) snapshot = new(user, workspace, []);
                memory[key] = snapshot;
            }
            return snapshot.Items.ToList();
        }
    }
    internal void Merge(string user, string workspace, IEnumerable<SearchItem> items)
    {
        lock (gate)
        {
            var merged = items.Concat(Read(user, workspace)).DistinctBy(i => (i.Type, i.Id)).ToList();
            var snapshot = new SearchSnapshot(user, workspace, merged);
            var key = Key(user, workspace);
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "search-" + key + ".json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(snapshot));
            File.Move(path + ".tmp", path, true);
            memory[key] = snapshot;
        }
    }
}

internal interface IClickUpSearch
{
    Task<SearchPage> Search(string workspace, string query, string type, string? list, string? cursor, CancellationToken cancellation);
}

// A request exists only while a picker is open and a user has edited its query/scope.
// Callers own rendering and caching. There are no scheduled refreshes or retries.
internal sealed class InteractiveSearch : IDisposable
{
    private CancellationTokenSource? pending;
    internal bool Active { get; private set; }
    internal void Open() { Cancel(); Active = true; }
    internal void Close() { Active = false; Cancel(); }
    internal void Cancel() { pending?.Cancel(); pending = null; }
    internal async Task Run(string query, Func<CancellationToken, Task> action, Action<Exception> failed, int delay = 650)
    {
        Cancel();
        if (!Active || string.IsNullOrWhiteSpace(query)) return;
        using var request = new CancellationTokenSource(); pending = request;
        try
        {
            await Task.Delay(delay, request.Token);
            request.Token.ThrowIfCancellationRequested();
            await action(request.Token);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception e) { if (!request.IsCancellationRequested) failed(e); }
        finally { if (pending == request) pending = null; }
    }
    public void Dispose() => Close();
}
