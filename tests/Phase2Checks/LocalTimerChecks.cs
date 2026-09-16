using System.IO;
using ClickUpTimer;

internal static class LocalTimerChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var path = Path.Combine(Path.GetTempPath(), "ClickUpTimer-local-" + Guid.NewGuid());
        try
        {
            var store = new SettingsStore(path);
            store.Save(new() { UserId = "u", WorkspaceId = "w" });
            var task = new TaskSummary("one", "First", "open");
            var entry = new TimeEntry("stale", "u", task, 1000, -1, 0, "old");
            store.SaveTiming(new() { UserId = "u", WorkspaceId = "w", Selected = task, Entry = entry,
                Stopping = new(entry, 2000, true), PauseAt = 2000 });
            using var services = new AppServices(store);
            var clock = new Clock();
            var calls = 0;
            TimerCoordinator NewTimer() => new(services, () => { calls++; throw new ClickUpException("Sign-in expired"); }, clock);
            var timer = NewTimer();
            await timer.Refresh();
            check(timer.HasPending && !timer.Online, "Fixture begins with inaccessible ClickUp and pending recovery");
            var before = calls;
            await timer.UseLocalTimer();
            check(timer.LocalOnly && !timer.HasPending && !timer.NeedsReview && timer.CanChangeAccount && calls == before,
                "Discarding recovery unlocks local mode without OAuth or network calls despite backoff");
            await timer.Start(); clock.Advance(10);
            check(timer.IsRunning && timer.Elapsed == "00m 10s", "Local Start measures time with expired authorization");
            await timer.Select(new("two", "Second", "open")); clock.Advance(3);
            check(timer.IsRunning && timer.SelectedTask?.Id == "two" && timer.Elapsed == "00m 03s", "Local task switching works while running without ClickUp");
            await timer.RefreshAfterReconnect();
            check(timer.LocalOnly && calls == before, "Refresh and reconnect cannot upload local time or reimport the stale timer");
            timer = NewTimer(); clock.Advance(2);
            check(timer.LocalOnly && timer.IsRunning && timer.Elapsed == "00m 05s", "Local mode and running stopwatch survive restart");
            timer.RequestPause(); clock.Advance(4); await timer.Stop();
            check(!timer.IsRunning && timer.Elapsed == "00m 05s" && !timer.HasPending && calls == before,
                "Local lock/sleep/Stop freezes elapsed time without creating a remote recovery request");
            services.Save(services.Settings with { WorkspaceId = "new-workspace" });
            check(services.Settings.WorkspaceId == "new-workspace", "Discarded recovery cannot block changing workspace");
            await timer.EnableClickUpLogging();
            check(!timer.LocalOnly && store.LoadTiming().LocalStartedAt is null && calls == before,
                "Explicitly enabling ClickUp logging discards local time without uploading it");

            store.SaveTiming(new() { Starting = new("unknown", task, 1000), Selected = task });
            timer = NewTimer(); await timer.UseLocalTimer(); await timer.Start();
            check(timer.IsRunning && !timer.HasPending && calls == before, "Unconfirmed starts can be abandoned without contacting ClickUp");
            File.WriteAllText(Path.Combine(path, "timer-state.json"), "broken json");
            timer = NewTimer(); check(timer.NeedsReview, "Fixture detects corrupt timer state");
            await timer.UseLocalTimer(); await timer.Start(); clock.Advance(1); await timer.Stop();
            check(timer.LocalOnly && !timer.NeedsReview && timer.Elapsed == "00m 01s" && store.LoadTiming().LocalOnly,
                "Corrupt timer state can be reset to a working local stopwatch without an account or task");
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
}
