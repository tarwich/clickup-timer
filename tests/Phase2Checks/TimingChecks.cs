using ClickUpTimer;
using System.IO;

internal static class TimingChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "ClickUpTimer-timing-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var time = new TestClock();
            var api = new FakeApi(time);
            var store = new SettingsStore(root);
            store.Save(new() { UserId = "u", WorkspaceId = "w", PreferredListId = "list" });
            using var services = new AppServices(store);
            TimerCoordinator NewTimer() => new(services, () => api, time);
            var timer = NewTimer();
            var one = new TaskSummary("one", "First", "open"); var two = new TaskSummary("two", "Second", "open");
            await timer.Refresh(); await timer.Start();
            check(api.Starts == 0 && !timer.IsRunning, "No server start without selected task");
            await timer.Select(one); await timer.Start();
            check(timer.IsRunning && api.Starts == 1, "Start reconciles the server entry before showing running");
            var runningEntry = api.CurrentEntry;
            var runningState = store.LoadTiming();
            services.StoreConnection(new("fixture-token", "fixture-client"), new("u", "Fixture"), [new("w", "Workspace")]);
            check(timer.IsRunning && store.LoadTiming() == runningState,
                "Same-account OAuth reconnect is allowed while running without changing timer state");
            services.Save(services.Settings with { Presentation = "Minimal", Appearance = "Dark" });
            check(timer.IsRunning && api.CurrentEntry == runningEntry && api.Starts == 1 && api.Finishes == 0, "Changing appearance and presentation leaves the running entry untouched");
            var accountBlocked = false;
            try { services.Save(services.Settings with { WorkspaceId = "different" }); }
            catch (ClickUpException) { accountBlocked = true; }
            check(accountBlocked && services.Settings.WorkspaceId == "w", "Active timer prevents account changes that would strand recovery");
            time.Advance(12); check(timer.Elapsed == "00m 12s", "Elapsed derives from server timestamp");
            await timer.Stop(); time.Advance(20);
            check(!timer.IsRunning && timer.Elapsed == "00m 12s", "Confirmed Stop retains completed session duration");
            await timer.Start(); check(timer.Elapsed == "00m 00s", "Next server Start begins a fresh session");
            time.Advance(5); await timer.Select(two);
            check(timer.IsRunning && timer.SelectedTask?.Id == "two" && api.Starts == 3 && api.Finishes == 2, "Running task transfer finishes old entry before starting next");
            time.Advance(7); api.Offline = true; await timer.Stop();
            var saved = store.LoadTiming();
            check(saved.Stopping is not null && timer.HasPending && !timer.Online, "Offline Stop persists exact cutoff and remains visibly unconfirmed");
            var cutoff = saved.Stopping!.RequestedAt;
            var reconnectSession = new OAuthSession("fixture-token", "fixture-client");
            services.StoreConnection(reconnectSession, new("u", "Fixture"), [new("w", "Workspace")]);
            check(store.LoadTiming() == saved && services.OAuth.Read()?.AccessToken == "fixture-token",
                "Reconnect during pending Stop preserves the durable cutoff and installs same-account authorization");
            foreach (var wrongScope in new[] { new ConnectedAccount(new("other", "Other"), [new("w", "Workspace")]),
                new ConnectedAccount(new("u", "Fixture"), [new("other", "Other workspace")]) })
            {
                var rejected = false;
                try { services.StoreConnection(reconnectSession with { AccessToken = "wrong-token" }, wrongScope.User, wrongScope.Workspaces); }
                catch (ClickUpException) { rejected = true; }
                check(rejected && services.OAuth.Read()?.AccessToken == "fixture-token" && store.LoadTiming() == saved,
                    "Reconnect rejects a different user or missing timer workspace without replacing credentials or recovery");
            }
            api.Offline = false;
            await timer.RefreshAfterReconnect();
            check(!timer.HasPending && api.All[saved.Stopping.Entry.Id].End == cutoff,
                "Reconnect immediately recovers pending Stop despite the previous connection backoff");
            api.Offline = false; time.Advance(60); timer = NewTimer(); await timer.Refresh();
            check(!timer.HasPending && timer.Elapsed == "00m 07s" && api.All[saved.Stopping.Entry.Id].End == cutoff, "Restart recovery corrects the original entry to saved stop timestamp");
            await timer.Select(one); api.LoseStartResponse = true; await timer.Start();
            check(timer.HasPending && store.LoadTiming().Starting is not null, "Lost Start response leaves a durable recovery marker");
            var starts = api.Starts;
            timer = NewTimer(); await timer.Refresh();
            check(timer.IsRunning && !timer.HasPending && api.Starts == starts, "Restart finds uncertain Start without issuing a duplicate");
            time.Advance(4); api.LoseFinishResponse = true; await timer.Stop();
            var finishes = api.Finishes;
            timer = NewTimer(); await timer.Refresh();
            check(!timer.HasPending && !timer.IsRunning && api.Finishes == finishes, "Lost Stop response reconciles without rewriting confirmed entry");
            await timer.Start(); time.Advance(2);
            var externalStop = api.CurrentEntry!;
            api.All[externalStop.Id] = externalStop with { Duration = 2000, End = externalStop.Start + 2000 };
            time.Advance(10); await timer.Stop();
            check(!timer.HasPending && !timer.IsRunning && timer.Elapsed == "00m 02s", "Stop after an external stop adopts ClickUp duration instead of getting stuck");
            time.Advance(10); await timer.Refresh();
            check(timer.Elapsed == "00m 02s", "Externally stopped timer stays frozen on subsequent refresh");
            api.OmitTaskMetadata = true;
            await timer.Start(); time.Advance(3); await timer.Stop();
            check(!timer.HasPending && !timer.IsRunning && timer.Elapsed == "00m 03s", "Missing task metadata in singular entry does not block Stop");
            api.OmitTaskMetadata = false;
            await timer.Start(); time.Advance(3);
            var old = api.CurrentEntry!;
            api.Offline = true; await timer.Stop(); api.Offline = false;
            api.All[old.Id] = old with { Duration = 1000, End = old.Start + 1000 };
            api.External(two);
            finishes = api.Finishes;
            timer = NewTimer(); await timer.Refresh();
            check(!timer.HasPending && api.Finishes == finishes && api.CurrentEntry?.Task?.Id == "two", "External stop resolves pending request without stopping replacement timer");
            await timer.AcceptRemote();
            check(!timer.HasPending && timer.SelectedTask?.Id == "two", "Explicit review can accept remote state");
            await timer.Stop(); await timer.Select(one);
            api.StartGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var starting = timer.Start();
            await timer.Start();
            time.Advance(2); var stopping = timer.Stop();
            api.StartGate.SetResult(); await starting; await stopping; api.StartGate = null;
            check(!timer.IsRunning && !timer.HasPending, "Lock or Stop during in-flight Start prevents an unattended timer");
            var count = api.Starts; await timer.Refresh();
            check(api.Starts == count, "Refresh and resume never automatically start a timer");
            api.RateLimited = true; await timer.Refresh(); var reads = api.Reads;
            await timer.Refresh(); check(api.Reads == reads, "Rate-limit response backs off instead of hammering API");
            api.RateLimited = false; time.Advance(61); await timer.Refresh();
            check(timer.Online, "Polling recovers after backoff");
            var day = TimingMath.DayStart(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
            var running = new TimeEntry("run", "u", one, day - 10000, -1, 0, "");
            var stopped = new TimeEntry("done", "u", one, day - 20000, 25000, day + 5000, "");
            check(TimingMath.Total([running, stopped, stopped with { Id = "other", UserId = "x" }], running, "u", "one", day, day + 15000) == 20000,
                "Today clips cross-midnight entries, filters user/task, and never doubles running entry");
            check(TimingMath.Format(25 * 3600000L) == "1d 1h 00m 00s", "Long sessions use elapsed days and hours");
            var central = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
            var dstDay = TimingMath.DayStart(new DateTimeOffset(2026, 3, 8, 20, 0, 0, TimeSpan.Zero), central);
            check(dstDay == new DateTimeOffset(2026, 3, 8, 6, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), "Local-day boundary respects daylight-saving transition");
            api.External(one);
            timer = NewTimer();
            await timer.Stop();
            check(api.CurrentEntry is null, "Quit before initial reconciliation still stops a timer existing at the quit cutoff");
            using var wire = new ClickUpClient("fixture", new FixtureHandler(request =>
            {
                if (request.Method == System.Net.Http.HttpMethod.Put)
                {
                    var body = System.Text.Json.JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    check(request.RequestUri!.AbsolutePath.EndsWith("/time_entries/1234567890123456789") && body.RootElement.GetProperty("end").GetInt64() == 2000
                        && body.RootElement.GetProperty("tag_action").GetString() == "add", "Stop writes target entry ID and exact cutoff without replacing metadata");
                }
                return new(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("""{"data":{"id":"1234567890123456789","user":{"id":42},"task":{"id":"task","name":"Example"},"start":"1000","duration":"-1000","description":"marker"}}""") };
            }));
            var parsed = await wire.Current("w");
            check(parsed?.Running == true && parsed.Id == "1234567890123456789" && parsed.UserId == "42" && parsed.Start == 1000,
                "Timing API parses string timestamps and full-width IDs without precision loss");
            await wire.Finish("w", parsed!, 2000);
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 9, 10, 15, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
    private sealed class FakeApi(TestClock clock) : ITimingApi
    {
        internal readonly Dictionary<string, TimeEntry> All = [];
        internal int Starts, Finishes, Reads;
        internal bool Offline, LoseStartResponse, LoseFinishResponse, RateLimited, OmitTaskMetadata;
        internal TaskCompletionSource? StartGate;
        internal TimeEntry? CurrentEntry => All.Values.LastOrDefault(e => e.Running);
        public Task<string> User() { Read(); return Task.FromResult("u"); }
        private void Read() { Reads++; if (Offline) throw new ClickUpException("Offline"); if (RateLimited) throw new ClickUpException("Rate limited", true); }
        public Task<TimeEntry?> Current(string workspace) { Read(); return Task.FromResult(CurrentEntry); }
        public Task<TimeEntry> Entry(string workspace, string id) { Read(); return Task.FromResult(OmitTaskMetadata ? All[id] with { Task = null } : All[id]); }
        public async Task Start(string workspace, string task, string marker)
        {
            Starts++;
            var id = "e" + Starts;
            All[id] = new(id, "u", new(task, task, "open"), clock.GetUtcNow().ToUnixTimeMilliseconds(), -1, 0, marker);
            if (StartGate is not null) await StartGate.Task;
            if (LoseStartResponse) { LoseStartResponse = false; throw new ClickUpException("Lost start response"); }
        }
        internal void External(TaskSummary task) => All["external"] = new("external", "u", task, clock.GetUtcNow().ToUnixTimeMilliseconds(), -1, 0, "External");
        public Task Finish(string workspace, TimeEntry entry, long end)
        {
            Finishes++; All[entry.Id] = entry with { End = end, Duration = end - entry.Start };
            if (LoseFinishResponse) { LoseFinishResponse = false; throw new ClickUpException("Lost stop response"); }
            return Task.CompletedTask;
        }
        public Task<bool> StopCurrent(string workspace, string expectedId)
        {
            if (CurrentEntry?.Id != expectedId) return Task.FromResult(false);
            var entry = All[expectedId];
            var end = clock.GetUtcNow().ToUnixTimeMilliseconds();
            All[expectedId] = entry with { End = end, Duration = end - entry.Start };
            return Task.FromResult(true);
        }
        public Task<List<TimeEntry>> Entries(string workspace, long start, long end) { Read(); return Task.FromResult(All.Values.ToList()); }
        public void Dispose() { }
    }
}
