using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClickUpTimer;

internal sealed class TaskPickerPanel : StackPanel
{
    private readonly AppServices services;
    private readonly Func<TaskSummary?> active;
    private readonly Func<TaskSummary, Task> select;
    private readonly Func<ClickUpClient> createClient;
    private readonly TextBox query = new() { Height = 32, Padding = new Thickness(6), MaxLength = 500 };
    private readonly ListBox results = new() { DisplayMemberPath = "Label", MaxHeight = 250, MinHeight = 65 };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly Button search = new() { Content = "Search workspace", Height = 30 };
    private readonly Button create = new() { Height = 32 };
    private readonly List<TaskSummary> workspace = [];
    private CancellationTokenSource? request;
    private string? scope;
    private string searchStatus = "";
    private bool creating;
    internal TaskPickerPanel(AppServices services, Func<TaskSummary?> active, Func<TaskSummary, Task> select, Action openTask, Func<ClickUpClient>? createClient = null)
    {
        this.services = services; this.active = active; this.select = select;
        this.createClient = createClient ?? (() => new(services.Credentials.Read() ?? throw new ClickUpException("Connect an API key in Settings first.")));
        NameScope.SetNameScope(this, new NameScope());
        RegisterName("SearchText", query); RegisterName("CreateTask", create); RegisterName("Notice", notice);
        Margin = new Thickness(14); SetValue(TextBlock.ForegroundProperty, Brushes.Black);
        Children.Add(new TextBlock { Text = "Choose a task", FontSize = 18, FontWeight = FontWeights.SemiBold });
        Children.Add(new TextBlock { Text = "Search names or IDs in your preferred list and recent tasks", Margin = new Thickness(0, 6, 0, 4), TextWrapping = TextWrapping.Wrap });
        Children.Add(query); Children.Add(notice); Children.Add(results);
        query.TextChanged += (_, _) => Refresh();
        results.MouseDoubleClick += (_, _) => Choose();
        results.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Choose(); e.Handled = true; } };
        var use = new Button { Content = "Use selected task", Height = 30, Margin = new Thickness(0, 6, 0, 6) };
        use.Click += (_, _) => Choose(); Children.Add(use);
        search.Click += async (_, _) => await SearchWorkspace(); Children.Add(search);
        create.Margin = new Thickness(0, 6, 0, 6);
        create.Click += async (_, _) => await Create(); Children.Add(create);
        var open = new Button { Content = "Open current task in ClickUp", Height = 28 };
        open.Click += (_, _) => openTask(); Children.Add(open);
        var refresh = new Button { Content = "Refresh preferred list", Height = 28, Margin = new Thickness(0, 6, 0, 0) };
        refresh.Click += async (_, _) => { refresh.IsEnabled = false; await services.RefreshCache(); refresh.IsEnabled = true; Refresh(); };
        Children.Add(refresh);
    }
    private async void Choose()
    {
        if (creating || results.SelectedItem is not TaskRow row) return;
        try { services.Save(services.Settings with { RecentTasks = TaskCatalog.Remember(services.Settings, row.Task) }); await select(row.Task); }
        catch (Exception) { notice.Text = "Could not save recent tasks. Try again."; }
    }
    internal void Open()
    {
        Cancel(); workspace.Clear(); searchStatus = ""; Refresh();
        Dispatcher.BeginInvoke(() => query.Focus());
    }
    internal void Refresh()
    {
        var settings = services.Settings;
        var nextScope = $"{settings.UserId}/{settings.WorkspaceId}/{settings.PreferredListId}";
        if (scope != nextScope) { Cancel(); workspace.Clear(); scope = nextScope; searchStatus = ""; }
        var cache = settings.IsConfigured ? services.Store.LoadCache(settings.UserId!, settings.WorkspaceId!, settings.PreferredListId!) : null;
        var selected = (results.SelectedItem as TaskRow)?.Task.Id;
        var rows = TaskCatalog.Filter(settings, cache?.Tasks ?? [], workspace, query.Text, active());
        results.ItemsSource = rows;
        results.SelectedItem = rows.FirstOrDefault(r => r.Task.Id == selected);
        notice.Text = !settings.IsConfigured ? "Choose a preferred list in Settings first."
            : $"{rows.Count} shown · {settings.PreferredListName}\n" + (searchStatus.Length > 0 ? searchStatus : cache is null ? "Preferred list not loaded yet. Refresh to load it." : "Preferred list and recent tasks searched first.");
        if (services.CacheNotice?.Contains("could not") == true) notice.Text += "\nConnection failed; cached results may be out of date.";
        create.Content = "Create task in " + (settings.PreferredListName ?? "preferred list");
        create.IsEnabled = settings.IsConfigured && !creating && !string.IsNullOrWhiteSpace(query.Text);
        search.IsEnabled = settings.IsConfigured && request is null && !creating;
    }
    private ClickUpClient Client() => createClient();
    private async Task SearchWorkspace()
    {
        Cancel(); var pending = new CancellationTokenSource(); request = pending;
        var settings = services.Settings; workspace.Clear(); searchStatus = "Searching workspace — results are incomplete…"; Refresh();
        try
        {
            using var client = Client();
            for (var page = 0; ; page++)
            {
                var tasks = await client.WorkspacePage(settings.WorkspaceId!, page, pending.Token);
                if (request != pending) return;
                workspace.AddRange(tasks);
                searchStatus = $"Searching workspace — {workspace.Count} tasks checked; results are incomplete…"; Refresh();
                if (tasks.Count == 0) break;
            }
            searchStatus = "Workspace search complete.";
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { searchStatus = "Workspace search incomplete. " + Error(ex); }
        finally { if (request == pending) { request = null; pending.Dispose(); Refresh(); } }
    }
    private async Task Create()
    {
        if (creating || string.IsNullOrWhiteSpace(query.Text)) return;
        var settings = services.Settings; var title = query.Text.Trim();
        creating = true; query.IsEnabled = false; Refresh(); notice.Text = "Creating task…";
        TaskSummary? created = null;
        try
        {
            using var client = Client();
            created = await client.CreateTask(settings.PreferredListId!, title, CancellationToken.None);
            if (services.Settings.UserId != settings.UserId || services.Settings.WorkspaceId != settings.WorkspaceId || services.Settings.PreferredListId != settings.PreferredListId)
            { notice.Text = "Task created in the original preferred list. Your setup changed; select it from that list."; return; }
            services.Save(services.Settings with { RecentTasks = TaskCatalog.Remember(services.Settings, created) });
            await select(created); query.Clear(); _ = services.RefreshCache();
        }
        catch (Exception ex)
        {
            notice.Text = created is not null ? "Task was created, but recent tasks could not be saved. Refresh before creating another."
                : Error(ex) + " Your title is retained. If the connection was lost, refresh and check for the task before retrying.";
        }
        finally { creating = false; query.IsEnabled = true; create.IsEnabled = !string.IsNullOrWhiteSpace(query.Text); search.IsEnabled = request is null; }
    }
    private static string Error(Exception ex) => ex is ClickUpException ? ex.Message : "The operation could not be completed. Try again.";
    internal void Cancel() { request?.Cancel(); request?.Dispose(); request = null; }
}
