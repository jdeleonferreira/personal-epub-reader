using System.Text.Json;

namespace Atril.Local;

public sealed record LocalBook(string File, long Size, DateTime Modified);
public sealed record FolderItem(string Name, string Path);

public sealed class LocalLibraryException(string message) : Exception(message);

/// <summary>
/// The folder on this computer where the EPUB files live. The user picks it and it is saved in
/// %LOCALAPPDATA%\Atril\config.json (outside the project, never goes to GitHub).
/// Every path coming from the page is relative to that folder and is checked so it can't leave it.
/// </summary>
public sealed class LocalLibrary
{
    static readonly string ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atril");
    static readonly string ConfigFile = Path.Combine(ConfigDir, "config.json");
    readonly object gate = new();
    string folder;

    public LocalLibrary() => folder = ReadConfig() ?? DefaultFolder;

    public static string DefaultFolder
    {
        get
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
            return Path.Combine(docs, "Atril");
        }
    }

    public string Folder { get { lock (gate) return folder; } }

    public object Status() => new { folder = Folder, defaultFolder = DefaultFolder, exists = Directory.Exists(Folder) };

    // ---------- Settings ----------

    static string? ReadConfig()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigFile));
            // "carpeta" is the key used by earlier versions
            foreach (var key in new[] { "folder", "carpeta" })
                if (doc.RootElement.TryGetProperty(key, out var c) && c.GetString() is { Length: > 0 } s) return s;
            return null;
        }
        catch { return null; }
    }

    /// <summary>Changes the folder. When <paramref name="move"/> is true, the EPUB files of the old folder are moved to the new one.</summary>
    public (int moved, int skipped) ChangeFolder(string newFolder, bool move)
    {
        if (string.IsNullOrWhiteSpace(newFolder) || !Path.IsPathFullyQualified(newFolder))
            throw new LocalLibraryException("Choose a folder with a full path.");
        newFolder = Path.GetFullPath(newFolder.Trim());
        Directory.CreateDirectory(newFolder);

        var previous = Folder;
        int moved = 0, skipped = 0;
        if (move && Directory.Exists(previous) && !SamePath(previous, newFolder))
        {
            if (IsInside(previous, newFolder) || IsInside(newFolder, previous))
                throw new LocalLibraryException("To move your books, the new folder can't be inside the old one (or the other way around).");
            foreach (var source in Directory.EnumerateFiles(previous, "*.epub", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(newFolder, Path.GetRelativePath(previous, source));
                if (File.Exists(destination)) { skipped++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(source, destination);
                moved++;
            }
        }

        lock (gate) folder = newFolder;
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(new { folder = newFolder }, new JsonSerializerOptions { WriteIndented = true }));
        return (moved, skipped);
    }

    // ---------- Books ----------

    public List<LocalBook> List()
    {
        var root = Folder;
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*.epub", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive })
            .Select(f => new FileInfo(f))
            .Select(f => new LocalBook(Path.GetRelativePath(root, f.FullName).Replace('\\', '/'), f.Length, f.LastWriteTimeUtc))
            .OrderBy(b => b.File, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Absolute path of a library file, checking that it stays inside the folder.</summary>
    public string PathOf(string relative)
    {
        var root = Path.GetFullPath(Folder);
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(full, root) || !full.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
            throw new LocalLibraryException("Invalid path.");
        return full;
    }

    /// <summary>
    /// Saves an EPUB in the folder. If one with the same name and size already exists, it is reused.
    /// If one exists with different content, the new one is saved as "name (2).epub".
    /// </summary>
    public async Task<string> SaveAsync(Stream content, string name, long? expectedSize, CancellationToken ct)
    {
        var root = Folder;
        Directory.CreateDirectory(root);
        var clean = SafeFileName(name);
        var baseName = Path.GetFileNameWithoutExtension(clean);
        var destination = Path.Combine(root, clean);

        if (expectedSize is long s && File.Exists(destination) && new FileInfo(destination).Length == s)
            return Relative(root, destination);

        var temp = Path.Combine(root, $".{Guid.NewGuid():N}.part");
        try
        {
            await using (var f = File.Create(temp)) await content.CopyToAsync(f, ct);
            var length = new FileInfo(temp).Length;
            for (var i = 2; File.Exists(destination); i++)
            {
                if (new FileInfo(destination).Length == length && await SameContentAsync(destination, temp, ct)) { File.Delete(temp); return Relative(root, destination); }
                destination = Path.Combine(root, $"{baseName} ({i}).epub");
            }
            File.Move(temp, destination);
            return Relative(root, destination);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void Delete(string relative)
    {
        var path = PathOf(relative);
        if (File.Exists(path)) File.Delete(path);
    }

    // ---------- Folder browser for this computer ----------

    public static object Browse(string? path)
    {
        var shortcuts = new List<FolderItem>();
        void Shortcut(string name, Environment.SpecialFolder f)
        {
            var p = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) shortcuts.Add(new(name, p));
        }
        Shortcut("Home", Environment.SpecialFolder.UserProfile);
        Shortcut("Documents", Environment.SpecialFolder.MyDocuments);
        Shortcut("Desktop", Environment.SpecialFolder.DesktopDirectory);
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) shortcuts.Add(new("Downloads", downloads));
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network))
            if (OperatingSystem.IsWindows() || d.Name == "/") shortcuts.Add(new(OperatingSystem.IsWindows() ? $"Drive {d.Name.TrimEnd('\\')}" : "Root /", d.Name));

        if (string.IsNullOrWhiteSpace(path))
            return new { path = (string?)null, parent = (string?)null, folders = shortcuts, shortcuts };

        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new LocalLibraryException("That folder doesn't exist.");
        List<FolderItem> children;
        try
        {
            children = new DirectoryInfo(full).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
                .Where(d => !d.Name.StartsWith('.'))
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(d => new FolderItem(d.Name, d.FullName)).ToList();
        }
        catch (UnauthorizedAccessException) { throw new LocalLibraryException("No permission to open that folder."); }
        return new { path = full, parent = Directory.GetParent(full)?.FullName, folders = children, shortcuts };
    }

    public static string CreateFolder(string parent, string name)
    {
        var clean = string.Concat(name.Trim().Where(c => !Path.GetInvalidFileNameChars().Contains(c) && !"<>:\"/\\|?*".Contains(c)));
        if (clean.Length == 0 || clean is "." or "..") throw new LocalLibraryException("Invalid folder name.");
        var created = Path.Combine(Path.GetFullPath(parent), clean);
        Directory.CreateDirectory(created);
        return created;
    }

    // ---------- Helpers ----------

    static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>A file name that is valid on Windows, macOS and Linux, always ending in .epub.</summary>
    public static string SafeFileName(string name)
    {
        var only = Path.GetFileName(name.Replace('\\', '/').Split('/').Last());
        var clean = string.Concat(only.Select(c => Path.GetInvalidFileNameChars().Contains(c) || "<>:\"/\\|?*".Contains(c) || char.IsControl(c) ? '_' : c)).Trim().TrimEnd('.');
        if (!clean.EndsWith(".epub", StringComparison.OrdinalIgnoreCase)) clean += ".epub";
        if (clean.Length <= 5 || clean.StartsWith('.')) clean = "book" + (clean.StartsWith('.') ? clean : ".epub");
        return clean.Length > 180 ? clean[..175] + ".epub" : clean;
    }

    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    static bool IsInside(string path, string root)
    {
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(r, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    static async Task<bool> SameContentAsync(string a, string b, CancellationToken ct)
    {
        await using var fa = File.OpenRead(a); await using var fb = File.OpenRead(b);
        var ha = await System.Security.Cryptography.SHA256.HashDataAsync(fa, ct);
        var hb = await System.Security.Cryptography.SHA256.HashDataAsync(fb, ct);
        return ha.AsSpan().SequenceEqual(hb);
    }
}
