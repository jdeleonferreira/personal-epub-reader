// In-app updates: looks for new Atril versions in the GitHub Releases of the repository
// (free, no account or key needed) and, in the Windows app, downloads and installs them.
//
//   Check:   GET https://api.github.com/repos/{Repository}/releases  (only public data, no personal data is sent)
//   Install: downloads Atril-Windows-{x64|arm64}.zip from the newest release, checks its SHA-256,
//            unpacks it and hands over to a small PowerShell script that waits for Atril to close,
//            swaps the program folder (keeping the old one until the copy succeeds) and opens Atril again.
// Books, library, reading progress and settings live outside the program folder, so they are kept.

using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Atril.Updates;

public sealed class UpdateOptions
{
    /// <summary>GitHub repository ("owner/name") whose Releases publish Atril versions.</summary>
    public string Repository { get; set; } = "jdeleonferreira/personal-epub-reader";
    /// <summary>Checks for new versions when Atril opens.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>GitHub API address (only changed for tests).</summary>
    public string Api { get; set; } = "https://api.github.com";
}

public sealed class UpdateException(string message) : Exception(message);

public sealed record ReleaseNote(string Version, string? Date, string Notes, string Url);

public sealed record UpdateInfo(
    string Current,
    bool Available,
    string? Latest,
    string? Url,
    IReadOnlyList<ReleaseNote> Releases,
    bool CanInstall,
    string? InstallNote,
    string? Error,
    UpdateResult? LastUpdate,
    DateTimeOffset Checked);

/// <summary>What happened with the last update (written by the update script).</summary>
public sealed record UpdateResult(string Version, bool Ok, string? Error);

public sealed class UpdateService(HttpClient http, IOptions<UpdateOptions> options, ServerOptions server)
{
    public const string ScriptName = "atril-update.ps1";
    static readonly string ResultFile = Path.Combine(Synology.CredentialStore.DataDir, "update-result.json");
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly SemaphoreSlim gate = new(1, 1);
    UpdateInfo? cached;
    Release? latest;

    public static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    public static string AssetName => $"Atril-Windows-{Arch}.zip";

