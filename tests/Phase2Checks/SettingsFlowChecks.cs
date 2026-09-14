using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ClickUpTimer;

internal static class SettingsFlowChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "ClickUpTimer-search-tests-" + Guid.NewGuid());
                var credentials = new CredentialStore("ClickUpTimer.Tests/" + Guid.NewGuid());
                Window? window = null;
                try
                {
                    using var services = new AppServices(new SettingsStore(path), credentials);
                    services.Save(new AppSettings { UserId = "u", WorkspaceId = "w", PreferredListId = "l", PreferredListName = "Saved list" });
                    services.SearchCache.Merge("u", "w", [new("cached", "Cached list", "list"), new("t1", "Cached task", "task", "l", "open")]);
                    var connections = 0;
                    var search = new FakeSearch();
                    Task<ConnectedAccount> Connect(CancellationToken ct) { connections++; return Task.FromResult(new ConnectedAccount(new("u", "Fixture"), [new("w", "Workspace"), new("w2", "Second workspace")], false)); }
                    window = new SettingsWindow(services, Connect, search);
                    var lists = (PreferredListPicker)window.FindName("PreferredList");
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    await Task.Delay(750);
                    check(window.FindName("ApiKey") is null && window.FindName("KeyStatus") is null, "Settings exposes browser connect without an API-key field");
                    check(connections == 0 && search.Calls.Count == 0, "Opening settings does not authorize or search automatically");
                    check(lists.AvailableCount == 2 && lists.SelectedChoice?.Id == "l", "Cached and saved lists appear immediately with the saved selection");
                    ((Button)window.FindName("Connect")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(30);
                    check(connections == 1 && search.Calls.Count == 0, "Connect loads account choices without searching lists");
                    var connectionText = ((TextBlock)window.FindName("Connection")).Text;
                    check(connectionText.Contains("Search connected as Fixture") && ((Button)window.FindName("Connect")).Content.ToString() == "Reconnect to ClickUp", "MCP-only OAuth updates connection immediately without reopening Settings");
                    lists.SearchBox.Text = "p"; lists.SearchBox.Text = "pay"; lists.SearchBox.Text = "payroll";
                    await Task.Delay(800);
                    check(search.Calls.Count == 1 && search.Calls[0] is ("w", "payroll", "list", null), "List typing debounces to one scoped MCP search");
                    check(lists.DisplayedCount == 1 && lists.SelectedChoice?.Id == "l", "Remote list matches display without changing selected list");
                    check(((TextBlock)window.FindName("Connection")).Text == connectionText && ((TextBlock)window.FindName("SearchNotice")).Text.Contains("1 matching"), "Search counts do not overwrite account connection status");
                    check(new SearchCache(path).Read("u", "w").Any(i => i.Id == "found-list"), "Found lists persist for the next picker opening");
                    ((ComboBox)window.FindName("Workspaces")).SelectedValue = "w2";
                    await Task.Delay(750);
                    check(search.Calls.Count == 1 && lists.AvailableCount == 0 && lists.SelectedChoice is null, "Switching workspace clears other-workspace choices without a search");
                    ((ComboBox)window.FindName("Workspaces")).SelectedValue = "w";
                    check(lists.AvailableCount == 3 && lists.SelectedChoice?.Id == "l", "Returning to workspace restores cached choices and saved list");
                    ((ComboBox)window.FindName("Presentation")).SelectedItem = "Minimal";
                    ((ComboBox)window.FindName("Appearance")).SelectedItem = "Dark";
                    ((Button)window.FindName("ApplyDisplay")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check(new SettingsStore(path).Load() is { Presentation: "Minimal", Appearance: "Dark" }, "Display settings apply independently of account search");
                    lists.SearchBox.Text = "cancel this search"; window.Close(); await Task.Delay(750);
                    check(search.Calls.Count == 1, "Closing settings cancels pending debounced search");
                    window = null;
                    var large = new PreferredListPicker();
                    var many = Enumerable.Range(0, 5000).Select(i => new Choice("list-" + i, i == 4321 ? "Operations / Payroll" : "List " + i)).ToList();
                    large.SetChoices(many, many[3210]);
                    check(large.DisplayedCount == 50 && large.SelectedChoice?.Id == "list-3210", "Large cached list display is bounded and selection preserved");
                    large.SearchBox.Text = "operations payroll";
                    check(large.DisplayedCount == 1, "Cached list search supports words across paths");
                    large.SearchBox.Text = "list";
                    check(large.DisplayedCount == 200, "Broad cached list matches are capped for responsiveness");
                    var taskSearch = new FakeSearch();
                    var picker = new TaskPickerPanel(services, () => null, _ => Task.CompletedTask, () => { }, search: taskSearch);
                    var draft = (TextBox)picker.FindName("SearchText");
                    var toggle = (ToggleButton)picker.FindName("CurrentList");
                    picker.Open(); await Task.Delay(750);
                    check(taskSearch.Calls.Count == 0 && ((ListBox)picker.FindName("Results")).Items.Count == 1, "Opening task picker shows cached tasks and makes no search call");
                    draft.Text = "needle"; await Task.Delay(800);
                    check(taskSearch.Calls.Single() is ("w", "needle", "task", "l"), "Task search defaults to current list");
                    check(((ListBox)picker.FindName("Results")).Items.Count == 1, "Server content matches survive local name filtering");
                    toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(800);
                    check(taskSearch.Calls.Last() is ("w", "needle", "task", null) && !new SettingsStore(path).Load().SearchCurrentList, "Scope switch searches workspace and persists immediately");
                    draft.Clear(); await Task.Delay(750);
                    check(taskSearch.Calls.Count == 2, "Clearing task query makes no search call");
                    picker.Cancel(); draft.Text = "closed"; await Task.Delay(750);
                    check(taskSearch.Calls.Count == 2, "Closed task picker cannot start remote search");
                    var restored = new TaskPickerPanel(services, () => null, _ => Task.CompletedTask, () => { }, search: taskSearch);
                    check(((ToggleButton)restored.FindName("CurrentList")).IsChecked == false, "New task picker restores workspace scope");
                    var attempted = 0;
                    var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var failing = new TaskPickerPanel(services, () => null, _ => throw new Exception("Failed creation selected a task"), () => { },
                        () => new ClickUpClient("fixture", new DelayedHandler(async (_, cancellation) =>
                        { attempted++; await failure.Task.WaitAsync(cancellation); return new HttpResponseMessage(HttpStatusCode.InternalServerError); })), taskSearch);
                    draft = (TextBox)failing.FindName("SearchText");
                    var create = (Button)failing.FindName("CreateTask");
                    draft.Text = "Keep this title";
                    create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check(attempted == 1 && !create.IsEnabled, "Repeated create clicks issue only one request");
                    failure.SetResult(); await Task.Delay(100);
                    check(draft.Text == "Keep this title" && draft.IsEnabled && create.IsEnabled && ((TextBlock)failing.FindName("Notice")).Text.Contains("retained"), "Failed creation retains the draft and explains recovery");
                    finished.TrySetResult();
                }
                catch (Exception ex) { finished.TrySetException(ex); }
                finally { window?.Close(); credentials.Delete(); if (Directory.Exists(path)) Directory.Delete(path, true); dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    private sealed class FakeSearch : IClickUpSearch
    {
        internal List<(string Workspace, string Query, string Type, string? List)> Calls = [];
        public Task<SearchPage> Search(string workspace, string query, string type, string? list, string? cursor, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls.Add((workspace, query, type, list));
            return Task.FromResult(new SearchPage(type == "list" ? [new("found-list", "Operations", "list")]
                : [new("found-task", "Content matched", "task", "l", "open")]));
        }
    }
    private sealed class DelayedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }
}
