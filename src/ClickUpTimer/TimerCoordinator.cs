using System.Diagnostics;

namespace ClickUpTimer;

// Demo only until Phase 5 provides server-backed timing.
internal sealed class TimerCoordinator
{
    private readonly Stopwatch watch = new();
    internal TaskSummary? SelectedTask { get; private set; }
    internal bool IsRunning => watch.IsRunning;
    internal string Elapsed => $"{(int)watch.Elapsed.TotalHours:00}:{watch.Elapsed.Minutes:00}:{watch.Elapsed.Seconds:00}";
    internal void Select(TaskSummary? task)
    {
        if (SelectedTask?.Id == task?.Id) return;
        watch.Reset(); SelectedTask = task;
    }
    internal void Toggle()
    {
        if (SelectedTask is null) return;
        if (watch.IsRunning) watch.Stop(); else watch.Restart();
    }
}
