using System.Globalization;
using System.Text.Json;

namespace ClickUpTimer;

// Time tracking uses the same OAuth audience as search. MCP tokens are not REST tokens.
internal sealed class McpTimingApi : ITimingApi
{
    private readonly ClickUpMcp client;
    private readonly string workspace;
    private readonly Func<string, object, Task<JsonElement>> call;
    private readonly Dictionary<string, TimeEntry> known = [];
    internal McpTimingApi(string token, string workspace, IEnumerable<TimeEntry>? hints = null,
        Func<string, object, Task<JsonElement>>? call = null)
    {
        client = new(token); this.workspace = workspace;
        this.call = call ?? (async (name, args) => McpSearchService.Content(await client.Call(name, args, default)));
        foreach (var entry in hints ?? []) known[entry.Id] = entry;
    }
    public bool CanEditStopTime => false;
    private void Scope(string value)
    {
        if (value != workspace) throw new ClickUpException("Reconnect to the timer's original workspace.");
    }
    private async Task<JsonElement> Invoke(string name, object args)
    {
        var result = await call(name, args);
        if (result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            throw new ClickUpException("ClickUp could not confirm the timer operation. Your recovery request is retained.");
        return result;
    }
    public async Task<string> User()
    {
        var result = await Invoke("clickup_resolve_assignees", new { workspace_id = workspace, assignees = new[] { "me" } });
        var users = result.GetProperty("userIds");
        if (users.GetArrayLength() != 1) throw new ClickUpException("ClickUp could not identify the signed-in user.");
        return users[0].ToString();
    }
    internal static TimeEntry Parse(JsonElement data)
    {
        static string Required(JsonElement obj, string key)
        {
            if (!obj.TryGetProperty(key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || string.IsNullOrEmpty(value.ToString()))
                throw new ClickUpException("ClickUp returned incomplete timer data. Your recovery request is retained.");
            return value.ToString();
        }
        static long Timestamp(string value)
        {
            if (long.TryParse(value, CultureInfo.InvariantCulture, out var number)) return number;
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)) return time.ToUnixTimeMilliseconds();
            throw new ClickUpException("ClickUp returned an unreadable timer timestamp.");
        }
        var id = Required(data, "id");
        var user = Required(data.GetProperty("user"), "id");
        var start = Timestamp(Required(data, "start"));
        var durationText = data.TryGetProperty("duration_ms", out var durationMs) ? durationMs.ToString() : Required(data, "duration");
        if (!long.TryParse(durationText, CultureInfo.InvariantCulture, out var duration)) throw new ClickUpException("ClickUp returned an unreadable timer duration.");
        var end = data.TryGetProperty("end", out var endValue) && endValue.ValueKind != JsonValueKind.Null ? Timestamp(endValue.ToString()) : 0;
        TaskSummary? task = null;
        if (data.TryGetProperty("task", out var taskValue) && taskValue.ValueKind == JsonValueKind.Object)
            task = new(Required(taskValue, "id"), taskValue.TryGetProperty("name", out var name) ? name.ToString() : "ClickUp task", "");
        return new(id, user, task, start, duration, end, data.TryGetProperty("description", out var description) ? description.ToString() : "");
    }
    private TimeEntry Remember(JsonElement data)
    {
        var entry = Parse(data); known[entry.Id] = entry; return entry;
    }
    public async Task<TimeEntry?> Current(string workspace)
    {
        Scope(workspace);
        var result = await Invoke("clickup_get_current_time_entry", new { workspace_id = workspace });
        var data = result.GetProperty("currentEntry");
        if (data.ValueKind == JsonValueKind.Null && result.GetProperty("isTracking").ValueKind == JsonValueKind.False) return null;
        return Remember(data);
    }
    public async Task<TimeEntry> Entry(string workspace, string id)
    {
        Scope(workspace);
        var current = await Current(workspace);
        if (current?.Id == id) return current;
        // Hints bound the history query but are never used as server confirmation.
        var start = known.TryGetValue(id, out var hint) ? Math.Max(0, hint.Start - 86400000) : 0;
        var entries = await Entries(workspace, start, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 86400000);
        return entries.FirstOrDefault(e => e.Id == id) ?? throw new ClickUpException("ClickUp did not return the saved time entry. Review it in ClickUp; recovery is retained.");
    }
    public async Task Start(string workspace, string task, string marker)
    {
        Scope(workspace);
        var result = await Invoke("clickup_start_time_tracking", new { workspace_id = workspace, task_id = task, description = marker });
        Remember(result.GetProperty("timeEntry"));
    }
    public async Task<bool> StopCurrent(string workspace, string expectedId)
    {
        Scope(workspace);
        if ((await Current(workspace))?.Id != expectedId) return false;
        var result = await Invoke("clickup_stop_time_tracking", new { workspace_id = workspace });
        var entry = Remember(result.GetProperty("timeEntry"));
        if (entry.Id != expectedId || entry.Running) throw new ClickUpException("ClickUp did not confirm the expected timer stopped. Review the saved request.");
        return true;
    }
    public Task Finish(string workspace, TimeEntry entry, long end) => throw new NotSupportedException("MCP cannot edit an existing time entry.");
    public async Task<List<TimeEntry>> Entries(string workspace, long start, long end)
    {
        Scope(workspace);
        // Whole-day padding covers the server's workspace timezone and minute-only input.
        var result = await Invoke("clickup_get_time_entries", new
        {
            workspace_id = workspace,
            start_date = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, start - 86400000)).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            end_date = DateTimeOffset.FromUnixTimeMilliseconds(end).AddDays(1).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        });
        return result.GetProperty("entries").EnumerateArray().Select(Remember).ToList();
    }
    public void Dispose() => client.Dispose();
}
