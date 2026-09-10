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
    private readonly TextBlock today = new() { FontSize = 10, Foreground = Brushes.LightGray };
    private readonly DispatcherTimer reconcile = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool quitting, mayClose;
    private readonly WindowPositioner positioning;
    private readonly Forms.NotifyIcon tray;
    private readonly DispatcherTimer pulse = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly TextBlock elapsed = new() { Text = "00:00:00", FontFamily = new FontFamily("Consolas"), FontSize = 23, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { Text = "Connecting…", FontSize = 11 };
    private readonly Button toggle = new() { Content = "▶", ToolTip = "Start logging to ClickUp", Width = 34 };
    private readonly TextBlock taskName = new() { Text = "Choose task ▴", FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
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
        Background = new SolidColorBrush(Color.FromRgb(27, 32, 39)); Foreground = Brushes.White;
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Text = "ClickUp Timer", Visible = true };
        positioning = new(this, services, Notice);
        positioning.ShellRestarted += () => { tray.Visible = false; tray.Visible = true; };
        var panel = new Grid { Margin = new Thickness(8, 1, 6, 1) };
        foreach (var width in new[] { 18.0, 105, 108, 42, 32 }) panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        void Add(UIElement element, int col) { Grid.SetColumn(element, col); panel.Children.Add(element); }
        var grip = new TextBlock { Text = "⠿", FontSize = 20, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.SizeAll, ToolTip = "Drag to move timer" };
        grip.MouseLeftButtonDown += (_, e) =>
        {
            if (services.Settings.Mode == "Floating") positioning.DragFloating();
            else { positioning.BeginDrag(); grip.CaptureMouse(); }
            e.Handled = true;
        };
        grip.MouseMove += (_, _) => positioning.Drag();
        grip.MouseLeftButtonUp += (_, _) => { positioning.EndDrag(); grip.ReleaseMouseCapture(); };
        grip.LostMouseCapture += (_, _) => positioning.EndDrag(); Add(grip, 0);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(taskName);
        labels.Children.Add(status);
        var choose = new Button { Content = labels, Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, Cursor = Cursors.Hand, ToolTip = "Choose a task" };
        choose.Click += (_, _) => OpenPicker(); Add(choose, 1);
        var times = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        elapsed.FontSize = 21;
        times.Children.Add(elapsed);
        times.Children.Add(today);
        Add(times, 2);
        StyleButton(toggle); toggle.Click += async (_, _) => { if (timer.IsRunning || timer.HasPending) await timer.Stop(); else await timer.Start(); }; Add(toggle, 3);
        var options = new Button { Content = "⚙", ToolTip = "Settings", Width = 28 }; StyleButton(options); options.Click += (_, _) => OpenSettings(); Add(options, 4);
        Content = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(68, 91, 102)), BorderThickness = new Thickness(1), Child = panel };
        var menu = new ContextMenu();
        void Item(string name, Action action) { var item = new MenuItem { Header = name }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Item("Choose task", OpenPicker); Item("Open task in ClickUp", OpenTask);
        Item("Retry ClickUp connection", () => _ = timer.Refresh());
        Item("Accept ClickUp state…", async () =>
        {
            if (MessageBox.Show(timer.ReviewText, "Review timer recovery", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                await timer.AcceptRemote();
        });
        Item("Settings", OpenSettings); Item("Reset position", positioning.Reset); Item("Save positioning diagnostics", positioning.SaveDiagnostics); Item("Exit", Close);
        pickerPanel = new TaskPickerPanel(services, () => timer.SelectedTask, async task => { await timer.Select(task); if (timer.SelectedTask?.Id == task.Id) picker.IsOpen = false; UpdateTimer(); }, OpenTask);
        picker.Child = new Border { Width = 440, Background = Brushes.White, BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1), Child = pickerPanel };
        picker.Closed += (_, _) => pickerPanel.Cancel();
        picker.PlacementTarget = choose;
        ContextMenu = menu;
        var trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Settings", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        trayMenu.Items.Add("Reset position", null, (_, _) => Dispatcher.Invoke(positioning.Reset));
        trayMenu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Close));
        tray.ContextMenuStrip = trayMenu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(OpenSettings);
        pulse.Tick += (_, _) => { elapsed.Text = timer.Elapsed; today.Text = "Today: " + timer.Today; }; pulse.Start();
        timer.Changed += UpdateTimer;
        reconcile.Tick += async (_, _) => await timer.Refresh(); reconcile.Start();
        SystemEvents.SessionSwitch += SessionSwitch;
        SystemEvents.PowerModeChanged += PowerChanged;
        services.Changed += UpdateSummary; UpdateSummary();
        Loaded += async (_, _) => { if (openSettings || !services.Settings.IsConfigured) _ = Dispatcher.BeginInvoke(OpenSettings); if (services.Settings.IsConfigured) _ = services.RefreshCache(); await timer.Refresh(); };
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
    private static void StyleButton(Button b)
    {
        b.Height = 30; b.FontSize = 17; b.Cursor = Cursors.Hand; b.Background = new SolidColorBrush(Color.FromRgb(43, 54, 65));
        b.Foreground = Brushes.White; b.BorderThickness = new Thickness(0); b.VerticalAlignment = VerticalAlignment.Center;
    }
    private void UpdateTimer()
    {
        taskName.Text = timer.SelectedTask?.Name ?? "Choose task ▴";
        taskName.ToolTip = timer.SelectedTask?.Name ?? "Choose a task from your preferred list";
        status.Text = timer.StatusText;
        status.FontSize = 10;
        status.Foreground = timer.HasPending || !timer.Online || timer.NeedsReview ? Brushes.Orange : timer.IsRunning ? new SolidColorBrush(Color.FromRgb(102, 235, 181)) : Brushes.LightGray;
        toggle.Content = timer.IsRunning || timer.HasPending ? "■" : "▶";
        toggle.IsEnabled = timer.CanRequestStop || (!timer.Busy && (timer.SelectedTask is not null || timer.HasPending || timer.IsRunning));
        toggle.ToolTip = timer.IsRunning || timer.HasPending ? "Stop logging to ClickUp" : "Start logging to ClickUp";
        ToolTip = timer.Message ?? "Time is logged directly to ClickUp. Today is your personal task total in the Windows local timezone.";
        today.Text = "Today: " + timer.Today;
        elapsed.Text = timer.Elapsed;
    }
    private void UpdateSummary()
    {
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
    private void OpenPicker() { Activate(); picker.IsOpen = true; pickerPanel.Open(); }
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
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        settingsWindow = new(services); settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show(); settingsWindow.Activate();
    }
}
