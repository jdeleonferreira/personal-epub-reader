using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Atril.Windows;

/// <summary>
/// Atril's window: a WebView2 (the Edge engine) showing the same web app.
/// Requests to https://atril.local are served right here by the internal server.
/// </summary>
sealed class MainWindow : Form
{
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atril");

    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(238, 240, 242) };
    InternalServer? server;
    string? pendingFile;
    bool pageReady;

    public MainWindow(string? initialFile)
    {
        pendingFile = initialFile;
        Text = "Atril";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        MinimumSize = new Size(480, 400);
        StartPosition = FormStartPosition.Manual;
        WindowPlacement.Restore(this);
        Controls.Add(web);
        Load += async (_, _) => await InitializeAsync();
        FormClosing += (_, _) => WindowPlacement.Save(this);
        FormClosed += async (_, _) => { if (server != null) await server.DisposeAsync(); };
    }

    async Task InitializeAsync()
    {
        try
        {
            server = await InternalServer.StartAsync();

            // Web view data (library, progress, settings) goes in the user's folder:
            // a Store app's install folder is read-only.
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "WebView2"));
            await web.EnsureCoreWebView2Async(environment);
            var cw = web.CoreWebView2;

            cw.Settings.IsStatusBarEnabled = false;
            cw.Settings.AreDevToolsEnabled = Debugger.IsAttached;
            cw.Settings.IsPasswordAutosaveEnabled = false;
            cw.Settings.IsGeneralAutofillEnabled = false;

            cw.AddWebResourceRequestedFilter(InternalServer.Origin + "/*", CoreWebView2WebResourceContext.All);
            cw.WebResourceRequested += OnResourceRequested;
            cw.WebMessageReceived += OnMessageReceived;

            // External links (e.g. inside a book) open in the system browser
            cw.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternally(e.Uri); };
            cw.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith(InternalServer.Origin, StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
                e.Cancel = true; OpenExternally(e.Uri);
            };
            cw.ProcessFailed += (_, e) =>
            {
                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                    MessageBox.Show(this, "The viewer closed unexpectedly. Please open Atril again.", "Atril", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else cw.Reload();
            };

            cw.Navigate(InternalServer.Origin + "/");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this,
                "Atril needs the Microsoft Edge WebView2 Runtime, which comes with Windows 10 and 11.\n\nInstall it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and open Atril again.",
                "Atril", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "Atril couldn't start:\n\n" + e.Message, "Atril", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    // ---------- Page requests → internal server ----------

    async void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var cw = web.CoreWebView2;
        var deferral = e.GetDeferral();
        try
        {
            // WebView2 only allows reading the request on the window thread: copy it before awaiting
            var r = e.Request;
            var headers = r.Headers.Select(h => new KeyValuePair<string, string>(h.Key, h.Value)).ToList();
            byte[]? body = null;
            if (r.Content != null)
            {
                using var ms = new MemoryStream();
                r.Content.CopyTo(ms);
                body = ms.ToArray();
            }
            var resp = await server!.ForwardAsync(r.Method, r.Uri, headers, body);
            e.Response = cw.Environment.CreateWebResourceResponse(resp.Content, resp.Status, resp.Reason, resp.Headers);
        }
        catch (Exception ex)
        {
            var json = JsonSerializer.Serialize(new { error = "Atril internal error: " + ex.Message });
            e.Response = cw.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(json)), 502, "Bad Gateway", "Content-Type: application/json; charset=utf-8");
        }
        finally { deferral.Complete(); }
    }

    // ---------- Messages from the page ----------

    void OnMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? type = null; string? initial = null;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            if (doc.RootElement.TryGetProperty("type", out var t)) type = t.GetString();
            if (doc.RootElement.TryGetProperty("initial", out var i) && i.ValueKind == JsonValueKind.String) initial = i.GetString();
        }
        catch (JsonException) { return; }

        switch (type)
        {
            case "ready":
                pageReady = true;
                if (pendingFile != null) { OpenFile(pendingFile); pendingFile = null; }
                break;

            case "pickFolder":
                using (var d = new FolderBrowserDialog
                {
                    Description = "Choose the folder where Atril keeps your books",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = true,
                    InitialDirectory = initial != null && Directory.Exists(initial) ? initial : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                })
                {
                    if (d.ShowDialog(this) == DialogResult.OK)
                        Send(new { type = "folderPicked", path = d.SelectedPath });
                }
                break;
        }
    }

    /// <summary>Opens an .epub (double click in File Explorer or a second Atril instance).</summary>
    public void OpenFile(string path)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        if (!pageReady) { pendingFile = path; return; }
        Send(new { type = "openFile", path });
    }

    void Send(object message) => web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message));

    static void OpenExternally(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https" or "mailto")) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }
}

/// <summary>Remembers the window size and position between sessions.</summary>
static class WindowPlacement
{
    static readonly string StateFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atril", "window.json");
    sealed record State(int X, int Y, int Width, int Height, bool Maximized);

    public static void Restore(Form f)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        f.Bounds = new Rectangle(area.X + area.Width / 10, area.Y + area.Height / 12, Math.Min(1200, area.Width * 8 / 10), Math.Min(860, area.Height * 5 / 6));
        try
        {
            if (!File.Exists(StateFile)) return;
            var s = JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile));
            if (s is null) return;
            var r = new Rectangle(s.X, s.Y, s.Width, s.Height);
            if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r)) && s.Width >= f.MinimumSize.Width && s.Height >= f.MinimumSize.Height) f.Bounds = r;
            if (s.Maximized) f.WindowState = FormWindowState.Maximized;
        }
        catch { }
    }

    public static void Save(Form f)
    {
        try
        {
            var r = f.WindowState == FormWindowState.Normal ? f.Bounds : f.RestoreBounds;
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(new State(r.X, r.Y, r.Width, r.Height, f.WindowState == FormWindowState.Maximized)));
        }
        catch { }
    }
}
