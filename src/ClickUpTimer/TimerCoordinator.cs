namespace ClickUpTimer;

internal sealed class TimerCoordinator
{
    private readonly AppServices services;
    private readonly Func<ITimingApi> createApi;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private TimingState state;
    private List<TimeEntry> entries = [];
    private long totalsDay;
    private bool corrupt, inhibitStart;
    private long? unsavedPauseAt;
    internal bool StopRequestDurable => unsavedPauseAt is null;
    private DateTimeOffset retryAfter;
    internal event Action? Changed;
    internal bool Busy { get; private set; }
    internal bool Online { get; private set; }
    internal bool Conflict { get; private set; }
    internal string? Message { get; private set; }
    internal bool HasPending => state.Starting is not null || state.Stopping is not null || state.PauseAt is not null;
    internal bool CanRequestStop => (state.Entry?.Running == true || state.Starting is not null) && state.Stopping is null && state.PauseAt is null;
    internal bool IsRunning => state.Entry?.Running == true && state.Stopping is null;
    internal TaskSummary? SelectedTask => state.Selected;
    internal bool CanChangeAccount => !Busy && !HasPending && state.Entry?.Running != true && !corrupt;
    internal bool NeedsReview => Conflict || corrupt || state.Starting is not null;
    internal string StatusText => corrupt || Conflict ? "⚠ Review needed" : state.Stopping is not null ? "! Stop pending"
        : state.Starting is not null ? "! Start pending" : !Online ? Busy ? "… Connecting" : "⚠ Offline" : IsRunning ? "▶ Running" : "■ Stopped";
    internal string ReviewText => $"{Message}\n\nTask: {state.Selected?.Name ?? "None"}\nEntry: {state.Stopping?.Entry.Id ?? state.Entry?.Id ?? "Unconfirmed start"}\nRequested stop: {(state.Stopping is { } s ? DateTimeOffset.FromUnixTimeMilliseconds(s.RequestedAt).ToLocalTime().ToString() : "None")}\n\nAccepting ClickUp state abandons the saved request and keeps ClickUp's current record. Review the task in ClickUp first. Continue?";
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    internal string Elapsed => TimingMath.Format(state.Stopping is { } stop ? Math.Max(0, stop.RequestedAt - stop.Entry.Start)
        : state.Entry?.Running == true ? Now - state.Entry.Start : state.CompletedMilliseconds);
    internal string Today => !Online || SelectedTask is null || totalsDay != TimingMath.DayStart(clock.GetUtcNow(), TimeZoneInfo.Local) ? "—"
        : TimingMath.Format(TimingMath.Total(entries, state.Entry?.Running == true ? state.Entry : null, state.UserId!, SelectedTask.Id, totalsDay, Now));

