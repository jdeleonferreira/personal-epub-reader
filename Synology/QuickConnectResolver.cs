using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Atril.Synology;

/// <summary>
/// Convierte un ID de QuickConnect (p. ej. "minas") en una dirección real del NAS.
///
/// Cómo funciona QuickConnect:
///  1. Se pregunta a global.quickconnect.to/Serv.php por el ID ("get_server_info").
///     Responde con las IP de la red local del NAS, su IP pública, DDNS, puertos y la región del relay.
///     Si el NAS está registrado en otro servidor de control, responde con "sites" y hay que repetir allí.
///  2. Con esos datos se arma una lista de direcciones candidatas, de la más rápida (red local)
///     a la más lenta (internet), y se prueba cada una con /webman/pingpong.cgi.
///     El NAS responde {"success":true,"ezid":"..."}; ezid es el MD5 del serverID, así se confirma que es el NAS correcto.
///  3. Si ninguna responde (NAS detrás de un router sin puertos abiertos), se pide un túnel
///     ("request_tunnel") y se usa el relay de Synology.
/// </summary>
public sealed class QuickConnectResolver(HttpClient http, ILogger<QuickConnectResolver> log)
{
    public sealed record Intento(string Url, string Tipo, bool Ok, string? Detalle);
    public sealed record Resultado(string? Url, List<Intento> Intentos);

