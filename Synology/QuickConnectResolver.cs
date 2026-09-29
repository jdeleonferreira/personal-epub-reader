using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Atril.Synology;

/// <summary>
/// Turns a QuickConnect ID (e.g. "mynas") into a real NAS address.
///
/// How QuickConnect works:
///  1. Ask global.quickconnect.to/Serv.php about the ID ("get_server_info").
///     It answers with the NAS LAN IPs, public IP, DDNS name, ports and relay region.
///     If the NAS is registered on another control server, it answers with "sites" and the request is repeated there.
///  2. From that data a list of candidate addresses is built, from fastest (home network)
///     to slowest (internet), and each one is checked with /webman/pingpong.cgi.
///     The NAS answers {"success":true,"ezid":"..."}; ezid is the MD5 of the serverID, which proves it's the right NAS.
///  3. If nothing answers (NAS behind a router with no open ports), a tunnel is requested
///     ("request_tunnel") and the Synology relay is used.
/// </summary>
public sealed class QuickConnectResolver(HttpClient http, ILogger<QuickConnectResolver> log)
{
    public sealed record Attempt(string Url, string Kind, bool Ok, string? Detail);
    public sealed record Result(string? Url, List<Attempt> Attempts);

    public static string NormalizeId(string id)
    {
        id = id.Trim();
        // Also accepts "https://mynas.quickconnect.to/", "https://mynas.us.quickconnect.to/#/..." or "QuickConnect.to/mynas"
        if (Uri.TryCreate(id.Contains("://") ? id : "https://" + id, UriKind.Absolute, out var u))
        {
            // mynas.quickconnect.to or mynas.us.quickconnect.to (the relay region sits in the middle)
            if (u.Host.EndsWith(".quickconnect.to", StringComparison.OrdinalIgnoreCase) && u.Host.Count(c => c == '.') >= 2
                && !u.Host.EndsWith(".direct.quickconnect.to", StringComparison.OrdinalIgnoreCase))
                return u.Host.Split('.')[0];
            if (u.Host.Equals("quickconnect.to", StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.Trim('/').Length > 0)
                return u.AbsolutePath.Trim('/').Split('/')[0];
        }
        return id.Trim('/');
    }

    public async Task<Result> ResolveAsync(string rawId, string controlServer, CancellationToken ct)
    {
        var id = NormalizeId(rawId);
        var attempts = new List<Attempt>();

        var (https, httpInfo, controlHost) = await ServerInfoAsync(id, controlServer, "get_server_info", ct);
        var serverId = https?["server"]?["serverID"]?.GetValue<string>();

        // Direct and relay addresses are tried together; the highest-priority one that answers wins (home network first)
        var candidates = DirectCandidates(id, https, httpInfo);
        candidates.AddRange(RelayCandidates(id, https).Where(c => !candidates.Contains(c)));
        var url = await ProbeAsync(candidates, serverId, attempts, ct);
        if (url != null) return new(url, attempts);

        // Nothing answered: ask Synology to open the relay tunnel and try again
        var host = controlHost ?? new Uri(controlServer).Host;
        try
        {
            var (tunnel, _, _) = await ServerInfoAsync(id, "https://" + host, "request_tunnel", ct);
            var relay = RelayCandidates(id, tunnel ?? https);
            await Task.Delay(1000, ct); // the tunnel takes a moment to be ready
            url = await ProbeAsync(relay, serverId, attempts, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            attempts.Add(new("(tunnel)", "relay", false, e.Message));
        }
        return new(url, attempts);
    }

    // ---------- Serv.php ----------

    async Task<(JsonNode? https, JsonNode? http, string? controlHost)> ServerInfoAsync(string id, string server, string command, CancellationToken ct, int hops = 0)
    {
        var body = new JsonArray(
            Request(command, "dsm_portal_https", id),
            Request(command, "dsm_portal", id));
        var endpoint = server.TrimEnd('/') + "/Serv.php";
        using var resp = await http.PostAsync(endpoint, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = node as JsonArray ?? new JsonArray(node?.DeepClone());

        // Answers come in the same order as the requests: [0] = HTTPS, [1] = HTTP
        JsonNode? h = null, p = null;
        for (var i = 0; i < list.Count; i++)
        {
            var r = list[i];
            if (r is null) continue;
            var errno = r["errno"]?.GetValue<int>() ?? 0;
            // The NAS is registered on another control server: repeat the request there
            if (errno != 0 && r["sites"] is JsonArray sites && sites.Count > 0 && hops < 2)
            {
                foreach (var s in sites)
                {
                    try { return await ServerInfoAsync(id, "https://" + s!.GetValue<string>(), command, ct, hops + 1); }
                    catch (Exception e) when (e is not OperationCanceledException) { log.LogDebug(e, "Site {S} failed", s); }
                }
            }
            if (errno != 0) continue;
            if (i == 0) h = r; else p ??= r;
        }
        h ??= p;
        if (h is null)
        {
            var err = list.FirstOrDefault()?["errno"]?.GetValue<int>();
            throw new SynologyException(err == 4
                ? $"QuickConnect couldn't find the ID \"{id}\", or QuickConnect isn't enabled on the NAS."
                : $"QuickConnect answered with error {err} for \"{id}\".");
        }
        return (h, p, h["env"]?["control_host"]?.GetValue<string>());
    }

    static JsonObject Request(string command, string service, string id) => new()
    {
        ["version"] = 1, ["command"] = command, ["stop_when_error"] = false, ["stop_when_success"] = false,
        ["id"] = service, ["serverID"] = id, ["is_gofile"] = false
    };

    // ---------- Candidates ----------

    static string? Text(JsonNode? n) { var s = n?.GetValue<string>(); return string.IsNullOrWhiteSpace(s) || s == "NULL" ? null : s; }
    static int Integer(JsonNode? n) { try { return n?.GetValue<int>() ?? 0; } catch { return 0; } }

    public static List<(string Url, string Kind)> DirectCandidates(string id, JsonNode? https, JsonNode? httpInfo)
    {
        var list = new List<(string, string)>();
        if (https is null) return list;
        var server = https["server"];
        var port = Integer(https["service"]?["port"]); if (port == 0) port = 5001;
        var extPort = Integer(https["service"]?["ext_port"]); if (extPort == 0) extPort = port;

        // Synology already provides DNS names with a valid certificate for each route ("smartdns" block)
        var smart = https["smartdns"];
        var lanDns = (smart?["lan"] as JsonArray ?? []).Select(Text).Where(x => x != null).Cast<string>().ToList();

        // 1. Home network (fastest)
        foreach (var dn in lanDns) list.Add(($"https://{dn}:{port}", "home network"));
        var lanIps = (server?["interface"] as JsonArray ?? [])
            .Select(i => Text(i?["ip"])).Where(ip => ip != null && IPAddress.TryParse(ip, out var a) && a.AddressFamily == AddressFamily.InterNetwork)
            .Cast<string>().Distinct().ToList();
        foreach (var ip in lanIps)
        {
            if (lanDns.Count == 0) list.Add(($"https://{ip.Replace('.', '-')}.{id}.direct.quickconnect.to:{port}", "home network"));
            list.Add(($"https://{ip}:{port}", "home network"));
        }
        // 2. Internet: custom domain, Synology DDNS, public IP
        if (Text(server?["fqdn"]) is { } fqdn) list.Add(($"https://{fqdn}:{extPort}", "domain"));
        if (Text(server?["ddns"]) is { } ddns) list.Add(($"https://{ddns}:{extPort}", "DDNS"));
        if (Text(smart?["external"]) is { } extDn) list.Add(($"https://{extDn}:{extPort}", "public IP"));
        else if (Text(server?["external"]?["ip"]) is { } ext && ext != "0.0.0.0")
            list.Add(($"https://{ext.Replace('.', '-')}.{id}.direct.quickconnect.to:{extPort}", "public IP"));
        // 3. Unencrypted HTTP, only inside the home network
        var httpPort = Integer(httpInfo?["service"]?["port"]);
        if (httpPort > 0) foreach (var ip in lanIps) list.Add(($"http://{ip}:{httpPort}", "home network (http)"));
        return list;
    }

    public static List<(string Url, string Kind)> RelayCandidates(string id, JsonNode? info)
    {
        var list = new List<(string, string)>();
        var svc = info?["service"];
        // This is the address QuickConnect's own portal uses (e.g. mynas.us3.quickconnect.to)
        if (Text(info?["env"]?["relay_region"]) is { } region)
            list.Add(($"https://{id}.{region}.quickconnect.to", "relay"));
        var port = Integer(svc?["relay_port"]);
        foreach (var field in new[] { "relay_dualstack", "relay_dn" })
            if (Text(svc?[field]) is { } dn && port > 0)
                list.Add(($"https://{dn}:{port}", "relay"));
        return list.Distinct().ToList();
    }

    // ---------- Ping ----------

    async Task<string?> ProbeAsync(List<(string Url, string Kind)> candidates, string? serverId, List<Attempt> attempts, CancellationToken ct)
    {
        if (candidates.Count == 0) return null;
        var ezid = serverId is null ? null : Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(serverId))).ToLowerInvariant();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(8));

        // All are tried at once; the first one in the list (highest priority) that answered is chosen
        var tasks = candidates.Select(async c =>
        {
            try
            {
                using var r = await http.GetAsync(c.Url + "/webman/pingpong.cgi?action=cors&quickconnect=true", limit.Token);
                var j = JsonNode.Parse(await r.Content.ReadAsStringAsync(limit.Token));
                var ok = j?["success"]?.GetValue<bool>() == true && (ezid is null || j?["ezid"]?.GetValue<string>() == ezid);
                return new Attempt(c.Url, c.Kind, ok, ok ? null : "answered, but it isn't this NAS");
            }
            catch (Exception e) { return new Attempt(c.Url, c.Kind, false, e is OperationCanceledException ? "no answer" : e.GetBaseException().Message); }
        }).ToList();
        var results = await Task.WhenAll(tasks);
        attempts.AddRange(results);
        return results.FirstOrDefault(r => r.Ok)?.Url;
    }
}
