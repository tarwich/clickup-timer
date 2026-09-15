namespace ClickUpTimer;

internal sealed record TimeEntry(string Id, string UserId, TaskSummary? Task, long Start, long Duration, long End, string Description)
{
    internal bool Running => Duration < 0;
}
internal interface ITimingApi : IDisposable
{
    bool CanEditStopTime => true;
    Task<string> User();
    Task<TimeEntry?> Current(string workspace);
    Task<TimeEntry> Entry(string workspace, string id);
    Task Start(string workspace, string task, string marker);
    Task Finish(string workspace, TimeEntry entry, long end);
    Task<bool> StopCurrent(string workspace, string expectedId);
    Task<List<TimeEntry>> Entries(string workspace, long start, long end);
}
internal sealed record StartRequest(string Marker, TaskSummary Task, long RequestedAt);
internal sealed record StopRequest(TimeEntry Entry, long RequestedAt, bool Sent = false);
internal sealed record TimingState
{
    public string? UserId { get; init; }
    public string? WorkspaceId { get; init; }
    public TaskSummary? Selected { get; init; }
    public TimeEntry? Entry { get; init; }
    public StartRequest? Starting { get; init; }
    public StopRequest? Stopping { get; init; }
    public long? PauseAt { get; init; }
    public long CompletedMilliseconds { get; init; }
}

internal static class TimingMath
{
    internal static string Format(long milliseconds)
    {
        var seconds = Math.Max(0, milliseconds) / 1000;
        var weeks = seconds / 604800;
        var days = seconds / 86400 % 7;
        var hours = seconds / 3600 % 24;
        var prefix = weeks > 0 ? $"{weeks}w {days}d {hours}h " : days > 0 ? $"{days}d {hours}h " : hours > 0 ? $"{hours}h " : "";
        return prefix + $"{seconds / 60 % 60:00}m {seconds % 60:00}s";
    }
    internal static long DayStart(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        return new DateTimeOffset(local.Date, zone.GetUtcOffset(local.Date)).ToUnixTimeMilliseconds();
    }
    internal static long Total(IEnumerable<TimeEntry> entries, TimeEntry? running, string user, string task, long start, long now)
    {
        var all = (running is null ? entries : new[] { running }.Concat(entries)).DistinctBy(e => e.Id);
        return all.Where(e => e.UserId == user && e.Task?.Id == task).Sum(e =>
        {
            var end = e.Running ? now : e.End > 0 ? e.End : e.Start + e.Duration;
            return Math.Max(0, Math.Min(now, end) - Math.Max(start, e.Start));
        });
    }
}
