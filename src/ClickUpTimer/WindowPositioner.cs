using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ClickUpTimer;

internal sealed class WindowPositioner : IDisposable
{
    private readonly Window window;
    private readonly AppServices services;
    private readonly Action<string> notify;
    private readonly CancellationTokenSource stop = new();
    private readonly DispatcherTimer pulse = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private List<TaskbarSnapshot> snapshots = [];
    private TaskbarSnapshot? selected;
    private Box? position;
    private nint handle, originalOwner;
    private bool visible, dragging, nativeDragging, disposed;
    private Native.Point dragOffset;
    private uint taskbarCreated;
    private int diagnosticsTicks;
    private string state = "Finding taskbar";
    internal event Action? ShellRestarted;
    internal WindowPositioner(Window window, AppServices services, Action<string> notify)
    {
        this.window = window; this.services = services; this.notify = notify;
        window.SourceInitialized += (_, _) => Initialize();
        services.Changed += Refresh;
        pulse.Tick += (_, _) => Tick();
    }
    private void Initialize()
    {
        handle = new WindowInteropHelper(window).Handle;
        originalOwner = Native.GetWindowLongPtr(handle, -8);
        if (!window.ShowInTaskbar)
        {
            var style = (long)Native.GetWindowLongPtr(handle, -20);
            Native.SetWindowLongPtr(handle, -20, (nint)((style | 0x80) & ~0x40000L));
        }
        taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        pulse.Start();
        _ = Task.Run(ScanLoop);
        Refresh();
    }
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)msg == taskbarCreated) { ShellRestarted?.Invoke(); window.Dispatcher.BeginInvoke(Refresh); }
        if (msg is 0x007E or 0x02E0) window.Dispatcher.BeginInvoke(Refresh);
        if (msg == 0x0021) { handled = true; return 3; }
        return 0;
    }
    private async Task ScanLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var next = TaskbarScanner.Scan();
                await window.Dispatcher.InvokeAsync(() => { snapshots = next; if (!dragging) Refresh(); });
                await Task.Delay(1000, stop.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception)
            {
                if (disposed) break;
                try { await Task.Delay(2000, stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }
    private Forms.Screen Screen => Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == services.Settings.Monitor)
        ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
    private static Box Work(Forms.Screen screen) => new(screen.WorkingArea.Left, screen.WorkingArea.Top, screen.WorkingArea.Right, screen.WorkingArea.Bottom);
    internal void Refresh()
    {
        if (handle == 0 || disposed) return;
        var config = services.Settings;
        if (config.Mode == "Floating")
        {
            var screen = Screen;
            var scale = snapshots.FirstOrDefault(b => b.Device == screen.DeviceName)?.Scale ?? Math.Max(96, Native.GetDpiForWindow(handle)) / 96.0;
            Apply(Placement.Floating(Work(screen), scale, config.FloatingX, config.FloatingY), originalOwner);
            return;
        }
        selected = snapshots.FirstOrDefault(b => b.Device == config.Monitor) ?? snapshots.FirstOrDefault(b => b.Primary) ?? snapshots.FirstOrDefault();
        if (selected is null || !selected.Reliable)
        {
            if (!ValidTaskbarGeometry()) Hide("Waiting for taskbar controls");
            return;
        }
        var x = selected.Bounds.Left + (int)Math.Round(selected.Bounds.Width * config.TaskbarX);
        var candidate = Placement.Find(selected.Bounds, selected.Occupied, selected.Scale, x);
        if (candidate is null) { Hide("No safe taskbar gap. Choose Floating in Settings."); return; }
        Apply(candidate.Value, (nint)selected.Handle);
    }
    private void Apply(Box box, nint owner)
    {
        var changed = position != box || Native.GetWindowLongPtr(handle, -8) != owner;
        if (Native.GetWindowLongPtr(handle, -8) != owner) Native.SetWindowLongPtr(handle, -8, owner);
        position = box; state = services.Settings.Mode;
        if (changed || !visible) Native.SetWindowPos(handle, -1, box.Left, box.Top, box.Width, box.Height, 0x0010 | 0x0040);
        visible = true;
    }
    private bool ValidTaskbarGeometry()
    {
        if (selected is null || position is null) return false;
        var bar = (nint)selected.Handle;
        if (Native.Class(bar) is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd") || !Native.GetWindowRect(bar, out var r)) return false;
        var info = new Native.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>(), Device = "" };
        if (!Native.GetMonitorInfo(Native.MonitorFromWindow(bar, 0), ref info)) return false;
        return (selected with { Bounds = r.Box, Monitor = info.Monitor.Box, WorkArea = info.Work.Box, Device = info.Device,
            Scale = Math.Max(96, Native.GetDpiForWindow(bar)) / 96.0 }).HasSameReservedGeometry(selected);
    }
    private void Tick()
    {
        if (position is null) return;
        if (services.Settings.Mode == "Taskbar" && !ValidTaskbarGeometry()) { Hide("Finding taskbar"); return; }
        if (position is not Box b) return;
        if (!visible) { Native.ShowWindow(handle, 4); visible = true; }
        var hit = Native.GetAncestor(Native.WindowFromPoint(new Native.Point { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 }), 2);
        if (Native.Class(hit) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") Native.SetWindowPos(handle, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        if (++diagnosticsTicks % 10 == 0) SaveDiagnostics();
    }
    private void Hide(string reason)
    {
        if (state != reason) notify(reason);
        state = reason; Native.ShowWindow(handle, 0); visible = false;
        position = null;
    }
    internal void BeginDrag()
    {
        if (position is not Box b) return;
        Native.GetCursorPos(out var p); dragOffset = new() { X = p.X - b.Left, Y = p.Y - b.Top }; dragging = true;
    }
    internal void DragFloating()
    {
        dragging = true; nativeDragging = true;
        try { window.DragMove(); }
        finally
        {
            if (Native.GetWindowRect(handle, out var rect)) position = rect.Box;
            nativeDragging = false;
            EndDrag(); Refresh();
        }
    }
    internal void Drag()
    {
        if (!dragging || nativeDragging) return;
        Native.GetCursorPos(out var p);
        var config = services.Settings;
        if (config.Mode == "Floating")
        {
            var screen = Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y));
            var work = Work(screen);
            var scale = snapshots.FirstOrDefault(b => b.Device == screen.DeviceName)?.Scale ?? Math.Max(96, Native.GetDpiForWindow(handle)) / 96.0;
            var size = Placement.Floating(work, scale, 0, 0);
            var x = Math.Clamp(p.X - dragOffset.X, work.Left, Math.Max(work.Left, work.Right - size.Width));
            var y = Math.Clamp(p.Y - dragOffset.Y, work.Top, Math.Max(work.Top, work.Bottom - size.Height));
            Apply(new(x, y, x + size.Width, y + size.Height), originalOwner);
        }
        else
        {
            var target = snapshots.FirstOrDefault(b => b.Bounds.Contains(p.X, p.Y)) ?? selected;
            if (target is not { Reliable: true }) return;
            var candidate = Placement.Find(target.Bounds, target.Occupied, target.Scale, p.X - dragOffset.X);
            if (candidate is Box box) { selected = target; Apply(box, (nint)target.Handle); }
        }
    }
    internal void EndDrag()
    {
        if (!dragging || nativeDragging) return;
        dragging = false;
        if (position is not Box b) return;
        var config = services.Settings;
        if (config.Mode == "Floating")
        {
            var screen = Forms.Screen.FromPoint(new System.Drawing.Point(b.Left, b.Top)); var work = Work(screen);
            config = config with { Monitor = screen.DeviceName, FloatingX = (b.Left - work.Left) / (double)Math.Max(1, work.Width - b.Width), FloatingY = (b.Top - work.Top) / (double)Math.Max(1, work.Height - b.Height) };
        }
        else if (selected is not null) config = config with { Monitor = selected.Device, TaskbarX = (b.Left - selected.Bounds.Left) / (double)selected.Bounds.Width };
        try { services.SavePosition(config); } catch (IOException) { notify("Position could not be saved."); } catch (UnauthorizedAccessException) { notify("Position could not be saved."); }
    }
    internal void Reset()
    {
        try { services.Save(services.Settings with { TaskbarX = 0, FloatingX = .05, FloatingY = .85 }); }
        catch (Exception) { notify("Position could not be saved."); }
    }
    internal void SaveDiagnostics()
    {
        try
        {
            var hits = position is Box b ? new[] { b.Left + 2, b.Left + b.Width / 2, b.Right - 2 }.Select(x =>
            {
                var h = Native.GetAncestor(Native.WindowFromPoint(new Native.Point { X = x, Y = b.Top + b.Height / 2 }), 2);
                return new { X = x, VisibleAtPoint = h == handle, CoveringClass = Native.Class(h) };
            }).ToArray() : null;
            var directory = Path.Combine(SettingsStore.DefaultDirectory, "prototype"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "diagnostics.json"), JsonSerializer.Serialize(new { Timestamp = DateTimeOffset.Now, state, position, visible, hits }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose() { disposed = true; stop.Cancel(); pulse.Stop(); services.Changed -= Refresh; }
}
