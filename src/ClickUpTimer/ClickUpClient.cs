using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ClickUpTimer;

internal sealed record Choice(string Id, string Name) { public override string ToString() => Name; }
internal sealed record ClickUpUser(string Id, string Name);
internal sealed class ClickUpException(string message) : Exception(message);

internal sealed class ClickUpClient : IDisposable
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
    private async Task<JsonDocument> Get(string path, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
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
                });
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
        void AddLists(JsonElement root, string prefix)
        {
            if (!root.TryGetProperty("lists", out var lists)) return;
            foreach (var list in lists.EnumerateArray())
            {
                if (list.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True) continue;
                var id = Id(list); result.TryAdd(id, new Choice(id, prefix + Name(list)));
            }
            progress?.Report(result.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        }
        using var spaces = await Get($"team/{Segment(workspace)}/space?archived=false", cancellation);
        foreach (var space in spaces.RootElement.GetProperty("spaces").EnumerateArray())
        {
            var path = Name(space) + " / ";
            using var loose = await Get($"space/{Segment(Id(space))}/list?archived=false", cancellation);
            AddLists(loose.RootElement, path);
            using var folders = await Get($"space/{Segment(Id(space))}/folder?archived=false", cancellation);
            foreach (var folder in folders.RootElement.GetProperty("folders").EnumerateArray())
            {
                using var lists = await Get($"folder/{Segment(Id(folder))}/list?archived=false", cancellation);
                AddLists(lists.RootElement, path + Name(folder) + " / ");
            }
        }
        // Guests and members can also have lists shared outside their space membership.
        using var shared = await Get($"team/{Segment(workspace)}/shared", cancellation);
        AddLists(shared.RootElement, "Shared / ");
        if (shared.RootElement.TryGetProperty("folders", out var sharedFolders))
            foreach (var folder in sharedFolders.EnumerateArray())
            {
                using var lists = await Get($"folder/{Segment(Id(folder))}/list?archived=false", cancellation);
                AddLists(lists.RootElement, "Shared / " + Name(folder) + " / ");
            }
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
                result[Id(item)] = new(Id(item), Name(item), item.GetProperty("status").GetProperty("status").GetString() ?? "");
            if (tasks.GetArrayLength() == 0 || (json.RootElement.TryGetProperty("last_page", out var last) && last.ValueKind == JsonValueKind.True)) break;
        }
        return result.Values.ToList();
    }
    public void Dispose() => http.Dispose();
}
