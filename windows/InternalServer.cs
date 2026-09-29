using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Atril.Windows;

/// <summary>A response ready to hand to WebView2.</summary>
public sealed record InternalResponse(int Status, string Reason, string Headers, Stream Content);

/// <summary>
/// The Atril server running inside the app, only on 127.0.0.1 and on a random port.
///
/// The window doesn't navigate to that port: it navigates to https://atril.local (a fixed origin, so the
/// library and reading progress stored by the web view survive between sessions) and WebView2 hands every
/// request to this class, which forwards it to the server adding the session's secret token.
/// That way no other program or website can use the API even if it finds the port.
///
/// Doesn't depend on Windows Forms or WebView2, so it can be tested on any OS.
/// </summary>
public sealed class InternalServer : IAsyncDisposable
{
    public const string Origin = "https://atril.local";

    // Headers not copied from the web view request to the server
    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    { "Host", "Origin", "Referer", "Cookie", "Connection", "Content-Length", "Transfer-Encoding", "X-Atril" };

    readonly WebApplication app;
    readonly HttpClient client;
    public string Token { get; }
    public Uri Address { get; }

    InternalServer(WebApplication app, Uri address, string token)
    {
        this.app = app; Address = address; Token = token;
        client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        { BaseAddress = address, Timeout = Timeout.InfiniteTimeSpan };
    }

    public static async Task<InternalServer> StartAsync(CancellationToken ct = default)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var app = AtrilServer.Create(new ServerOptions { Token = token, Urls = "http://127.0.0.1:0", AppMode = true });
        await app.StartAsync(ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new InternalServer(app, new Uri(address), token);
    }

    /// <summary>
    /// Forwards a request made to https://atril.local/... to the internal server.
    /// <paramref name="body"/> must already be copied into memory (WebView2 only allows reading it on the window thread).
    /// </summary>
    public async Task<InternalResponse> ForwardAsync(string method, string uri, IReadOnlyList<KeyValuePair<string, string>> headers,
        byte[]? body, CancellationToken ct = default)
    {
        var u = new Uri(uri);
        using var request = new HttpRequestMessage(new HttpMethod(method), u.PathAndQuery);
        if (body is { Length: > 0 } && method is not ("GET" or "HEAD"))
            request.Content = new ByteArrayContent(body);

        foreach (var (name, value) in headers)
        {
            if (Skip.Contains(name)) continue;
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content?.Headers.TryAddWithoutValidation(name, value);
        }
        request.Headers.TryAddWithoutValidation("X-Atril", Token);

        using var resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // WebView2 reads the content from its own thread: hand it over in memory to avoid issues with network streams
        var content = new MemoryStream();
        await resp.Content.CopyToAsync(content, ct).ConfigureAwait(false);
        content.Position = 0;

        var lines = new List<string>();
        foreach (var h in resp.Headers.Concat(resp.Content.Headers))
        {
            if (h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            lines.Add($"{h.Key}: {string.Join(", ", h.Value)}");
        }
        return new InternalResponse((int)resp.StatusCode, resp.ReasonPhrase ?? "", string.Join("\r\n", lines), content);
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await app.StopAsync(limit.Token); } catch { }
        await app.DisposeAsync();
    }
}
