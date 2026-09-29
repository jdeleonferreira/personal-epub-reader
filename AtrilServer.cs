// Atril server: serves the web app (wwwroot), stores books in the folder the user picks
// and bridges to the Synology NAS (File Station).
// Used by two programs:
//   - Program.cs (dotnet run / browser version): listens on localhost:5080.
//   - windows/ (Microsoft Store app): listens on a random 127.0.0.1 port,
//     only for Atril's own window, protected with a per-session token.

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

public sealed class ServerOptions
{
    public string[] Args { get; init; } = [];
    /// <summary>When set, the X-Atril header must carry exactly this value.</summary>
    public string? Token { get; init; }
    /// <summary>Addresses to listen on (e.g. "http://127.0.0.1:0"). When null, configuration decides.</summary>
    public string? Urls { get; init; }
    /// <summary>Desktop app mode: doesn't open the browser.</summary>
    public bool AppMode { get; init; }
}

public static class AtrilServer
{
    public static string Version { get; } =
        typeof(AtrilServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static WebApplication Create(ServerOptions o)
    {
        // wwwroot sits next to the executable in the published version and in the Windows app;
        // when opened with a double click, the current directory may be somewhere else.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = o.Args,
            ContentRootPath = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")) ? AppContext.BaseDirectory : null
        });