    /// <summary>Folder Atril runs from, without the trailing separator.</summary>
    static string InstallDir => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>Whether this copy of Atril can replace itself, and if not, how to update it.</summary>
    (bool can, string? note) InstallMode()
    {
        if (!server.AppMode || !OperatingSystem.IsWindows())
            return (false, "This is the browser version: download the new version from its release page, or run git pull and start Atril again.");
        if (InstallDir.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            return (false, "Atril from the Microsoft Store is updated by the Store.");
        return (true, null);
    }

    public async Task<UpdateInfo> CheckAsync(bool force, CancellationToken ct)
    {
        var current = AtrilServer.Version;
        var (can, note) = InstallMode();
        var last = ReadLastResult(current);
        if (!force && cached is { } c && DateTimeOffset.Now - c.Checked < TimeSpan.FromMinutes(30))
            return c with { LastUpdate = last };

        try
        {
            var releases = await GetReleasesAsync(ct);
            var mine = ParseVersion(current);
            var newer = releases.Where(r => r.Version > mine).OrderByDescending(r => r.Version).ToList();
            latest = newer.FirstOrDefault();
            var notes = newer.Select(r => new ReleaseNote(r.Version.ToString(3), r.Published?.ToString("yyyy-MM-dd"), r.Body, r.Page)).ToList();
            if (latest != null && can && latest.Asset(AssetName) is null)
                (can, note) = (false, $"This release doesn't include {AssetName}: download it from its release page.");
            cached = new UpdateInfo(current, latest != null, latest?.Version.ToString(3), latest?.Page, notes, can, note, null, last, DateTimeOffset.Now);
            return cached;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or UpdateException)
        {
            if (ct.IsCancellationRequested) throw;
            var msg = e is UpdateException ? e.Message : "Couldn't check for updates (no internet connection?).";
            return new UpdateInfo(current, false, null, null, [], can, note, msg, last, DateTimeOffset.Now);
        }
    }

    async Task<List<Release>> GetReleasesAsync(CancellationToken ct)
    {
        var o = options.Value;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{o.Api.TrimEnd('/')}/repos/{o.Repository}/releases?per_page=30");
        req.Headers.UserAgent.ParseAdd($"Atril/{AtrilServer.Version}");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await http.SendAsync(req, timeout.Token);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new UpdateException($"The repository {o.Repository} wasn't found on GitHub (or it is private).");
        if ((int)resp.StatusCode is 403 or 429)
            throw new UpdateException("GitHub is limiting requests for now. Try again in a while.");
        resp.EnsureSuccessStatusCode();
        var list = await resp.Content.ReadFromJsonAsync<List<Release>>(Json, timeout.Token) ?? [];
        return list.Where(r => !r.Draft && !r.Prerelease && r.Version > new Version(0, 0, 0)).ToList();
    }

    /// <summary>
    /// Downloads and unpacks the newest version, then starts the script that replaces the program folder
    /// once Atril closes. The caller must close Atril right after this returns.
    /// </summary>
    public async Task<string> InstallAsync(CancellationToken ct)
    {
        var (can, note) = InstallMode();
        if (!can) throw new UpdateException(note!);
        if (!await gate.WaitAsync(0, ct)) throw new UpdateException("The update is already being installed.");
        try
        {
            if (latest is null) await CheckAsync(true, ct);
            var release = latest ?? throw new UpdateException("Atril is already up to date.");
            var asset = release.Asset(AssetName) ?? throw new UpdateException($"The release doesn't include {AssetName}.");
            var version = release.Version.ToString(3);

            var work = Path.Combine(Path.GetTempPath(), "Atril-update-" + version);
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            var zip = Path.Combine(work, asset.Name);
            var unpacked = Path.Combine(work, "app");

            using (var req = new HttpRequestMessage(HttpMethod.Get, asset.Url))
            {
                req.Headers.UserAgent.ParseAdd($"Atril/{AtrilServer.Version}");
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using var source = await resp.Content.ReadAsStreamAsync(ct);
                await using var file = File.Create(zip);
                await source.CopyToAsync(file, ct);
            }
            var info = new FileInfo(zip);
            if (asset.Size > 0 && info.Length != asset.Size) throw new UpdateException("The download is incomplete. Try again.");
            // GitHub publishes the SHA-256 of every release file
            if (asset.Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                await using var f = File.OpenRead(zip);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
                if (!hash.Equals(d[7..], StringComparison.OrdinalIgnoreCase)) throw new UpdateException("The downloaded file is damaged (its checksum doesn't match). Try again.");
            }

            ZipFile.ExtractToDirectory(zip, unpacked);
            File.Delete(zip);
            // The zip may hold the files directly or inside a single folder
            var root = unpacked;
            if (!File.Exists(Path.Combine(root, "Atril.exe")) && Directory.GetFiles(root).Length == 0 && Directory.GetDirectories(root) is [var only])
                root = only;
            if (!File.Exists(Path.Combine(root, "Atril.exe")) || !Directory.Exists(Path.Combine(root, "wwwroot")))
                throw new UpdateException("The downloaded file isn't an Atril for Windows package.");

            var script = Path.Combine(work, ScriptName);
            await File.WriteAllTextAsync(script, UpdateScript, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(ResultFile)!);
            if (File.Exists(ResultFile)) File.Delete(ResultFile);

            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                         "-ProcessId", Environment.ProcessId.ToString(), "-Source", root, "-Target", InstallDir,
                         "-Version", version, "-Work", work, "-Result", ResultFile })
                start.ArgumentList.Add(a);
            Process.Start(start);
            return version;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException)
        {
            throw new UpdateException("Couldn't download the update: " + e.Message);
        }
        finally { gate.Release(); }
    }

    /// <summary>Result of the last update, shown once after Atril opens again.</summary>
    static UpdateResult? ReadLastResult(string current)
    {
        try
        {
            if (!File.Exists(ResultFile)) return null;
            var r = JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(ResultFile).TrimStart('﻿'), Json);
            File.Delete(ResultFile);
            return r is null ? null : r with { Ok = r.Ok && r.Version == current };
        }
        catch { return null; }
    }

    static Version ParseVersion(string? s)
    {
        s = s?.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(s, out var v) && v.Build >= 0 ? new Version(v.Major, v.Minor, v.Build) : new Version(0, 0, 0);
    }

    sealed class Release
    {
        [JsonPropertyName("tag_name")] public string Tag { get; set; } = "";
        [JsonPropertyName("html_url")] public string Page { get; set; } = "";
        [JsonPropertyName("body")] public string? BodyRaw { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("published_at")] public DateTimeOffset? Published { get; set; }
        [JsonPropertyName("assets")] public List<Asset> Assets { get; set; } = [];
        [JsonIgnore] public string Body => BodyRaw ?? "";
        [JsonIgnore] public Version Version => ParseVersion(Tag);
        public Asset? Asset(string name) => Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    sealed class Asset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string Url { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }

    // Runs with Windows PowerShell 5.1 after Atril closes. ASCII only (PowerShell 5.1 reads scripts without a BOM as ANSI).
    const string UpdateScript = """
        param([int]$ProcessId, [string]$Source, [string]$Target, [string]$Version, [string]$Work, [string]$Result)
        $ErrorActionPreference = 'Stop'
        $log = Join-Path (Split-Path $Result -Parent) 'update.log'
        function Write-Log($m) { Add-Content -Path $log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  ' + $m) }
        function Save-Result($ok, $err) { @{ version = $Version; ok = $ok; error = $err } | ConvertTo-Json | Set-Content -Path $Result -Encoding UTF8 }
        $old = $Target + '.old'
        try {
          Write-Log "Updating Atril to $Version in $Target"
          $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
          if ($p -and -not $p.WaitForExit(60000)) { $p.Kill(); $p.WaitForExit(10000) | Out-Null }
          if (Test-Path $old) { Remove-Item $old -Recurse -Force }
          # The viewer (WebView2) may keep files open for a few seconds after the window closes
          $moved = $false
          for ($i = 0; $i -lt 40 -and -not $moved; $i++) {
            try { Rename-Item -Path $Target -NewName (Split-Path $old -Leaf); $moved = $true } catch { Start-Sleep -Milliseconds 500 }
          }
          if (-not $moved) { throw "Atril's folder is in use ($Target). Close Atril and try again." }
          try {
            Copy-Item -Path $Source -Destination $Target -Recurse
            $script = Join-Path $old 'install.ps1'
            if ((Test-Path $script) -and -not (Test-Path (Join-Path $Target 'install.ps1'))) { Copy-Item $script $Target }
          } catch {
            if (Test-Path $Target) { Remove-Item $Target -Recurse -Force }
            Rename-Item -Path $old -NewName (Split-Path $Target -Leaf)
            throw
          }
          Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue
          $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Atril'
          if ((Test-Path $key) -and ((Get-ItemProperty $key).InstallLocation -eq $Target)) { Set-ItemProperty -Path $key -Name DisplayVersion -Value $Version }
          Save-Result $true $null
          Write-Log 'Done'
        } catch {
          Write-Log ('Failed: ' + $_.Exception.Message)
          Save-Result $false $_.Exception.Message
        }
        try { Start-Process -FilePath (Join-Path $Target 'Atril.exe') -WorkingDirectory $Target } catch { Write-Log ('Could not open Atril: ' + $_.Exception.Message) }
        Set-Location $env:TEMP
        Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
        """;
}
