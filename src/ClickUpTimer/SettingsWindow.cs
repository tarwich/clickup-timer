using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ClickUpTimer;

internal sealed class SettingsWindow : Window
{
    private readonly AppServices services;
    private readonly StatusSettingsPanel statusFilters;
    private readonly Func<string, ClickUpClient> createClient;
    private readonly PasswordBox key = new() { Height = 34, Padding = new Thickness(7), MaxLength = 1280 };
    private readonly ComboBox workspaces = new() { Height = 34, DisplayMemberPath = "Name", SelectedValuePath = "Id" };
    private readonly PreferredListPicker lists = new();
    private readonly ComboBox mode = new() { Height = 34, ItemsSource = new[] { "Taskbar", "Floating" } };
    private readonly ComboBox monitor = new() { Height = 34, DisplayMemberPath = "Name", SelectedValuePath = "Id" };
    private readonly CheckBox startup = new() { Content = "Launch ClickUp Timer when I sign in", Margin = new Thickness(0, 16, 0, 0) };
    private readonly TextBlock connection = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock keyStatus = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
    private bool hasSavedKey;
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick, Margin = new Thickness(0, 0, 0, 10) };
    private readonly Button connect = new() { Content = "Connect to ClickUp", Height = 34, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button save = new() { Content = "Save settings", Width = 120, Height = 36, IsDefault = true };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? listRequest;
    private CancellationTokenSource? connectRequest;
    private readonly DispatcherTimer keyDelay = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly Dictionary<string, List<Choice>> listChoices = [];
    private string? loadedListScope;
    private bool updatingWorkspaces;
    private ClickUpClient? client;
    private ClickUpUser? validatedUser;
    private string? validatedKey;
    private int connectVersion, listVersion;
    private bool connecting, loadingLists, closed;

    internal SettingsWindow(AppServices services, Func<string, ClickUpClient>? createClient = null)
    {
        this.services = services;
        this.createClient = createClient ?? (secret => new ClickUpClient(secret));
        Title = "ClickUp Timer — Settings"; Width = 600; Height = 710; MinWidth = 480; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(245, 247, 250));
        Foreground = new SolidColorBrush(Color.FromRgb(25, 36, 48)); FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        var root = new DockPanel { Margin = new Thickness(24) };
        var title = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        title.Children.Add(new TextBlock { Text = "Make the timer yours", FontSize = 25, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock { Text = "Connect ClickUp and choose where the timer lives.", Margin = new Thickness(0, 6, 0, 0) });
        DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
        var footer = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        footer.Children.Add(message);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Width = 90, Height = 36, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.Click += (_, _) => Close(); save.Click += (_, _) => Save();
        actions.Children.Add(cancel); actions.Children.Add(save); footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var tabs = new TabControl(); root.Children.Add(tabs);
        var account = new StackPanel { Margin = new Thickness(16) };
        AddLabel(account, "Personal API key"); account.Children.Add(key);
        account.Children.Add(keyStatus);
        account.Children.Add(new TextBlock { Text = "Paste your key to load workspaces automatically. Save settings stores it securely; leave blank to keep your saved key.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
        account.Children.Add(connect); account.Children.Add(connection);
        AddLabel(account, "Workspace"); account.Children.Add(workspaces);
        AddLabel(account, "Preferred list"); account.Children.Add(lists);
        account.Children.Add(new TextBlock { Text = "New tasks will go into this list. You can save your key and workspace now and choose a list later.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });
        var refresh = new Button { Content = "Refresh saved task cache", Height = 32, Margin = new Thickness(0, 16, 0, 0) };
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false;
            await services.RefreshCache();
            if (!closed) { connection.Text = services.CacheNotice ?? "Connect and save a preferred list first."; refresh.IsEnabled = true; }
        };
        account.Children.Add(refresh);
        tabs.Items.Add(new TabItem { Header = "ClickUp account", Content = new ScrollViewer { Content = account, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var display = new StackPanel { Margin = new Thickness(16) };
        AddLabel(display, "Timer mode"); display.Children.Add(mode);
        display.Children.Add(new TextBlock { Text = "Taskbar uses an existing empty gap. Floating gives you a movable window above your work.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });
        AddLabel(display, "Monitor"); display.Children.Add(monitor); display.Children.Add(startup);
        display.Children.Add(new TextBlock { Text = "Drag the dotted grip to move the timer. Each mode remembers its position. If a monitor is disconnected, the timer returns to an available screen.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 0) });
        var applyDisplay = new Button { Content = "Apply display settings", Height = 36, Margin = new Thickness(0, 18, 0, 0) };
        applyDisplay.Click += (_, _) =>
        {
            try { services.Save(DisplaySettings()); message.Foreground = Brushes.DarkGreen; message.Text = "Display settings applied. Your account draft is still available in the ClickUp tab."; }
            catch (Exception ex) { message.Foreground = Brushes.Firebrick; message.Text = SafeMessage(ex); }
        };
        display.Children.Add(applyDisplay);
        tabs.Items.Add(new TabItem { Header = "Display & startup", Content = display });
        statusFilters = new StatusSettingsPanel(services);
        tabs.Items.Add(new TabItem { Header = "Ignored statuses", Content = new ScrollViewer { Content = statusFilters, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        Closed += (_, _) => statusFilters.Cancel();
        Content = root;
        NameScope.SetNameScope(this, new NameScope());
        RegisterName("ApiKey", key); RegisterName("Workspaces", workspaces); RegisterName("PreferredList", lists);
        RegisterName("ApplyDisplay", applyDisplay);
        RegisterName("KeyStatus", keyStatus); RegisterName("SaveSettings", save); RegisterName("Connect", connect);
        var settings = services.Settings;
        mode.SelectedItem = settings.Mode;
        var monitors = new List<Choice> { new("", "Primary monitor (automatic)") };
        monitors.AddRange(Forms.Screen.AllScreens.Select((s, i) => new Choice(s.DeviceName, $"Display {i + 1}{(s.Primary ? " (primary)" : "")} — {s.Bounds.Width} × {s.Bounds.Height}")));
        if (settings.Monitor is not null && !monitors.Any(m => m.Id == settings.Monitor)) monitors.Add(new(settings.Monitor, "Saved monitor (disconnected; primary is used)"));
        monitor.ItemsSource = monitors; monitor.SelectedValue = settings.Monitor ?? "";
        startup.IsChecked = settings.LaunchAtSignIn;
        if (settings.WorkspaceId is not null)
        {
            workspaces.ItemsSource = new[] { new Choice(settings.WorkspaceId!, settings.WorkspaceName ?? settings.WorkspaceId!) }; workspaces.SelectedIndex = 0;
            if (settings.PreferredListId is not null)
            {
                lists.SetChoices([new Choice(settings.PreferredListId!, settings.PreferredListName ?? settings.PreferredListId!)], new(settings.PreferredListId!, settings.PreferredListName ?? settings.PreferredListId!));
                loadedListScope = settings.UserId + "/" + settings.WorkspaceId;
                listChoices[loadedListScope] = [new(settings.PreferredListId!, settings.PreferredListName ?? settings.PreferredListId!)];
            }
            connection.Text = $"Saved account: {settings.UserName}. Connect to refresh available lists.";
        }
        else connection.Text = "Enter your key to choose a workspace and list.";
        try { hasSavedKey = services.Credentials.Exists(); }
        catch (Exception ex) { message.Text = SafeMessage(ex); }
        UpdateKeyStatus();
        connect.Click += async (_, _) => await Connect();
        connect.Content = "Refresh connection and lists";
        workspaces.SelectionChanged += async (_, _) => { if (!updatingWorkspaces && client is not null) await LoadLists(); };
        keyDelay.Tick += async (_, _) => { keyDelay.Stop(); await Connect(); };
        key.PasswordChanged += (_, _) =>
        {
            if (closed) return;
            keyDelay.Stop(); connectVersion++; listVersion++; listRequest?.Cancel(); connectRequest?.Cancel();
            validatedKey = null; validatedUser = null; client?.Dispose(); client = null;
            connecting = false; loadingLists = false; connect.IsEnabled = true;
            message.Text = "";
            UpdateKeyStatus();
            connection.Text = "Loading your account after you finish entering the key…";
            keyDelay.Start();
        };
        message.Text = services.Store.Warning;
        Loaded += async (_, _) =>
        {
            try { if (hasSavedKey) await Connect(); }
            catch (Exception ex) { message.Text = SafeMessage(ex); }
        };
        Closed += (_, _) => { closed = true; keyDelay.Stop(); lifetime.Cancel(); listRequest?.Cancel(); connectRequest?.Cancel(); client?.Dispose(); key.Clear(); validatedKey = null; };
    }
    private static void AddLabel(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
    private async Task Connect()
    {
        var version = ++connectVersion;
        keyDelay.Stop(); connectRequest?.Cancel(); connectRequest?.Dispose(); connectRequest = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var cancellation = connectRequest.Token;
        connecting = true; connect.IsEnabled = false; message.Text = ""; message.Foreground = Brushes.Firebrick;
        listVersion++; listRequest?.Cancel(); loadingLists = false; client?.Dispose(); client = null; validatedKey = null; validatedUser = null;
        ClickUpClient? candidate = null;
        try
        {
            var secret = string.IsNullOrWhiteSpace(key.Password) ? services.Credentials.Read() : key.Password.Trim();
            if (string.IsNullOrWhiteSpace(secret)) throw new ClickUpException("Enter your ClickUp personal API key.");
            connection.Text = "Connecting to ClickUp…";
            candidate = createClient(secret);
            var user = await candidate.Validate(cancellation);
            var available = await candidate.Workspaces(cancellation);
            if (version != connectVersion || closed) return;
            if (available.Count == 0) throw new ClickUpException("This account has no accessible workspaces.");
            validatedUser = user; validatedKey = secret; client = candidate; candidate = null;
            connection.Text = $"Connected as {user.Name}. Choose your preferred list.";
            var preferredWorkspace = (workspaces.SelectedItem as Choice)?.Id ?? services.Settings.WorkspaceId;
            updatingWorkspaces = true;
            try
            {
                workspaces.ItemsSource = available;
                workspaces.SelectedItem = available.FirstOrDefault(w => w.Id == preferredWorkspace) ?? available[0];
            }
            finally { updatingWorkspaces = false; }
            connecting = false; connect.IsEnabled = true;
            await LoadLists();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == connectVersion && !closed) { message.Text = SafeMessage(ex); connection.Text = "Not connected. Your saved settings have not changed."; } }
        finally { candidate?.Dispose(); if (version == connectVersion && !closed) { connecting = false; connect.IsEnabled = true; } }
    }
    private async Task LoadLists()
    {
        var api = client; if (api is null || workspaces.SelectedItem is not Choice workspace) return;
        var version = ++listVersion;
        listRequest?.Cancel(); listRequest?.Dispose(); listRequest = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var cancellation = listRequest.Token;
        var scope = validatedUser?.Id + "/" + workspace.Id;
        var preferredList = loadedListScope == scope ? lists.SelectedChoice : null;
        loadingLists = true;
        if (loadedListScope != scope)
        {
            lists.SetChoices(listChoices.GetValueOrDefault(scope) ?? [], preferredList, resetSearch: true, loading: true);
            loadedListScope = scope;
        }
        connection.Text = "Loading lists…"; message.Text = "";
        try
        {
            void ShowAvailable(List<Choice> available)
            {
                if (version != listVersion || closed) return;
                // Keep the current choice visible until its location has been fetched.
                // Reading it before replacing ItemsSource also honors changes made while loading.
                preferredList = lists.SelectedChoice ?? preferredList;
                var visible = available.ToList();
                if (preferredList is not null && !visible.Any(l => l.Id == preferredList.Id)) visible.Add(preferredList);
                listChoices[scope] = visible;
                lists.SetChoices(visible, preferredList, loading: loadingLists);
            }
            var progress = new Progress<List<Choice>>(available =>
            {
                if (!loadingLists || version != listVersion || closed) return;
                ShowAvailable(available);
                connection.Text = $"{available.Count} lists available — checking remaining locations…";
            });
            var available = await api.Lists(workspace.Id, cancellation, progress);
            if (version != listVersion || closed) return;
            ShowAvailable(available);
            connection.Text = available.Count == 0 ? "No accessible active lists in this workspace." : $"Connected as {validatedUser?.Name} · {available.Count} lists available.";
            if (lists.SelectedChoice is Choice selected && !available.Any(l => l.Id == selected.Id))
                message.Text = "Your selected list was not returned by ClickUp. It has been kept; refresh to retry or choose another list.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == listVersion && !closed)
            {
                message.Text = SafeMessage(ex);
                connection.Text = lists.AvailableCount > 0 ? "Some locations could not be loaded. Available lists are still searchable." : "Lists could not be loaded. Your key and workspace can still be saved; use Refresh to retry.";
            }
        }
        finally { if (version == listVersion && !closed) loadingLists = false; }
    }
    private void Save()
    {
        message.Text = ""; message.Foreground = Brushes.Firebrick;
        if (connecting || keyDelay.IsEnabled) { message.Text = "Your key is still being verified. Please wait a moment, then save."; return; }
        if (key.Password.Length > 0 && validatedKey != key.Password.Trim()) { message.Text = "Connect to validate the key before saving."; return; }
        var next = DisplaySettings();
        if (validatedUser is not null)
        {
            if (workspaces.SelectedItem is not Choice workspace) { message.Text = "Select a workspace."; return; }
            var list = loadedListScope == validatedUser.Id + "/" + workspace.Id ? lists.SelectedChoice : null;
            next = next with { UserId = validatedUser.Id, UserName = validatedUser.Name, WorkspaceId = workspace.Id, WorkspaceName = workspace.Name, PreferredListId = list?.Id, PreferredListName = list?.Name };
        }
        try
        {
            services.Save(statusFilters.ApplyTo(next), validatedKey);
            _ = services.RefreshCache();
            Close();
        }
        catch (Exception ex) { message.Text = SafeMessage(ex); }
    }
    private void UpdateKeyStatus()
    {
        keyStatus.Text = key.Password.Length > 0 ? "New key entered — not saved yet" : hasSavedKey ? "✓ API key saved on this PC" : "No API key saved yet";
        keyStatus.Foreground = key.Password.Length == 0 && hasSavedKey ? Brushes.DarkGreen : Brushes.DimGray;
        key.ToolTip = hasSavedKey ? "A key is saved. Enter a replacement only if you want to change it." : "Enter your personal ClickUp API key.";
    }
    private AppSettings DisplaySettings() => services.Settings with { Mode = mode.SelectedItem as string ?? "Taskbar", Monitor = string.IsNullOrEmpty(monitor.SelectedValue as string) ? null : monitor.SelectedValue as string, LaunchAtSignIn = startup.IsChecked == true };
    private static string SafeMessage(Exception ex) => ex switch
    {
        ClickUpException => ex.Message,
        System.ComponentModel.Win32Exception => "Windows Credential Manager could not complete the request. Your previous setup was retained.",
        System.IO.IOException or UnauthorizedAccessException => "Settings could not be saved. Check access to your local application-data folder.",
        _ => "The operation could not be completed. Try again."
    };
}
