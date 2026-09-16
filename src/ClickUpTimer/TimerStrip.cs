using System.Windows.Documents;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClickUpTimer;

internal sealed class TimerStrip : Border
{
    internal readonly Button Choose = new() { BorderThickness = new Thickness(0), Padding = new Thickness(2, 0, 2, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    internal readonly Button TimeButton = new() { BorderThickness = new Thickness(0), Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Right };
    internal readonly Button Toggle = new() { Width = 26, Height = 26, Padding = new Thickness(6), BorderThickness = new Thickness(0) };
    internal readonly Button State = new() { Width = 22, Height = 26, Padding = new Thickness(4), BorderThickness = new Thickness(0) };
    internal readonly FrameworkElement Grip;
    private readonly Grid grid = new();
    private readonly TextBlock task = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
    private readonly TextBlock stateText = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock elapsed = new() { FontSize = 14, FontFamily = new FontFamily("Segoe UI"), TextAlignment = TextAlignment.Right };
    private readonly TextBlock today = new() { FontSize = 11, TextAlignment = TextAlignment.Right };
    private readonly StackPanel labels = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel times = new() { VerticalAlignment = VerticalAlignment.Center };
    private string preset = "";
    private double timeWidth;
    private string? measuredShape, renderedState;
    internal Size Footprint { get; private set; }
    internal event Action? FootprintChanged;
    internal static string Normalize(string? value) => DisplayPreferences.NormalizePresentation(value);
    internal static string StateLabel(string value) => value.TrimStart('⚠', '!', '…', '▶', '■', ' ');
    internal TimerStrip()
    {
        SnapsToDevicePixels = true; UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, "Surface"); SetResourceReference(BorderBrushProperty, "Line");
        BorderThickness = new Thickness(1); Padding = new Thickness(3, 0, 3, 0);
        Typography.SetNumeralAlignment(elapsed, FontNumeralAlignment.Tabular);
        Typography.SetNumeralAlignment(today, FontNumeralAlignment.Tabular);
        Appearance.Color(today, TextBlock.ForegroundProperty, "Muted"); Appearance.Color(stateText, TextBlock.ForegroundProperty, "Muted");
        Grip = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 2 1 L 2 3 M 2 6 L 2 8 M 2 11 L 2 13 M 6 1 L 6 3 M 6 6 L 6 8 M 6 11 L 6 13"), StrokeThickness = 1.4, Width = 10, Height = 16, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.SizeAll, ToolTip = "Drag to move timer" };
        Grip.SetResourceReference(Shape.StrokeProperty, "Muted");
        labels.Children.Add(task); labels.Children.Add(stateText); Choose.Content = labels;
        times.Children.Add(elapsed); times.Children.Add(today); TimeButton.Content = times;
        Child = grid; SetPresentation("Compact");
    }
    internal void SetPresentation(string value)
    {
        value = Normalize(value); if (preset == value) return;
        preset = value; ResetDurationWidth();
        grid.Children.Clear(); grid.ColumnDefinitions.Clear();
        void Add(UIElement element, double width)
        {
            Grid.SetColumn(element, grid.ColumnDefinitions.Count);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) }); grid.Children.Add(element);
        }
        Add(Grip, 12);
        if (preset != "Minimal") Add(Choose, preset == "Detailed" ? 130 : 84);
        Add(TimeButton, 110); Add(State, 22); Add(Toggle, 26);
        stateText.Visibility = today.Visibility = preset == "Detailed" ? Visibility.Visible : Visibility.Collapsed;
        TimeButton.ToolTip = "Choose a task and view session details";
        AutomationProperties.SetName(TimeButton, "Choose task and view session details");
        SetTimes(elapsed.Text.Length == 0 ? "00m 00s" : elapsed.Text, "—");
    }
    internal void ResetDurationWidth() { timeWidth = 0; measuredShape = null; }
    internal void SetTimes(string duration, string total)
    {
        elapsed.Text = duration; today.Text = "Today " + total;
        static string Shape(string value) => new(value.Select(c => char.IsDigit(c) ? '8' : c).ToArray());
        var durationShape = Shape(duration);
        var totalShape = preset == "Detailed" ? Shape(today.Text) : "";
        var shape = durationShape + "/" + totalShape;
        if (shape == measuredShape) return;
        measuredShape = shape;
        double Measure(string text, double fontSize) => new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), fontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
        // Tabular digits: measure all-8s as a conservative bound even before font shaping.
        var width = Math.Ceiling(Math.Max(Measure("88h 88m 88s", 14), Measure(durationShape, 14))) + 4;
        if (preset == "Detailed") width = Math.Max(width, Math.Ceiling(Measure(totalShape, 11)) + 4);
        timeWidth = Math.Max(timeWidth, width);
        grid.ColumnDefinitions[preset == "Minimal" ? 1 : 2].Width = new GridLength(timeWidth);
        var next = new Size(8 + 12 + (preset == "Minimal" ? 0 : preset == "Detailed" ? 130 : 84) + timeWidth + 22 + 26, preset == "Detailed" ? 40 : 32);
        if (next == Footprint) return;
        Footprint = next; FootprintChanged?.Invoke();
    }
    internal void SetState(string name, string status, bool running, bool pending, bool online, bool review, bool enabled, bool localOnly = false)
    {
        status = StateLabel(status);
        var key = $"{name}/{status}/{running}/{pending}/{online}/{review}/{enabled}/{localOnly}";
        if (renderedState == key) return;
        renderedState = key;
        task.Text = name; Choose.ToolTip = name;
        AutomationProperties.SetName(Choose, "Choose task: " + name);
        stateText.Text = status;
        var kind = review ? "Review" : pending ? "Pending" : status.Contains("Connecting", StringComparison.OrdinalIgnoreCase) ? "Connecting" : !online ? "Offline" : running ? "Running" : "Stopped";
        State.Content = Icon(kind); State.ToolTip = status + " — view details";
        AutomationProperties.SetName(State, status + ". View details and recovery actions");
        Toggle.Content = Icon(running || pending ? "Stop" : "Start");
        Toggle.ToolTip = localOnly ? running ? "Stop local timer" : "Start local timer" : running || pending ? "Stop logging to ClickUp" : "Start logging to ClickUp";
        AutomationProperties.SetName(Toggle, (string)Toggle.ToolTip); Toggle.IsEnabled = enabled;
        Appearance.Color(stateText, TextBlock.ForegroundProperty, kind is "Review" or "Pending" or "Offline" ? "Warning" : "Muted");
    }
    private static System.Windows.Shapes.Path Icon(string kind)
    {
        var path = new System.Windows.Shapes.Path { Width = 13, Height = 13, Stretch = Stretch.Uniform, StrokeThickness = 1.3 };
        path.Data = Geometry.Parse(kind switch
        {
            "Start" or "Running" => "M 3 1 L 11 7 L 3 13 Z",
            "Stop" or "Stopped" => "M 2 2 L 12 2 L 12 12 L 2 12 Z",
            "Pending" or "Connecting" => "M 2 1 L 12 1 M 2 13 L 12 13 M 3 1 L 3 4 L 10 10 L 10 13 M 11 1 L 11 4 L 4 10 L 4 13",
            "Offline" => "M 1 1 L 13 13 M 1 5 Q 7 -1 13 5 M 4 8 Q 7 5 10 8 M 6 11 L 8 11",
            _ => "M 7 1 L 13 12 L 1 12 Z M 7 4 L 7 8 M 7 9 L 7 10"
        });
        path.SetResourceReference(Shape.StrokeProperty, kind is "Review" or "Pending" or "Offline" ? "Warning" : "Text");
        if (kind is "Start" or "Stop") path.SetResourceReference(Shape.FillProperty, "Text");
        return path;
    }
}
