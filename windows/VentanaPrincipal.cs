using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Atril.Windows;

/// <summary>
/// La ventana de Atril: un WebView2 (el motor de Edge) que muestra la misma app web.
/// Las peticiones a https://atril.local se atienden aquí mismo con el servidor interno.
/// </summary>
sealed class VentanaPrincipal : Form
{
    static readonly string DirDatos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atril");

    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(238, 240, 242) };
    ServidorInterno? servidor;
    string? archivoPendiente;
    bool paginaLista;

    public VentanaPrincipal(string? archivoInicial)
    {
        archivoPendiente = archivoInicial;
        Text = "Atril";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        MinimumSize = new Size(480, 400);
        StartPosition = FormStartPosition.Manual;
        EstadoVentana.Restaurar(this);
        Controls.Add(web);
        Load += async (_, _) => await IniciarAsync();
        FormClosing += (_, _) => EstadoVentana.Guardar(this);
        FormClosed += async (_, _) => { if (servidor != null) await servidor.DisposeAsync(); };
    }

    async Task IniciarAsync()
    {
        try
        {
            servidor = await ServidorInterno.IniciarAsync();

            // Los datos del navegador (biblioteca, avance, ajustes) van en la carpeta del usuario:
            // la carpeta de instalación de una app de la Tienda es de solo lectura.
            var entorno = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DirDatos, "WebView2"));
            await web.EnsureCoreWebView2Async(entorno);
            var cw = web.CoreWebView2;

            cw.Settings.IsStatusBarEnabled = false;
            cw.Settings.AreDevToolsEnabled = Debugger.IsAttached;
            cw.Settings.IsPasswordAutosaveEnabled = false;
            cw.Settings.IsGeneralAutofillEnabled = false;

            cw.AddWebResourceRequestedFilter(ServidorInterno.Origen + "/*", CoreWebView2WebResourceContext.All);
            cw.WebResourceRequested += AlPedirRecurso;
            cw.WebMessageReceived += AlRecibirMensaje;

            // Enlaces externos (por ejemplo, dentro de un libro): se abren en el navegador del sistema
            cw.NewWindowRequested += (_, e) => { e.Handled = true; AbrirFuera(e.Uri); };
            cw.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith(ServidorInterno.Origen, StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
                e.Cancel = true; AbrirFuera(e.Uri);
            };
            cw.ProcessFailed += (_, e) =>
            {
                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                    MessageBox.Show(this, "El visor se cerró inesperadamente. Vuelve a abrir Atril.", "Atril", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else cw.Reload();
            };

            cw.Navigate(ServidorInterno.Origen + "/");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this,
                "Atril necesita Microsoft Edge WebView2 Runtime, que viene con Windows 10 y 11.\n\nInstálalo desde https://go.microsoft.com/fwlink/p/?LinkId=2124703 y vuelve a abrir Atril.",
                "Atril", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "No se pudo iniciar Atril:\n\n" + e.Message, "Atril", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    // ---------- Peticiones de la página → servidor interno ----------

    async void AlPedirRecurso(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var cw = web.CoreWebView2;
        var espera = e.GetDeferral();
        try
        {
            // WebView2 solo deja leer la petición en el hilo de la ventana: se copia antes de esperar
            var r = e.Request;
            var cabeceras = r.Headers.Select(h => new KeyValuePair<string, string>(h.Key, h.Value)).ToList();
            byte[]? cuerpo = null;
            if (r.Content != null)
            {
                using var ms = new MemoryStream();
                r.Content.CopyTo(ms);
                cuerpo = ms.ToArray();
            }
            var resp = await servidor!.ReenviarAsync(r.Method, r.Uri, cabeceras, cuerpo);
            e.Response = cw.Environment.CreateWebResourceResponse(resp.Contenido, resp.Estado, resp.Motivo, resp.Cabeceras);
        }
        catch (Exception ex)
        {
            var json = JsonSerializer.Serialize(new { error = "Error interno de Atril: " + ex.Message });
            e.Response = cw.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(json)), 502, "Bad Gateway", "Content-Type: application/json; charset=utf-8");
        }
        finally { espera.Complete(); }
    }

    // ---------- Mensajes de la página ----------

    void AlRecibirMensaje(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? tipo = null; string? inicial = null;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            if (doc.RootElement.TryGetProperty("tipo", out var t)) tipo = t.GetString();
            if (doc.RootElement.TryGetProperty("inicial", out var i) && i.ValueKind == JsonValueKind.String) inicial = i.GetString();
        }
        catch (JsonException) { return; }

        switch (tipo)
        {
            case "listo":
                paginaLista = true;
                if (archivoPendiente != null) { AbrirArchivo(archivoPendiente); archivoPendiente = null; }
                break;

            case "elegirCarpeta":
                using (var d = new FolderBrowserDialog
                {
                    Description = "Elige la carpeta donde Atril guardará tus libros",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = true,
                    InitialDirectory = inicial != null && Directory.Exists(inicial) ? inicial : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                })
                {
                    if (d.ShowDialog(this) == DialogResult.OK)
                        Enviar(new { tipo = "carpetaElegida", ruta = d.SelectedPath });
                }
                break;
        }
    }

    /// <summary>Abre un .epub (doble clic en el Explorador o segunda instancia de Atril).</summary>
    public void AbrirArchivo(string ruta)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        if (!paginaLista) { archivoPendiente = ruta; return; }
        Enviar(new { tipo = "abrirArchivo", ruta });
    }

    void Enviar(object mensaje) => web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(mensaje));

    static void AbrirFuera(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https" or "mailto")) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }
}

/// <summary>Recuerda el tamaño y la posición de la ventana entre sesiones.</summary>
static class EstadoVentana
{
    static readonly string Archivo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atril", "ventana.json");
    sealed record Estado(int X, int Y, int Ancho, int Alto, bool Maximizada);

    public static void Restaurar(Form f)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        f.Bounds = new Rectangle(area.X + area.Width / 10, area.Y + area.Height / 12, Math.Min(1200, area.Width * 8 / 10), Math.Min(860, area.Height * 5 / 6));
        try
        {
            if (!File.Exists(Archivo)) return;
            var e = JsonSerializer.Deserialize<Estado>(File.ReadAllText(Archivo));
            if (e is null) return;
            var r = new Rectangle(e.X, e.Y, e.Ancho, e.Alto);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r)) && e.Ancho >= f.MinimumSize.Width && e.Alto >= f.MinimumSize.Height) f.Bounds = r;
            if (e.Maximizada) f.WindowState = FormWindowState.Maximized;
        }
        catch { }
    }

    public static void Guardar(Form f)
    {
        try
        {
            var r = f.WindowState == FormWindowState.Normal ? f.Bounds : f.RestoreBounds;
            Directory.CreateDirectory(Path.GetDirectoryName(Archivo)!);
            File.WriteAllText(Archivo, JsonSerializer.Serialize(new Estado(r.X, r.Y, r.Width, r.Height, f.WindowState == FormWindowState.Maximized)));
        }
        catch { }
    }
}
