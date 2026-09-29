using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Atril.Synology;

public sealed class SynologyOptions
{
    /// <summary>QuickConnect ID (the part before .quickconnect.to).</summary>
    public string QuickConnectId { get; set; } = "";
    /// <summary>Optional: direct NAS address (https://192.168.1.10:5001 or a DDNS name). When set, QuickConnect is not used.</summary>
    public string Url { get; set; } = "";
    /// <summary>NAS folder that holds the books. Atril never goes outside it.</summary>
    public string BooksFolder { get; set; } = "/";
    /// <summary>Older name of <see cref="BooksFolder"/>, still read so existing settings keep working.</summary>
    public string? CarpetaLibros { get; set; }
    /// <summary>QuickConnect server. Only changed for tests.</summary>
    public string QuickConnectServer { get; set; } = "https://global.quickconnect.to";

    public string EffectiveBooksFolder => string.IsNullOrWhiteSpace(CarpetaLibros) ? BooksFolder : CarpetaLibros;
}

public sealed class SynologyException(string message, int code = 0, bool needsOtp = false, bool needsLogin = false) : Exception(message)
{
    public int Code { get; } = code;
    public bool NeedsOtp { get; } = needsOtp;
    public bool NeedsLogin { get; } = needsLogin;
}

/// <summary>What the user types in Atril's sign-in form.</summary>
public sealed record ConnectRequest(string? QuickConnectId, string? Username, string? Password, string? Otp);

public sealed record NasEntry(string Name, string Path, bool IsFolder, long Size);

