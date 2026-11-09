using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Atril.Synology;

/// <summary>A Synology sign-in the user asked Atril to remember.</summary>
public sealed record SavedLogin(string? QuickConnectId, string Username, string Password);

/// <summary>
/// Keeps the Synology password the user chose to remember, encrypted, in the user's app data folder
/// (%LOCALAPPDATA%\Atril\synology-login.json). Never in the project, the browser or appsettings.
///
/// Encryption: ASP.NET Core Data Protection (AES-256-CBC + HMACSHA256). Its keys live in
/// %LOCALAPPDATA%\Atril\keys and, on Windows, are themselves protected with DPAPI tied to the Windows
/// account, so the file is useless if copied to another computer or read by another user.
/// </summary>
public sealed class CredentialStore(IDataProtectionProvider provider, ILogger<CredentialStore> log)
{
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atril");
    static readonly string FilePath = Path.Combine(DataDir, "synology-login.json");

    readonly IDataProtector protector = provider.CreateProtector("Atril.Synology.Password.v1");
    readonly object gate = new();

    sealed record Stored(string? QuickConnectId, string Username, string Password);

    public bool HasSaved { get { lock (gate) return File.Exists(FilePath); } }

    public SavedLogin? Load()
    {
        lock (gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath), Json);
                if (s is null || string.IsNullOrEmpty(s.Username) || string.IsNullOrEmpty(s.Password)) return null;
                return new SavedLogin(s.QuickConnectId, s.Username, protector.Unprotect(s.Password));
            }
            catch (Exception e)
            {
                // Keys lost or file copied from another account/computer: it can't be read, so it is dropped
                log.LogWarning("Synology: the saved password couldn't be read ({Error}); it will be asked again.", e.GetType().Name);
                TryDelete();
                return null;
            }
        }
    }

    public void Save(SavedLogin login)
    {
        lock (gate)
        {
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(new Stored(login.QuickConnectId, login.Username, protector.Protect(login.Password)), Json);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
    }

    public void Forget() { lock (gate) TryDelete(); }

    static void TryDelete() { try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { } }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
