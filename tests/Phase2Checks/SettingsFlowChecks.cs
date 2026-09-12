using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
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
                var path = Path.Combine(Path.GetTempPath(), "ClickUpTimer-flow-" + Guid.NewGuid());
                var credentials = new CredentialStore("ClickUpTimer.FlowTests/" + Guid.NewGuid());
                using var services = new AppServices(new SettingsStore(path), credentials);
                SettingsWindow? window = null;
                try
                {
                    var failLists = false;
                    var account = 1;
                    Func<string, ClickUpClient> factory = token => new ClickUpClient(token, new FixtureHandler(request =>
                    {
                        var route = request.RequestUri!.AbsolutePath;
                        if (failLists && route.EndsWith("/list")) return new(HttpStatusCode.InternalServerError);
                        var json = route.EndsWith("/user") ? "{\"user\":{\"id\":" + account + ",\"username\":\"Fixture\"}}"
                            : route.EndsWith("/team") ? """{"teams":[{"id":"w","name":"Workspace"}]}"""
                            : route.EndsWith("/space") ? """{"spaces":[{"id":"s","name":"Space"}]}"""
                            : route.EndsWith("/list") ? """{"lists":[{"id":"l","name":"List"}]}"""
                            : route.EndsWith("/folder") ? """{"folders":[]}""" : """{"lists":[],"folders":[]}""";
                        return new(HttpStatusCode.OK) { Content = new StringContent(json) };
                    }));
                    window = new SettingsWindow(services, factory);
                    var key = (PasswordBox)window.FindName("ApiKey");
                    var workspaces = (ComboBox)window.FindName("Workspaces");
                    var lists = (PreferredListPicker)window.FindName("PreferredList");
                    check(workspaces.IsEnabled && lists.IsEnabled, "Settings dropdowns are not disabled before connecting");
                    key.Password = "fixture-only-key";
                    check(workspaces.IsEnabled && lists.IsEnabled, "Editing a key keeps dropdowns usable");
                    await Task.Delay(1500);
                    check(workspaces.Items.Count == 1 && lists.AvailableCount == 1, "Pasting a key automatically loads workspace and list without Connect click");
                    key.Password = "replacement-fixture-key";
                    check(workspaces.Items.Count == 1 && lists.AvailableCount == 1, "Key changes retain available selections during refresh");
                    ((Button)window.FindName("ApplyDisplay")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check(File.Exists(Path.Combine(path, "settings.json")), "Display preferences save while account validation is pending");
                    check(!services.Settings.IsConfigured && credentials.Read() is null && key.Password == "replacement-fixture-key", "Applying display preserves unsaved key without committing account draft");
                    failLists = true; account = 2;
                    await Task.Delay(1500);
                    check(lists.IsEnabled && lists.AvailableCount == 0, "Failed list fetch leaves control enabled with no stale other-account choices");
                    ((Button)window.FindName("SaveSettings")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check(credentials.Exists() && services.Settings.WorkspaceId == "w" && services.Settings.PreferredListId is null, "Save keeps validated key and workspace even when list fetch fails");
                    window = new SettingsWindow(services, factory);
                    check(((TextBlock)window.FindName("KeyStatus")).Text.Contains("saved on this PC") && ((PasswordBox)window.FindName("ApiKey")).Password.Length == 0, "Reopened settings explicitly indicate saved key without exposing it");
                    check(((ComboBox)window.FindName("Workspaces")).IsEnabled && ((ComboBox)window.FindName("Workspaces")).SelectedValue as string == "w", "Reopened workspace remains enabled and selected with partial setup");
                    failLists = false;
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    await Task.Delay(150);
                    check(((PreferredListPicker)window.FindName("PreferredList")).AvailableCount == 1, "Reopening settings reconnects saved key and reloads lists automatically");
                    window.Close();
                    services.Save(services.Settings with { PreferredListId = "late", PreferredListName = "Saved list" });
                    for (var scenario = 0; scenario < 3; scenario++)
                    {
                        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var missing = scenario == 2;
                        window = new SettingsWindow(services, token => new ClickUpClient(token, new DelayedHandler(async (request, cancellation) =>
                        {
                            var route = request.RequestUri!.AbsolutePath;
                            if (route.EndsWith("/folder")) await release.Task.WaitAsync(cancellation);
                            var json = route.EndsWith("/user") ? """{"user":{"id":2,"username":"Fixture"}}"""
                                : route.EndsWith("/team") ? """{"teams":[{"id":"w","name":"Workspace"}]}"""
                                : route.EndsWith("/space") ? """{"spaces":[{"id":"s","name":"Space"}]}"""
                                : route.Contains("/space/") && route.EndsWith("/list") ? """{"lists":[{"id":"early","name":"First list"}]}"""
                                : route.EndsWith("/folder") ? """{"folders":[{"id":"f","name":"Folder"}]}"""
                                : route.EndsWith("/list") && !missing ? """{"lists":[{"id":"late","name":"Saved list"}]}"""
                                : """{"lists":[],"folders":[]}""";
                            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
                        })));
                        lists = (PreferredListPicker)window.FindName("PreferredList");
                        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                        await Task.Delay(100);
                        check(lists.AvailableCount == 2 && lists.SelectedChoice?.Id == "late", "Partial refresh preserves saved choice before its folder loads");
                        if (scenario == 1) lists.Select("early");
                        release.SetResult();
                        await Task.Delay(100);
                        check(lists.SelectedChoice?.Id == (scenario == 1 ? "early" : "late"),
                            scenario == 1 ? "User selection during refresh wins over saved choice" : missing ? "Missing list is retained instead of silently replaced" : "Completed refresh preserves saved list arriving in a later folder");
                        window.Close();
                    }
                    var largePicker = new PreferredListPicker();
                    var manyLists = Enumerable.Range(0, 5000)
                        .Select(i => new Choice($"list-{i}", i == 4321 ? "Operations / North America / Payroll" : $"Space {i / 100} / Folder {i / 10} / List {i}"))
                        .ToList();
                    largePicker.SetChoices(manyLists, manyLists[3210]);
                    check(largePicker.AvailableCount == 5000 && largePicker.DisplayedCount == 50 && largePicker.SelectedChoice?.Id == "list-3210",
                        "Large list picker caps the initial browse set and preserves the selected list");
                    largePicker.SearchBox.Text = "operations payroll";
                    check(largePicker.DisplayedCount == 1 && largePicker.SelectedChoice?.Id == "list-3210",
                        "List search matches multiple words across full paths without changing selection");
                    largePicker.SearchBox.Text = "list-4321";
                    check(largePicker.DisplayedCount == 1, "List search matches list IDs");
                    largePicker.SearchBox.Text = "list";
                    check(largePicker.DisplayedCount == 200, "Broad list searches are capped for responsive rendering");
                    var attempted = 0;
                    var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var picker = new TaskPickerPanel(services, () => null, _ => throw new Exception("Failed creation must not select a task"), () => { },
                        () => new ClickUpClient("fixture", new DelayedHandler(async (_, cancellation) =>
                        {
                            attempted++; await failure.Task.WaitAsync(cancellation);
                            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                        })));
                    var draft = (TextBox)picker.FindName("SearchText");
                    var create = (Button)picker.FindName("CreateTask");
                    draft.Text = "Keep this task title";
                    create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check(attempted == 1 && !create.IsEnabled, "Repeated create clicks issue only one request");
                    failure.SetResult(); await Task.Delay(100);
                    check(draft.Text == "Keep this task title" && draft.IsEnabled && create.IsEnabled, "Failed creation retains editable title and allows retry");
                    check(((TextBlock)picker.FindName("Notice")).Text.Contains("retained"), "Failed creation explains recovery without clearing the draft");
                    finished.TrySetResult();
                }
                catch (Exception error) { finished.TrySetException(error); }
                finally { window?.Close(); credentials.Delete(); if (Directory.Exists(path)) Directory.Delete(path, true); dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private sealed class DelayedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
