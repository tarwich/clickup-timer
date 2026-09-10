namespace ClickUpTimer;

internal sealed record TimeEntry(string Id, string UserId, TaskSummary? Task, long Start, long Duration, long End, string Description)
{
    internal bool Running => Duration < 0;
}
internal interface ITimingApi : IDisposable
{
    Task<string> User();
    Task<TimeEntry?> Current(string workspace);
    Task<TimeEntry> Entry(string workspace, string id);
    Task Start(string workspace, string task, string marker);
    Task Finish(string workspace, TimeEntry entry, long end);
    Task<bool> StopCurrent(string workspace, string expectedId);
    Task<List<TimeEntry>> Entries(string workspace, long start, long end);
}
internal sealed record StartRequest(string Marker, TaskSummary Task, long RequestedAt);
internal sealed record StopRequest(TimeEntry Entry, long RequestedAt);
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
        var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return $"{(long)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
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
