namespace ClickUpTimer;

internal sealed class AppServices : IDisposable
{
    internal SettingsStore Store { get; }
    internal CredentialStore Credentials { get; }
    internal OAuthStore OAuth { get; }
    internal SearchCache SearchCache { get; }
    internal IClickUpSearch Search { get; }
    internal ClickUpClient CreateClient() => new(RestAuthorization());
    internal ITimingApi CreateTimingApi(TimingState? timing = null)
    {
        var session = OAuth.Read() ?? throw new ClickUpException("Open Settings and reconnect to ClickUp to recover your timer.");
        return new McpTimingApi(session.AccessToken, Settings.WorkspaceId!,
            new[] { timing?.Entry, timing?.Stopping?.Entry }.OfType<TimeEntry>());
    }
    internal async Task<TaskSummary> GetTask(string id)
    {
        var session = OAuth.Read() ?? throw new ClickUpException("Reconnect to ClickUp in Settings.");
        using var client = new ClickUpMcp(session.AccessToken);
        var task = McpSearchService.Content(await client.Call("clickup_get_task", new { workspace_id = Settings.WorkspaceId, task_id = id }, default));
        return ReadMcpTask(task);
    }
    internal static TaskSummary ReadMcpTask(System.Text.Json.JsonElement task)
    {
        if (task.TryGetProperty("task", out var wrapped)) task = wrapped;
        var status = task.GetProperty("status");
        return new(task.GetProperty("id").ToString(), task.GetProperty("name").GetString()!,
            status.ValueKind == System.Text.Json.JsonValueKind.Object ? status.GetProperty("status").ToString() : status.ToString(),
            task.TryGetProperty("list", out var list) && list.TryGetProperty("id", out var listId) ? listId.ToString() : null,
            status.ValueKind == System.Text.Json.JsonValueKind.Object && status.TryGetProperty("type", out var type) ? type.ToString() : null);
    }
    internal string RestAuthorization()
    {
        var oauth = OAuth.Read();
        if (oauth?.RestCompatible == true) return "Bearer " + oauth.AccessToken;
        throw new ClickUpException("This feature is not yet available with your browser sign-in. Search, task selection, and timers use your existing connection.");
    }
    internal AppSettings Settings { get; private set; }
    internal event Action? Changed;
    internal event Action? Reconnected;
    internal bool LocalTimerMode { get; set; }
    internal Func<string?>? ValidateAccountChange { get; set; }
    internal async Task<ConnectedAccount> ConnectAccount(CancellationToken cancellation)
    {
        var session = await ClickUpOAuth.Connect(cancellation);
        return await CompleteConnection(session, cancellation);
    }
    internal async Task<ConnectedAccount?> RestoreConnection(CancellationToken cancellation)
    {
        var session = OAuth.Read();
        if (session is null) return null;
        if (session.User is not null && session.Workspaces is { Count: > 0 }) return new(session.User, session.Workspaces, session.RestCompatible);
        return await CompleteConnection(session, cancellation);
    }
    private async Task<ConnectedAccount> CompleteConnection(OAuthSession session, CancellationToken cancellation)
    {
        using var mcp = new ClickUpMcp(session.AccessToken);
        await mcp.Tools(cancellation);
        ClickUpUser user;
        List<Choice> workspaces;
        try
        {
            using var oauthApi = new ClickUpClient("Bearer " + session.AccessToken);
            user = await oauthApi.Validate(cancellation);
            workspaces = await oauthApi.Workspaces(cancellation);
            session = session with { RestCompatible = true };
        }
        catch (ClickUpException ex) when (ex.AuthenticationRejected)
        {
            // A successful MCP sign-in is a connected search session even when
            // the independently scoped REST API requires another OAuth grant.
            var args = new Dictionary<string, object?> { ["max_depth"] = "0", ["limit"] = 1 };
            if (Settings.WorkspaceId is not null) args["workspace_id"] = Settings.WorkspaceId;
            var hierarchy = McpSearchService.Content(await mcp.Call("clickup_get_workspace_hierarchy", args, cancellation));
            var root = hierarchy.GetProperty("hierarchy").GetProperty("root");
            var workspaceId = root.GetProperty("id").ToString();
            var me = McpSearchService.Content(await mcp.Call("clickup_resolve_assignees", new { workspace_id = workspaceId, assignees = new[] { "me" } }, cancellation));
            var userId = me.GetProperty("userIds")[0].GetString() ?? throw new ClickUpException("ClickUp could not identify the connected user.");
            user = new(userId, userId == Settings.UserId ? Settings.UserName ?? "ClickUp user" : "ClickUp user");
            workspaces = [new(workspaceId, workspaceId == Settings.WorkspaceId ? Settings.WorkspaceName ?? "Workspace" : root.GetProperty("name").GetString() ?? "Workspace")];
        }
        cancellation.ThrowIfCancellationRequested();
        return StoreConnection(session, user, workspaces);
    }
    internal ConnectedAccount StoreConnection(OAuthSession session, ClickUpUser user, List<Choice> workspaces)
    {
        if (workspaces.Count == 0) throw new ClickUpException("Authorize at least one ClickUp workspace.");
        // Do not silently replace the timer account while its settings are still a draft.
        if (Settings.UserId is not null && user.Id != Settings.UserId)
            throw new ClickUpException("Sign in with the account already used by this timer.");
        if (Settings.WorkspaceId is { } workspace && !workspaces.Any(w => w.Id == workspace)
            && ValidateAccountChange?.Invoke() is not null)
            throw new ClickUpException("Authorize the workspace already used by this timer so its saved timer can recover.");
        OAuth.Write(session with { User = user, Workspaces = workspaces });
        if (session.RestCompatible) Credentials.Delete();
        Reconnected?.Invoke();
        return new(user, workspaces, session.RestCompatible);
    }
    private CancellationTokenSource? refresh;
    internal string? CacheNotice { get; private set; }
    internal AppServices(SettingsStore? store = null, CredentialStore? credentials = null)
    {
        Store = store ?? new(); Credentials = credentials ?? new(); Settings = Store.Load();
        OAuth = new(Store.DirectoryPath); SearchCache = new(Store.DirectoryPath); Search = new McpSearchService(OAuth);
    }

