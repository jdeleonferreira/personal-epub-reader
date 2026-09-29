using System.Text.Json;

namespace Atril.Local;

public sealed record ArchivoLocal(string Archivo, long Tamano, DateTime Modificado);
public sealed record CarpetaItem(string Nombre, string Ruta);

public sealed class BibliotecaLocalException(string mensaje) : Exception(mensaje);

/// <summary>
/// La carpeta del equipo donde viven los EPUB. La elige el usuario y se guarda en
/// %LOCALAPPDATA%\Atril\config.json (fuera del proyecto, nunca va a GitHub).
/// Todas las rutas que llegan del navegador son relativas a esa carpeta y se validan para no salir de ella.
/// </summary>
public sealed class BibliotecaLocal
{
    static readonly string DirConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atril");
    static readonly string ArchivoConfig = Path.Combine(DirConfig, "config.json");
    readonly object candado = new();
    string carpeta;

    public BibliotecaLocal() => carpeta = LeerConfig() ?? PorDefecto;

    public static string PorDefecto
    {
        get
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
            return Path.Combine(docs, "Atril");
        }
    }

    public string Carpeta { get { lock (candado) return carpeta; } }

    public object Estado() => new { carpeta = Carpeta, porDefecto = PorDefecto, existe = Directory.Exists(Carpeta) };

    // ---------- Configuración ----------

    static string? LeerConfig()
    {
        try
        {
            if (!File.Exists(ArchivoConfig)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(ArchivoConfig));
            return doc.RootElement.TryGetProperty("carpeta", out var c) && c.GetString() is { Length: > 0 } s ? s : null;
        }
        catch { return null; }
    }

    /// <summary>Cambia la carpeta. Si <paramref name="mover"/>, lleva los EPUB de la carpeta anterior a la nueva.</summary>
    public (int movidos, int omitidos) CambiarCarpeta(string nueva, bool mover)
    {
        if (string.IsNullOrWhiteSpace(nueva) || !Path.IsPathFullyQualified(nueva))
            throw new BibliotecaLocalException("Elige una carpeta con ruta completa.");
        nueva = Path.GetFullPath(nueva.Trim());
        Directory.CreateDirectory(nueva);

        var anterior = Carpeta;
        int movidos = 0, omitidos = 0;
        if (mover && Directory.Exists(anterior) && !MismaRuta(anterior, nueva))
        {
            if (Dentro(anterior, nueva) || Dentro(nueva, anterior))
                throw new BibliotecaLocalException("La carpeta nueva no puede estar dentro de la anterior (ni al revés) si vas a mover los libros.");
            foreach (var origen in Directory.EnumerateFiles(anterior, "*.epub", SearchOption.AllDirectories))
            {
                var destino = Path.Combine(nueva, Path.GetRelativePath(anterior, origen));
                if (File.Exists(destino)) { omitidos++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
                File.Move(origen, destino);
                movidos++;
            }
        }

        lock (candado) carpeta = nueva;
        Directory.CreateDirectory(DirConfig);
        File.WriteAllText(ArchivoConfig, JsonSerializer.Serialize(new { carpeta = nueva }, new JsonSerializerOptions { WriteIndented = true }));
        return (movidos, omitidos);
    }

    // ---------- Libros ----------

    public List<ArchivoLocal> Listar()
    {
        var raiz = Carpeta;
        if (!Directory.Exists(raiz)) return [];
        return Directory.EnumerateFiles(raiz, "*.epub", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive })
            .Select(f => new FileInfo(f))
            .Select(f => new ArchivoLocal(Path.GetRelativePath(raiz, f.FullName).Replace('\\', '/'), f.Length, f.LastWriteTimeUtc))
            .OrderBy(a => a.Archivo, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Ruta absoluta de un archivo de la biblioteca, validando que no salga de la carpeta.</summary>
    public string RutaDe(string relativa)
    {
        var raiz = Path.GetFullPath(Carpeta);
        var completa = Path.GetFullPath(Path.Combine(raiz, relativa.Replace('/', Path.DirectorySeparatorChar)));
        if (!Dentro(completa, raiz) || !completa.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
            throw new BibliotecaLocalException("Ruta no válida.");
        return completa;
    }

    /// <summary>
    /// Guarda un EPUB en la carpeta. Si ya existe uno con el mismo nombre y tamaño, se reutiliza.
    /// Si existe con otro contenido, se guarda como "nombre (2).epub".
    /// </summary>
    public async Task<string> GuardarAsync(Stream contenido, string nombre, long? tamanoEsperado, CancellationToken ct)
    {
        var raiz = Carpeta;
        Directory.CreateDirectory(raiz);
        var limpio = NombreSeguro(nombre);
        var baseNombre = Path.GetFileNameWithoutExtension(limpio);
        var destino = Path.Combine(raiz, limpio);

        if (tamanoEsperado is long t && File.Exists(destino) && new FileInfo(destino).Length == t)
            return Path.GetRelativePath(raiz, destino).Replace('\\', '/');

        var temporal = Path.Combine(raiz, $".{Guid.NewGuid():N}.part");
        try
        {
            await using (var f = File.Create(temporal)) await contenido.CopyToAsync(f, ct);
            var largo = new FileInfo(temporal).Length;
            for (var i = 2; File.Exists(destino); i++)
            {
                if (new FileInfo(destino).Length == largo && await IgualesAsync(destino, temporal, ct)) { File.Delete(temporal); return Relativa(raiz, destino); }
                destino = Path.Combine(raiz, $"{baseNombre} ({i}).epub");
            }
            File.Move(temporal, destino);
            return Relativa(raiz, destino);
        }
        finally { if (File.Exists(temporal)) File.Delete(temporal); }
    }

    public void Borrar(string relativa)
    {
        var ruta = RutaDe(relativa);
        if (File.Exists(ruta)) File.Delete(ruta);
    }

    // ---------- Explorador de carpetas del equipo ----------

    public static object Explorar(string? ruta)
    {
        var accesos = new List<CarpetaItem>();
        void Acceso(string nombre, Environment.SpecialFolder f)
        {
            var p = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) accesos.Add(new(nombre, p));
        }
        Acceso("Inicio", Environment.SpecialFolder.UserProfile);
        Acceso("Documentos", Environment.SpecialFolder.MyDocuments);
        Acceso("Escritorio", Environment.SpecialFolder.DesktopDirectory);
        var descargas = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(descargas)) accesos.Add(new("Descargas", descargas));
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network))
            if (OperatingSystem.IsWindows() || d.Name == "/") accesos.Add(new(OperatingSystem.IsWindows() ? $"Disco {d.Name.TrimEnd('\\')}" : "Raíz /", d.Name));

        if (string.IsNullOrWhiteSpace(ruta))
            return new { ruta = (string?)null, padre = (string?)null, carpetas = accesos, accesos };

        var completa = Path.GetFullPath(ruta);
        if (!Directory.Exists(completa)) throw new BibliotecaLocalException("Esa carpeta no existe.");
        List<CarpetaItem> hijas;
        try
        {
            hijas = new DirectoryInfo(completa).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
                .Where(d => !d.Name.StartsWith('.'))
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(d => new CarpetaItem(d.Name, d.FullName)).ToList();
        }
        catch (UnauthorizedAccessException) { throw new BibliotecaLocalException("No hay permiso para abrir esa carpeta."); }
        return new { ruta = completa, padre = Directory.GetParent(completa)?.FullName, carpetas = hijas, accesos };
    }

    public static string CrearCarpeta(string padre, string nombre)
    {
        var limpio = string.Concat(nombre.Trim().Where(c => !Path.GetInvalidFileNameChars().Contains(c) && !"<>:\"/\\|?*".Contains(c)));
        if (limpio.Length == 0 || limpio is "." or "..") throw new BibliotecaLocalException("Nombre de carpeta no válido.");
        var nueva = Path.Combine(Path.GetFullPath(padre), limpio);
        Directory.CreateDirectory(nueva);
        return nueva;
    }

    // ---------- Utilidades ----------

    static string Relativa(string raiz, string ruta) => Path.GetRelativePath(raiz, ruta).Replace('\\', '/');

    /// <summary>Nombre válido en Windows, macOS y Linux, siempre terminado en .epub.</summary>
    public static string NombreSeguro(string nombre)
    {
        var solo = Path.GetFileName(nombre.Replace('\\', '/').Split('/').Last());
        var limpio = string.Concat(solo.Select(c => Path.GetInvalidFileNameChars().Contains(c) || "<>:\"/\\|?*".Contains(c) || char.IsControl(c) ? '_' : c)).Trim().TrimEnd('.');
        if (!limpio.EndsWith(".epub", StringComparison.OrdinalIgnoreCase)) limpio += ".epub";
        if (limpio.Length <= 5 || limpio.StartsWith('.')) limpio = "libro" + (limpio.StartsWith('.') ? limpio : ".epub");
        return limpio.Length > 180 ? limpio[..175] + ".epub" : limpio;
    }

    static bool MismaRuta(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    static bool Dentro(string ruta, string raiz)
    {
        var r = Path.GetFullPath(raiz).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(ruta).StartsWith(r, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    static async Task<bool> IgualesAsync(string a, string b, CancellationToken ct)
    {
        await using var fa = File.OpenRead(a); await using var fb = File.OpenRead(b);
        var ha = await System.Security.Cryptography.SHA256.HashDataAsync(fa, ct);
        var hb = await System.Security.Cryptography.SHA256.HashDataAsync(fb, ct);
        return ha.AsSpan().SequenceEqual(hb);
    }
}
