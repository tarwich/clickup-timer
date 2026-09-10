using System.Net;
using System.IO;
using System.Net.Http;
using ClickUpTimer;

var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
var requests = new List<string>();
var handler = new FixtureHandler(request =>
{
    Check(request.Headers.GetValues("Authorization").Single() == "test-only-placeholder", "API key sent only in Authorization header");
    Check(request.RequestUri!.Host == "api.clickup.com", "Requests target ClickUp");
    var path = request.RequestUri.PathAndQuery.Replace("/api/v2/", ""); requests.Add(path);
    var body = path switch
    {
        "user" => """{"user":{"id":123,"username":"Test User"}}""",
        "team" => """{"teams":[{"id":"42","name":"Workspace"}]}""",
        "team/42/space?archived=false" => """{"spaces":[{"id":"s1","name":"Engineering"}]}""",
        "space/s1/list?archived=false" => """{"lists":[{"id":"l1","name":"Inbox"}]}""",
        "space/s1/folder?archived=false" => """{"folders":[{"id":"f1","name":"Projects"}]}""",
        "folder/f1/list?archived=false" => """{"lists":[{"id":"l2","name":"Inbox"}]}""",
        "team/42/shared" => """{"lists":[{"id":"l1","name":"Inbox"},{"id":"l3","name":"Guest list"},{"id":"old","name":"Old","archived":true}],"folders":[]}""",
        "list/l1/task?page=0&subtasks=true&include_closed=true&include_timl=true" => """{"tasks":[{"id":"t1","name":"One","status":{"status":"open"}}],"last_page":false}""",
        "list/l1/task?page=1&subtasks=true&include_closed=true&include_timl=true" => """{"tasks":[{"id":"t2","name":"Two","status":{"status":"done"}}],"last_page":true}""",
        _ => throw new Exception("Unexpected fixture route: " + path)
    };
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
});
using (var api = new ClickUpClient("test-only-placeholder", handler))
{
    var user = await api.Validate(default); Check(user.Id == "123" && user.Name == "Test User", "Validate numeric user ID");
    Check((await api.Workspaces(default)).Single().Id == "42", "Discover workspace");
    var lists = await api.Lists("42", default);
    Check(lists.Count == 3 && lists.Single(l => l.Id == "l2").Name == "Engineering / Projects / Inbox", "Folder and folderless paths disambiguate names");
    Check(lists.Count(l => l.Id == "l1") == 1 && lists.Any(l => l.Id == "l3"), "Shared lists included, duplicates and archives excluded");
    Check((await api.Tasks("l1", default)).Count == 2, "Cache loader follows last_page and includes subtasks/closed tasks");
}
foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
{
    using var api = new ClickUpClient("test-only-placeholder", new FixtureHandler(_ => new(code) { Content = new StringContent("server echo: test-only-placeholder") }));
    try { await api.Validate(default); throw new Exception("Error expected"); }
    catch (ClickUpException e) { Check(!e.Message.Contains("test-only-placeholder") && !e.Message.Contains("server echo"), $"{(int)code} reports a safe error without raw response or key"); }
}
using (var api = new ClickUpClient("test-only-placeholder", new FixtureHandler(_ => throw new HttpRequestException("test-only-placeholder"))))
{
    try { await api.Validate(default); throw new Exception("Offline error expected"); }
    catch (ClickUpException e) { Check(e.Message.Contains("internet") && !e.Message.Contains("placeholder"), "Offline failure gives safe actionable message"); }
}
using (var api = new ClickUpClient("test-only-placeholder", new FixtureHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("not json") })))
{
    try { await api.Validate(default); throw new Exception("Malformed error expected"); }
    catch (ClickUpException e) { Check(e.Message.Contains("unreadable"), "Malformed JSON is reported safely"); }
}
var directory = Path.Combine(Path.GetTempPath(), "ClickUpTimer-checks-" + Guid.NewGuid());
Directory.CreateDirectory(directory);
try
{
    var store = new SettingsStore(directory);
    Check(!store.Load().LaunchAtSignIn && store.Load().Mode == "Taskbar", "Fresh defaults keep startup off");
    var config = new AppSettings { Mode = "Floating", UserId = "123", WorkspaceId = "42", PreferredListId = "l1", FloatingX = .2, FloatingY = .6 };
    store.Save(config);
    Check(new SettingsStore(directory).Load() == config, "Settings survive a fresh store instance");
    Check(!File.ReadAllText(Path.Combine(directory, "settings.json")).Contains("placeholder"), "Settings contain no API key");
    store.SaveCache(new("123", "42", "l1", DateTimeOffset.Now, [new("t1", "Task", "open")]));
    Check(store.LoadCache("123", "42", "l1")?.Tasks.Count == 1, "Task cache persists");
    Check(store.LoadCache("other-user", "42", "l1") is null && store.LoadCache("123", "42", "other-list") is null, "Cache isolated by account and location");
    File.WriteAllText(Path.Combine(directory, "settings.json"), "broken");
    var corrupt = new SettingsStore(directory); Check(corrupt.Load().Mode == "Taskbar" && corrupt.Warning is not null, "Corrupt settings recover with warning");
}
finally { Directory.Delete(directory, true); }
var vault = new CredentialStore("ClickUpTimer.Tests/" + Guid.NewGuid());
try
{
    Check(vault.Read() is null, "Isolated Credential Manager target starts empty");
    vault.Write("test-only-placeholder"); Check(vault.Read() == "test-only-placeholder", "Windows Credential Manager round trip");
    vault.Write("replacement-test-placeholder"); Check(vault.Read() == "replacement-test-placeholder", "Windows Credential Manager replacement");
}
finally { vault.Delete(); }
Check(vault.Read() is null, "Only the isolated test credential is removed");
await SettingsFlowChecks.Run(Check);
Console.WriteLine($"{checks} Phase 2 checks passed.");

sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
}
