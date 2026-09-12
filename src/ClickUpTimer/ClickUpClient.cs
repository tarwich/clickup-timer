using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ClickUpTimer;

internal sealed record Choice(string Id, string Name) { public override string ToString() => Name; }
internal sealed record ClickUpUser(string Id, string Name);
internal sealed class ClickUpException(string message, bool rateLimited = false) : Exception(message)
{ internal bool RateLimited { get; } = rateLimited; }

internal sealed class ClickUpClient : IDisposable, ITimingApi
{
    private readonly HttpClient http;
    private readonly string token;
    internal ClickUpClient(string token, HttpMessageHandler? handler = null)
    {
        this.token = token.Trim();
        if (this.token.Length == 0 || this.token.Contains('\r') || this.token.Contains('\n')) throw new ClickUpException("Enter a personal API key.");
        http = handler is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.clickup.com/api/v2/");
        http.Timeout = TimeSpan.FromSeconds(25);
    }
    private async Task<JsonDocument> Get(string path, CancellationToken cancellation, object? body = null, HttpMethod? method = null)
    {
        try
        {
            using var request = new HttpRequestMessage(method ?? (body is null ? HttpMethod.Get : HttpMethod.Post), path);
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            request.Headers.Add("Authorization", token);
            using var response = await http.SendAsync(request, cancellation);
            if (!response.IsSuccessStatusCode)
                throw new ClickUpException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "ClickUp rejected the API key. Check it and try again.",
                    HttpStatusCode.Forbidden => "Your ClickUp account does not have access to this location.",
                    HttpStatusCode.NotFound => "This ClickUp location is no longer available. Select another list.",
                    HttpStatusCode.TooManyRequests => "ClickUp's request limit was reached. Wait a minute, then try again.",
                    _ => "ClickUp could not complete the request. Try again shortly."
                }, response.StatusCode == HttpStatusCode.TooManyRequests);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        }
        catch (HttpRequestException) { throw new ClickUpException("Cannot reach ClickUp. Check your internet connection and try again."); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new ClickUpException("ClickUp took too long to respond. Try again."); }
        catch (JsonException) { throw new ClickUpException("ClickUp returned an unreadable response. Try again."); }
    }
    private static string Id(JsonElement element) => element.GetProperty("id").ToString();
    private static string Name(JsonElement element) => element.TryGetProperty("name", out var name) ? name.GetString() ?? "Unnamed" : "Unnamed";
    private static string Segment(string id) => Uri.EscapeDataString(id);
    internal async Task<ClickUpUser> Validate(CancellationToken cancellation)
    {
        using var json = await Get("user", cancellation);
        var user = json.RootElement.GetProperty("user");
        return new(Id(user), user.GetProperty("username").GetString() ?? "ClickUp user");
    }
    internal async Task<List<Choice>> Workspaces(CancellationToken cancellation)
    {
        using var json = await Get("team", cancellation);
        return json.RootElement.GetProperty("teams").EnumerateArray().Select(e => new Choice(Id(e), Name(e))).ToList();
    }
    internal async Task<List<Choice>> Lists(string workspace, CancellationToken cancellation, IProgress<List<Choice>>? progress = null)
    {
        var result = new Dictionary<string, Choice>();
        void Report() => progress?.Report(result.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        void AddLists(JsonElement root, string prefix, bool report = true)
        {
            if (!root.TryGetProperty("lists", out var lists)) return;
            foreach (var list in lists.EnumerateArray())
            {
                if (list.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True) continue;
                var id = Id(list); result.TryAdd(id, new Choice(id, prefix + Name(list)));
            }
            if (report) Report();
        }
        static string? ParentId(JsonElement folder)
        {
            if (!folder.TryGetProperty("parent_folder", out var parent) || parent.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty("id", out var id)) return id.ToString();
            return parent.ToString();
        }
        void AddFolderLists(JsonElement folderArray, string prefix)
        {
            var folders = new Dictionary<string, (JsonElement Folder, string? Parent)>();
            void Collect(JsonElement items, string? impliedParent = null)
            {
                foreach (var folder in items.EnumerateArray())
                {
                    var id = Id(folder);
                    folders[id] = (folder, ParentId(folder) ?? impliedParent);
                    if (folder.TryGetProperty("folders", out var children) && children.ValueKind == JsonValueKind.Array)
                        Collect(children, id);
                }
            }
            Collect(folderArray);
            string PathFor(string id)
            {
                var names = new Stack<string>();
                var seen = new HashSet<string>();
                while (folders.TryGetValue(id, out var node) && seen.Add(id))
                {
                    names.Push(Name(node.Folder));
                    if (node.Parent is null) break;
                    id = node.Parent;
                }
                return prefix + string.Join(" / ", names) + " / ";
            }
            foreach (var (id, node) in folders) AddLists(node.Folder, PathFor(id), report: false);
            Report();
        }
        using var spaces = await Get($"team/{Segment(workspace)}/space?archived=false", cancellation);
        foreach (var space in spaces.RootElement.GetProperty("spaces").EnumerateArray())
        {
            var path = Name(space) + " / ";
            using var loose = await Get($"space/{Segment(Id(space))}/list?archived=false", cancellation);
            AddLists(loose.RootElement, path);
            using var folders = await Get($"space/{Segment(Id(space))}/folder?archived=false", cancellation);
            AddFolderLists(folders.RootElement.GetProperty("folders"), path);
        }
        // Guests and members can also have lists shared outside their space membership.
        using var shared = await Get($"team/{Segment(workspace)}/shared", cancellation);
        AddLists(shared.RootElement, "Shared / ");
        if (shared.RootElement.TryGetProperty("folders", out var sharedFolders))
            AddFolderLists(sharedFolders, "Shared / ");
        return result.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    internal async Task<List<TaskSummary>> Tasks(string list, CancellationToken cancellation)
    {
        var result = new Dictionary<string, TaskSummary>();
        for (var page = 0; ; page++)
        {
            using var json = await Get($"list/{Segment(list)}/task?page={page}&subtasks=true&include_closed=true&include_timl=true", cancellation);
            var tasks = json.RootElement.GetProperty("tasks");
            foreach (var item in tasks.EnumerateArray())
                result[Id(item)] = ParseTask(item, list);
            if (tasks.GetArrayLength() == 0 || (json.RootElement.TryGetProperty("last_page", out var last) && last.ValueKind == JsonValueKind.True)) break;
        }
        return result.Values.ToList();
    }
    private static TaskSummary ParseTask(JsonElement item, string? list = null)
    {
        var status = item.GetProperty("status");
        return new(Id(item), Name(item), status.GetProperty("status").GetString() ?? "",
            item.TryGetProperty("list", out var location) ? Id(location) : list,
            status.TryGetProperty("type", out var type) ? type.GetString() : null);
    }
    internal async Task<List<TaskSummary>> WorkspacePage(string workspace, int page, CancellationToken cancellation)
    {
        using var json = await Get($"team/{Segment(workspace)}/task?page={page}&subtasks=true&include_closed=true", cancellation);
        return json.RootElement.GetProperty("tasks").EnumerateArray().Select(t => ParseTask(t)).ToList();
    }
    internal async Task<List<Choice>> Statuses(string list, CancellationToken cancellation)
    {
        using var json = await Get($"list/{Segment(list)}", cancellation);
        return json.RootElement.GetProperty("statuses").EnumerateArray()
            .Select(s => new Choice(s.GetProperty("type").GetString() ?? "", s.GetProperty("status").GetString() ?? "")).ToList();
    }
    internal async Task<TaskSummary> CreateTask(string list, string name, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ClickUpException("Enter a task title first.");
        using var json = await Get($"list/{Segment(list)}/task", cancellation,
            new { name = name.Trim(), assignees = Array.Empty<int>(), notify_all = false });
        return ParseTask(json.RootElement, list);
    }
    public void Dispose() => http.Dispose();
    public async Task<string> User() => (await Validate(default)).Id;
    private static TimeEntry ReadEntry(JsonElement data)
    {
        static long Number(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) && long.TryParse(value.ToString(), out var result) ? result : 0;
        TaskSummary? task = null;
        if (data.TryGetProperty("task", out var t) && t.ValueKind == JsonValueKind.Object)
            task = new(Id(t), Name(t), "", data.TryGetProperty("task_location", out var location) && location.TryGetProperty("list_id", out var list) ? list.ToString() : null);
        return new(Id(data), data.GetProperty("user").GetProperty("id").ToString(), task,
            Number(data, "start"), Number(data, "duration"), Number(data, "end"),
            data.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "");
    }
    public async Task<TimeEntry?> Current(string workspace)
    {
        using var json = await Get($"team/{Segment(workspace)}/time_entries/current", default);
        var data = json.RootElement.GetProperty("data");
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out _) ? ReadEntry(data) : null;
    }
    public async Task<TimeEntry> Entry(string workspace, string id)
    {
        using var json = await Get($"team/{Segment(workspace)}/time_entries/{Segment(id)}", default);
        return ReadEntry(json.RootElement.GetProperty("data"));
    }
    public async Task Start(string workspace, string task, string marker)
    {
        using var json = await Get($"team/{Segment(workspace)}/time_entries/start", default, new { tid = task, description = marker });
    }
    public async Task Finish(string workspace, TimeEntry entry, long end)
    {
        // Correct the stopped entry's cutoff without changing another running timer.
        using var json = await Get($"team/{Segment(workspace)}/time_entries/{Segment(entry.Id)}", default,
            new { start = entry.Start, end, duration = Math.Max(0, end - entry.Start), tags = Array.Empty<object>(), tag_action = "add" }, HttpMethod.Put);
    }
    public async Task<bool> StopCurrent(string workspace, string expectedId)
    {
        var current = await Current(workspace);
        if (current?.Id != expectedId) return false;
        using var json = await Get($"team/{Segment(workspace)}/time_entries/stop", default, new { });
        var stopped = json.RootElement.GetProperty("data");
        if (stopped.ValueKind != JsonValueKind.Object || Id(stopped) != expectedId)
            throw new ClickUpException("ClickUp changed timers during Stop. Refresh to check its current state.");
        return true;
    }
    public async Task<List<TimeEntry>> Entries(string workspace, long start, long end)
    {
        using var json = await Get($"team/{Segment(workspace)}/time_entries?start_date={start}&end_date={end}", default);
        return json.RootElement.GetProperty("data").EnumerateArray().Select(ReadEntry).ToList();
    }
}
