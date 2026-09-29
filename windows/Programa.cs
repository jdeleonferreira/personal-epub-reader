using System.IO.Pipes;

namespace Atril.Windows;

/// <summary>
/// Atril para Windows (versión de la Microsoft Store).
/// Si Atril ya está abierto y se hace doble clic en un .epub, el archivo se envía a la ventana
/// abierta en lugar de abrir otra.
/// </summary>
static class Programa
{
    static readonly string NombreCanal = "Atril-" + Environment.UserName;

    [STAThread]
    static void Main(string[] args)
    {
        var archivo = args.FirstOrDefault(a => a.EndsWith(".epub", StringComparison.OrdinalIgnoreCase) && File.Exists(a));

        using var unica = new Mutex(true, "Local\\" + NombreCanal, out var primera);
        if (!primera)
        {
            EnviarAInstanciaAbierta(archivo ?? "");
            return;
        }

        ApplicationConfiguration.Initialize();
        var ventana = new VentanaPrincipal(archivo);
        EscucharOtrasInstancias(ventana);
        Application.Run(ventana);
    }

    static void EnviarAInstanciaAbierta(string archivo)
    {
        try
        {
            using var canal = new NamedPipeClientStream(".", NombreCanal, PipeDirection.Out);
            canal.Connect(3000);
            using var w = new StreamWriter(canal);
            w.WriteLine(archivo);
        }
        catch { }
    }

    static void EscucharOtrasInstancias(VentanaPrincipal ventana)
    {
        var hilo = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var canal = new NamedPipeServerStream(NombreCanal, PipeDirection.In, 1);
                    canal.WaitForConnection();
                    using var r = new StreamReader(canal);
                    var archivo = r.ReadLine() ?? "";
                    if (ventana.IsDisposed) return;
                    ventana.BeginInvoke(new Action(() =>
                    {
                        if (archivo.Length > 0) ventana.AbrirArchivo(archivo);
                        else { if (ventana.WindowState == FormWindowState.Minimized) ventana.WindowState = FormWindowState.Normal; ventana.Activate(); }
                    }));
                }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }
                catch (IOException) { Thread.Sleep(200); }
            }
        }) { IsBackground = true, Name = "Atril-instancia" };
        hilo.Start();
    }
}
