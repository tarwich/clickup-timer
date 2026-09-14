using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClickUpTimer;

internal sealed record OAuthSession(string AccessToken, string ClientId, string? RefreshToken = null, DateTimeOffset? ExpiresAt = null, bool RestCompatible = false,
    ClickUpUser? User = null, List<Choice>? Workspaces = null);

// OAuth tokens are larger than Windows Credential Manager's credential-blob limit.
// DPAPI binds this encrypted file to the current Windows user.
internal sealed class OAuthStore(string? directory = null)
{
    private readonly string path = Path.Combine(directory ?? SettingsStore.DefaultDirectory, "clickup-oauth.bin");
    internal OAuthSession? Read()
    {
        if (!File.Exists(path)) return null;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<OAuthSession>(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal void Write(OAuthSession session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(session);
        try
        {
            File.WriteAllBytes(path + ".tmp", ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
            File.Move(path + ".tmp", path, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

internal static class ClickUpOAuth
{
    internal const string Origin = "https://mcp.clickup.com";
    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static bool ValidState(string? actual, string expected) => actual is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));

    internal static async Task<OAuthSession> Connect(CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var ct = timeout.Token;
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        // Reserve an ephemeral loopback port; the listener is opened before launching the browser.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var redirect = $"http://127.0.0.1:{port}/callback/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect); listener.Start();
        var metadata = await http.GetStringAsync(Origin + "/.well-known/oauth-authorization-server", ct);
        using var discovery = JsonDocument.Parse(metadata);
        string Endpoint(string name)
        {
            var value = discovery.RootElement.GetProperty(name).GetString()!;
            var uri = new Uri(value);
            if (uri.Scheme != "https" || uri.Host != "mcp.clickup.com") throw new ClickUpException("ClickUp returned an unexpected OAuth endpoint.");
            return value;
        }
        using var registration = await http.PostAsync(Endpoint("registration_endpoint"), new StringContent(JsonSerializer.Serialize(new
        {
            client_name = "ClickUp Timer", redirect_uris = new[] { redirect },
            grant_types = new[] { "authorization_code" }, response_types = new[] { "code" }, token_endpoint_auth_method = "none"
        }), Encoding.UTF8, "application/json"), ct);
        if (!registration.IsSuccessStatusCode) throw new ClickUpException("ClickUp could not register this app for sign-in.");
        using var registered = JsonDocument.Parse(await registration.Content.ReadAsStringAsync(ct));
        var client = registered.RootElement.GetProperty("client_id").GetString()!;
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var args = new Dictionary<string, string>
        {
            ["client_id"] = client, ["redirect_uri"] = redirect, ["response_type"] = "code",
            ["scope"] = "read write", ["state"] = state, ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256", ["resource"] = Origin
        };
        var url = Endpoint("authorization_endpoint") + "?" + string.Join("&", args.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        string code;
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(ct);
            var query = context.Request.QueryString;
            var valid = context.Request.HttpMethod == "GET" && ValidState(query["state"], state) &&
                (query["iss"] is null || query["iss"] == Origin);
            code = valid ? query["code"] ?? "" : "";
            var message = !valid ? "This sign-in response was not recognized. Return to the original ClickUp sign-in tab."
                : code.Length == 0 ? "ClickUp sign-in was canceled. You can return to ClickUp Timer."
                : "ClickUp authorization received. You can close this tab and return to ClickUp Timer.";
            var body = Encoding.UTF8.GetBytes(message);
            context.Response.StatusCode = valid ? 200 : 400;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.OutputStream.WriteAsync(body, ct); context.Response.Close();
            if (!valid) continue;
            if (code.Length == 0) throw new ClickUpException("ClickUp sign-in was canceled.");
            break;
        }
        using var tokenResponse = await http.PostAsync(Endpoint("token_endpoint"), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = client,
            ["redirect_uri"] = redirect, ["code_verifier"] = verifier, ["resource"] = Origin
        }), ct);
        if (!tokenResponse.IsSuccessStatusCode) throw new ClickUpException("ClickUp could not finish sign-in. Connect again.");
        using var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));
        var root = token.RootElement;
        return new(root.GetProperty("access_token").GetString()!, client,
            root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            root.TryGetProperty("expires_in", out var expires) && expires.TryGetDouble(out var seconds) ? DateTimeOffset.UtcNow.AddSeconds(seconds) : null);
    }
}
