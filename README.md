# Atril

An EPUB reader that runs in the browser and as a Windows app.
It keeps your books in a folder you choose and can open them straight from your Synology NAS.

Current version: see `<Version>` in [`Directory.Build.props`](Directory.Build.props) and the changes in [`CHANGELOG.md`](CHANGELOG.md).

Two ways to use it:

- **Windows app** (`windows/`): a normal Windows program with its own window. It runs everything inside,
  including the Synology connection, so nothing else needs to be running. See [Install on Windows](#install-on-windows).
- **Browser** (`dotnet run`): Windows, macOS and Linux. Opens http://localhost:5080.

## Requirements

- .NET 8 SDK or newer (`dotnet --version` to check)

## Run

```bash
cd personal-epub-reader
dotnet run
```

Open http://localhost:5080. In Visual Studio or Rider, open `Atril.csproj` and press ▶.

## Install on Windows

In PowerShell, at the project root:

```powershell
powershell -ExecutionPolicy Bypass -File windows\install.ps1
```

It builds Atril and installs it for your user (no administrator rights):

- Program in `%LOCALAPPDATA%\Programs\Atril`, with a **Start menu** shortcut
  (add `-DesktopShortcut` for a desktop one too).
- Atril appears in **Open with** for `.epub` files; to make it the default, choose
  *Open with → Choose another app → Atril → Always*.
- It's listed in **Settings → Apps**, where it can be uninstalled like any other app
  (or run the script with `-Uninstall`).

To update: `git pull`, then run the same command again. Your books, library and settings are kept.

Requirements: .NET 8 SDK to build it, and Windows 10 or 11 (they include the WebView2 runtime Atril uses).

## First time after cloning

```bash
git config core.hooksPath .githooks                        # turns on the secret check before every commit
cp appsettings.Local.example.json appsettings.Local.json   # your personal settings (never pushed)
```

Optional: write in `.git/info/atril-private` (one entry per line) personal data that must never
appear in the code, such as your QuickConnect ID or NAS username. The hook blocks any commit that contains them.

## Books folder

EPUB files live in a folder on your computer; by default `Documents/Atril`. Change it with
**Change folder** in the library, where you can also create a new folder and move your books into it.

- Whatever you open with **Open EPUB** or drop on the window is copied to that folder.
- Whatever you download from the Synology is saved there (if it's already there, it isn't downloaded again).
- EPUB files you copy into the folder yourself (subfolders included) show up in the library automatically.
- **Delete** removes the book and its file.

The chosen path is stored in `%LOCALAPPDATA%\Atril\config.json` (Windows) or `~/.local/share/Atril/config.json`.
The browser only keeps each book's record (title, cover) and your reading progress.
When Atril runs without its server (plain web version), books are stored inside the browser.

## Synology connection (QuickConnect)

The **Synology** button lists the NAS folders and opens EPUB files directly.
The page never talks to the NAS: the Atril server acts as the bridge (`/api/synology/*`).

1. Click **Synology** in the library.
2. Enter the **QuickConnect ID** (the part before `.quickconnect.to`), your DSM **username** and **password**.
3. If the account uses two-step verification, Atril asks for the code once.

What is stored, and where:

- **Password:** only in the Atril server's memory while it runs. Never on disk or in the browser.
  Atril asks for it again after a restart. **Sign out** forgets it immediately.
- **QuickConnect ID and username:** in the browser (localStorage), to prefill the form.
- **2FA device token:** in `%LOCALAPPDATA%\Atril` (Windows) or `~/.local/share/Atril`, one per user and NAS.

Options in `appsettings.Local.json`, `Synology` section (all optional):

- `QuickConnectId`: value prefilled in the form.
- `BooksFolder`: NAS folder with the books, e.g. `/home` or `/home/Books`. Atril never goes outside it. `/` shows every shared folder.
- `Url`: fixed NAS address (`https://192.168.1.10:5001` or your DDNS name). When set, QuickConnect isn't used.

Using a DSM account with read-only access to the books folder is recommended.

### How it finds the NAS

1. Asks `global.quickconnect.to` about your ID and gets the NAS addresses: home network, public IP, DDNS and relay.
2. Tries them all at once with `/webman/pingpong.cgi` and keeps the best one that answers:
   home network → DDNS → public IP → Synology relay. The NAS proves its identity with `ezid` (MD5 of its serverID).
3. Signs in to the File Station API and keeps the session in memory. If you change networks, it looks for the NAS again.

If it can't connect, see which addresses were tried:

```bash
curl -H "X-Atril: 1" http://localhost:5080/api/synology/diagnostics
```

## Security: what never goes to GitHub

| Protection | Where |
|---|---|
| Personal settings in `appsettings.Local.json` (ignored by git) | `.gitignore` |
| Certificates, keys, `.env`, 2FA tokens and the books themselves are ignored | `.gitignore` |
| Check before every commit: passwords, tokens, connection IDs, your private list | `.githooks/pre-commit` → `scripts/check-secrets.sh` |
| The same check plus [Gitleaks](https://github.com/gitleaks/gitleaks) over the whole history on every push | `.github/workflows/ci.yml` |

On GitHub, also turn on **Settings → Code security → Secret scanning → Push protection**.

`appsettings.json` only holds generic values; the hook rejects any `QuickConnectId`, `Username`,
`Url` or password with a value in that file.

## Versions

Semantic versioning (`MAJOR.MINOR.PATCH`). The version is defined **only** in `Directory.Build.props`
(used by both the web and Windows apps); the app shows it next to its name and in `/api/health`.

To publish a version:

1. Write the changes in `CHANGELOG.md` under `## [Unreleased]`.
2. Run (PowerShell):

   ```powershell
   ./scripts/new-version.ps1 1.2.0
   git push --follow-tags
   ```

   The script bumps the number in `Directory.Build.props` and `sw.js`, dates the CHANGELOG section, commits and creates the `v1.2.0` tag.
3. GitHub Actions (`release.yml`) checks for secrets, confirms the tag matches the project,
   builds for Windows, macOS and Linux, creates the `.msixbundle` for the Microsoft Store
   and publishes the *Release* with the files and the CHANGELOG notes.

## Manual publish

```bash
dotnet publish Atril.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

The executable opens the browser at http://localhost:5080 (turn it off with `"OpenBrowser": false`).
It only listens on `localhost`: other computers on the network can't use it.

## Structure

| File | What it does |
|---|---|
| `wwwroot/index.html` | The app (library, reader, folder, Synology) using epub.js |
| `wwwroot/sw.js` | Service worker: the browser version opens offline |
| `AtrilServer.cs` | ASP.NET Core server and `/api/*` endpoints (shared) |
| `Program.cs` | Starts the browser version |
| `Local/LocalLibrary.cs` | Books folder on this computer |
| `Synology/QuickConnectResolver.cs` | Finds the NAS through QuickConnect |
| `Synology/SynologyService.cs` | Session, listing and download with File Station |
| `windows/` | Windows app (WebView2): `install.ps1` installs it; `package.ps1` and `STORE.md` for an optional Microsoft Store release |

### Endpoints

Every `/api/local` and `/api/synology` route requires the `X-Atril` header, so other websites
open in the browser can't use them.

| Route | What it does |
|---|---|
| `GET /api/health` | Version and status |
| `GET/PUT /api/local/config` | Books folder (`{ "folder", "move" }`) |
| `GET /api/local/browse?path=` | Folders on this computer to choose from |
| `POST /api/local/folders` | Create a folder (`{ "parent", "name" }`) |
| `GET /api/local/books` | EPUB files in the folder |
| `GET/DELETE /api/local/book?file=` | Read or delete a book |
| `POST /api/local/books?name=` | Save an EPUB in the folder |
| `POST /api/local/import` | Windows app: copy into the library an `.epub` opened with a double click |
| `GET /api/synology/status` | Whether it's connected |
| `POST /api/synology/connect` | Find the NAS and sign in (`{ "quickConnectId", "username", "password", "otp" }`) |
| `POST /api/synology/disconnect` | Sign out and forget the password |
| `GET /api/synology/folders?path=` | NAS folders and EPUB files |
| `GET /api/synology/book?path=&size=` | Download from the NAS into the local folder |
| `GET /api/synology/diagnostics` | Addresses tried by QuickConnect |

## Privacy

See [`PRIVACY.md`](PRIVACY.md). Atril doesn't collect or send personal data.

## Third-party licenses

See [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
