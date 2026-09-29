// Atril: lector EPUB. Este servidor entrega la app web (carpeta wwwroot),
// guarda los libros en la carpeta que elija el usuario
// y hace de puente con el Synology (File Station) para leer libros del NAS.

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Atril.Local;
using Atril.Synology;

// En la versión publicada, wwwroot está junto al ejecutable; al abrirla con doble clic
// la carpeta actual puede ser otra, así que se usa la del ejecutable.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")) ? AppContext.BaseDirectory : null
});

// Configuración personal (ID de QuickConnect, carpeta del NAS...). Este archivo está en .gitignore:
// nunca se sube a GitHub. Copia appsettings.Local.example.json como appsettings.Local.json.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.Configure<SynologyOptions>(builder.Configuration.GetSection("Synology"));

// Un solo HttpClient para hablar con QuickConnect y con el NAS.
// Certificados: se exige uno válido, salvo cuando se entra por la IP privada del NAS en la red local
// (el certificado del NAS nunca coincide con una IP como 192.168.1.10).
builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler
{
    UseCookies = false,
    ServerCertificateCustomValidationCallback = (pedido, cert, chain, errores) =>
        errores == System.Net.Security.SslPolicyErrors.None ||
        (pedido.RequestUri is { } u && EsIpPrivada(u.Host))
}) { Timeout = TimeSpan.FromMinutes(10) });
builder.Services.AddSingleton<QuickConnectResolver>();
builder.Services.AddSingleton<SynologyService>();
builder.Services.AddSingleton<BibliotecaLocal>();

// Versión única del proyecto: <Version> en Atril.csproj
var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

var app = builder.Build();

// Tipos que ASP.NET no conoce por defecto
var tipos = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
tipos.Mappings[".webmanifest"] = "application/manifest+json";
tipos.Mappings[".epub"] = "application/epub+zip";

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = tipos,
    OnPrepareResponse = ctx =>
    {
        // El service worker y la página siempre se revisan para recibir actualizaciones
        var nombre = ctx.File.Name;
        if (nombre is "sw.js" or "index.html")
            ctx.Context.Response.Headers.CacheControl = "no-cache";
    }
});

app.MapGet("/api/salud", () => Results.Ok(new { app = "Atril", version, hora = DateTimeOffset.Now }));

// ---------- Carpeta local de libros ----------
var local = app.MapGroup("/api/local").AddEndpointFilter(async (ctx, next) =>
{
    if (!ctx.HttpContext.Request.Headers.ContainsKey("X-Atril")) return Results.StatusCode(403);
    try { return await next(ctx); }
    catch (BibliotecaLocalException e) { return Results.Json(new { error = e.Message }, statusCode: 400); }
    catch (UnauthorizedAccessException) { return Results.Json(new { error = "No hay permiso para usar esa carpeta." }, statusCode: 400); }
    catch (IOException e) { return Results.Json(new { error = "Error de disco: " + e.Message }, statusCode: 500); }
});

local.MapGet("/config", (BibliotecaLocal b) => Results.Ok(b.Estado()));

local.MapPut("/config", (BibliotecaLocal b, CambioCarpeta p) =>
{
    var (movidos, omitidos) = b.CambiarCarpeta(p.Carpeta, p.Mover);
    return Results.Ok(new { estado = b.Estado(), movidos, omitidos });
});

local.MapGet("/explorar", (string? ruta) => Results.Ok(BibliotecaLocal.Explorar(ruta)));

local.MapPost("/carpeta", (NuevaCarpeta p) => Results.Ok(new { ruta = BibliotecaLocal.CrearCarpeta(p.Padre, p.Nombre) }));

local.MapGet("/libros", (BibliotecaLocal b) => Results.Ok(b.Listar()));

local.MapGet("/libro", (BibliotecaLocal b, string archivo) =>
{
    var ruta = b.RutaDe(archivo);
    return File.Exists(ruta) ? Results.File(ruta, "application/epub+zip", enableRangeProcessing: true) : Results.NotFound(new { error = "El archivo ya no está en la carpeta." });
});

// El navegador envía el EPUB que abriste desde tu equipo; se guarda en la carpeta de la biblioteca
local.MapPost("/libros", async (BibliotecaLocal b, string nombre, HttpRequest req, CancellationToken ct) =>
    Results.Ok(new { archivo = await b.GuardarAsync(req.Body, nombre, null, ct) }))
    .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(1L << 30));

