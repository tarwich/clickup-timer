namespace ClickUpTimer;

internal sealed class AppServices : IDisposable
{
    internal SettingsStore Store { get; }
    internal CredentialStore Credentials { get; }
    internal AppSettings Settings { get; private set; }
    internal event Action? Changed;
    internal Func<string?>? ValidateAccountChange { get; set; }
    private CancellationTokenSource? refresh;
    internal string? CacheNotice { get; private set; }
    internal AppServices(SettingsStore? store = null, CredentialStore? credentials = null)
    { Store = store ?? new(); Credentials = credentials ?? new(); Settings = Store.Load(); }

    internal void Save(AppSettings next, string? replacementKey = null)
    {
        var oldKey = replacementKey is not null ? Credentials.Read() : null;
        if (next.UserId != Settings.UserId || next.WorkspaceId != Settings.WorkspaceId || (replacementKey is not null && replacementKey != oldKey))
        {
            var problem = ValidateAccountChange?.Invoke();
            if (problem is not null) throw new ClickUpException(problem);
        }
        var oldStartup = StartupRegistration.Read();
        try
        {
            if (replacementKey is not null) Credentials.Write(replacementKey);
            if (next.LaunchAtSignIn != Settings.LaunchAtSignIn) StartupRegistration.Set(next.LaunchAtSignIn);
            Store.Save(next);
        }
        catch
        {
            // Restore the previously configured account if a later persistence step failed.
            if (replacementKey is not null) { if (oldKey is null) Credentials.Delete(); else Credentials.Write(oldKey); }
            StartupRegistration.Restore(oldStartup);
            throw;
        }
        Settings = next;
        Changed?.Invoke();
    }
    internal void SavePosition(AppSettings next)
    {
        Store.Save(next); Settings = next;
    }
    internal async Task RefreshCache()
    {
        refresh?.Cancel(); refresh?.Dispose(); refresh = new();
        var cancellation = refresh.Token;
        var config = Settings;
        if (!config.IsConfigured) return;
        try
        {
            var key = Credentials.Read();
            if (key is null) return;
            using var client = new ClickUpClient(key);
            var tasks = await client.Tasks(config.PreferredListId!, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (Settings.UserId != config.UserId || Settings.WorkspaceId != config.WorkspaceId || Settings.PreferredListId != config.PreferredListId) return;
            Store.SaveCache(new(config.UserId!, config.WorkspaceId!, config.PreferredListId!, DateTimeOffset.Now, tasks));
            CacheNotice = $"{tasks.Count} tasks cached for your preferred list.";
        }
        catch (OperationCanceledException) { return; }
        catch (Exception) { CacheNotice = "Settings saved. Task cache could not refresh; it can be retried from Settings."; }
        Changed?.Invoke();
    }
    public void Dispose() { refresh?.Cancel(); refresh?.Dispose(); }
}
