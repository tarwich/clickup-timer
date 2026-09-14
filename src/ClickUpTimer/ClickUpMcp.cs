using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ClickUpTimer;

internal sealed class ClickUpMcp : IDisposable
{
    private readonly HttpClient http;
    private string? sessionId;
    private string protocol = "2025-06-18";
    private bool initialized;
    private int sequence;
    internal ClickUpMcp(string token, HttpMessageHandler? handler = null)
    {
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
        http.Timeout = TimeSpan.FromSeconds(40);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
    }
    private async Task<JsonElement> Send(string method, object? parameters, CancellationToken ct, bool notification = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(40)); ct = timeout.Token;
        var id = Interlocked.Increment(ref sequence);
        object payload = notification ? new { jsonrpc = "2.0", method, @params = parameters } : new { jsonrpc = "2.0", id, method, @params = parameters };
        using var request = new HttpRequestMessage(HttpMethod.Post, ClickUpOAuth.Origin + "/mcp");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        if (sessionId is not null) request.Headers.Add("Mcp-Session-Id", sessionId);
        if (initialized) request.Headers.Add("MCP-Protocol-Version", protocol);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new ClickUpException("ClickUp sign-in expired. Reconnect in Settings.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new ClickUpException("ClickUp's search limit was reached. Cached results are still available; try later.", true);
        if (!response.IsSuccessStatusCode) throw new ClickUpException("ClickUp search could not connect. Cached results are still available.");
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessions)) sessionId = sessions.First();
        if (notification) return default;
        JsonElement envelope;
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
            var data = new StringBuilder();
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null || line.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        using var parsed = JsonDocument.Parse(data.ToString());
                        if (parsed.RootElement.TryGetProperty("id", out var responseId) && responseId.ToString() == id.ToString())
                        { envelope = parsed.RootElement.Clone(); break; }
                        data.Clear();
                    }
                    if (line is null) throw new ClickUpException("ClickUp search ended before returning results.");
                }
                else if (line.StartsWith("data:")) data.AppendLine(line[5..].TrimStart());
            }
        }
        else
        {
            using var parsed = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            envelope = parsed.RootElement.Clone();
        }
        if (envelope.TryGetProperty("error", out _)) throw new ClickUpException("ClickUp rejected the search request. Reconnect or try a different search.");
        return envelope.GetProperty("result").Clone();
    }
    internal async Task Initialize(CancellationToken ct)
    {
        if (initialized) return;
        var result = await Send("initialize", new { protocolVersion = protocol, capabilities = new { }, clientInfo = new { name = "clickup-timer", version = "1.0.0" } }, ct);
        protocol = result.GetProperty("protocolVersion").GetString()!;
        initialized = true;
        await Send("notifications/initialized", new { }, ct, true);
    }
    internal async Task<JsonElement> Tools(CancellationToken ct)
    {
        await Initialize(ct);
        var tools = new List<JsonElement>();
        string? cursor = null;
        var seen = new HashSet<string>();
        do
        {
            var page = await Send("tools/list", cursor is null ? new { } : (object)new { cursor }, ct);
            tools.AddRange(page.GetProperty("tools").EnumerateArray().Select(t => t.Clone()));
            cursor = page.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            if (cursor is not null && !seen.Add(cursor)) throw new ClickUpException("ClickUp repeated its tool discovery page.");
        } while (cursor is not null);
        return JsonSerializer.SerializeToElement(new { tools });
    }
    internal async Task<JsonElement> Call(string name, object arguments, CancellationToken ct)
    {
        await Initialize(ct);
        var result = await Send("tools/call", new { name, arguments }, ct);
        if (result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True)
            throw new ClickUpException("ClickUp could not complete the search. Check your connection and workspace access.");
        return result;
    }
    public void Dispose() => http.Dispose();
}
