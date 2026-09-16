using System.IO;
using System.Text.Json;
using ClickUpTimer;

internal static class McpTimingChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var path = Path.Combine(Path.GetTempPath(), "ClickUpTimer-mcp-timing-" + Guid.NewGuid());
        try
        {
            var clock = new Clock();
            var server = new Server(clock);
            var store = new SettingsStore(path);
            store.Save(new() { UserId = "123", WorkspaceId = "456", PreferredListId = "list" });
            using var services = new AppServices(store);
            services.OAuth.Write(new("mcp-only-fixture", "client", RestCompatible: false));
            using (var productionApi = services.CreateTimingApi())
                check(productionApi is McpTimingApi, "Production timers use MCP even when REST rejects the OAuth token");
            McpTimingApi NewApi()
            {
                var saved = store.LoadTiming();
                return new("fixture", "456", new[] { saved.Entry, saved.Stopping?.Entry }.OfType<TimeEntry>(), server.Call);
            }
            TimerCoordinator NewTimer() => new(services, NewApi, clock);
            var timer = NewTimer();
            await timer.Select(new("task", "Task", "open"));
            await timer.Start();
            check(timer.IsRunning && !timer.HasPending && server.Starts == 1, "MCP Start confirms the marker, task and current server entry");
            clock.Advance(10); await timer.Stop();
            check(!timer.IsRunning && !timer.HasPending && timer.Elapsed == "00m 10s" && server.Stops == 1,
                "MCP Stop confirms the original entry without requiring REST or an edit tool");

            await timer.Start(); clock.Advance(7);
            server.Expired = true; await timer.Stop();
            var pending = store.LoadTiming().Stopping!;
            check(timer.HasPending && timer.Message!.Contains("expired") && pending.RequestedAt == clock.GetUtcNow().ToUnixTimeMilliseconds(),
                "Expired MCP authorization preserves the requested stop time and explains reconnect");
            clock.Advance(120); server.Expired = false;
            await timer.RefreshAfterReconnect();
            check(!timer.IsRunning && !timer.HasPending && !timer.NeedsReview && server.Current is null,
                "Delayed MCP Stop accepts the recorded duration without blocking further use");
            var stops = server.Stops;
            timer = NewTimer(); await timer.Refresh();
            check(!timer.NeedsReview && !timer.HasPending && server.Stops == stops,
                "Restart after delayed Stop does not restore a recovery lock");

            server.LoseStart = true; await timer.Start();
            check(timer.HasPending, "Lost MCP Start response retains the durable start marker");
            var starts = server.Starts;
            timer = NewTimer(); await timer.Refresh();
            check(timer.IsRunning && !timer.HasPending && server.Starts == starts, "MCP recovery finds a lost Start response without duplicating the entry");
            clock.Advance(8); server.LoseStop = true; await timer.Stop();
            check(timer.HasPending && store.LoadTiming().Stopping!.Sent, "Lost MCP Stop response retains its attempted-write marker");
            timer = NewTimer(); await timer.Refresh();
            check(!timer.HasPending && !timer.IsRunning, "Lost MCP Stop response recovers from confirmed history");

            await timer.Start(); clock.Advance(3); server.Expired = true; await timer.Stop();
            var original = server.Current!;
            server.All[original.Id] = original with { Duration = 3000, End = original.Start + 3000 };
            server.All["replacement"] = original with { Id = "replacement", Start = clock.GetUtcNow().ToUnixTimeMilliseconds(), Description = "external" };
            server.Expired = false; stops = server.Stops;
            await timer.RefreshAfterReconnect();
            check(!timer.HasPending && server.Stops == stops && server.Current?.Id == "replacement",
                "MCP recovery adopts an external stop without stopping a replacement timer");
            using (var api = NewApi())
            {
                check(!await api.StopCurrent("456", original.Id) && server.Stops == stops, "MCP checks the expected timer ID before sending Stop");
                var rejected = false;
                try { await api.Current("other"); } catch (ClickUpException) { rejected = true; }
                check(rejected, "MCP timer operations cannot cross the configured workspace");
            }
            var formatted = JsonSerializer.SerializeToElement(new { id = "999999999999999999", user = new { id = 123 },
                start = "2026-09-15T12:00:00Z", end = "2026-09-15T12:00:08Z", duration = "8 seconds", duration_ms = 8000, description = "", task = (object?)null });
            var parsed = McpTimingApi.Parse(formatted);
            check(parsed.Id == "999999999999999999" && parsed.Duration == 8000 && parsed.End - parsed.Start == 8000,
                "MCP history parses duration_ms, ISO timestamps and full-width entry IDs");
            using var failed = new McpTimingApi("fixture", "456", call: (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { success = false })));
            var falseRejected = false;
            try { await failed.Start("456", "task", "marker"); } catch (ClickUpException) { falseRejected = true; }
            check(falseRejected, "MCP success:false cannot be treated as a confirmed timer write");
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
    private sealed class Server(Clock clock)
    {
        internal readonly Dictionary<string, TimeEntry> All = [];
        internal TimeEntry? Current => All.Values.LastOrDefault(e => e.Running);
        internal int Starts, Stops;
        internal bool Expired, LoseStart, LoseStop;
        private static object Wire(TimeEntry entry) => new
        {
            id = entry.Id, user = new { id = entry.UserId }, task = entry.Task is null ? null : new { id = entry.Task.Id, name = entry.Task.Name },
            start = entry.Start.ToString(), end = entry.End > 0 ? entry.End.ToString() : null,
            duration = entry.Duration.ToString(), duration_ms = entry.Duration, description = entry.Description
        };
        internal Task<JsonElement> Call(string name, object args)
        {
            if (Expired) throw new ClickUpException("ClickUp sign-in expired. Reconnect in Settings.", authenticationRejected: true);
            var arguments = JsonSerializer.SerializeToElement(args);
            if (arguments.GetProperty("workspace_id").GetString() != "456") throw new Exception("Missing MCP workspace scope");
            object result;
            switch (name)
            {
                case "clickup_resolve_assignees": result = new { userIds = new[] { "123" } }; break;
                case "clickup_get_current_time_entry": result = new { currentEntry = Current is { } current ? Wire(current) : null, isTracking = Current is not null }; break;
                case "clickup_get_time_entries": result = new { entries = All.Values.Select(Wire).ToArray() }; break;
                case "clickup_start_time_tracking":
                    if (Current is not null) throw new Exception("Duplicate timer start");
                    var marker = arguments.GetProperty("description").GetString()!;
                    var task = arguments.GetProperty("task_id").GetString()!;
                    var entry = new TimeEntry("entry" + ++Starts, "123", new(task, "Task", ""), clock.GetUtcNow().ToUnixTimeMilliseconds(), -1, 0, marker);
                    All[entry.Id] = entry;
                    if (LoseStart) { LoseStart = false; throw new ClickUpException("Lost Start response"); }
                    result = new { success = true, timeEntry = Wire(entry) }; break;
                case "clickup_stop_time_tracking":
                    var old = Current ?? throw new Exception("No timer to stop");
                    var end = clock.GetUtcNow().ToUnixTimeMilliseconds();
                    var stopped = old with { End = end, Duration = end - old.Start }; All[old.Id] = stopped; Stops++;
                    if (arguments.EnumerateObject().Count() != 1) throw new Exception("Stop must preserve description and tags");
                    if (LoseStop) { LoseStop = false; throw new ClickUpException("Lost Stop response"); }
                    result = new { success = true, timeEntry = Wire(stopped) }; break;
                default: throw new Exception("Unexpected tool " + name);
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(result));
        }
    }
}
