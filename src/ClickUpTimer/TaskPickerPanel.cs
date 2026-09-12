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
    private readonly TextBox query = new() { Height = 28, Padding = new Thickness(6, 3, 6, 3), MaxLength = 500 };
    private readonly ListBox results = new() { DisplayMemberPath = "Label", MaxHeight = 250, MinHeight = 65 };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly Button search = new() { Content = "Search workspace", Height = 28 };
    private readonly Button create = new() { Height = 28 };
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
        Margin = new Thickness(12);
        Children.Add(new TextBlock { Text = "Tasks", FontSize = 14, FontWeight = FontWeights.SemiBold });
        Children.Add(new TextBlock { Text = "Search task names or IDs", Margin = new Thickness(0, 6, 0, 4), TextWrapping = TextWrapping.Wrap });
        Children.Add(query); Children.Add(notice); Children.Add(results);
        query.KeyDown += (_, e) => { if (e.Key == Key.Down && results.Items.Count > 0) { results.SelectedIndex = Math.Max(0, results.SelectedIndex); results.Focus(); e.Handled = true; } else if (e.Key == Key.Enter) { if (results.SelectedIndex < 0 && results.Items.Count > 0) results.SelectedIndex = 0; Choose(); e.Handled = true; } };
        // Ellipsize long names instead of allowing the list to force a wider popup.
        results.DisplayMemberPath = "";
        var row = new FrameworkElementFactory(typeof(TextBlock));
        row.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Label"));
        row.SetBinding(TextBlock.ToolTipProperty, new System.Windows.Data.Binding("Label"));
        row.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        results.ItemTemplate = new DataTemplate { VisualTree = row };
        results.Loaded += (_, _) =>
        {
            var itemStyle = new Style(typeof(ListBoxItem), results.TryFindResource(typeof(ListBoxItem)) as Style);
            itemStyle.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.NameProperty, new System.Windows.Data.Binding("Label")));
            results.ItemContainerStyle = itemStyle;
        };
        query.TextChanged += (_, _) => Refresh();
        results.MouseDoubleClick += (_, _) => Choose();
        results.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Choose(); e.Handled = true; } };
        var use = new Button { Content = "Use selected task", Height = 28, Margin = new Thickness(0, 6, 0, 6) };
        var actions = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition());
        use.Margin = new Thickness(0, 0, 4, 0); actions.Children.Add(use);
        use.Click += (_, _) => Choose(); Children.Add(actions);
        search.Click += async (_, _) => await SearchWorkspace(); Grid.SetColumn(search, 1); actions.Children.Add(search);
        create.Margin = new Thickness(0, 6, 0, 6);
        create.Click += async (_, _) => await Create(); Children.Add(create);
        var more = new Button { Content = "More actions", HorizontalAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0) };
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open current task in ClickUp" }; open.Click += (_, _) => openTask(); menu.Items.Add(open);
        var refresh = new MenuItem { Header = "Refresh preferred list" };
        refresh.Click += async (_, _) => { refresh.IsEnabled = false; await services.RefreshCache(); refresh.IsEnabled = true; Refresh(); }; menu.Items.Add(refresh);
        more.ContextMenu = menu; more.Click += (_, _) => { menu.PlacementTarget = more; menu.IsOpen = true; }; Children.Add(more);
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
            : $"{rows.Count} shown · {settings.PreferredListName}\n" + (searchStatus.Length > 0 ? searchStatus : cache is null ? "Preferred list not loaded yet. Refresh to load it." : "");
        if (services.CacheNotice?.Contains("could not") == true) notice.Text += "\nConnection failed; cached results may be out of date.";
        create.Visibility = string.IsNullOrWhiteSpace(query.Text) ? Visibility.Collapsed : Visibility.Visible;
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