    internal void Save(AppSettings next, string? replacementKey = null)
    {
        var oldKey = replacementKey is not null ? Credentials.Read() : null;
        if (next.UserId != Settings.UserId || next.WorkspaceId != Settings.WorkspaceId || (replacementKey is not null && replacementKey != oldKey))
        {
            var problem = ValidateAccountChange?.Invoke();
            if (problem is not null) throw new ClickUpException(problem);
        }
        var oldStartup = StartupRegistration.Read();
        try
        {
            if (replacementKey is not null) Credentials.Write(replacementKey);
            if (next.LaunchAtSignIn != Settings.LaunchAtSignIn) StartupRegistration.Set(next.LaunchAtSignIn);
            Store.Save(next);
        }
        catch
        {
            // Restore the previously configured account if a later persistence step failed.
            if (replacementKey is not null) { if (oldKey is null) Credentials.Delete(); else Credentials.Write(oldKey); }
            StartupRegistration.Restore(oldStartup);
            throw;
        }
        Settings = next;
        Changed?.Invoke();
    }
    internal void SavePosition(AppSettings next)
    {
        Store.Save(next); Settings = next;
    }
    internal async Task RefreshCache()
    {
        refresh?.Cancel(); refresh?.Dispose(); refresh = new();
        var cancellation = refresh.Token;
        var config = Settings;
        if (!config.IsConfigured) return;
        try
        {
            using var client = CreateClient();
            var tasks = await client.Tasks(config.PreferredListId!, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (Settings.UserId != config.UserId || Settings.WorkspaceId != config.WorkspaceId || Settings.PreferredListId != config.PreferredListId) return;
            Store.SaveCache(new(config.UserId!, config.WorkspaceId!, config.PreferredListId!, DateTimeOffset.Now, tasks));
            CacheNotice = $"{tasks.Count} tasks cached for your preferred list.";
        }
        catch (OperationCanceledException) { return; }
        catch (Exception) { CacheNotice = "Settings saved. Task cache could not refresh; it can be retried from Settings."; }
        Changed?.Invoke();
    }
    public void Dispose() { refresh?.Cancel(); refresh?.Dispose(); (Search as IDisposable)?.Dispose(); }
}
