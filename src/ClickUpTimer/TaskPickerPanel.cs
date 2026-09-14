using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ClickUpTimer;

internal sealed class TaskPickerPanel : StackPanel
{
    private readonly AppServices services;
    private readonly Func<TaskSummary?> active;
    private readonly Func<TaskSummary, Task> select;
    private readonly Func<ClickUpClient> createClient;
    private readonly IClickUpSearch search;
    private readonly InteractiveSearch interaction = new();
    private readonly TextBox query = new() { Height = 28, Padding = new Thickness(6, 3, 6, 3), MaxLength = 500 };
    private readonly ListBox results = new() { MaxHeight = 250, MinHeight = 65 };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly ToggleButton currentList = new() { Height = 28, Margin = new Thickness(0, 6, 0, 2), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 3, 8, 3) };
    private readonly Button create = new() { Height = 28 };
    private readonly HashSet<string> remoteMatches = [];
    private string? scope;
    private string searchStatus = "";
    private bool creating, updating;
    internal TaskPickerPanel(AppServices services, Func<TaskSummary?> active, Func<TaskSummary, Task> select, Action openTask,
        Func<ClickUpClient>? createClient = null, IClickUpSearch? search = null)
    {
        this.services = services; this.active = active; this.select = select;
        this.createClient = createClient ?? services.CreateClient;
        this.search = search ?? services.Search;
        NameScope.SetNameScope(this, new NameScope());
        RegisterName("SearchText", query); RegisterName("CreateTask", create); RegisterName("Notice", notice);
        RegisterName("CurrentList", currentList); RegisterName("Results", results);
        Margin = new Thickness(12);
        Children.Add(new TextBlock { Text = "Choose a task", FontSize = 14, FontWeight = FontWeights.SemiBold });
        Children.Add(currentList); Children.Add(query); Children.Add(notice); Children.Add(results);
        currentList.SetResourceReference(StyleProperty, "SearchScopeToggle");
        currentList.ToolTip = "Switch between your current list and the entire workspace. This choice is saved.";
        currentList.IsChecked = services.Settings.SearchCurrentList;
        currentList.Click += async (_, _) =>
        {
            try { services.Save(services.Settings with { SearchCurrentList = currentList.IsChecked == true }); }
            catch (Exception) { currentList.IsChecked = services.Settings.SearchCurrentList; notice.Text = "Search scope could not be saved."; return; }
            await SearchChanged();
        };
        query.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && results.Items.Count > 0) { results.SelectedIndex = Math.Max(0, results.SelectedIndex); results.Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter) { if (results.SelectedIndex < 0 && results.Items.Count > 0) results.SelectedIndex = 0; Choose(); e.Handled = true; }
        };
        var row = new FrameworkElementFactory(typeof(TextBlock));
        row.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Label"));
        row.SetBinding(TextBlock.ToolTipProperty, new System.Windows.Data.Binding("Label"));
        row.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        results.ItemTemplate = new DataTemplate { VisualTree = row };
        VirtualizingPanel.SetIsVirtualizing(results, true);
        query.TextChanged += async (_, _) => { if (!updating) await SearchChanged(); };
        results.MouseDoubleClick += (_, _) => Choose();
        results.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Choose(); e.Handled = true; } };
        var use = new Button { Content = "Use selected task", Height = 28, Margin = new Thickness(0, 6, 0, 6) };
        use.Click += (_, _) => Choose(); Children.Add(use);
        create.Margin = new Thickness(0, 6, 0, 6);
        create.Click += async (_, _) => await Create(); Children.Add(create);
        var open = new Button { Content = "Open current task in ClickUp", HorizontalAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0) };
        open.Click += (_, _) => openTask(); Children.Add(open);
        Refresh();
    }
    private async void Choose()
    {
        if (creating || results.SelectedItem is not TaskRow row) return;
        interaction.Cancel();
        var settings = services.Settings;
        try
        {
            using var client = createClient();
            var task = await client.TaskById(row.Task.Id, default);
            if (settings.UserId != services.Settings.UserId || settings.WorkspaceId != services.Settings.WorkspaceId) return;
            services.SearchCache.Merge(settings.UserId!, settings.WorkspaceId!, [new(task.Id, task.Name, "task", task.ListId, task.Status, task.StatusType)]);
            services.Save(services.Settings with { RecentTasks = TaskCatalog.Remember(services.Settings, task) }); await select(task);
        }
        catch (Exception ex) { notice.Text = Error(ex); }
    }
    internal void Open()
    {
        interaction.Open(); remoteMatches.Clear(); searchStatus = "";
        updating = true; query.Clear(); updating = false;
        Refresh(); Dispatcher.BeginInvoke(() => query.Focus());
    }
    internal void Refresh()
    {
        var settings = services.Settings;
        var nextScope = $"{settings.UserId}/{settings.WorkspaceId}/{settings.PreferredListId}";
        if (scope != nextScope) { interaction.Cancel(); remoteMatches.Clear(); scope = nextScope; searchStatus = ""; }
        currentList.IsChecked = settings.SearchCurrentList;
        currentList.Content = settings.SearchCurrentList ? "Current list" : "Entire workspace";
        var items = settings.IsConfigured ? services.SearchCache.Read(settings.UserId!, settings.WorkspaceId!).Where(i => i.Type == "task").Select(i => i.Task).ToList() : [];
        var legacy = settings.IsConfigured ? services.Store.LoadCache(settings.UserId!, settings.WorkspaceId!, settings.PreferredListId!)?.Tasks ?? [] : [];
        var all = items.Concat(legacy).DistinctBy(t => t.Id).ToList();
        var preferred = all.Where(t => t.ListId == settings.PreferredListId).ToList();
        var filteredSettings = settings.SearchCurrentList ? settings with { RecentTasks = settings.RecentTasks.Where(t => t.Task.ListId == settings.PreferredListId).ToList() } : settings;
        var selected = (results.SelectedItem as TaskRow)?.Task.Id;
        var rows = TaskCatalog.Filter(filteredSettings, preferred, settings.SearchCurrentList ? [] : all, query.Text, active(), remoteMatches);
        results.ItemsSource = rows.Take(200).ToList();
        results.SelectedItem = rows.FirstOrDefault(r => r.Task.Id == selected);
        notice.Text = !settings.IsConfigured ? "Connect and choose a list in Settings first."
            : (searchStatus.Length > 0 ? searchStatus : "Cached tasks · type to search ClickUp") + $"\n{Math.Min(rows.Count, 200)} shown" + (rows.Count > 200 ? " · narrow your search to see more" : "");
        create.Visibility = string.IsNullOrWhiteSpace(query.Text) ? Visibility.Collapsed : Visibility.Visible;
        create.Content = "Create task in " + (settings.PreferredListName ?? "current list");
        create.IsEnabled = settings.IsConfigured && !creating && !string.IsNullOrWhiteSpace(query.Text);
        currentList.IsEnabled = !creating;
    }
    private async Task SearchChanged()
    {
        remoteMatches.Clear(); searchStatus = ""; Refresh();
        var settings = services.Settings;
        var text = query.Text.Trim();
        if (!settings.IsConfigured || creating) { interaction.Cancel(); return; }
        await interaction.Run(text, async ct =>
        {
            searchStatus = "Searching ClickUp…"; Refresh();
            string? cursor = null;
            var seen = new HashSet<string>();
            do
            {
                var page = await search.Search(settings.WorkspaceId!, text, "task", settings.SearchCurrentList ? settings.PreferredListId : null, cursor, ct);
                ct.ThrowIfCancellationRequested();
                services.SearchCache.Merge(settings.UserId!, settings.WorkspaceId!, page.Items);
                foreach (var item in page.Items.Where(i => i.Type == "task" && (!settings.SearchCurrentList || i.ListId == settings.PreferredListId))) remoteMatches.Add(item.Id);
                cursor = page.Cursor;
                searchStatus = cursor is null ? "Search complete" : "Searching ClickUp · more results loading…";
                Refresh();
                if (cursor is not null && !seen.Add(cursor)) throw new ClickUpException("ClickUp repeated a result page. Showing the results received.");
            } while (cursor is not null);
        }, ex => { searchStatus = Error(ex); Refresh(); });
    }
    private async Task Create()
    {
        if (creating || string.IsNullOrWhiteSpace(query.Text)) return;
        interaction.Cancel();
        var settings = services.Settings; var title = query.Text.Trim();
        creating = true; query.IsEnabled = false; Refresh(); notice.Text = "Creating task…";
        TaskSummary? created = null;
        try
        {
            using var client = createClient();
            created = await client.CreateTask(settings.PreferredListId!, title, CancellationToken.None);
            if (services.Settings.UserId != settings.UserId || services.Settings.WorkspaceId != settings.WorkspaceId || services.Settings.PreferredListId != settings.PreferredListId)
            { notice.Text = "Task created in the original list. Your setup changed; select it from that list."; return; }
            services.SearchCache.Merge(settings.UserId!, settings.WorkspaceId!, [new(created.Id, created.Name, "task", created.ListId, created.Status, created.StatusType)]);
            services.Save(services.Settings with { RecentTasks = TaskCatalog.Remember(services.Settings, created) });
            await select(created); updating = true; query.Clear(); updating = false;
        }
        catch (Exception ex)
        {
            notice.Text = created is not null ? "Task was created, but recent tasks could not be saved. Search before creating another."
                : Error(ex) + " Your title is retained. If the connection was lost, check for the task before retrying.";
        }
        finally { creating = false; query.IsEnabled = true; create.IsEnabled = !string.IsNullOrWhiteSpace(query.Text); currentList.IsEnabled = true; }
    }
    private static string Error(Exception ex) => ex is ClickUpException ? ex.Message : "Could not complete the request. Cached results are still available.";
    internal void Cancel() => interaction.Close();
}