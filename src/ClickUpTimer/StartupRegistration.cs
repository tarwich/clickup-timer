using Microsoft.Win32;

namespace ClickUpTimer;

internal static class StartupRegistration
{
    private const string Path = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static string? Read() { using var key = Registry.CurrentUser.OpenSubKey(Path); return key?.GetValue("ClickUpTimer") as string; }
    internal static void Restore(string? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Path);
        if (value is null) key.DeleteValue("ClickUpTimer", false); else key.SetValue("ClickUpTimer", value);
    }
    internal static void Set(bool enabled)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Application path is unavailable.");
        if (enabled && System.IO.Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Launch the published ClickUpTimer.exe before enabling launch at sign-in.");
        Restore(enabled ? $"\"{exe}\"" : null);
    }
}
