using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClickUpTimer;

internal static class Appearance
{
    internal static string Normalize(string? value) => DisplayPreferences.NormalizeAppearance(value);
    internal static void Attach(Window window, AppServices services)
    {
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ClickUpTimer;component/Theme.xaml", UriKind.Relative) });
        void Refresh()
        {
            SetPalette(window.Resources, services.Settings.Appearance);
            // WPF creates tooltips and menu popups outside the owning Window's visual tree.
            if (Application.Current is { } application && application.Dispatcher == window.Dispatcher)
            {
                if (!application.Resources.Contains("TimerThemeInstalled"))
                {
                    application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ClickUpTimer;component/Theme.xaml", UriKind.Relative) });
                    application.Resources["TimerThemeInstalled"] = true;
                }
                SetPalette(application.Resources, services.Settings.Appearance);
            }
        }
        void SystemChanged(object sender, UserPreferenceChangedEventArgs e) => window.Dispatcher.BeginInvoke(Refresh);
        Refresh();
        window.SetResourceReference(Control.BackgroundProperty, "Surface");
        window.SetResourceReference(Control.ForegroundProperty, "Text");
        window.FontFamily = new FontFamily("Segoe UI"); window.FontSize = 12;
        services.Changed += Refresh;
        SystemEvents.UserPreferenceChanged += SystemChanged;
        window.Closed += (_, _) => { services.Changed -= Refresh; SystemEvents.UserPreferenceChanged -= SystemChanged; };
    }
    internal static void SetPalette(ResourceDictionary resources, string preference)
    {
        var dark = preference == "Dark";
        if (Normalize(preference) == "System")
        {
            try { dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
            catch { dark = false; }
        }
        var colors = dark
            ? new[] { "#202124", "#292B2F", "#F0F0F2", "#B7BAC2", "#45474E", "#34373E", "#A8C7FA", "#423F32", "#F4C477", "#FFB4AB" }
            : new[] { "#F7F7F8", "#FFFFFF", "#24262B", "#5D626D", "#D4D6DC", "#E8EBF0", "#245FA8", "#FFF0D5", "#885400", "#B42318" };
        var names = new[] { "Surface", "Input", "Text", "Muted", "Line", "Hover", "Accent", "WarningSurface", "Warning", "Error" };
        for (var i = 0; i < names.Length; i++)
        {
            Brush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            if (SystemParameters.HighContrast)
                brush = names[i] switch { "Surface" or "Input" or "WarningSurface" or "Hover" => SystemColors.WindowBrush, _ => SystemColors.WindowTextBrush };
            resources[names[i]] = brush;
        }
    }
    internal static void Color(FrameworkElement element, DependencyProperty property, string key) => element.SetResourceReference(property, key);
}