    public static string NormalizarId(string id)
    {
        id = id.Trim();
        // Acepta también "https://minas.quickconnect.to/", "https://minas.us.quickconnect.to/#/..." o "QuickConnect.to/minas"
        if (Uri.TryCreate(id.Contains("://") ? id : "https://" + id, UriKind.Absolute, out var u))
        {
            // minas.quickconnect.to o minas.us.quickconnect.to (la región del relay va en medio)
            if (u.Host.EndsWith(".quickconnect.to", StringComparison.OrdinalIgnoreCase) && u.Host.Count(c => c == '.') >= 2
                && !u.Host.EndsWith(".direct.quickconnect.to", StringComparison.OrdinalIgnoreCase))
                return u.Host.Split('.')[0];
            if (u.Host.Equals("quickconnect.to", StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.Trim('/').Length > 0)
                return u.AbsolutePath.Trim('/').Split('/')[0];
        }
        return id.Trim('/');
    }

    public async Task<Resultado> ResolverAsync(string idCrudo, string servidorControl, CancellationToken ct)
    {
        var id = NormalizarId(idCrudo);
        var intentos = new List<Intento>();

        var (https, httpInfo, controlHost) = await InfoServidorAsync(id, servidorControl, "get_server_info", ct);
        var serverId = https?["server"]?["serverID"]?.GetValue<string>();

        // Directas y relay se prueban juntas; gana la de mayor prioridad que responda (la red local primero)
        var candidatos = CandidatosDirectos(id, https, httpInfo);
        candidatos.AddRange(CandidatosRelay(id, https).Where(c => !candidatos.Contains(c)));
        var url = await ProbarAsync(candidatos, serverId, intentos, ct);
        if (url != null) return new(url, intentos);

        // Nada respondió: pedir que Synology abra el túnel del relay y probar de nuevo
        var hostControl = controlHost ?? new Uri(servidorControl).Host;
        try
        {
            var (tunel, _, _) = await InfoServidorAsync(id, "https://" + hostControl, "request_tunnel", ct);
            var relay = CandidatosRelay(id, tunel ?? https);
            await Task.Delay(1000, ct); // el túnel tarda un momento en quedar listo
            url = await ProbarAsync(relay, serverId, intentos, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            intentos.Add(new("(túnel)", "relay", false, e.Message));
        }
        return new(url, intentos);
    }

    // ---------- Serv.php ----------

    async Task<(JsonNode? https, JsonNode? http, string? controlHost)> InfoServidorAsync(string id, string servidor, string comando, CancellationToken ct, int saltos = 0)
    {
        var cuerpo = new JsonArray(
            Pedido(comando, "dsm_portal_https", id),
            Pedido(comando, "dsm_portal", id));
        var destino = servidor.TrimEnd('/') + "/Serv.php";
        using var resp = await http.PostAsync(destino, new StringContent(cuerpo.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();
        var nodo = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        var lista = nodo as JsonArray ?? new JsonArray(nodo?.DeepClone());

        // Las respuestas llegan en el mismo orden de los pedidos: [0] = HTTPS, [1] = HTTP
        JsonNode? h = null, p = null;
        for (var i = 0; i < lista.Count; i++)
        {
            var r = lista[i];
            if (r is null) continue;
            var errno = r["errno"]?.GetValue<int>() ?? 0;
            // El NAS está registrado en otro servidor de control: repetir allí
            if (errno != 0 && r["sites"] is JsonArray sitios && sitios.Count > 0 && saltos < 2)
            {
                foreach (var s in sitios)
                {
                    try { return await InfoServidorAsync(id, "https://" + s!.GetValue<string>(), comando, ct, saltos + 1); }
                    catch (Exception e) when (e is not OperationCanceledException) { log.LogDebug(e, "Sitio {S} falló", s); }
                }
            }
            if (errno != 0) continue;
            if (i == 0) h = r; else p ??= r;
        }
        h ??= p;
        if (h is null)
        {
            var err = lista.FirstOrDefault()?["errno"]?.GetValue<int>();
            throw new SynologyException(err == 4
                ? $"QuickConnect no encontró el ID \"{id}\" o el NAS no tiene QuickConnect activo."
                : $"QuickConnect respondió con error {err} para \"{id}\".");
        }
        return (h, p, h["env"]?["control_host"]?.GetValue<string>());
    }

    static JsonObject Pedido(string comando, string servicio, string id) => new()
    {
        ["version"] = 1, ["command"] = comando, ["stop_when_error"] = false, ["stop_when_success"] = false,
        ["id"] = servicio, ["serverID"] = id, ["is_gofile"] = false
    };

    // ---------- Candidatos ----------

    static string? Texto(JsonNode? n) { var s = n?.GetValue<string>(); return string.IsNullOrWhiteSpace(s) || s == "NULL" ? null : s; }
    static int Entero(JsonNode? n) { try { return n?.GetValue<int>() ?? 0; } catch { return 0; } }

    public static List<(string Url, string Tipo)> CandidatosDirectos(string id, JsonNode? https, JsonNode? httpInfo)
    {
        var lista = new List<(string, string)>();
        if (https is null) return lista;
        var server = https["server"];
        var puerto = Entero(https["service"]?["port"]); if (puerto == 0) puerto = 5001;
        var puertoExt = Entero(https["service"]?["ext_port"]); if (puertoExt == 0) puertoExt = puerto;

        // Synology ya entrega nombres DNS con certificado válido para cada ruta (bloque "smartdns")
        var smart = https["smartdns"];
        var lanDns = (smart?["lan"] as JsonArray ?? []).Select(Texto).Where(x => x != null).Cast<string>().ToList();

        // 1. Red local (lo más rápido).
        foreach (var dn in lanDns) lista.Add(($"https://{dn}:{puerto}", "red local"));
        var ipsLocales = (server?["interface"] as JsonArray ?? [])
            .Select(i => Texto(i?["ip"])).Where(ip => ip != null && IPAddress.TryParse(ip, out var a) && a.AddressFamily == AddressFamily.InterNetwork)
            .Cast<string>().Distinct().ToList();
        foreach (var ip in ipsLocales)
        {
            if (lanDns.Count == 0) lista.Add(($"https://{ip.Replace('.', '-')}.{id}.direct.quickconnect.to:{puerto}", "red local"));
            lista.Add(($"https://{ip}:{puerto}", "red local"));
        }
        // 2. Internet: dominio propio, DDNS de Synology, IP pública
        if (Texto(server?["fqdn"]) is { } fqdn) lista.Add(($"https://{fqdn}:{puertoExt}", "dominio"));
        if (Texto(server?["ddns"]) is { } ddns) lista.Add(($"https://{ddns}:{puertoExt}", "DDNS"));
        if (Texto(smart?["external"]) is { } extDn) lista.Add(($"https://{extDn}:{puertoExt}", "IP pública"));
        else if (Texto(server?["external"]?["ip"]) is { } ext && ext != "0.0.0.0")
            lista.Add(($"https://{ext.Replace('.', '-')}.{id}.direct.quickconnect.to:{puertoExt}", "IP pública"));
        // 3. HTTP sin cifrar, solo dentro de la red local
        var puertoHttp = Entero(httpInfo?["service"]?["port"]);
        if (puertoHttp > 0) foreach (var ip in ipsLocales) lista.Add(($"http://{ip}:{puertoHttp}", "red local (http)"));
        return lista;
    }

    public static List<(string Url, string Tipo)> CandidatosRelay(string id, JsonNode? info)
    {
        var lista = new List<(string, string)>();
        var svc = info?["service"];
        // Es la dirección que usa el propio portal de QuickConnect (p. ej. minas.us3.quickconnect.to)
        if (Texto(info?["env"]?["relay_region"]) is { } region)
            lista.Add(($"https://{id}.{region}.quickconnect.to", "relay"));
        var puerto = Entero(svc?["relay_port"]);
        foreach (var campo in new[] { "relay_dualstack", "relay_dn" })
            if (Texto(svc?[campo]) is { } dn && puerto > 0)
                lista.Add(($"https://{dn}:{puerto}", "relay"));
        return lista.Distinct().ToList();
    }

    // ---------- Ping ----------

    async Task<string?> ProbarAsync(List<(string Url, string Tipo)> candidatos, string? serverId, List<Intento> intentos, CancellationToken ct)
    {
        if (candidatos.Count == 0) return null;
        var ezid = serverId is null ? null : Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(serverId))).ToLowerInvariant();
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(TimeSpan.FromSeconds(8));

        // Se prueban todas a la vez y se elige la primera de la lista (la de mayor prioridad) que respondió
        var tareas = candidatos.Select(async c =>
        {
            try
            {
                using var r = await http.GetAsync(c.Url + "/webman/pingpong.cgi?action=cors&quickconnect=true", limite.Token);
                var j = JsonNode.Parse(await r.Content.ReadAsStringAsync(limite.Token));
                var ok = j?["success"]?.GetValue<bool>() == true && (ezid is null || j?["ezid"]?.GetValue<string>() == ezid);
                return new Intento(c.Url, c.Tipo, ok, ok ? null : "respondió, pero no es este NAS");
            }
            catch (Exception e) { return new Intento(c.Url, c.Tipo, false, e is OperationCanceledException ? "sin respuesta" : e.GetBaseException().Message); }
        }).ToList();
        var resultados = await Task.WhenAll(tareas);
        intentos.AddRange(resultados);
        return resultados.FirstOrDefault(r => r.Ok)?.Url;
    }
}
