namespace ClickUpTimer;

internal sealed record TaskRow(TaskSummary Task, string Group)
{
    public string Label => $"{Group} · {Task.Name}  [{Task.Status}]";
}

internal static class TaskCatalog
{
    internal static string StatusKey(AppSettings settings, string? list) => $"{settings.UserId}/{settings.WorkspaceId}/{list}";
    internal static bool Ignored(AppSettings settings, TaskSummary task)
    {
        if (settings.IgnoredStatuses.TryGetValue(StatusKey(settings, task.ListId ?? settings.PreferredListId), out var statuses))
            return statuses.Contains(task.Status, StringComparer.OrdinalIgnoreCase);
        return task.StatusType is "done" or "closed" || task.Status.Equals("done", StringComparison.OrdinalIgnoreCase) || task.Status.Equals("closed", StringComparison.OrdinalIgnoreCase);
    }
    internal static List<RecentTask> Remember(AppSettings settings, TaskSummary task) =>
        new[] { new RecentTask(settings.UserId!, settings.WorkspaceId!, task with { ListId = task.ListId ?? settings.PreferredListId }) }
        .Concat(settings.RecentTasks.Where(r => r.UserId == settings.UserId && r.WorkspaceId == settings.WorkspaceId && r.Task.Id != task.Id)).Take(8).ToList();
    internal static List<TaskRow> Filter(AppSettings settings, IEnumerable<TaskSummary> preferred, IEnumerable<TaskSummary> workspace, string query, TaskSummary? active, IReadOnlySet<string>? remoteMatches = null)
    {
        var fresh = workspace.Concat(preferred).DistinctBy(t => t.Id).ToDictionary(t => t.Id);
        var rows = new List<TaskRow>();
        if (active is not null) rows.Add(new(active, "Current"));
        rows.AddRange(settings.RecentTasks.Where(r => r.UserId == settings.UserId && r.WorkspaceId == settings.WorkspaceId)
            .Take(8).Select(r => new TaskRow(fresh.GetValueOrDefault(r.Task.Id) ?? r.Task, "Recent")));
        rows.AddRange(preferred.OrderBy(t => t.Name).Select(t => new TaskRow(fresh[t.Id], "Preferred list")));
        rows.AddRange(workspace.OrderBy(t => t.Name).Select(t => new TaskRow(t, "Workspace")));
        query = query.Trim();
        return rows.Where(r => r.Group == "Current" || (!Ignored(settings, r.Task) &&
            (remoteMatches?.Contains(r.Task.Id) == true || r.Task.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Task.Id.Contains(query, StringComparison.OrdinalIgnoreCase))))
            .DistinctBy(r => r.Task.Id).ToList();
    }
}
