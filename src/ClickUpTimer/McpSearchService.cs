using System.Text.Json;

namespace ClickUpTimer;

internal sealed class McpSearchService(OAuthStore store) : IClickUpSearch, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ClickUpMcp? client;
    private string? token;
    private JsonElement searchTool;
    private DateTimeOffset retryAfter;
    private readonly Dictionary<(string Workspace, string Cursor), (DateTimeOffset Fetched, SearchPage Page)> hierarchyCache = [];
    internal static JsonElement Content(JsonElement result)
    {
        if (result.TryGetProperty("structuredContent", out var structured)) return structured;
        if (result.TryGetProperty("content", out var blocks))
            foreach (var block in blocks.EnumerateArray())
                if (block.TryGetProperty("type", out var type) && type.GetString() == "text" && block.TryGetProperty("text", out var text))
                {
                    try { using var json = JsonDocument.Parse(text.GetString()!); return json.RootElement.Clone(); }
                    catch (JsonException) { }
                }
        throw new ClickUpException("ClickUp returned an unreadable search response.");
    }
    public async Task<SearchPage> Search(string workspace, string query, string type, string? list, string? cursor, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(query)) return new([]);
        await gate.WaitAsync(cancellation);
        try
        {
            if (DateTimeOffset.UtcNow < retryAfter) throw new ClickUpException("ClickUp's search limit was reached. Cached results are available; try later.", true);
            var session = store.Read() ?? throw new ClickUpException("Connect to ClickUp in Settings to search. Cached results are still available.");
            if (session.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
                throw new ClickUpException("ClickUp sign-in expired. Reconnect in Settings.");
            if (client is null || token != session.AccessToken)
            {
                if (token != session.AccessToken) hierarchyCache.Clear();
                client?.Dispose(); client = new(session.AccessToken); token = session.AccessToken;
                var tools = await client.Tools(cancellation);
                searchTool = tools.GetProperty("tools").EnumerateArray().FirstOrDefault(t => t.GetProperty("name").GetString() == "clickup_search");
                if (searchTool.ValueKind == JsonValueKind.Object) searchTool = searchTool.Clone();
            }
            if (type == "list")
            {
                var key = (workspace, cursor ?? "");
                if (hierarchyCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.Fetched < TimeSpan.FromMinutes(10)) return cached.Page;
                var args = new Dictionary<string, object?> { ["workspace_id"] = workspace, ["max_depth"] = "2", ["limit"] = 50 };
                if (cursor is not null) args["cursor"] = cursor;
                var hierarchy = Content(await client.Call("clickup_get_workspace_hierarchy", args, cancellation));
                cancellation.ThrowIfCancellationRequested();
                var page = ParseHierarchy(hierarchy, workspace);
                hierarchyCache[key] = (DateTimeOffset.UtcNow, page);
                return page;
            }
            if (searchTool.ValueKind != JsonValueKind.Object) throw new ClickUpException("ClickUp's workspace search tool is unavailable.");
            // The live tool schema is validated before sending a search.
            var arguments = Arguments(searchTool.GetProperty("inputSchema"), workspace, query, type, list, cursor);
            var result = Content(await client.Call(searchTool.GetProperty("name").GetString()!, arguments, cancellation));
            return Parse(result);
        }
        catch (ClickUpException ex) when (ex.RateLimited)
        {
            if (retryAfter <= DateTimeOffset.UtcNow) retryAfter = DateTimeOffset.UtcNow.AddMinutes(1);
            throw;
        }
        catch { client?.Dispose(); client = null; throw; }
        finally { gate.Release(); }
    }
    internal static Dictionary<string, object?> Arguments(JsonElement schema, string workspace, string query, string type, string? list, string? cursor)
    {
        var properties = schema.GetProperty("properties");
        if (!properties.TryGetProperty("keywords", out _) || !properties.TryGetProperty("workspace_id", out _))
            throw new ClickUpException("ClickUp's search format changed. Update the app before searching.");
        var arguments = new Dictionary<string, object?> { ["keywords"] = query, ["workspace_id"] = workspace };
        if (cursor is not null)
        {
            if (!properties.TryGetProperty("cursor", out _)) throw new ClickUpException("ClickUp returned more results but does not support result paging.");
            arguments["cursor"] = cursor;
        }
        if (properties.TryGetProperty("count", out var count))
            arguments["count"] = count.TryGetProperty("maximum", out var max) ? Math.Min(100, max.GetInt32()) : 100;
        if (properties.TryGetProperty("filters", out var filtersSchema) && filtersSchema.TryGetProperty("properties", out var filters))
        {
            var values = new Dictionary<string, object?>();
            if (filters.TryGetProperty("asset_types", out var assets) && assets.TryGetProperty("items", out var itemSchema) &&
                itemSchema.TryGetProperty("enum", out var types) && types.EnumerateArray().Any(t => t.GetString() == type))
                values["asset_types"] = new[] { type };
            // Only send location filters advertised by the server. Otherwise filter returned items locally.
            if (list is not null && filters.TryGetProperty("location", out var location) && location.TryGetProperty("properties", out var locations))
            {
                if (locations.TryGetProperty("subcategories", out _)) values["location"] = new { subcategories = new[] { list } };
                else if (locations.TryGetProperty("lists", out _)) values["location"] = new { lists = new[] { list } };
            }
            if (values.Count > 0) arguments["filters"] = values;
        }
        return arguments;
    }
    internal static SearchPage Parse(JsonElement result)
    {
        static string? Text(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;
        var cursor = Text(result, "next_cursor");
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("data", out var data) && data.ValueKind is JsonValueKind.Object or JsonValueKind.Array) result = data;
        JsonElement items;
        if (result.ValueKind == JsonValueKind.Array) items = result;
        else if (result.ValueKind == JsonValueKind.Object && (result.TryGetProperty("results", out items) || result.TryGetProperty("items", out items))) { }
        else throw new ClickUpException("ClickUp returned an unfamiliar search response. Cached results are still available.");
        if (items.ValueKind != JsonValueKind.Array) throw new ClickUpException("ClickUp returned an unreadable result list.");
        var found = new List<SearchItem>();
        foreach (var item in items.EnumerateArray())
        {
            var id = Text(item, "id");
            var name = Text(item, "name") ?? Text(item, "title");
            var type = (Text(item, "type") ?? Text(item, "asset_type"))?.ToLowerInvariant();
            if (id is null || name is null || type is null) throw new ClickUpException("ClickUp returned a result without its identity or type.");
            var list = Text(item, "list_id");
            string? path = Text(item, "path");
            if (item.TryGetProperty("hierarchy", out var hierarchy) && hierarchy.ValueKind == JsonValueKind.Object)
            {
                if (hierarchy.TryGetProperty("subcategory", out var subcategory)) list ??= Text(subcategory, "id");
                path ??= string.Join(" / ", new[] { "project", "category", "subcategory" }
                    .Select(key => hierarchy.TryGetProperty(key, out var ancestor) ? Text(ancestor, "name") : null).Where(n => !string.IsNullOrWhiteSpace(n)));
            }
            if (item.TryGetProperty("list", out var parent)) list ??= Text(parent, "id");
            if (item.TryGetProperty("location", out var location) && location.ValueKind == JsonValueKind.Object)
            {
                list ??= Text(location, "list_id");
                if (location.TryGetProperty("list", out var locationList)) list ??= Text(locationList, "id");
            }
            var status = Text(item, "status");
            string? statusType = Text(item, "status_type");
            if (item.TryGetProperty("status", out var statusObject) && statusObject.ValueKind == JsonValueKind.Object)
            { status = Text(statusObject, "status"); statusType = Text(statusObject, "type"); }
            found.Add(new(id, name, type, list, status, statusType, Text(item, "url"), path));
        }
        cursor ??= Text(result, "next_cursor");
        if (string.IsNullOrWhiteSpace(cursor)) cursor = null;
        return new(found, cursor);
    }
    internal static SearchPage ParseHierarchy(JsonElement result, string workspace)
    {
        var root = result.GetProperty("hierarchy").GetProperty("root");
        if (root.GetProperty("id").ToString() != workspace) throw new ClickUpException("ClickUp returned a different workspace. Reconnect and select the intended workspace.");
        var items = new List<SearchItem>();
        void Visit(JsonElement nodes, string prefix, int depth)
        {
            if (depth > 64) throw new ClickUpException("ClickUp returned an unreadable list hierarchy.");
            foreach (var node in nodes.EnumerateArray())
            {
                var id = node.GetProperty("id").ToString();
                var name = node.GetProperty("name").GetString() ?? "Unnamed";
                var type = node.TryGetProperty("type", out var kind) ? kind.GetString() : null;
                var path = prefix + name;
                if (type is not null) items.Add(new(id, name, type, Path: path));
                if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array) Visit(children, path + " / ", depth + 1);
            }
        }
        Visit(root.GetProperty("children"), "", 0);
        var cursor = result.TryGetProperty("next_cursor", out var next) ? next.GetString() : null;
        if (result.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True && string.IsNullOrEmpty(cursor))
            throw new ClickUpException("ClickUp returned incomplete list locations without a next page.");
        return new(items, string.IsNullOrEmpty(cursor) ? null : cursor, true);
    }
    public void Dispose() => client?.Dispose();
}
