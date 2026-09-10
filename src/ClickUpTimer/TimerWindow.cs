using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ClickUpTimer;

internal sealed class TimerWindow : Window
{
    private readonly AppServices services;
    private readonly TimerCoordinator timer = new();
    private readonly WindowPositioner positioning;
    private readonly Forms.NotifyIcon tray;
    private readonly DispatcherTimer pulse = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly TextBlock elapsed = new() { Text = "00:00:00", FontFamily = new FontFamily("Consolas"), FontSize = 23, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { Text = "Demo · stopped", FontSize = 11 };
    private readonly Button toggle = new() { Content = "▶", ToolTip = "Start demo timer (no ClickUp logging)", Width = 34 };
    private SettingsWindow? settingsWindow;

    internal TimerWindow(AppServices services, bool inspect, bool openSettings = false)
    {
        this.services = services;
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
        labels.Children.Add(new TextBlock { Text = "CLICKUP TIMER", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(155, 171, 190)) });
        labels.Children.Add(status); Add(labels, 1); Add(elapsed, 2);
        StyleButton(toggle); toggle.Click += (_, _) => { timer.Toggle(); UpdateTimer(); }; Add(toggle, 3);
        var options = new Button { Content = "⚙", ToolTip = "Settings", Width = 28 }; StyleButton(options); options.Click += (_, _) => OpenSettings(); Add(options, 4);
        Content = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(68, 91, 102)), BorderThickness = new Thickness(1), Child = panel };
        var menu = new ContextMenu();
        void Item(string name, Action action) { var item = new MenuItem { Header = name }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Item("Settings", OpenSettings); Item("Reset position", positioning.Reset); Item("Save positioning diagnostics", positioning.SaveDiagnostics); Item("Exit", Close);
        ContextMenu = menu;
        var trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Settings", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        trayMenu.Items.Add("Reset position", null, (_, _) => Dispatcher.Invoke(positioning.Reset));
        trayMenu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Close));
        tray.ContextMenuStrip = trayMenu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(OpenSettings);
        pulse.Tick += (_, _) => elapsed.Text = timer.Elapsed; pulse.Start();
        services.Changed += UpdateSummary; UpdateSummary();
        Loaded += (_, _) => { if (openSettings || !services.Settings.IsConfigured) Dispatcher.BeginInvoke(OpenSettings); };
        Closed += (_, _) => { settingsWindow?.Close(); positioning.Dispose(); pulse.Stop(); tray.Dispose(); services.Changed -= UpdateSummary; services.Dispose(); };
    }
    private static void StyleButton(Button b)
    {
        b.Height = 30; b.FontSize = 17; b.Cursor = Cursors.Hand; b.Background = new SolidColorBrush(Color.FromRgb(43, 54, 65));
        b.Foreground = Brushes.White; b.BorderThickness = new Thickness(0); b.VerticalAlignment = VerticalAlignment.Center;
    }
    private void UpdateTimer()
    {
        status.Text = timer.IsRunning ? "Demo · RUNNING" : "Demo · stopped";
        status.Foreground = timer.IsRunning ? new SolidColorBrush(Color.FromRgb(102, 235, 181)) : Brushes.LightGray;
        toggle.Content = timer.IsRunning ? "■" : "▶";
    }
    private void UpdateSummary() => ToolTip = services.Settings.IsConfigured
        ? $"Preferred list: {services.Settings.PreferredListName}\n{services.CacheNotice ?? "Demo timer — time is not logged yet."}"
        : "Open Settings to connect your ClickUp account. Demo timer — time is not logged yet.";
    private void Notice(string message) => tray.ShowBalloonTip(4000, "ClickUp Timer", message, Forms.ToolTipIcon.Info);
    internal void OpenSettings()
    {
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        settingsWindow = new(services); settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show(); settingsWindow.Activate();
    }
}
