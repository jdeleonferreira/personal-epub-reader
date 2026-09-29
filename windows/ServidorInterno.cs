using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Atril.Windows;

/// <summary>Respuesta lista para entregar a WebView2.</summary>
public sealed record RespuestaInterna(int Estado, string Motivo, string Cabeceras, Stream Contenido);

/// <summary>
/// El servidor de Atril corriendo dentro de la app, solo en 127.0.0.1 y en un puerto aleatorio.
///
/// La ventana no navega a ese puerto: navega a https://atril.local (un origen fijo, para que la
/// biblioteca y el avance guardados en el navegador no se pierdan entre sesiones) y WebView2 le
/// pasa cada petición a esta clase, que la reenvía al servidor agregando el token secreto de la sesión.
/// Así ningún otro programa ni página web puede usar la API aunque encuentre el puerto.
///
/// No depende de Windows Forms ni de WebView2, para poder probarla en cualquier sistema.
/// </summary>
public sealed class ServidorInterno : IAsyncDisposable
{
    public const string Origen = "https://atril.local";

    // Cabeceras que no se copian de la petición del navegador al servidor
    static readonly HashSet<string> Omitir = new(StringComparer.OrdinalIgnoreCase)
    { "Host", "Origin", "Referer", "Cookie", "Connection", "Content-Length", "Transfer-Encoding", "X-Atril" };

    readonly WebApplication app;
    readonly HttpClient cliente;
    public string Token { get; }
    public Uri Direccion { get; }

    ServidorInterno(WebApplication app, Uri direccion, string token)
    {
        this.app = app; Direccion = direccion; Token = token;
        cliente = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        { BaseAddress = direccion, Timeout = Timeout.InfiniteTimeSpan };
    }

    public static async Task<ServidorInterno> IniciarAsync(CancellationToken ct = default)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var app = AtrilServidor.Crear(new OpcionesServidor { Token = token, Urls = "http://127.0.0.1:0", ModoApp = true });
        await app.StartAsync(ct);
        var direccion = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new ServidorInterno(app, new Uri(direccion), token);
    }

    /// <summary>
    /// Reenvía una petición hecha a https://atril.local/... al servidor interno.
    /// <paramref name="cuerpo"/> debe venir ya copiado en memoria (WebView2 solo deja leerlo en el hilo de la ventana).
    /// </summary>
    public async Task<RespuestaInterna> ReenviarAsync(string metodo, string uri, IReadOnlyList<KeyValuePair<string, string>> cabeceras,
        byte[]? cuerpo, CancellationToken ct = default)
    {
        var u = new Uri(uri);
        using var pedido = new HttpRequestMessage(new HttpMethod(metodo), u.PathAndQuery);
        if (cuerpo is { Length: > 0 } && metodo is not ("GET" or "HEAD"))
            pedido.Content = new ByteArrayContent(cuerpo);

        foreach (var (nombre, valor) in cabeceras)
        {
            if (Omitir.Contains(nombre)) continue;
            if (!pedido.Headers.TryAddWithoutValidation(nombre, valor))
                pedido.Content?.Headers.TryAddWithoutValidation(nombre, valor);
        }
        pedido.Headers.TryAddWithoutValidation("X-Atril", Token);

        using var resp = await cliente.SendAsync(pedido, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // WebView2 lee el contenido desde su propio hilo: se entrega en memoria para evitar problemas con flujos de red
        var contenido = new MemoryStream();
        await resp.Content.CopyToAsync(contenido, ct).ConfigureAwait(false);
        contenido.Position = 0;

        var lineas = new List<string>();
        foreach (var h in resp.Headers.Concat(resp.Content.Headers))
        {
            if (h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            lineas.Add($"{h.Key}: {string.Join(", ", h.Value)}");
        }
        return new RespuestaInterna((int)resp.StatusCode, resp.ReasonPhrase ?? "", string.Join("\r\n", lineas), contenido);
    }

    public async ValueTask DisposeAsync()
    {
        cliente.Dispose();
        using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await app.StopAsync(limite.Token); } catch { }
        await app.DisposeAsync();
    }
}
