// Servidor de Atril: entrega la app web (wwwroot), guarda los libros en la carpeta que elija
// el usuario y hace de puente con el Synology (File Station).
// Lo usan dos programas:
//   - Program.cs (dotnet run / versión para navegador): escucha en localhost:5080.
//   - windows/ (app de la Microsoft Store): escucha en un puerto aleatorio de 127.0.0.1,
//     solo para la ventana de Atril, protegido con un token por sesión.

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Atril.Local;
using Atril.Synology;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Atril;

public sealed class OpcionesServidor
{
    public string[] Args { get; init; } = [];
    /// <summary>Si se indica, la cabecera X-Atril debe traer exactamente este valor.</summary>
    public string? Token { get; init; }
    /// <summary>Direcciones donde escuchar (p. ej. "http://127.0.0.1:0"). Si es null se usa la configuración.</summary>
    public string? Urls { get; init; }
    /// <summary>Modo app de escritorio: no abre el navegador ni usa el service worker.</summary>
    public bool ModoApp { get; init; }
}

public static class AtrilServidor
{
    public static string Version { get; } =
        typeof(AtrilServidor).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static WebApplication Crear(OpcionesServidor o)
    {
        // wwwroot está junto al ejecutable en la versión publicada y en la app de Windows;
        // al abrir con doble clic la carpeta actual puede ser otra.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = o.Args,
            ContentRootPath = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")) ? AppContext.BaseDirectory : null
        });

        // Configuración personal (ID de QuickConnect, carpeta del NAS...). Está en .gitignore:
        // nunca se sube a GitHub. Copia appsettings.Local.example.json como appsettings.Local.json.
        builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: !o.ModoApp);
        if (o.Urls != null) builder.WebHost.UseUrls(o.Urls);

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
                if (ctx.File.Name is "sw.js" or "index.html")
                    ctx.Context.Response.Headers.CacheControl = "no-cache";
            }
        });

        app.MapGet("/api/salud", () => Results.Ok(new { app = "Atril", version = Version, modoApp = o.ModoApp, hora = DateTimeOffset.Now }));

        // Todas las rutas de /api/local y /api/synology exigen la cabecera X-Atril.
        // En el navegador, otra página web no puede enviarla (el navegador lo bloquea entre sitios).
        // En la app de Windows, además debe traer el token secreto de la sesión.
        bool Autorizado(HttpContext c) =>
            c.Request.Headers.TryGetValue("X-Atril", out var v) && (o.Token is null || v == o.Token);

        // ---------- Carpeta local de libros ----------
        var local = app.MapGroup("/api/local").AddEndpointFilter(async (ctx, next) =>
        {
            if (!Autorizado(ctx.HttpContext)) return Results.StatusCode(403);
            try { return await next(ctx); }
            catch (BibliotecaLocalException e) { return Results.Json(new { error = e.Message }, statusCode: 400); }
            catch (UnauthorizedAccessException) { return Results.Json(new { error = "No hay permiso para usar esa carpeta." }, statusCode: 400); }
            catch (FileNotFoundException) { return Results.Json(new { error = "No se encontró el archivo." }, statusCode: 404); }
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

        // App de Windows: copia a la biblioteca un .epub abierto con doble clic desde el Explorador
        local.MapPost("/importar", async (BibliotecaLocal b, Importar p, CancellationToken ct) =>
        {
            if (!p.Ruta.EndsWith(".epub", StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(p.Ruta))
                throw new BibliotecaLocalException("Solo se pueden abrir archivos .epub.");
            var info = new FileInfo(p.Ruta);
            if (!info.Exists) throw new FileNotFoundException();
            // Si ya está dentro de la carpeta de libros, no se copia
            var raiz = Path.GetFullPath(b.Carpeta).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (info.FullName.StartsWith(raiz, StringComparison.OrdinalIgnoreCase))
                return Results.Ok(new { archivo = Path.GetRelativePath(raiz, info.FullName).Replace('\\', '/') });
            await using var f = info.OpenRead();
            return Results.Ok(new { archivo = await b.GuardarAsync(f, info.Name, info.Length, ct) });
        });

        local.MapDelete("/libro", (BibliotecaLocal b, string archivo) => { b.Borrar(archivo); return Results.Ok(); });

        // ---------- Synology ----------
        var nas = app.MapGroup("/api/synology").AddEndpointFilter(async (ctx, next) =>
        {
            if (!Autorizado(ctx.HttpContext)) return Results.StatusCode(403);
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
        // y lo entrega. La cabecera X-Atril-Archivo dice con qué nombre quedó guardado.
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

        // Versión para navegador publicada: abrir el navegador al arrancar ("AbrirNavegador": false lo desactiva)
        if (!o.ModoApp && !app.Environment.IsDevelopment() && app.Configuration.GetValue("AbrirNavegador", true))
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                var url = app.Urls.FirstOrDefault(u => u.StartsWith("http://")) ?? "http://localhost:5080";
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            });

        return app;
    }

    static bool EsIpPrivada(string host)
    {
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || b[0] == 127;
    }
}

public record CambioCarpeta(string Carpeta, bool Mover);
public record NuevaCarpeta(string Padre, string Nombre);
public record Importar(string Ruta);
