using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Microsoft.Win32;

namespace ClickUpTimer;

internal sealed class TimerWindow : Window
{
    private readonly AppServices services;
    private readonly TimerCoordinator timer;
    private readonly TimerStrip strip = new();
    private readonly DispatcherTimer reconcile = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer pulse = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool quitting, mayClose;
    private readonly WindowPositioner positioning;
    private readonly Forms.NotifyIcon tray;
    private string? displayedTask;
    private bool focusSessionDetails;
    private readonly TextBox details = new() { TextWrapping = TextWrapping.Wrap, IsReadOnly = true, Height = double.NaN, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Margin = new Thickness(0, 4, 0, 8) };
    private readonly StackPanel recovery = new() { Orientation = Orientation.Horizontal };
    private readonly Popup picker = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Top };
    private readonly TaskPickerPanel pickerPanel;
    private string? taskScope;
    private SettingsWindow? settingsWindow;

    internal TimerWindow(AppServices services, bool inspect, bool openSettings = false)
    {
        this.services = services;
        timer = new(services);
        Title = "ClickUp Timer"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = inspect; ShowActivated = false; Topmost = true; Width = 336; Height = 40; Left = -10000; Top = -10000;
        Appearance.Attach(this, services);
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Text = "ClickUp Timer", Visible = true };
        positioning = new(this, services, Notice);
        positioning.ShellRestarted += () => { tray.Visible = false; tray.Visible = true; };
        var grip = strip.Grip;
        grip.MouseLeftButtonDown += (_, e) =>
        {
            if (services.Settings.Mode == "Floating") positioning.DragFloating();
            else { positioning.BeginDrag(); grip.CaptureMouse(); }
            e.Handled = true;
        };
        grip.MouseMove += (_, _) => positioning.Drag();
        grip.MouseLeftButtonUp += (_, _) => { positioning.EndDrag(); grip.ReleaseMouseCapture(); };
        grip.LostMouseCapture += (_, _) => positioning.EndDrag();
        strip.Choose.Click += (_, _) => OpenPicker();
        strip.TimeButton.Click += (_, _) => OpenPicker();
        strip.State.Click += (_, _) => { OpenPicker(); focusSessionDetails = true; FocusDetails(); };
        strip.Toggle.Click += async (_, _) => { if (timer.IsRunning || timer.HasPending) await timer.Stop(); else await timer.Start(); };
        strip.FootprintChanged += () => positioning.SetFootprint(strip.Footprint);
        positioning.SetFootprint(strip.Footprint);
        Content = strip;
        var menu = new ContextMenu();
        void Item(string name, Action action) { var item = new MenuItem { Header = name }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Item("Choose task", OpenPicker); Item("Open task in ClickUp", OpenTask);
        Item("Retry ClickUp connection", () => _ = timer.Refresh());
        Item("Accept ClickUp state…", Review);
        Item("Settings", OpenSettings); Item("Reset position", positioning.Reset); Item("Save positioning diagnostics", positioning.SaveDiagnostics); Item("Exit", Close);
        pickerPanel = new TaskPickerPanel(services, () => timer.SelectedTask, async task => { await timer.Select(task); if (timer.SelectedTask?.Id == task.Id) picker.IsOpen = false; UpdateTimer(); }, OpenTask);
        var current = new StackPanel { Margin = new Thickness(12, 8, 12, 0) };
        var heading = new TextBlock { Text = "Current session", FontWeight = FontWeights.SemiBold };
        current.Children.Add(heading); current.Children.Add(details);
        var retry = new Button { Content = "Retry connection", Margin = new Thickness(0, 0, 4, 0) }; retry.Click += async (_, _) => await timer.Refresh();
        var review = new Button { Content = "Accept ClickUp state…" }; review.Click += (_, _) => Review();
        recovery.Children.Add(retry); recovery.Children.Add(review); current.Children.Add(recovery);
        var pickerContent = new StackPanel(); pickerContent.Children.Add(current); pickerContent.Children.Add(pickerPanel);
        var frame = new Border { Width = 390, BorderThickness = new Thickness(1), Child = new ScrollViewer { Content = pickerContent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 600 } };
        // Popup content has its own visual root. Share the live resource dictionary explicitly.
        frame.Resources.MergedDictionaries.Add(Resources);
        frame.SetResourceReference(Border.BackgroundProperty, "Surface"); frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        picker.Child = frame;
        picker.Closed += (_, _) => pickerPanel.Cancel();
        picker.Opened += (_, _) => { if (focusSessionDetails) FocusDetails(); };
        frame.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { picker.IsOpen = false; e.Handled = true; } };
        picker.PlacementTarget = strip;
        ContextMenu = menu;
        var trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Choose task", null, (_, _) => Dispatcher.Invoke(OpenPicker));
        trayMenu.Items.Add("Settings", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        trayMenu.Items.Add("Reset position", null, (_, _) => Dispatcher.Invoke(positioning.Reset));
        trayMenu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Close));
        tray.ContextMenuStrip = trayMenu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(OpenSettings);
        pulse.Tick += (_, _) => UpdateTimer(); pulse.Start();
        timer.Changed += UpdateTimer;
        reconcile.Tick += async (_, _) => await timer.Refresh(); reconcile.Start();
        SystemEvents.SessionSwitch += SessionSwitch;
        SystemEvents.PowerModeChanged += PowerChanged;
        services.Changed += UpdateSummary; UpdateSummary();
        Loaded += async (_, _) => { if (openSettings || !services.Settings.IsConfigured) _ = Dispatcher.BeginInvoke(OpenSettings); await timer.Refresh(); };
        Closing += async (_, e) =>
        {
            if (mayClose) return;
            e.Cancel = true;
            if (quitting) return;
            quitting = true; reconcile.Stop();
            await timer.Stop();
            if (!timer.StopRequestDurable) { quitting = false; reconcile.Start(); Notice(timer.Message ?? "Stop request could not be saved."); return; }
            mayClose = true; Close();
        };
        Closed += (_, _) => { SystemEvents.SessionSwitch -= SessionSwitch; SystemEvents.PowerModeChanged -= PowerChanged; timer.Changed -= UpdateTimer; reconcile.Stop(); picker.IsOpen = false; settingsWindow?.Close(); positioning.Dispose(); pulse.Stop(); tray.Dispose(); services.Changed -= UpdateSummary; services.Dispose(); };
    }
    private async void Review()
    {
        if (MessageBox.Show(timer.ReviewText, "Review timer recovery", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            await timer.AcceptRemote();
    }
    private void UpdateTimer()
    {
        if (displayedTask != timer.SelectedTask?.Id && !timer.IsRunning) strip.ResetDurationWidth();
        displayedTask = timer.SelectedTask?.Id;
        strip.SetState(timer.SelectedTask?.Name ?? "Choose task", timer.StatusText, timer.IsRunning, timer.HasPending, timer.Online, timer.NeedsReview,
            timer.CanRequestStop || (!timer.Busy && (timer.SelectedTask is not null || timer.HasPending || timer.IsRunning)));
        strip.SetTimes(timer.Elapsed, timer.Today);
        details.Text = $"{timer.SelectedTask?.Name ?? "No task selected"}\n{TimerStrip.StateLabel(timer.StatusText)} · {timer.Elapsed}\nToday {timer.Today}" + (timer.Message is { Length: > 0 } message ? "\n" + message : "");
        ToolTip = details.Text;
        recovery.Visibility = timer.HasPending || !timer.Online || timer.NeedsReview ? Visibility.Visible : Visibility.Collapsed;
        recovery.Children[1].Visibility = timer.HasPending || timer.NeedsReview ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateSummary()
    {
        strip.SetPresentation(services.Settings.Presentation);
        var scope = services.Settings.UserId + "/" + services.Settings.WorkspaceId;
        if (scope != taskScope) { taskScope = scope; _ = timer.Refresh(); }
        UpdateTimer();
        if (picker.IsOpen) LoadPicker();
    }
    private void SessionSwitch(object sender, SessionSwitchEventArgs e) => Dispatcher.Invoke(() =>
    {
        if (e.Reason == SessionSwitchReason.SessionLock) { timer.RequestPause(); _ = timer.Stop(); }
        else if (e.Reason == SessionSwitchReason.SessionUnlock) _ = timer.Refresh();
    });
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => Dispatcher.Invoke(() =>
    {
        if (e.Mode == PowerModes.Suspend) { timer.RequestPause(); _ = timer.Stop(); }
        else if (e.Mode == PowerModes.Resume) _ = timer.Refresh();
    });
    private void OpenPicker()
    {
        focusSessionDetails = false;
        Activate();
        var screen = Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        if (picker.Child is Border { Child: ScrollViewer scroll }) scroll.MaxHeight = Math.Max(160, screen.WorkingArea.Height / dpi - 16);
        picker.IsOpen = true; pickerPanel.Open();
    }
    private void FocusDetails() => Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
    {
        if (picker.IsOpen && focusSessionDetails) { details.BringIntoView(); Keyboard.Focus(details); }
    }));
    private void LoadPicker() => pickerPanel.Refresh();
    private void OpenTask()
    {
        if (timer.SelectedTask is not { } task) { OpenPicker(); return; }
        try { Process.Start(new ProcessStartInfo("https://app.clickup.com/t/" + Uri.EscapeDataString(task.Id)) { UseShellExecute = true }); }
        catch (Exception) { Notice("The task could not be opened in your browser."); }
    }
    private void Notice(string message) => tray.ShowBalloonTip(4000, "ClickUp Timer", message, Forms.ToolTipIcon.Info);
    internal void RequestShutdownStop() { timer.RequestPause(); _ = timer.Stop(); }
    internal void OpenSettings()
    {
        picker.IsOpen = false;
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        settingsWindow = new(services); settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show(); settingsWindow.Activate();
    }
}