    internal TimerCoordinator(AppServices services, Func<ITimingApi>? createApi = null, TimeProvider? clock = null)
    {
        this.services = services; this.clock = clock ?? TimeProvider.System;
        this.createApi = createApi ?? (() => services.CreateTimingApi(state));
        try { state = services.Store.LoadTiming(); }
        catch (Exception) { state = new(); corrupt = true; Message = "Saved timer recovery data could not be read. Review ClickUp, then use Accept ClickUp state."; }
        services.ValidateAccountChange = () => CanChangeAccount ? null : "Stop and confirm the current timer before changing account, workspace, or API key.";
    }
    private void Save(TimingState next)
    {
        // Persist intent before changing memory or issuing any server write.
        if (next == state) return;
        services.Store.SaveTiming(next); state = next;
    }
    private void Scope()
    {
        var settings = services.Settings;
        if (settings.UserId is null || settings.WorkspaceId is null) throw new ClickUpException("Connect your ClickUp account in Settings.");
        if (state.UserId == settings.UserId && state.WorkspaceId == settings.WorkspaceId) return;
        if (HasPending || state.Entry?.Running == true) throw new ClickUpException("Restore the original account and workspace to recover its timer.");
        Save(new() { UserId = settings.UserId, WorkspaceId = settings.WorkspaceId }); entries.Clear();
    }
    private async Task Run(Func<ITimingApi, Task> action, bool wait = false, bool allowReview = false, bool reconnected = false)
    {
        if (wait) await gate.WaitAsync(); else if (!await gate.WaitAsync(0)) return;
        Busy = true; Changed?.Invoke();
        try
        {
            if (reconnected) retryAfter = default;
            if (corrupt && !allowReview) return;
            if (unsavedPauseAt is not null) throw new ClickUpException("Stop could not be saved locally. Keep the app open and retry Stop, or stop it in ClickUp.");
            if (clock.GetUtcNow() < retryAfter) { Message = "Waiting before retrying ClickUp. Your request remains saved."; return; }
            Scope();
            using var api = createApi();
            if (await api.User() != services.Settings.UserId) throw new ClickUpException("The saved key belongs to a different user. Reconnect the correct account in Settings.");
            await action(api);
            Online = true;
            if (!HasPending && !Conflict) Message = null;
        }
        catch (Exception ex)
        {
            Online = false;
            Message = (HasPending ? "Unconfirmed — " : "Connection problem — ") + (ex is ClickUpException ? ex.Message : "Could not complete the operation. Your recovery record is retained.");
            retryAfter = clock.GetUtcNow().AddSeconds(ex is ClickUpException { RateLimited: true } ? 60 : 15);
        }
        finally { Busy = false; gate.Release(); Changed?.Invoke(); }
    }
    internal Task Refresh() => Run(async api => { await Reconcile(api); await Totals(api); });
    internal Task RefreshAfterReconnect() => Run(async api => { await Reconcile(api); await Totals(api); }, wait: true, reconnected: true);
    private bool Own(TimeEntry e) => e.UserId == state.UserId;
    private async Task Reconcile(ITimingApi api)
    {
        var current = await api.Current(state.WorkspaceId!);
        if (current is not null && !Own(current)) throw new ClickUpException("The API key belongs to a different user. Reconnect in Settings.");
        if (state.Starting is { } start)
        {
            var found = current?.Description == start.Marker ? current :
                (await api.Entries(state.WorkspaceId!, start.RequestedAt - 60000, Now + 1))
                .FirstOrDefault(e => Own(e) && e.Description == start.Marker && e.Task?.Id == start.Task.Id);
            if (found is null)
            {
                Message = "Start is unconfirmed. Check ClickUp before using Accept ClickUp state; Start will not be resent automatically.";
                return;
            }
            Save(state with { Starting = null, Entry = found, Selected = found.Task ?? start.Task,
                Stopping = state.PauseAt is long paused && found.Running ? new(found, Math.Max(found.Start, paused)) : null });
        }
        if (state.Stopping is not null)
        {
            await RecoverStop(api);
            if (state.Stopping is not null) return;
            current = await api.Current(state.WorkspaceId!);
            if (current is not null && !Own(current)) throw new ClickUpException("ClickUp returned another user's timer.");
        }
        if (state.PauseAt is long pause && state.Starting is null && state.Stopping is null)
        {
            if (current is { Running: true } && current.Start <= pause)
            {
                Save(state with { Entry = current, Selected = current.Task, Stopping = new(current, pause) });
                await RecoverStop(api);
                if (HasPending) return;
                current = await api.Current(state.WorkspaceId!);
            }
            else Save(state with { PauseAt = null });
        }
        if (current is { Running: true })
        {
            Save(state with { Entry = current, Selected = current.Task ?? new TaskSummary("", "Unlinked ClickUp timer", ""), CompletedMilliseconds = 0 });
        }
        else if (state.Entry is { Running: true } old)
        {
            var stopped = await api.Entry(state.WorkspaceId!, old.Id);
            if (!stopped.Running) Save(state with { Entry = stopped, CompletedMilliseconds = Math.Max(0, stopped.Duration) });
            else { Message = "ClickUp's timer responses disagree. Refresh to reconcile."; throw new ClickUpException(Message); }
        }
    }
    private async Task RecoverStop(ITimingApi api)
    {
        var stop = state.Stopping!;
        async Task<TimeEntry> ReadTarget()
        {
            var entry = await api.Entry(state.WorkspaceId!, stop.Entry.Id);
            // Singular entries can omit task metadata even while /current includes it.
            // Missing metadata is not evidence that the task association changed.
            if (entry.Task is null)
            {
                var running = await api.Current(state.WorkspaceId!);
                entry = entry with { Task = running?.Id == entry.Id ? running.Task ?? stop.Entry.Task : stop.Entry.Task };
            }
            return entry;
        }
        var remote = await ReadTarget();
        if (!Own(remote) || remote.Start != stop.Entry.Start || remote.Task?.Id != stop.Entry.Task?.Id || remote.Description != stop.Entry.Description)
        { if (!remote.Running) Save(state with { Entry = remote, CompletedMilliseconds = Math.Max(0, remote.Duration) }); Conflict = true; Message = "The time entry was edited elsewhere. Review it in ClickUp, then Accept ClickUp state."; return; }
        var end = Math.Max(remote.Start, stop.RequestedAt);
        Conflict = false;
        if (!remote.Running)
        {
            // A stopped server record is authoritative, including a stop made in ClickUp.
            // Preserve its duration instead of treating a different cutoff as still running.
            Conflict = false;
        }
        else
        {
            // Record the attempted write before sending it, including a lost Stop response.
            if (!api.CanEditStopTime && !stop.Sent)
            {
                stop = stop with { Sent = true };
                Save(state with { Stopping = stop });
            }
            if (!await api.StopCurrent(state.WorkspaceId!, remote.Id))
                throw new ClickUpException("The running timer changed. Refreshing the original entry before retrying.");
            remote = await ReadTarget();
            if (remote.Running) throw new ClickUpException("ClickUp has not confirmed Stop yet.");
            if (remote.Start != stop.Entry.Start || remote.Task?.Id != stop.Entry.Task?.Id || remote.Description != stop.Entry.Description)
                throw new ClickUpException("The entry changed while stopping. Refresh to reconcile it.");
            if (api.CanEditStopTime)
            {
                await api.Finish(state.WorkspaceId!, remote, end);
                remote = await ReadTarget();
                if (remote.Running || Math.Abs((remote.End > 0 ? remote.End : remote.Start + remote.Duration) - end) > 1000)
                    throw new ClickUpException("Stop has not been confirmed by ClickUp yet.");
            }
        }
        var current = await api.Current(state.WorkspaceId!);
        if (current?.Id == remote.Id && current.Running) throw new ClickUpException("ClickUp still reports this timer running. Stop remains unconfirmed.");
        if (!api.CanEditStopTime && stop.Sent && Math.Abs((remote.End > 0 ? remote.End : remote.Start + remote.Duration) - end) > 5000)
        {
            Save(state with { Entry = remote, CompletedMilliseconds = Math.Max(0, remote.Duration) });
            Conflict = true;
            Message = $"Timer stopped in ClickUp. Its recorded stop differs from your requested stop at {DateTimeOffset.FromUnixTimeMilliseconds(end).ToLocalTime():g}. This connection cannot adjust recorded time. Correct the entry in ClickUp, then Refresh, or use Accept ClickUp state to keep its recorded duration. Your requested stop remains saved.";
            return;
        }
        Save(state with { Entry = remote, Stopping = null, PauseAt = null, CompletedMilliseconds = Math.Max(0, remote.Duration) });
    }
    private async Task Totals(ITimingApi api)
    {
        var day = TimingMath.DayStart(clock.GetUtcNow(), TimeZoneInfo.Local);
        // Include older entries that may span midnight; count only their overlap with today.
        entries = await api.Entries(state.WorkspaceId!, 0, Now + 1);
        totalsDay = day;
    }
    internal Task Start() => Run(async api =>
    {
        inhibitStart = false;
        await Reconcile(api);
        if (HasPending || Conflict || inhibitStart || IsRunning) return;
        await Begin(api);
        await Totals(api);
    });
    private async Task Begin(ITimingApi api)
    {
        if (inhibitStart || state.Selected is not { } task || string.IsNullOrEmpty(task.Id)) return;
        var current = await api.Current(state.WorkspaceId!);
        if (current is not null) { await Reconcile(api); Message = "ClickUp already has a running timer. Review it before starting another."; return; }
        if (inhibitStart) return;
        var request = new StartRequest("ClickUp Timer session " + Guid.NewGuid().ToString("N"), task, Now);
        Save(state with { Starting = request, PauseAt = null, Entry = null, CompletedMilliseconds = 0 });
        Changed?.Invoke();
        await api.Start(state.WorkspaceId!, task.Id, request.Marker);
        await Reconcile(api);
    }
    internal Task Select(TaskSummary task) => Run(async api =>
    {
        await Reconcile(api);
        if (HasPending || Conflict || state.Selected?.Id == task.Id) return;
        var transfer = IsRunning;
        if (transfer)
        {
            Save(state with { Stopping = new(state.Entry!, Now) });
            await RecoverStop(api);
            if (HasPending || Conflict) return;
        }
        Save(state with { Selected = task, Entry = null, CompletedMilliseconds = 0 });
        if (transfer && !inhibitStart) await Begin(api);
        await Totals(api);
    });
    // Called synchronously by lock/sleep/quit handlers so the cutoff is durable before suspension.
    internal void RequestPause()
    {
        inhibitStart = true;
        var cutoff = unsavedPauseAt ?? Now;
        try
        {
            if (services.Settings.UserId is null || services.Settings.WorkspaceId is null) return;
            Scope();
            if (state.Stopping is null)
                Save(state with { PauseAt = cutoff, Stopping = state.Entry?.Running == true ? new(state.Entry, cutoff) : null });
            unsavedPauseAt = null;
        }
        catch (Exception) { unsavedPauseAt = cutoff; Message = "Stop request could not be saved. Keep the app open and stop the timer in ClickUp."; }
        Changed?.Invoke();
    }
    internal Task Stop()
    {
        RequestPause();
        return Run(async api => { await Reconcile(api); await Totals(api); }, wait: true);
    }
    internal Task AcceptRemote() => Run(async api =>
    {
        var current = await api.Current(services.Settings.WorkspaceId!);
        if (current is not null && current.UserId != services.Settings.UserId) throw new ClickUpException("Reconnect the correct account first.");
        Save(new() { UserId = services.Settings.UserId, WorkspaceId = services.Settings.WorkspaceId,
            Entry = current, Selected = current?.Task ?? state.Selected });
        corrupt = false; Conflict = false; await Totals(api);
    }, allowReview: true);
}
