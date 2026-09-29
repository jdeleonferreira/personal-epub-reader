using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Atril.Synology;

public sealed class SynologyOptions
{
    /// <summary>ID de QuickConnect (lo que va antes de .quickconnect.to).</summary>
    public string QuickConnectId { get; set; } = "";
    /// <summary>Opcional: dirección directa del NAS (https://192.168.1.10:5001 o un DDNS). Si se llena, no se usa QuickConnect.</summary>
    public string Url { get; set; } = "";
    /// <summary>Carpeta del NAS donde están los libros. Atril no deja salir de ella.</summary>
    public string CarpetaLibros { get; set; } = "/libros";
    /// <summary>Servidor de QuickConnect. Solo se cambia para pruebas.</summary>
    public string ServidorQuickConnect { get; set; } = "https://global.quickconnect.to";
}

public sealed class SynologyException(string mensaje, int codigo = 0, bool requiereOtp = false, bool requiereLogin = false) : Exception(mensaje)
{
    public int Codigo { get; } = codigo;
    public bool RequiereOtp { get; } = requiereOtp;
    public bool RequiereLogin { get; } = requiereLogin;
}

/// <summary>Lo que el usuario escribe en el formulario de Atril.</summary>
public sealed record PedidoConexion(string? QuickConnectId, string? Usuario, string? Clave, string? Otp);

public sealed record Entrada(string Nombre, string Ruta, bool EsCarpeta, long Tamano);

