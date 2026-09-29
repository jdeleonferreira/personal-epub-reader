using System.IO.Pipes;

namespace Atril.Windows;

/// <summary>
/// Atril for Windows (Microsoft Store version).
/// If Atril is already open and an .epub is double-clicked, the file is sent to the open window
/// instead of opening a second one.
/// </summary>
static class Program
{
    static readonly string PipeName = "Atril-" + Environment.UserName;

    [STAThread]
    static void Main(string[] args)
    {
        var file = args.FirstOrDefault(a => a.EndsWith(".epub", StringComparison.OrdinalIgnoreCase) && File.Exists(a));

        using var single = new Mutex(true, "Local\\" + PipeName, out var first);
        if (!first)
        {
            SendToRunningInstance(file ?? "");
            return;
        }

        ApplicationConfiguration.Initialize();
        var window = new MainWindow(file);
        ListenForOtherInstances(window);
        Application.Run(window);
    }

    static void SendToRunningInstance(string file)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(3000);
            using var w = new StreamWriter(pipe);
            w.WriteLine(file);
        }
        catch { }
    }

    static void ListenForOtherInstances(MainWindow window)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    pipe.WaitForConnection();
                    using var r = new StreamReader(pipe);
                    var file = r.ReadLine() ?? "";
                    if (window.IsDisposed) return;
                    window.BeginInvoke(new Action(() =>
                    {
                        if (file.Length > 0) window.OpenFile(file);
                        else { if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal; window.Activate(); }
                    }));
                }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }
                catch (IOException) { Thread.Sleep(200); }
            }
        }) { IsBackground = true, Name = "Atril-instance" };
        thread.Start();
    }
}