local.MapDelete("/libro", (BibliotecaLocal b, string archivo) => { b.Borrar(archivo); return Results.Ok(); });

// ---------- Synology ----------
// Todas las rutas exigen la cabecera X-Atril. Así otra página web abierta en el navegador
// no puede usar este puente para leer tu NAS (el navegador bloquea esa cabecera entre sitios).
var nas = app.MapGroup("/api/synology").AddEndpointFilter(async (ctx, next) =>
{
    if (!ctx.HttpContext.Request.Headers.ContainsKey("X-Atril")) return Results.StatusCode(403);
    try { return await next(ctx); }
    catch (SynologyException e) { return Results.Json(new { error = e.Message, requiereOtp = e.RequiereOtp, requiereLogin = e.RequiereLogin },
        statusCode: e.RequiereOtp || e.RequiereLogin ? 401 : e.Codigo == -1 ? 400 : 502); }
    catch (HttpRequestException e) { return Results.Json(new { error = "No se pudo comunicar con el NAS: " + e.GetBaseException().Message }, statusCode: 502); }
    catch (TaskCanceledException) when (!ctx.HttpContext.RequestAborted.IsCancellationRequested)
    { return Results.Json(new { error = "El NAS tardó demasiado en responder." }, statusCode: 504); }
});

nas.MapGet("/estado", (SynologyService s) => Results.Ok(s.Estado()));

// Recibe usuario y contraseña del formulario de Atril. La contraseña solo vive en memoria del servidor.
nas.MapPost("/conectar", async (SynologyService s, PedidoConexion? p, CancellationToken ct) =>
{
    await s.ConectarAsync(p ?? new(null, null, null, null), ct);
    return Results.Ok(s.Estado());
});

nas.MapPost("/desconectar", async (SynologyService s, CancellationToken ct) =>
{
    await s.DesconectarAsync(ct);
    return Results.Ok(s.Estado());
});

nas.MapGet("/carpetas", async (SynologyService s, string? ruta, CancellationToken ct) =>
    Results.Ok(new { ruta = ruta ?? s.Raiz, raiz = s.Raiz, entradas = await s.ListarAsync(ruta, ct) }));

// Descarga el libro del NAS a la carpeta local (si ya está con el mismo tamaño, no lo baja de nuevo)
// y lo entrega al navegador. La cabecera X-Atril-Archivo dice con qué nombre quedó guardado.
nas.MapGet("/libro", async (SynologyService s, BibliotecaLocal b, string ruta, long? tamano, HttpContext ctx, CancellationToken ct) =>
{
    var nombre = BibliotecaLocal.NombreSeguro(ruta);
    var existente = Path.Combine(b.Carpeta, nombre);
    string archivo;
    if (tamano is long t && File.Exists(existente) && new FileInfo(existente).Length == t)
        archivo = nombre;
    else
    {
        using var resp = await s.DescargarAsync(ruta, ct);
        await using var origen = await resp.Content.ReadAsStreamAsync(ct);
        archivo = await b.GuardarAsync(origen, nombre, tamano, ct);
    }
    ctx.Response.Headers["X-Atril-Archivo"] = Uri.EscapeDataString(archivo);
    return Results.File(b.RutaDe(archivo), "application/epub+zip");
});

nas.MapGet("/diagnostico", (SynologyService s) => Results.Ok(new { estado = s.Estado(), intentos = s.UltimosIntentos }));

// Versión publicada: abrir el navegador al arrancar (se desactiva con "AbrirNavegador": false)
if (!app.Environment.IsDevelopment() && app.Configuration.GetValue("AbrirNavegador", true))
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var url = app.Urls.FirstOrDefault(u => u.StartsWith("http://")) ?? "http://localhost:5080";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    });

app.Run();

static bool EsIpPrivada(string host)
{
    if (!IPAddress.TryParse(host, out var ip)) return false;
    if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
    var b = ip.GetAddressBytes();
    return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || b[0] == 127;
}

record CambioCarpeta(string Carpeta, bool Mover);
record NuevaCarpeta(string Padre, string Nombre);
public partial class Program;