/// <summary>
/// Talks to the DSM web API (File Station). Keeps a single session for the whole app.
/// Flow: address (QuickConnect or Url) → SYNO.API.Info (path of each API) → SYNO.API.Auth login (sid)
/// → SYNO.FileStation.List / SYNO.FileStation.Download using _sid.
/// </summary>
public sealed class SynologyService(
    HttpClient http, QuickConnectResolver resolver,
    Microsoft.Extensions.Options.IOptionsMonitor<SynologyOptions> options, ILogger<SynologyService> log)
{
    readonly SemaphoreSlim gate = new(1, 1);
    string? baseUrl, sid;
    // Credentials live only in memory: they are gone when the server stops. Used to renew an expired session.
    string? target, username, password;
    Dictionary<string, (string Path, int Max)> apis = new();
    public List<QuickConnectResolver.Attempt> LastAttempts { get; private set; } = [];

    SynologyOptions O => options.CurrentValue;
    public string Root => NormalizePath(string.IsNullOrWhiteSpace(O.EffectiveBooksFolder) ? "/" : O.EffectiveBooksFolder) ?? "/";
    public object Status() => new
    {
        connected = sid != null,
        username,
        quickConnectId = target ?? O.QuickConnectId,
        fixedAddress = O.Url.Length > 0,
        address = baseUrl,
        root = Root,
        via = O.Url.Length > 0 ? "direct" : "QuickConnect"
    };

    // ---------- Connection ----------

    public async Task ConnectAsync(ConnectRequest r, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(r.Username) && !string.IsNullOrEmpty(r.Password))
            {
                var newTarget = string.IsNullOrWhiteSpace(r.QuickConnectId) ? O.QuickConnectId : QuickConnectResolver.NormalizeId(r.QuickConnectId);
                if (O.Url.Length == 0 && string.IsNullOrWhiteSpace(newTarget))
                    throw new SynologyException("Enter your NAS QuickConnect ID.", -1);
                if (newTarget != target) { baseUrl = null; apis.Clear(); }
                target = newTarget; username = r.Username.Trim(); password = r.Password; sid = null;
            }
            try { await SignInAsync(r.Otp, ct); }
            catch (SynologyException e) when (e.Code is 400 or 401 or 402 or 406 or 407)
            {
                password = null; // never keep a password the NAS rejected
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (sid != null && apis.TryGetValue("SYNO.API.Auth", out var auth))
                try { await CallAsync(auth.Path, new() { ["api"] = "SYNO.API.Auth", ["version"] = "1", ["method"] = "logout", ["session"] = "FileStation", ["_sid"] = sid }, ct); } catch { }
            sid = null; username = null; password = null;
        }
        finally { gate.Release(); }
    }

    async Task SignInAsync(string? otp, CancellationToken ct)
    {
        if (username is null || password is null)
            throw new SynologyException("Sign in to your Synology.", needsLogin: true);

        if (baseUrl is null)
        {
            if (O.Url.Length > 0) baseUrl = O.Url.TrimEnd('/');
            else
            {
                var r = await resolver.ResolveAsync(target!, O.QuickConnectServer, ct);
                LastAttempts = r.Attempts;
                baseUrl = r.Url ?? throw new SynologyException(
                    "Couldn't reach the NAS through QuickConnect. See /api/synology/diagnostics for the addresses that were tried.");
            }
            log.LogInformation("Synology: using {Url}", baseUrl);
        }

        if (apis.Count == 0)
        {
            var info = await CallAsync("query.cgi", new()
            {
                ["api"] = "SYNO.API.Info", ["version"] = "1", ["method"] = "query",
                ["query"] = "SYNO.API.Auth,SYNO.FileStation.List,SYNO.FileStation.Download"
            }, ct);
            apis = info!.AsObject().ToDictionary(kv => kv.Key, kv => (kv.Value!["path"]!.GetValue<string>(), kv.Value!["maxVersion"]!.GetValue<int>()));
        }

        var auth = apis["SYNO.API.Auth"];
        var p = new Dictionary<string, string>
        {
            ["api"] = "SYNO.API.Auth", ["version"] = Math.Min(auth.Max, 6).ToString(), ["method"] = "login",
            ["account"] = username, ["passwd"] = password, ["session"] = "FileStation", ["format"] = "sid"
        };
        var account = $"{username}@{target ?? O.Url}";
        var device = DeviceToken.Read(account);
        if (!string.IsNullOrEmpty(otp))
        {
            p["otp_code"] = otp.Trim();
            // Ask for a "device token" so the 2FA code isn't needed every time
            p["enable_device_token"] = "yes"; p["device_name"] = "Atril";
        }
        else if (device != null) { p["device_id"] = device; p["device_name"] = "Atril"; }

        var data = await CallAsync(auth.Path, p, ct, post: true);
        sid = data?["sid"]?.GetValue<string>() ?? throw new SynologyException("The NAS didn't return a session.");
        if (data?["did"]?.GetValue<string>() is { Length: > 0 } did) DeviceToken.Save(account, did);
    }

    async Task<T> WithSessionAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        if (sid is null) await ConnectAsync(new(null, null, null, null), ct);
        try { return await action(); }
        catch (SynologyException e) when (e.Code is 106 or 107 or 119)
        {
            // Session expired: sign in again once
            sid = null; await ConnectAsync(new(null, null, null, null), ct);
            return await action();
        }
        catch (HttpRequestException)
        {
            // The NAS address changed (e.g. you left the home network): resolve it again
            baseUrl = null; sid = null; apis.Clear(); await ConnectAsync(new(null, null, null, null), ct);
            return await action();
        }
    }

    // ---------- Files ----------

    public Task<List<NasEntry>> ListAsync(string? path, CancellationToken ct) => WithSessionAsync(async () =>
    {
        var folder = AllowedPath(path ?? Root);
        JsonNode? data;
        if (folder == "/")
            data = await CallAsync(apis["SYNO.FileStation.List"].Path, new()
            { ["api"] = "SYNO.FileStation.List", ["version"] = "2", ["method"] = "list_share", ["sort_by"] = "name", ["_sid"] = sid! }, ct);
        else
            data = await CallAsync(apis["SYNO.FileStation.List"].Path, new()
            {
                ["api"] = "SYNO.FileStation.List", ["version"] = "2", ["method"] = "list", ["folder_path"] = folder,
                ["additional"] = "[\"size\"]", ["sort_by"] = "name", ["limit"] = "2000", ["_sid"] = sid!
            }, ct);

        var items = (data?["files"] ?? data?["shares"]) as JsonArray ?? [];
        return items.Select(f => new NasEntry(
                f!["name"]!.GetValue<string>(), f["path"]!.GetValue<string>(),
                f["isdir"]?.GetValue<bool>() ?? true,
                f["additional"]?["size"]?.GetValue<long>() ?? 0))
            .Where(e => e.IsFolder || e.Name.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
            .Where(e => !e.Name.StartsWith('#') && !e.Name.StartsWith('@')) // #recycle, @eaDir
            .OrderByDescending(e => e.IsFolder).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }, ct);

    public Task<HttpResponseMessage> DownloadAsync(string path, CancellationToken ct) => WithSessionAsync(async () =>
    {
        var file = AllowedPath(path);
        if (!file.EndsWith(".epub", StringComparison.OrdinalIgnoreCase)) throw new SynologyException("Only .epub files can be opened.", -1);
        var q = Query(new()
        {
            ["api"] = "SYNO.FileStation.Download", ["version"] = "2", ["method"] = "download",
            ["path"] = JsonSerializer.Serialize(new[] { file }), ["mode"] = "download", ["_sid"] = sid!
        });
        var resp = await http.GetAsync($"{baseUrl}/webapi/{apis["SYNO.FileStation.Download"].Path}?{q}", HttpCompletionOption.ResponseHeadersRead, ct);
        // When something fails, DSM answers with JSON instead of the file
        if (resp.Content.Headers.ContentType?.MediaType is "application/json" or "text/plain")
        {
            var j = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)); resp.Dispose();
            Check(j);
        }
        resp.EnsureSuccessStatusCode();
        return resp;
    }, ct);

    // ---------- Helpers ----------

    async Task<JsonNode?> CallAsync(string path, Dictionary<string, string> p, CancellationToken ct, bool post = false)
    {
        var url = $"{baseUrl}/webapi/{path}";
        using var resp = post
            ? await http.PostAsync(url, new FormUrlEncodedContent(p), ct)
            : await http.GetAsync($"{url}?{Query(p)}", ct);
        resp.EnsureSuccessStatusCode();
        var j = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        Check(j, p.GetValueOrDefault("api") == "SYNO.API.Auth");
        return j?["data"];
    }

    static string Query(Dictionary<string, string> p) => string.Join("&", p.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

    static void Check(JsonNode? j, bool isAuth = false)
    {
        if (j?["success"]?.GetValue<bool>() == true) return;
        var c = j?["error"]?["code"]?.GetValue<int>() ?? 0;
        throw c switch
        {
            400 when isAuth => new SynologyException("Wrong username or password.", c, needsLogin: true),
            401 when isAuth => new SynologyException("The account is disabled.", c),
            402 when isAuth => new SynologyException("The account isn't allowed to use File Station.", c),
            403 when isAuth => new SynologyException("This account uses two-step verification. Enter the code from your authenticator app.", c, needsOtp: true),
            404 when isAuth => new SynologyException("The verification code isn't valid.", c, needsOtp: true),
            406 when isAuth => new SynologyException("The NAS requires two-step verification for this account. Turn it on in DSM first.", c),
            407 when isAuth => new SynologyException("The NAS blocked this IP after too many failed attempts.", c),
            408 or 409 or 410 when isAuth => new SynologyException("The password expired or must be changed in DSM.", c),
            408 => new SynologyException("The file or folder doesn't exist.", c),
            105 => new SynologyException("The account doesn't have permission for that folder.", 0),
            106 or 107 or 119 => new SynologyException("The session expired.", c),
            _ => new SynologyException($"The NAS answered with error {c}.", c)
        };
    }

    /// <summary>Normalizes the path and makes sure it stays inside BooksFolder.</summary>
    string AllowedPath(string path)
    {
        var r = NormalizePath(path) ?? throw new SynologyException("Invalid path.", -1);
        var root = Root;
        if (root != "/" && r != root && !r.StartsWith(root + "/", StringComparison.Ordinal))
            throw new SynologyException("That folder is outside the configured library.", -1);
        return r;
    }

    static string? NormalizePath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or "..")) return null;
        return "/" + string.Join('/', parts);
    }
}

/// <summary>
/// Stores the 2FA device token (one per user and NAS) in the user's app data folder, never in the project.
/// With it DSM doesn't ask for the code again; the password is still asked every time the server restarts.
/// </summary>
static class DeviceToken
{
    static string FileFor(string account) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atril",
        "synology-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(account.ToLowerInvariant())))[..16] + ".txt");
    public static string? Read(string account) { try { var f = FileFor(account); return File.Exists(f) ? File.ReadAllText(f).Trim() : null; } catch { return null; } }
    public static void Save(string account, string did) { try { var f = FileFor(account); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, did); } catch { } }
}
