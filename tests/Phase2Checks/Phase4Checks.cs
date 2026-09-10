using System.Net;
using System.Net.Http;
using System.Text.Json;
using ClickUpTimer;

internal static class Phase4Checks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var settings = new AppSettings { UserId = "u", WorkspaceId = "w", PreferredListId = "l" };
        var open = new TaskSummary("id-1", "Find This", "open", "l", "open");
        var done = new TaskSummary("id-2", "Finished", "complete", "l", "done");
        check(TaskCatalog.Ignored(settings, done), "Done status types hidden by default even with custom names");
        settings = settings with { RecentTasks = TaskCatalog.Remember(settings, open) };
        settings = settings with { RecentTasks = TaskCatalog.Remember(settings, done) };
        check(TaskCatalog.Filter(settings, [open, done], [], "", null).Select(r => r.Task.Id).SequenceEqual(["id-1"]), "MRU and preferred tasks deduplicate and exclude ignored statuses");
        check(TaskCatalog.Filter(settings, [open], [], "ID-1", null).Count == 1 && TaskCatalog.Filter(settings, [open], [], "find", null).Count == 1, "Local search matches names and IDs case-insensitively");
        check(TaskCatalog.Filter(settings, [done], [], "no match", done).First().Group == "Current", "Current task remains visible despite status and search exclusions");
        settings.IgnoredStatuses[TaskCatalog.StatusKey(settings, "l")] = [];
        check(!TaskCatalog.Ignored(settings, done), "Explicit status choice can show completed tasks");
        for (var i = 0; i < 12; i++) settings = settings with { RecentTasks = TaskCatalog.Remember(settings, new(i.ToString(), "Task", "open")) };
        check(settings.RecentTasks.Count == 8 && settings.RecentTasks[0].Task.Id == "11", "MRU capped at eight newest selections");
        check(TaskCatalog.Filter(settings with { UserId = "other" }, [], [], "", null).Count == 0, "MRU isolated across accounts");
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClickUpTimer-phase4-" + Guid.NewGuid());
        try
        {
            var store = new SettingsStore(directory); store.Save(settings);
            var restored = new SettingsStore(directory).Load();
            check(restored.RecentTasks.Select(r => r.Task.Id).SequenceEqual(settings.RecentTasks.Select(r => r.Task.Id)) && restored.IgnoredStatuses.Count == 1, "MRU order and per-list exclusions survive restart");
        }
        finally { System.IO.Directory.Delete(directory, true); }
        using var client = new ClickUpClient("fixture", new FixtureHandler(request =>
        {
            var route = request.RequestUri!;
            if (request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                check(route.AbsolutePath.EndsWith("/list/l/task") && body.RootElement.GetProperty("name").GetString() == "New task", "Create targets preferred list with trimmed title");
                check(body.RootElement.GetProperty("assignees").GetArrayLength() == 0 && !body.RootElement.TryGetProperty("status", out _), "Create leaves task unassigned and uses list initial status");
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"id":"new","name":"New task","status":{"status":"open","type":"open"}}""") };
            }
            check(route.Query.Contains("page=1") && route.Query.Contains("subtasks=true") && route.Query.Contains("include_closed=true") && !route.Query.Contains("assignees"), "Workspace paging includes subtasks and all assignees");
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"tasks":[{"id":"t","name":"Task","list":{"id":"other"},"status":{"status":"done","type":"closed"}}]}""") };
        }));
        var page = await client.WorkspacePage("w", 1, default);
        check(page.Single().ListId == "other" && page.Single().StatusType == "closed", "Workspace results retain list and status type for filtering");
        var created = await client.CreateTask("l", " New task ", default);
        check(created.ListId == "l" && created.Id == "new", "Created task can be selected with its destination list");
    }
}
