using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace ClickUpTimer;

internal sealed record ConnectedAccount(ClickUpUser User, List<Choice> Workspaces, bool RestAuthorized = true);

internal sealed class SettingsWindow : Window
{
    private readonly AppServices services;
    private readonly StatusSettingsPanel statusFilters;
    private readonly Func<CancellationToken, Task<ConnectedAccount>> connectAccount;
    private readonly IClickUpSearch search;
    private readonly InteractiveSearch interaction = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly ComboBox workspaces = new() { Height = 28, DisplayMemberPath = "Name", SelectedValuePath = "Id" };
    private readonly PreferredListPicker lists = new();
    private readonly ComboBox mode = new() { Height = 28, ItemsSource = new[] { "Taskbar", "Floating" } };
    private readonly ComboBox presentation = new() { Height = 28, ItemsSource = new[] { "Minimal", "Compact", "Detailed" } };
    private readonly ComboBox appearance = new() { Height = 28, ItemsSource = new[] { "System", "Light", "Dark" } };
    private readonly TimerStrip preview = new();
    private readonly ComboBox monitor = new() { Height = 28, DisplayMemberPath = "Name", SelectedValuePath = "Id" };
    private readonly CheckBox startup = new() { Content = "Launch ClickUp Timer when I sign in", Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock connection = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock searchNotice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    private readonly Button connect = new() { Content = "Connect to ClickUp", Height = 30, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button save = new() { Content = "Save settings", Width = 120, Height = 28, IsDefault = true };
    private ClickUpUser? user;
    private bool updating, connecting, closed;
    private int connectionVersion;
    internal SettingsWindow(AppServices services, Func<CancellationToken, Task<ConnectedAccount>>? connectAccount = null, IClickUpSearch? search = null)
    {
        this.services = services; this.connectAccount = connectAccount ?? services.ConnectAccount;
        this.search = search ?? services.Search;
        Title = "ClickUp Timer — Settings"; Width = 520; Height = 580; MinWidth = 440; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Appearance.Attach(this, services);
        var root = new DockPanel { Margin = new Thickness(12) };
        var title = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        title.Children.Add(new TextBlock { Text = "Settings", FontSize = 18, FontWeight = FontWeights.SemiBold });
        DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
        var footer = new StackPanel { Margin = new Thickness(0, 14, 0, 0) }; footer.Children.Add(message);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Width = 90, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.Click += (_, _) => Close(); save.Click += (_, _) => Save();
        actions.Children.Add(cancel); actions.Children.Add(save); footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var tabs = new TabControl(); root.Children.Add(tabs);
        var account = new StackPanel { Margin = new Thickness(12) };
        account.Children.Add(new TextBlock { Text = "Sign in securely in your browser to connect ClickUp.", TextWrapping = TextWrapping.Wrap });
        account.Children.Add(connect); account.Children.Add(connection);
        AddLabel(account, "Workspace"); account.Children.Add(workspaces);
        AddLabel(account, "Current list"); account.Children.Add(lists);
        account.Children.Add(searchNotice);
        account.Children.Add(new TextBlock { Text = "New tasks go into your selected list.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });
        tabs.Items.Add(new TabItem { Header = "ClickUp account", Content = new ScrollViewer { Content = account, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var display = new StackPanel { Margin = new Thickness(12) };
        AddLabel(display, "Appearance"); display.Children.Add(appearance);
        AddLabel(display, "Presentation"); display.Children.Add(presentation);
        var previewHost = new Border { Child = preview, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        previewHost.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ClickUpTimer;component/Theme.xaml", UriKind.Relative) });
        preview.IsHitTestVisible = false; preview.Focusable = false;
        foreach (var control in new[] { preview.Choose, preview.TimeButton, preview.State, preview.Toggle }) control.IsTabStop = false;
        void Preview()
        {
            Appearance.SetPalette(previewHost.Resources, appearance.SelectedItem as string ?? "System");
            preview.SetPresentation(presentation.SelectedItem as string ?? "Compact");
            preview.SetState("Design review", "Running", true, false, true, false, true);
            preview.SetTimes("1h 04m 05s", "2h 08m 10s");
            preview.Width = preview.Footprint.Width; preview.Height = preview.Footprint.Height;
        }
        presentation.SelectionChanged += (_, _) => Preview(); appearance.SelectionChanged += (_, _) => Preview();
        display.Children.Add(previewHost);
        display.Children.Add(new TextBlock { Text = "Minimal: time only. Compact: task + time. Detailed: adds state and Today.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        AddLabel(display, "Timer mode"); display.Children.Add(mode);
        display.Children.Add(new TextBlock { Text = "Taskbar uses an existing empty gap. Floating gives you a movable window above your work.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });
        AddLabel(display, "Monitor"); display.Children.Add(monitor); display.Children.Add(startup);
        display.Children.Add(new TextBlock { Text = "Drag the dotted grip to move the timer. Each mode remembers its position. If a monitor is disconnected, the timer returns to an available screen.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        var applyDisplay = new Button { Content = "Apply display settings", Height = 28, Margin = new Thickness(0, 12, 0, 0) };
        applyDisplay.Click += (_, _) =>
        {
            try { services.Save(DisplaySettings()); Appearance.Color(message, TextBlock.ForegroundProperty, "Accent"); message.Text = "Display settings applied. Your account draft is still available in the ClickUp tab."; }
            catch (Exception ex) { Appearance.Color(message, TextBlock.ForegroundProperty, "Error"); message.Text = SafeMessage(ex); }
        };
        display.Children.Add(applyDisplay);
        tabs.Items.Add(new TabItem { Header = "Display & startup", Content = new ScrollViewer { Content = display, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        statusFilters = new StatusSettingsPanel(services);
        tabs.Items.Add(new TabItem { Header = "Ignored statuses", Content = new ScrollViewer { Content = statusFilters, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        Content = root;
        NameScope.SetNameScope(this, new NameScope());
        RegisterName("Workspaces", workspaces); RegisterName("PreferredList", lists); RegisterName("Connection", connection);
        RegisterName("SearchNotice", searchNotice);
        RegisterName("ApplyDisplay", applyDisplay); RegisterName("Presentation", presentation); RegisterName("Appearance", appearance);
        RegisterName("SaveSettings", save); RegisterName("Connect", connect);
        var settings = services.Settings;
        mode.SelectedItem = settings.Mode;
        presentation.SelectedItem = TimerStrip.Normalize(settings.Presentation); appearance.SelectedItem = Appearance.Normalize(settings.Appearance); Preview();
        var monitors = new List<Choice> { new("", "Primary monitor (automatic)") };
        monitors.AddRange(Forms.Screen.AllScreens.Select((s, i) => new Choice(s.DeviceName, $"Display {i + 1}{(s.Primary ? " (primary)" : "")} — {s.Bounds.Width} × {s.Bounds.Height}")));
        if (settings.Monitor is not null && !monitors.Any(m => m.Id == settings.Monitor)) monitors.Add(new(settings.Monitor, "Saved monitor (disconnected; primary is used)"));
        monitor.ItemsSource = monitors; monitor.SelectedValue = settings.Monitor ?? "";
        startup.IsChecked = settings.LaunchAtSignIn;
        user = settings.UserId is null ? null : new(settings.UserId, settings.UserName ?? "ClickUp user");
        updating = true;
        if (settings.WorkspaceId is not null)
        {
            workspaces.ItemsSource = new[] { new Choice(settings.WorkspaceId, settings.WorkspaceName ?? settings.WorkspaceId) };
            workspaces.SelectedIndex = 0;
        }
        updating = false;
        ShowCachedLists();
        connection.Text = user is null ? "Not connected." : $"{user.Name} · cached lists ready. Connect to authorize search.";
        try
        {
            if (services.OAuth.Read() is { } session)
            {
                if (session.User is { } connected && (user is null || user.Id == connected.Id) && session.Workspaces is { Count: > 0 } authorized)
                {
                    user = connected; updating = true; workspaces.ItemsSource = authorized;
                    workspaces.SelectedItem = authorized.FirstOrDefault(w => w.Id == settings.WorkspaceId) ?? authorized[0];
                    updating = false; ShowCachedLists();
                }
                connect.Content = "Reconnect to ClickUp";
                connection.Text = session.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow ? "Sign-in expired. Reconnect to resume search and timers; saved timer requests are retained." : ConnectionText(user?.Name ?? "ClickUp");
            }
        }
        catch (Exception) { connection.Text = "Saved sign-in could not be read. Connect again."; }
        message.Text = services.Store.Warning;
        connect.Click += async (_, _) => await Connect();
        workspaces.SelectionChanged += (_, _) => { if (!updating) { interaction.Cancel(); searchNotice.Text = ""; ShowCachedLists(); } };
        lists.QueryChanged += async () => await SearchLists();
        account.IsVisibleChanged += (_, _) => { if (account.IsVisible) interaction.Open(); else interaction.Close(); };
        Loaded += async (_, _) =>
        {
            interaction.Open();
            // Upgrade sessions saved by the previous build without reopening the browser
            // or issuing a content search. Valid cached account metadata needs no requests.
            if (connectAccount is not null) return;
            var version = connectionVersion;
            try
            {
                var restored = await services.RestoreConnection(lifetime.Token);
                if (!closed && version == connectionVersion && restored is not null) ApplyConnection(restored);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!closed && version == connectionVersion) connection.Text = SafeMessage(ex); }
        };
        Closed += (_, _) => { closed = true; lifetime.Cancel(); interaction.Close(); statusFilters.Cancel(); };
    }
    private static void AddLabel(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) });
    private void ShowCachedLists()
    {
        var settings = services.Settings;
        var workspace = workspaces.SelectedItem as Choice;
        var choices = user is not null && workspace is not null ? services.SearchCache.Read(user.Id, workspace.Id).Where(i => i.Type == "list").Select(i => i.Choice).ToList() : [];
        var preferred = user?.Id == settings.UserId && workspace?.Id == settings.WorkspaceId && settings.PreferredListId is not null
            ? new Choice(settings.PreferredListId, settings.PreferredListName ?? settings.PreferredListId) : null;
        if (preferred is not null && !choices.Any(c => c.Id == preferred.Id)) choices.Insert(0, preferred);
        lists.SetChoices(choices, preferred, resetSearch: true);
    }
    private async Task Connect()
    {
        if (connecting) return;
        connectionVersion++;
        interaction.Cancel(); connecting = true; connect.IsEnabled = false; save.IsEnabled = false;
        connection.Text = "Complete ClickUp sign-in in your browser…"; message.Text = "";
        try
        {
            var account = await connectAccount(lifetime.Token);
            if (closed) return;
            ApplyConnection(account);
        }
        catch (OperationCanceledException) { if (!closed) connection.Text = "Sign-in was canceled or timed out. Connect to try again."; }
        catch (Exception ex) { if (!closed) connection.Text = SafeMessage(ex); }
        finally { connecting = false; if (!closed) { connect.IsEnabled = true; save.IsEnabled = true; } }
    }
    private static string ConnectionText(string name) => $"Connected as {name}. Search and timers use this sign-in.";
    private void ApplyConnection(ConnectedAccount account)
    {
        var previousUser = user?.Id;
        user = account.User;
        var previous = (workspaces.SelectedItem as Choice)?.Id ?? services.Settings.WorkspaceId;
        updating = true; workspaces.ItemsSource = account.Workspaces;
        workspaces.SelectedItem = account.Workspaces.FirstOrDefault(w => w.Id == previous) ?? account.Workspaces.FirstOrDefault();
        updating = false;
        if (previousUser != user.Id || previous != (workspaces.SelectedItem as Choice)?.Id) ShowCachedLists();
        connection.Text = ConnectionText(user.Name); connect.Content = "Reconnect to ClickUp";
    }
    private async Task SearchLists()
    {
        var accountUser = user;
        var workspace = workspaces.SelectedItem as Choice;
        if (accountUser is null || workspace is null || connecting) { interaction.Cancel(); return; }
        var text = lists.SearchBox.Text.Trim();
        searchNotice.Text = "";
        var found = new HashSet<string>();
        await interaction.Run(text, async ct =>
        {
            searchNotice.Text = "Finding ClickUp lists…";
            string? cursor = null;
            var seen = new HashSet<string>();
            do
            {
                var page = await search.Search(workspace.Id, text, "list", null, cursor, ct);
                ct.ThrowIfCancellationRequested();
                services.SearchCache.Merge(accountUser.Id, workspace.Id, page.Items);
                foreach (var item in page.Items.Where(i => i.Type == "list" && (!page.FilterLocally || i.Matches(text)))) found.Add(item.Id);
                var choices = services.SearchCache.Read(accountUser.Id, workspace.Id).Where(i => i.Type == "list").Select(i => i.Choice);
                lists.SetChoices(choices, lists.SelectedChoice);
                lists.SetRemoteMatches(found);
                cursor = page.Cursor;
                searchNotice.Text = cursor is null ? $"{found.Count} matching lists found in ClickUp." : $"{found.Count} matching lists · loading more…";
                if (cursor is not null && !seen.Add(cursor)) throw new ClickUpException("ClickUp repeated a result page. Showing the results received.");
            } while (cursor is not null);
        }, ex => searchNotice.Text = SafeMessage(ex));
    }
    private void Save()
    {
        if (connecting) return;
        try
        {
            var next = DisplaySettings();
            if (user is not null && workspaces.SelectedItem is Choice workspace)
                next = next with { UserId = user.Id, UserName = user.Name, WorkspaceId = workspace.Id, WorkspaceName = workspace.Name,
                    PreferredListId = lists.SelectedChoice?.Id, PreferredListName = lists.SelectedChoice?.Name };
            services.Save(statusFilters.ApplyTo(next)); Close();
        }
        catch (Exception ex) { message.Text = SafeMessage(ex); }
    }
    private AppSettings DisplaySettings() => services.Settings with { Mode = mode.SelectedItem as string ?? "Taskbar", Monitor = string.IsNullOrEmpty(monitor.SelectedValue as string) ? null : monitor.SelectedValue as string, LaunchAtSignIn = startup.IsChecked == true, Presentation = presentation.SelectedItem as string ?? "Compact", Appearance = appearance.SelectedItem as string ?? "System" };
    private static string SafeMessage(Exception ex) => ex is ClickUpException ? ex.Message : "The operation could not be completed. Cached results and your saved settings are still available.";
}