        // Personal settings (QuickConnect ID, NAS books folder...). This file is in .gitignore:
        // it never goes to GitHub. Copy appsettings.Local.example.json to appsettings.Local.json.
        builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: !o.AppMode);
        if (o.Urls != null) builder.WebHost.UseUrls(o.Urls);

        builder.Services.Configure<SynologyOptions>(builder.Configuration.GetSection("Synology"));

        // A single HttpClient to talk to QuickConnect and the NAS.
        // Certificates must be valid, except when reaching the NAS by its private IP on the home network
        // (the NAS certificate never matches an IP like 192.168.1.10).
        builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler
        {
            UseCookies = false,
            ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None ||
                (request.RequestUri is { } u && IsPrivateIp(u.Host))
        }) { Timeout = TimeSpan.FromMinutes(10) });
        builder.Services.AddSingleton<QuickConnectResolver>();
        builder.Services.AddSingleton<SynologyService>();
        builder.Services.AddSingleton<LocalLibrary>();

        var app = builder.Build();

        // Types ASP.NET doesn't know by default
        var types = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
        types.Mappings[".webmanifest"] = "application/manifest+json";
        types.Mappings[".epub"] = "application/epub+zip";

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            ContentTypeProvider = types,
            OnPrepareResponse = ctx =>
            {
                // The service worker and the page are always revalidated so updates arrive
                if (ctx.File.Name is "sw.js" or "index.html")
                    ctx.Context.Response.Headers.CacheControl = "no-cache";
            }
        });

        app.MapGet("/api/health", () => Results.Ok(new { app = "Atril", version = Version, appMode = o.AppMode, time = DateTimeOffset.Now }));

        // Every /api/local and /api/synology route requires the X-Atril header.
        // In a browser, other websites can't send it (the browser blocks it across sites).
        // In the Windows app it must also carry the session's secret token.
        bool Authorized(HttpContext c) =>
            c.Request.Headers.TryGetValue("X-Atril", out var v) && (o.Token is null || v == o.Token);

        // ---------- Local books folder ----------
        var local = app.MapGroup("/api/local").AddEndpointFilter(async (ctx, next) =>
        {
            if (!Authorized(ctx.HttpContext)) return Results.StatusCode(403);
            try { return await next(ctx); }
            catch (LocalLibraryException e) { return Results.Json(new { error = e.Message }, statusCode: 400); }
            catch (UnauthorizedAccessException) { return Results.Json(new { error = "No permission to use that folder." }, statusCode: 400); }
            catch (FileNotFoundException) { return Results.Json(new { error = "File not found." }, statusCode: 404); }
            catch (IOException e) { return Results.Json(new { error = "Disk error: " + e.Message }, statusCode: 500); }
        });

        local.MapGet("/config", (LocalLibrary l) => Results.Ok(l.Status()));

        local.MapPut("/config", (LocalLibrary l, FolderChange p) =>
        {
            var (moved, skipped) = l.ChangeFolder(p.Folder, p.Move);
            return Results.Ok(new { status = l.Status(), moved, skipped });
        });

        local.MapGet("/browse", (string? path) => Results.Ok(LocalLibrary.Browse(path)));

        local.MapPost("/folders", (NewFolder p) => Results.Ok(new { path = LocalLibrary.CreateFolder(p.Parent, p.Name) }));

        local.MapGet("/books", (LocalLibrary l) => Results.Ok(l.List()));

        local.MapGet("/book", (LocalLibrary l, string file) =>
        {
            var path = l.PathOf(file);
            return File.Exists(path) ? Results.File(path, "application/epub+zip", enableRangeProcessing: true) : Results.NotFound(new { error = "The file is no longer in the folder." });
        });

        // The page sends an EPUB opened from this computer; it is saved in the library folder
        local.MapPost("/books", async (LocalLibrary l, string name, HttpRequest req, CancellationToken ct) =>
            Results.Ok(new { file = await l.SaveAsync(req.Body, name, null, ct) }))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(1L << 30));

        // Windows app: copies into the library an .epub opened with a double click in File Explorer
        local.MapPost("/import", async (LocalLibrary l, ImportRequest p, CancellationToken ct) =>
        {
            if (!p.Path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(p.Path))
                throw new LocalLibraryException("Only .epub files can be opened.");
            var info = new FileInfo(p.Path);
            if (!info.Exists) throw new FileNotFoundException();
            // Already inside the books folder: no copy needed
            var root = Path.GetFullPath(l.Folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (info.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return Results.Ok(new { file = Path.GetRelativePath(root, info.FullName).Replace('\\', '/') });
            await using var f = info.OpenRead();
            return Results.Ok(new { file = await l.SaveAsync(f, info.Name, info.Length, ct) });
        });

        local.MapDelete("/book", (LocalLibrary l, string file) => { l.Delete(file); return Results.Ok(); });

        // ---------- Synology ----------
        var nas = app.MapGroup("/api/synology").AddEndpointFilter(async (ctx, next) =>
        {
            if (!Authorized(ctx.HttpContext)) return Results.StatusCode(403);
            try { return await next(ctx); }
            catch (SynologyException e) { return Results.Json(new { error = e.Message, needsOtp = e.NeedsOtp, needsLogin = e.NeedsLogin },
                statusCode: e.NeedsOtp || e.NeedsLogin ? 401 : e.Code == -1 ? 400 : 502); }
            catch (HttpRequestException e) { return Results.Json(new { error = "Couldn't talk to the NAS: " + e.GetBaseException().Message }, statusCode: 502); }
            catch (TaskCanceledException) when (!ctx.HttpContext.RequestAborted.IsCancellationRequested)
            { return Results.Json(new { error = "The NAS took too long to answer." }, statusCode: 504); }
        });

        nas.MapGet("/status", (SynologyService s) => Results.Ok(s.Status()));

        // Receives username and password from Atril's form. The password only lives in the server's memory.
        nas.MapPost("/connect", async (SynologyService s, ConnectRequest? r, CancellationToken ct) =>
        {
            await s.ConnectAsync(r ?? new(null, null, null, null), ct);
            return Results.Ok(s.Status());
        });

        nas.MapPost("/disconnect", async (SynologyService s, CancellationToken ct) =>
        {
            await s.DisconnectAsync(ct);
            return Results.Ok(s.Status());
        });

        nas.MapGet("/folders", async (SynologyService s, string? path, CancellationToken ct) =>
            Results.Ok(new { path = path ?? s.Root, root = s.Root, entries = await s.ListAsync(path, ct) }));

        // Downloads the book from the NAS into the local folder (if it's already there with the same size,
        // it isn't downloaded again) and returns it. The X-Atril-File header says the name it was saved under.
        nas.MapGet("/book", async (SynologyService s, LocalLibrary l, string path, long? size, HttpContext ctx, CancellationToken ct) =>
        {
            var name = LocalLibrary.SafeFileName(path);
            var existing = Path.Combine(l.Folder, name);
            string file;
            if (size is long n && File.Exists(existing) && new FileInfo(existing).Length == n)
                file = name;
            else
            {
                using var resp = await s.DownloadAsync(path, ct);
                await using var source = await resp.Content.ReadAsStreamAsync(ct);
                file = await l.SaveAsync(source, name, size, ct);
            }
            ctx.Response.Headers["X-Atril-File"] = Uri.EscapeDataString(file);
            return Results.File(l.PathOf(file), "application/epub+zip");
        });

        nas.MapGet("/diagnostics", (SynologyService s) => Results.Ok(new { status = s.Status(), attempts = s.LastAttempts }));

        // Published browser version: open the browser on start ("OpenBrowser": false turns it off)
        if (!o.AppMode && !app.Environment.IsDevelopment() && app.Configuration.GetValue("OpenBrowser", true))
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                var url = app.Urls.FirstOrDefault(u => u.StartsWith("http://")) ?? "http://localhost:5080";
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            });

        return app;
    }

    static bool IsPrivateIp(string host)
    {
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || b[0] == 127;
    }
}

public record FolderChange(string Folder, bool Move);
public record NewFolder(string Parent, string Name);
public record ImportRequest(string Path);
