using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClickUpTimer;

internal static class AppearanceChecks
{
    internal static void ShowFixture(bool timerFixture = false)
    {
        var thread = new Thread(() =>
        {
            var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "ClickUpTimer-preview-" + Guid.NewGuid()));
            store.Save(timerFixture ? new AppSettings { Appearance = "Dark", Mode = "Floating", UserId = "fixture", WorkspaceId = "fixture", PreferredListId = "fixture", PreferredListName = "Design", FloatingX = .7, FloatingY = .65 } : new AppSettings { Appearance = "Dark" });
            if (timerFixture) store.SaveTiming(new TimingState { UserId = "fixture", WorkspaceId = "fixture", Selected = new TaskSummary("fixture", "Design review", "open", "fixture"), CompletedMilliseconds = 3845000 });
            using var services = new AppServices(store, new CredentialStore("ClickUpTimer.Preview/" + Guid.NewGuid()));
            var application = new Application();
            Window window = timerFixture ? new TimerWindow(services, true) { Title = "ClickUp Timer - isolated timer fixture" } : new SettingsWindow(services) { Title = "ClickUp Timer - isolated UI fixture" };
            application.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    }
    internal static void Run(Action<bool, string> check, string? output = null)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var directory = Path.Combine(Path.GetTempPath(), "ClickUpTimer-appearance-" + Guid.NewGuid());
                var store = new SettingsStore(directory);
                using var services = new AppServices(store, new CredentialStore("ClickUpTimer.AppearanceTests/" + Guid.NewGuid()));
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"Version\":1,\"Mode\":\"Floating\"}");
                check(store.Load() is { Presentation: "Compact", Appearance: "System", Mode: "Floating" }, "Legacy settings gain compact/system defaults without changing placement mode");
                store.Save(new AppSettings { Presentation = "Detailed", Appearance = "Dark" });
                check(store.Load() is { Presentation: "Detailed", Appearance: "Dark" }, "Appearance and presentation survive reload");
                store.Save(new AppSettings { Presentation = "unknown", Appearance = "unknown" });
                check(store.Load() is { Presentation: "Compact", Appearance: "System" }, "Unknown presentation values recover safely");

                foreach (var (milliseconds, text) in new (long, string)[] { (-1, "00m 00s"), (999, "00m 00s"), (60000, "01m 00s"), (3599000, "59m 59s"), (3600000, "1h 00m 00s"), (86400000, "1d 0h 00m 00s"), (604800000, "1w 0d 0h 00m 00s"), (788645000, "1w 2d 3h 04m 05s"), (1209600000, "2w 0d 0h 00m 00s") })
                    check(TimingMath.Format(milliseconds) == text, "Duration boundary: " + text);

                foreach (var theme in new[] { "Light", "Dark" })
                {
                    var sheet = new StackPanel { Margin = new Thickness(16) };
                    var host = new Window { Content = sheet, Width = 500, Height = 760 };
                    Appearance.Attach(host, services); Appearance.SetPalette(host.Resources, theme);
                    foreach (var preset in new[] { "Minimal", "Compact", "Detailed" })
                    {
                        var strip = new TimerStrip(); strip.SetPresentation(preset);
                        strip.SetState("Design review with a very long task name", "Running", true, false, true, false, true);
                        strip.SetTimes("00m 00s", "2h 08m 10s");
                        var original = strip.Footprint;
                        strip.SetTimes("23h 59m 59s", "2h 08m 10s");
                        check(strip.Footprint == original, preset + " width is stable through hours and minute rollovers");
                        check(strip.Footprint.Width <= (preset == "Minimal" ? 155 : preset == "Compact" ? 260 : 330), preset + " meets compact footprint target");
                        sheet.Children.Add(new TextBlock { Text = preset, Margin = new Thickness(0, 8, 0, 4) });
                        strip.Width = strip.Footprint.Width; strip.Height = strip.Footprint.Height; strip.HorizontalAlignment = HorizontalAlignment.Left;
                        sheet.Children.Add(strip);
                        strip.SetTimes("1w 2d 3h 04m 05s", "—");
                        check(strip.Footprint.Width > original.Width, preset + " grows to preserve long durations");
                        var expanded = strip.Footprint;
                        strip.SetTimes("00m 01s", "—"); check(strip.Footprint == expanded, preset + " retains grown width until explicit reset");
                        strip.ResetDurationWidth(); strip.SetTimes("1h 04m 05s", "2h 08m 10s");
                        check(strip.Footprint == original, preset + " releases extra width on reset");
                    }
                    foreach (var state in new[] { "Stopped", "Pending stop", "Offline", "Review required", "No task selected" })
                    {
                        sheet.Children.Add(new TextBlock { Text = state, Margin = new Thickness(0, 8, 0, 4) });
                        var strip = new TimerStrip(); strip.SetPresentation("Compact");
                        strip.SetState(state == "No task selected" ? "Choose task" : "Design review", state, false, state == "Pending stop", state != "Offline", state == "Review required", state != "No task selected");
                        strip.SetTimes("04m 05s", "—"); strip.Width = strip.Footprint.Width; strip.Height = strip.Footprint.Height; strip.HorizontalAlignment = HorizontalAlignment.Left; sheet.Children.Add(strip);
                    }
                    var longStrip = new TimerStrip(); longStrip.SetState("Long session", "Running", true, false, true, false, true); longStrip.SetTimes("1w 2d 3h 04m 05s", "—");
                    sheet.Children.Add(new TextBlock { Text = "Expanded duration", Margin = new Thickness(0, 8, 0, 4) });
                    longStrip.Width = longStrip.Footprint.Width; longStrip.Height = longStrip.Footprint.Height; longStrip.HorizontalAlignment = HorizontalAlignment.Left; sheet.Children.Add(longStrip);
                    if (output is not null)
                    {
                        Render(host, output, "timer-" + theme, 500, 660);
                        foreach (var scale in new[] { 1.25, 1.5, 2.0 }) Render(host, output, $"timer-{theme}-{scale * 100}", 500, 660, scale);
                    }
                    host.Close();

                    var menu = new ContextMenu();
                    foreach (var label in new[] { "Choose task", "Open task in ClickUp", "Retry ClickUp connection", "Accept ClickUp state…", "Settings", "Reset position", "Save positioning diagnostics", "Exit" })
                        menu.Items.Add(new MenuItem { Header = label });
                    var menuHost = new Window();
                    Appearance.Attach(menuHost, services); Appearance.SetPalette(menuHost.Resources, theme);
                    menu.Resources.MergedDictionaries.Add(menuHost.Resources);
                    if (output is not null) Render(menuHost, output, "context-menu-" + theme, 230, 240, visual: menu);
                    menuHost.Close();

                    var settings = new SettingsWindow(services);
                    Appearance.SetPalette(settings.Resources, theme);
                    if (output is not null)
                    {
                        Render(settings, output, "settings-account-" + theme, 504, 541);
                        Find<TabControl>((DependencyObject)settings.Content).SelectedIndex = 1;
                        Render(settings, output, "settings-display-" + theme, 504, 541);
                    }
                    settings.Close();

                    var task = new TaskSummary("fixture", "Prepare design review", "open", "l");
                    store.Save(new AppSettings { UserId = "u", WorkspaceId = "w", PreferredListId = "l", PreferredListName = "Design", RecentTasks = [new("u", "w", task)] });
                    using var pickerServices = new AppServices(store, services.Credentials);
                    var picker = new TaskPickerPanel(pickerServices, () => task, _ => Task.CompletedTask, () => { });
                    var pickerHost = new Window { Content = picker };
                    Appearance.Attach(pickerHost, pickerServices); Appearance.SetPalette(pickerHost.Resources, theme);
                    picker.Refresh();
                    if (output is not null) Render(pickerHost, output, "picker-" + theme, 390, 420);
                    picker.Cancel(); pickerHost.Close();
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("Appearance verification failed", failure);
    }
    private static T Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) return value;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            try { return Find<T>(child); } catch (InvalidOperationException) { }
        throw new InvalidOperationException("Control not found: " + typeof(T));
    }
    private static void Render(Window window, string output, string name, double width, double height, double scale = 1, FrameworkElement? visual = null)
    {
        // Render real controls with inherited Window resources, without showing or loading an account.
        window.Width = width; window.Height = height;
        window.Measure(new Size(width, height)); window.Arrange(new Rect(0, 0, width, height)); window.UpdateLayout();
        var content = visual ?? (FrameworkElement)window.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen()) context.DrawRectangle((Brush)window.Resources["Surface"], null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(drawing); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(output); using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
    }
}
