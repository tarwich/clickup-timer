using System.Windows;
using System.Windows.Controls;

namespace ClickUpTimer;

internal sealed class StatusSettingsPanel : StackPanel
{
    private readonly AppServices services;
    private readonly StackPanel groups = new();
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, List<CheckBox>> checks = [];
    private readonly CancellationTokenSource stop = new();
    private string? loadedScope;
    private bool loading;
    internal StatusSettingsPanel(AppServices services)
    {
        this.services = services; Margin = new Thickness(16);
        Children.Add(new TextBlock { Text = "Hide tasks with these statuses", FontSize = 18 });
        Children.Add(new TextBlock { Text = "Filters apply to search and recent tasks. The current task stays visible. Done and closed statuses are initially hidden. Save account changes before refreshing this tab.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        Children.Add(notice);
        var refresh = new Button { Content = "Load statuses for saved workspace", Height = 32, Margin = new Thickness(0, 8, 0, 8) };
        refresh.Click += async (_, _) => await Load(); Children.Add(refresh);
        Children.Add(groups);
        var apply = new Button { Content = "Apply status filters", Height = 32, Margin = new Thickness(0, 12, 0, 0) };
        apply.Click += (_, _) =>
        {
            if (loading) { notice.Text = "Statuses are still loading."; return; }
            if (loadedScope != Scope()) { notice.Text = "Your account changed. Load statuses again."; return; }
            try
            {
                services.Save(ApplyTo(services.Settings)); notice.Text = "Status filters saved.";
            }
            catch (Exception) { notice.Text = "Status filters could not be saved. Try again."; }
        };
        Children.Add(apply);
        Loaded += async (_, _) => { if (loadedScope is null) await Load(); };
    }
    internal AppSettings ApplyTo(AppSettings settings)
    {
        var filters = new Dictionary<string, List<string>>(settings.IgnoredStatuses);
        foreach (var (scope, boxes) in checks) filters[scope] = boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Content).ToList();
        return settings with { IgnoredStatuses = filters };
    }
    private string Scope() => services.Settings.UserId + "/" + services.Settings.WorkspaceId;
    private async Task Load()
    {
        if (loading) return;
        var settings = services.Settings;
        if (!settings.IsConfigured) { notice.Text = "Save your ClickUp account and preferred list first."; return; }
        loading = true; loadedScope = Scope(); groups.Children.Clear(); checks.Clear(); notice.Text = "Loading list statuses…";
        try
        {
            using var client = new ClickUpClient(services.Credentials.Read() ?? throw new ClickUpException("Save an API key first."));
            var lists = await client.Lists(settings.WorkspaceId!, stop.Token);
            foreach (var list in lists)
            {
                var statuses = await client.Statuses(list.Id, stop.Token);
                if (loadedScope != Scope()) { notice.Text = "Your account changed. Load statuses again."; loadedScope = null; return; }
                groups.Children.Add(new TextBlock { Text = list.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4), TextWrapping = TextWrapping.Wrap });
                var scope = TaskCatalog.StatusKey(settings, list.Id); var boxes = new List<CheckBox>(); checks[scope] = boxes;
                foreach (var status in statuses)
                {
                    var task = new TaskSummary("", "", status.Name, list.Id, status.Id);
                    var box = new CheckBox { Content = status.Name, IsChecked = TaskCatalog.Ignored(settings, task), Margin = new Thickness(0, 3, 0, 3) };
                    boxes.Add(box); groups.Children.Add(box);
                }
            }
            notice.Text = $"{checks.Count} lists loaded. Checked statuses are hidden.";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { notice.Text = "Some statuses could not be loaded. You can apply the visible filters or retry."; }
        finally { loading = false; }
    }
    internal void Cancel() => stop.Cancel();
}