/// <summary>
/// Habla con la API web de DSM (File Station). Mantiene una sola sesión para toda la app.
/// Flujo: dirección (QuickConnect o Url) → SYNO.API.Info (rutas de cada API) → SYNO.API.Auth login (sid)
/// → SYNO.FileStation.List / SYNO.FileStation.Download usando _sid.
/// </summary>
public sealed class SynologyService(
    HttpClient http, QuickConnectResolver resolver,
    Microsoft.Extensions.Options.IOptionsMonitor<SynologyOptions> opciones, ILogger<SynologyService> log)
{
    readonly SemaphoreSlim candado = new(1, 1);
    string? baseUrl, sid;
    // Credenciales solo en memoria: se pierden al cerrar el servidor. Sirven para renovar la sesión si vence.
    string? destino, usuario, clave;
    Dictionary<string, (string Ruta, int Max)> apis = new();
    public List<QuickConnectResolver.Intento> UltimosIntentos { get; private set; } = [];

    SynologyOptions O => opciones.CurrentValue;
    public string Raiz => NormalizarRuta(string.IsNullOrWhiteSpace(O.CarpetaLibros) ? "/" : O.CarpetaLibros) ?? "/";
    public object Estado() => new
    {
        conectado = sid != null,
        usuario,
        quickConnectId = destino ?? O.QuickConnectId,
        direccionFija = O.Url.Length > 0,
        direccion = baseUrl,
        raiz = Raiz,
        via = O.Url.Length > 0 ? "directa" : "QuickConnect"
    };

    // ---------- Conexión ----------

    public async Task ConectarAsync(PedidoConexion p, CancellationToken ct)
    {
        await candado.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(p.Usuario) && !string.IsNullOrEmpty(p.Clave))
            {
                var nuevoDestino = string.IsNullOrWhiteSpace(p.QuickConnectId) ? O.QuickConnectId : QuickConnectResolver.NormalizarId(p.QuickConnectId);
                if (O.Url.Length == 0 && string.IsNullOrWhiteSpace(nuevoDestino))
                    throw new SynologyException("Escribe el ID de QuickConnect de tu NAS.", -1);
                if (nuevoDestino != destino) { baseUrl = null; apis.Clear(); }
                destino = nuevoDestino; usuario = p.Usuario.Trim(); clave = p.Clave; sid = null;
            }
            try { await IniciarSesionAsync(p.Otp, ct); }
            catch (SynologyException e) when (e.Codigo is 400 or 401 or 402 or 406 or 407)
            {
                clave = null; // no guardar una contraseña que el NAS rechazó
                throw;
            }
        }
        finally { candado.Release(); }
    }

    public async Task DesconectarAsync(CancellationToken ct)
    {
        await candado.WaitAsync(ct);
        try
        {
            if (sid != null && apis.TryGetValue("SYNO.API.Auth", out var auth))
                try { await LlamarAsync(auth.Ruta, new() { ["api"] = "SYNO.API.Auth", ["version"] = "1", ["method"] = "logout", ["session"] = "FileStation", ["_sid"] = sid }, ct); } catch { }
            sid = null; usuario = null; clave = null;
        }
        finally { candado.Release(); }
    }

    async Task IniciarSesionAsync(string? otp, CancellationToken ct)
    {
        if (usuario is null || clave is null)
            throw new SynologyException("Inicia sesión en tu Synology.", requiereLogin: true);

        if (baseUrl is null)
        {
            if (O.Url.Length > 0) baseUrl = O.Url.TrimEnd('/');
            else
            {
                var r = await resolver.ResolverAsync(destino!, O.ServidorQuickConnect, ct);
                UltimosIntentos = r.Intentos;
                baseUrl = r.Url ?? throw new SynologyException(
                    "No se pudo llegar al NAS por QuickConnect. Revisa /api/synology/diagnostico para ver qué direcciones se probaron.");
            }
            log.LogInformation("Synology: usando {Url}", baseUrl);
        }

        if (apis.Count == 0)
        {
            var info = await LlamarAsync("query.cgi", new()
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
            ["account"] = usuario, ["passwd"] = clave, ["session"] = "FileStation", ["format"] = "sid"
        };
        var cuenta = $"{usuario}@{destino ?? O.Url}";
        var dispositivo = TokenDispositivo.Leer(cuenta);
        if (!string.IsNullOrEmpty(otp))
        {
            p["otp_code"] = otp.Trim();
            // Pide un "token de dispositivo" para no tener que escribir el código 2FA cada vez
            p["enable_device_token"] = "yes"; p["device_name"] = "Atril";
        }
        else if (dispositivo != null) { p["device_id"] = dispositivo; p["device_name"] = "Atril"; }

        var data = await LlamarAsync(auth.Ruta, p, ct, post: true);
        sid = data?["sid"]?.GetValue<string>() ?? throw new SynologyException("El NAS no devolvió una sesión.");
        if (data?["did"]?.GetValue<string>() is { Length: > 0 } did) TokenDispositivo.Guardar(cuenta, did);
    }

    async Task<T> ConSesionAsync<T>(Func<Task<T>> accion, CancellationToken ct)
    {
        if (sid is null) await ConectarAsync(new(null, null, null, null), ct);
        try { return await accion(); }
        catch (SynologyException e) when (e.Codigo is 106 or 107 or 119)
        {
            // Sesión vencida: entrar de nuevo una vez
            sid = null; await ConectarAsync(new(null, null, null, null), ct);
            return await accion();
        }
        catch (HttpRequestException)
        {
            // El NAS cambió de dirección (p. ej. saliste de la red local): resolver de nuevo
            baseUrl = null; sid = null; apis.Clear(); await ConectarAsync(new(null, null, null, null), ct);
            return await accion();
        }
    }

    // ---------- Archivos ----------

    public Task<List<Entrada>> ListarAsync(string? ruta, CancellationToken ct) => ConSesionAsync(async () =>
    {
        var carpeta = RutaPermitida(ruta ?? Raiz);
        JsonNode? data;
        if (carpeta == "/")
            data = await LlamarAsync(apis["SYNO.FileStation.List"].Ruta, new()
            { ["api"] = "SYNO.FileStation.List", ["version"] = "2", ["method"] = "list_share", ["sort_by"] = "name", ["_sid"] = sid! }, ct);
        else
            data = await LlamarAsync(apis["SYNO.FileStation.List"].Ruta, new()
            {
                ["api"] = "SYNO.FileStation.List", ["version"] = "2", ["method"] = "list", ["folder_path"] = carpeta,
                ["additional"] = "[\"size\"]", ["sort_by"] = "name", ["limit"] = "2000", ["_sid"] = sid!
            }, ct);

        var items = (data?["files"] ?? data?["shares"]) as JsonArray ?? [];
        return items.Select(f => new Entrada(
                f!["name"]!.GetValue<string>(), f["path"]!.GetValue<string>(),
                f["isdir"]?.GetValue<bool>() ?? true,
                f["additional"]?["size"]?.GetValue<long>() ?? 0))
            .Where(e => e.EsCarpeta || e.Nombre.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
            .Where(e => !e.Nombre.StartsWith('#') && !e.Nombre.StartsWith('@')) // #recycle, @eaDir
            .OrderByDescending(e => e.EsCarpeta).ThenBy(e => e.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }, ct);

    public Task<HttpResponseMessage> DescargarAsync(string ruta, CancellationToken ct) => ConSesionAsync(async () =>
    {
        var archivo = RutaPermitida(ruta);
        if (!archivo.EndsWith(".epub", StringComparison.OrdinalIgnoreCase)) throw new SynologyException("Solo se pueden abrir archivos .epub.", -1);
        var q = Consulta(new()
        {
            ["api"] = "SYNO.FileStation.Download", ["version"] = "2", ["method"] = "download",
            ["path"] = JsonSerializer.Serialize(new[] { archivo }), ["mode"] = "download", ["_sid"] = sid!
        });
        var resp = await http.GetAsync($"{baseUrl}/webapi/{apis["SYNO.FileStation.Download"].Ruta}?{q}", HttpCompletionOption.ResponseHeadersRead, ct);
        // Si algo falla, DSM responde JSON en lugar del archivo
        if (resp.Content.Headers.ContentType?.MediaType is "application/json" or "text/plain")
        {
            var j = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)); resp.Dispose();
            Verificar(j);
        }
        resp.EnsureSuccessStatusCode();
        return resp;
    }, ct);

    // ---------- Utilidades ----------

    async Task<JsonNode?> LlamarAsync(string ruta, Dictionary<string, string> p, CancellationToken ct, bool post = false)
    {
        var url = $"{baseUrl}/webapi/{ruta}";
        using var resp = post
            ? await http.PostAsync(url, new FormUrlEncodedContent(p), ct)
            : await http.GetAsync($"{url}?{Consulta(p)}", ct);
        resp.EnsureSuccessStatusCode();
        var j = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        Verificar(j, p.GetValueOrDefault("api") == "SYNO.API.Auth");
        return j?["data"];
    }

    static string Consulta(Dictionary<string, string> p) => string.Join("&", p.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

    static void Verificar(JsonNode? j, bool esAuth = false)
    {
        if (j?["success"]?.GetValue<bool>() == true) return;
        var c = j?["error"]?["code"]?.GetValue<int>() ?? 0;
        throw c switch
        {
            400 when esAuth => new SynologyException("Usuario o contraseña incorrectos.", c, requiereLogin: true),
            401 when esAuth => new SynologyException("La cuenta está deshabilitada.", c),
            402 when esAuth => new SynologyException("La cuenta no tiene permiso para usar File Station.", c),
            403 when esAuth => new SynologyException("La cuenta tiene verificación en dos pasos. Escribe el código de tu app de autenticación.", c, requiereOtp: true),
            404 when esAuth => new SynologyException("El código de verificación no es válido.", c, requiereOtp: true),
            406 when esAuth => new SynologyException("El NAS exige activar la verificación en dos pasos para esta cuenta. Actívala en DSM primero.", c),
            407 when esAuth => new SynologyException("El NAS bloqueó esta IP por demasiados intentos fallidos.", c),
            408 or 409 or 410 when esAuth => new SynologyException("La contraseña venció o debe cambiarse en DSM.", c),
            408 => new SynologyException("No existe el archivo o la carpeta.", c),
            105 => new SynologyException("La cuenta no tiene permiso sobre esa carpeta.", 0),
            106 or 107 or 119 => new SynologyException("La sesión venció.", c),
            _ => new SynologyException($"El NAS respondió con el error {c}.", c)
        };
    }

    /// <summary>Normaliza y confirma que la ruta queda dentro de CarpetaLibros.</summary>
    string RutaPermitida(string ruta)
    {
        var r = NormalizarRuta(ruta) ?? throw new SynologyException("Ruta no válida.", -1);
        var raiz = Raiz;
        if (raiz != "/" && r != raiz && !r.StartsWith(raiz + "/", StringComparison.Ordinal))
            throw new SynologyException("Esa carpeta está fuera de la biblioteca configurada.", -1);
        return r;
    }

    static string? NormalizarRuta(string ruta)
    {
        var partes = ruta.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (partes.Any(p => p is "." or "..")) return null;
        return "/" + string.Join('/', partes);
    }
}

/// <summary>
/// Guarda el token de dispositivo de 2FA (uno por usuario y NAS) en la carpeta del usuario, no en el proyecto.
/// Con él DSM no vuelve a pedir el código; la contraseña sí se pide cada vez que se reinicia el servidor.
/// </summary>
static class TokenDispositivo
{
    static string Archivo(string cuenta) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atril",
        "synology-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cuenta.ToLowerInvariant())))[..16] + ".txt");
    public static string? Leer(string cuenta) { try { var a = Archivo(cuenta); return File.Exists(a) ? File.ReadAllText(a).Trim() : null; } catch { return null; } }
    public static void Guardar(string cuenta, string did) { try { var a = Archivo(cuenta); Directory.CreateDirectory(Path.GetDirectoryName(a)!); File.WriteAllText(a, did); } catch { } }
}
