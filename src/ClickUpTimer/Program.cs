using System.IO;
using System.Text.Json;
using System.Windows;

namespace ClickUpTimer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--probe")
        {
            var result = Task.Run(TaskbarScanner.Scan).GetAwaiter().GetResult();
            File.WriteAllText(args[1], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        using var mutex = new Mutex(true, "Local\\ClickUpTimer.Phase1", out var first);
        using var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ClickUpTimer.ShowSettings");
        if (!first) { showSettings.Set(); return; }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new TimerWindow(new AppServices(), args.Contains("--inspect"), args.Contains("--settings"));
        app.SessionEnding += (_, _) => window.RequestShutdownStop();
        var registration = ThreadPool.RegisterWaitForSingleObject(showSettings, (_, _) => app.Dispatcher.BeginInvoke(window.OpenSettings), null, Timeout.Infinite, false);
        try { app.Run(window); } finally { registration.Unregister(null); }
    }
}
