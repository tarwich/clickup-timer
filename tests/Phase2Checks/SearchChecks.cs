using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ClickUpTimer;

internal static class SearchChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var path = Path.Combine(Path.GetTempPath(), "ClickUpTimer-search-" + Guid.NewGuid());
        try
        {
            var cache = new SearchCache(path);
            cache.Merge("u", "w", [new("1", "Old", "list"), new("1", "Task", "task", "l")]);
            cache.Merge("u", "w", [new("1", "New", "list"), new("2", "Other", "list")]);
            check(new SearchCache(path).Read("u", "w") is { Count: 3 } && cache.Read("u", "w").Single(i => i.Type == "list" && i.Id == "1").Name == "New", "Search cache merges discoveries, preserves types and refreshes names");
            check(cache.Read("other", "w").Count == 0 && cache.Read("u", "other").Count == 0, "Search cache cannot leak across users or workspaces");
            var oauth = new OAuthStore(path);
            oauth.Write(new("oauth-test-secret", "test-client"));
            check(oauth.Read()?.AccessToken == "oauth-test-secret" && !Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(path, "clickup-oauth.bin"))).Contains("oauth-test-secret"), "OAuth credentials persist only as Windows-user-bound encrypted data");
            var legacy = new CredentialStore("ClickUpTimer.OAuthOnly.Tests/" + Guid.NewGuid());
            try
            {
                legacy.Write("unused-legacy-fixture-key");
                using var services = new AppServices(new SettingsStore(path), legacy);
                try { services.RestAuthorization(); check(false, "Unverified OAuth must not fall back to a legacy key"); }
                catch (ClickUpException) { check(true, "OAuth-only runtime rejects legacy-key fallback even when a key is saved"); }
                oauth.Write(new("verified-fixture-token", "test-client", RestCompatible: true));
                check(services.RestAuthorization() == "Bearer verified-fixture-token", "Timer authorization uses only a verified OAuth token");
            }
            finally { legacy.Delete(); }
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
        check(ClickUpOAuth.ValidState("right", "right") && !ClickUpOAuth.ValidState("wrong", "right") && !ClickUpOAuth.ValidState(null, "right"), "OAuth callback rejects missing or mismatched state");
        check(ClickUpOAuth.Base64Url([251, 255]) == "-_8", "PKCE uses unpadded base64url encoding");
        using var interactive = new InteractiveSearch();
        var calls = 0;
        Task Work(CancellationToken ct) { calls++; return Task.CompletedTask; }
        await interactive.Run("query", Work, _ => throw new Exception(), 1);
        interactive.Open(); await interactive.Run(" ", Work, _ => throw new Exception(), 1);
        check(calls == 0, "Search controller refuses closed or empty queries");
        var first = interactive.Run("first", Work, _ => throw new Exception(), 40);
        var second = interactive.Run("second", Work, _ => throw new Exception(), 1);
        await Task.WhenAll(first, second);
        check(calls == 1, "New query cancels pending debounce");
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var applied = false;
        var pending = interactive.Run("late", async ct => { started.SetResult(); await release.Task; ct.ThrowIfCancellationRequested(); applied = true; }, _ => throw new Exception(), 1);
        await started.Task; interactive.Close(); release.SetResult(); await pending;
        check(!applied, "Late response after picker close cannot update cache or UI");
        interactive.Open(); var failures = 0;
        await interactive.Run("error", _ => { calls++; throw new ClickUpException("quota", true); }, _ => failures++, 1);
        await Task.Delay(30);
        check(calls == 2 && failures == 1, "Search failure is reported once without automatic retry");
        var schema = JsonSerializer.SerializeToElement(new
        {
            properties = new
            {
                keywords = new { type = "string" }, workspace_id = new { type = "string" }, cursor = new { type = "string" }, count = new { maximum = 50 },
                filters = new { properties = new { asset_types = new { items = new { @enum = new[] { "task", "list" } } }, location = new { properties = new { lists = new { type = "array" } } } } }
            }
        });
        var arguments = JsonSerializer.Serialize(McpSearchService.Arguments(schema, "w", "needle", "task", "l", "next"));
        check(arguments.Contains("\"workspace_id\":\"w\"") && arguments.Contains("\"asset_types\":[\"task\"]") && arguments.Contains("\"lists\":[\"l\"]") && arguments.Contains("\"cursor\":\"next\"") && arguments.Contains("\"count\":50"), "MCP search sends explicit workspace, server-supported scope and result cursor");
        using var payload = JsonDocument.Parse("""{"results":[{"id":"t","name":"Task","type":"task","list":{"id":"l"},"status":{"status":"done","type":"closed"}},{"id":"l","name":"List","type":"list"}],"next_cursor":"next"}""");
        var parsed = McpSearchService.Parse(payload.RootElement);
        check(parsed.Cursor == "next" && parsed.Items[0] is { ListId: "l", StatusType: "closed" } && parsed.Items[1].Type == "list", "Search response retains pagination, item types, location and statuses");
        using var hierarchy = JsonDocument.Parse("""{"hierarchy":{"root":{"id":"w","children":[{"id":"s","name":"Team Space","type":"space","children":[{"id":"f","name":"Projects","type":"folder","children":[{"id":"l1","name":"Project 1","type":"list"},{"id":"l2","name":"Project 2","type":"list"}]},{"id":"l3","name":"PHQ.9","type":"list"}]}]}},"next_cursor":null,"has_more":false}""");
        var hierarchyPage = McpSearchService.ParseHierarchy(hierarchy.RootElement, "w");
        check(hierarchyPage.FilterLocally && hierarchyPage.Items.Count(i => i.Type == "list") == 3 && hierarchyPage.Items.Count(i => i.Type == "list" && i.Matches("Proje")) == 2, "Live hierarchy shape finds both Project lists and caches other discoveries");
        check(hierarchyPage.Items.Single(i => i.Id == "l1").Matches("team project 1"), "Hierarchy matching includes parent path and multiple query words");
        try { McpSearchService.ParseHierarchy(hierarchy.RootElement, "other"); check(false, "Wrong workspace must be rejected"); }
        catch (ClickUpException) { check(true, "Hierarchy results cannot cross workspace boundaries"); }
        using var liveTask = JsonDocument.Parse("""{"results":[{"id":"t","name":"Task","type":"task","status":"open","hierarchy":{"project":{"id":"s","name":"Space"},"subcategory":{"id":"l","name":"List"}}}]}""");
        check(McpSearchService.Parse(liveTask.RootElement).Items.Single() is { ListId: "l", Path: "Space / List", Status: "open" }, "Live task hierarchy retains current-list identity");
        using var liveSchema = JsonDocument.Parse(schema.GetRawText().Replace("\"lists\"", "\"subcategories\""));
        check(JsonSerializer.Serialize(McpSearchService.Arguments(liveSchema.RootElement, "w", "query", "task", "l", null)).Contains("\"subcategories\":[\"l\"]"), "Current-list filter uses ClickUp's advertised subcategories field");
        var methods = new List<string>();
        using var client = new ClickUpMcp("fixture-token", new Handler(async (request, ct) =>
        {
            check(request.RequestUri?.Host == "mcp.clickup.com" && request.Headers.Authorization?.ToString() == "Bearer fixture-token", "MCP credentials are sent only to ClickUp in the authorization header");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var method = body.RootElement.GetProperty("method").GetString()!; methods.Add(method);
            if (method == "notifications/initialized") return new(HttpStatusCode.Accepted);
            var id = body.RootElement.GetProperty("id").GetInt32();
            if (method == "initialize")
            {
                var response = Json(new { jsonrpc = "2.0", id, result = new { protocolVersion = "2025-06-18", capabilities = new { tools = new { } }, serverInfo = new { name = "fixture", version = "1" } } });
                response.Headers.Add("Mcp-Session-Id", "session"); return response;
            }
            check(request.Headers.GetValues("Mcp-Session-Id").Single() == "session", "MCP session is retained between calls");
            if (method == "tools/list") return Json(new { jsonrpc = "2.0", id, result = new { tools = new[] { new { name = "clickup_search", inputSchema = schema } } } });
            var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { content = new[] { new { type = "text", text = "{\"results\":[]}" } } } });
            return new(HttpStatusCode.OK) { Content = new StringContent(": heartbeat\n\ndata: " + json + "\n\n", Encoding.UTF8, "text/event-stream") };
        }));
        await client.Tools(default);
        var empty = McpSearchService.Content(await client.Call("clickup_search", new { keywords = "typed by user" }, default));
        check(empty.GetProperty("results").GetArrayLength() == 0 && methods.Count(m => m == "initialize") == 1, "MCP supports streamed responses and initializes only once per client");
        using var limited = new ClickUpMcp("secret", new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests))));
        try { await limited.Tools(default); check(false, "429 must fail"); }
        catch (ClickUpException ex) { check(ex.RateLimited && !ex.Message.Contains("secret"), "MCP quota errors preserve cached-result guidance without exposing tokens"); }
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
