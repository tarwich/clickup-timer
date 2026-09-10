using System.Windows.Automation;

namespace ClickUpTimer;

internal static class TaskbarScanner
{
    private static readonly Dictionary<string, TaskbarSnapshot> lastVerified = [];
    // Run exclusively on a background MTA thread: Explorer's accessibility provider can block.
    internal static List<TaskbarSnapshot> Scan()
    {
        var handles = new List<nint>();
        Native.EnumWindows((h, _) => { if (Native.Class(h) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") handles.Add(h); return true; }, 0);
        var primary = Native.FindWindow("Shell_TrayWnd", null);
        if (primary != 0 && !handles.Contains(primary)) handles.Add(primary);
        return handles.Select(h =>
        {
            var current = Read(h);
            if (current.Reliable) lastVerified[current.Device] = current;
            else
            {
                lastVerified.TryGetValue(current.Device, out var previous);
                current = current.PreserveControlsFrom(previous);
            }
            return current;
        }).ToList();
    }

    private static TaskbarSnapshot Read(nint hwnd)
    {
        Native.GetWindowRect(hwnd, out var rect);
        var info = new Native.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>(), Device = "" };
        Native.GetMonitorInfo(Native.MonitorFromWindow(hwnd, 2), ref info);
        var occupied = new List<Box>();
        string? error = null;
        var controlCount = 0;
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement e in elements)
            {
                var c = e.Current;
                if (c.ProcessId == Environment.ProcessId) continue; // The taskbar-owned overlay is not an occupied shell control.
                if (c.IsOffscreen) continue;
                var r = c.BoundingRectangle;
                if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) continue;
                // Container panes often span the whole bar. Reserve actionable controls, including
                // search, widgets, task buttons, notification icons, and the clock, not their parent.
                if (c.ControlType != ControlType.Button && c.ControlType != ControlType.Edit &&
                    c.ControlType != ControlType.ListItem && c.ControlType != ControlType.MenuItem &&
                    !c.IsKeyboardFocusable) continue;
                var box = new Box((int)Math.Floor(r.Left), (int)Math.Floor(r.Top), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom));
                if (!box.Intersects(rect.Box)) continue;
                occupied.Add(box);
                controlCount++;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ElementNotAvailableException or InvalidOperationException)
        { error = ex.GetType().Name; }
        // Keep the notification area and Show Desktop protected even if their UIA children vary.
        Native.EnumChildWindows(hwnd, (child, _) =>
        {
            if (Native.Class(child) is "TrayNotifyWnd" or "TrayShowDesktopButton" && Native.IsWindowVisible(child)
                && Native.GetWindowRect(child, out var childRect)) occupied.Add(childRect.Box);
            return true;
        }, 0);
        var reliable = error is null && controlCount > 0 && Native.IsWindowVisible(hwnd);
        return new((long)hwnd, info.Device, (info.Flags & 1) != 0, info.Monitor.Box, info.Work.Box, rect.Box,
            Math.Max(96, Native.GetDpiForWindow(hwnd)) / 96.0, occupied, reliable, error);
    }
}
