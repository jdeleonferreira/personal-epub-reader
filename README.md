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

To update: Atril tells you when there's a new version and installs it from the app (see [Updates](#updates)).
You can also `git pull` and run the same command again. Your books, library and settings are kept either way.

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
- Whatever you download from the Synology is saved there.
- A book that is already in the library isn't added or downloaded again (see [Duplicates](#duplicates)).
- EPUB files you copy into the folder yourself (subfolders included) show up in the library automatically.
- **Delete** removes the book and its file.

The chosen path is stored in `%LOCALAPPDATA%\Atril\config.json` (Windows) or `~/.local/share/Atril/config.json`.
The browser only keeps each book's record (title, cover) and your reading progress.
When Atril runs without its server (plain web version), books are stored inside the browser.

## Duplicates

Before adding a book (Open EPUB, drag and drop, double click in File Explorer) or downloading it from the Synology,
Atril compares it with the library. It is the same book when it has, in this order:

1. The same file (SHA-1 of the content).
2. The same ISBN, from the metadata (ISBN-10 is converted to ISBN-13), or printed on the copyright page plus the same title.
3. The same publisher identifier (`dc:identifier`) and the same title.
4. The same title and author (accents, case, articles and "(Spanish Edition)"-style notes don't matter; "Tolkien, J. R. R."
   equals "J.R.R. Tolkien") and the same length: estimated pages (1,800 characters each, since EPUB has no fixed pages)
   within 3%, and the same number of sections.

The same title by a different author, or a much shorter or longer book (an abridged edition, a study guide), is a different book.
On the Synology, files with the same name and size are recognised without downloading them; the rest are downloaded,
checked and only saved if they're new. EPUB files you copy into the books folder yourself are always shown.

## Categories

Books are sorted in two levels, **knowledge area → topic** (*Science & Mathematics → Astronomy & space*). The 20 areas
follow the top level of [Thema](https://www.editeur.org/151/Thema/), the international book subject standard. Each book is placed by:

1. Subject codes in the EPUB (BISAC such as `FIC028000`, or Thema such as `FL`): exact mapping.
2. Subject words in the EPUB ("Fiction / Science Fiction", "Historia"), in English or Spanish.
3. Its content: title, description and the first ~60,000 characters, against keyword lists per topic in English and
   Spanish, plus the amount of dialogue (novels have a lot).

The chips above the shelf filter by area. The selector on each book changes its area by hand, which always wins.
Everything runs on your computer (`wwwroot/catalog.js`); no book content is sent anywhere.

## Synology connection (QuickConnect)

The **Synology** button lists the NAS folders and opens EPUB files directly.
The page never talks to the NAS: the Atril server acts as the bridge (`/api/synology/*`).

1. Click **Synology** in the library.
2. Enter the **QuickConnect ID** (the part before `.quickconnect.to`), your DSM **username** and **password**.
3. If the account uses two-step verification, Atril asks for the code once.

What is stored, and where:

- **Password:** with **Remember password on this computer** ticked (the default), it is saved encrypted in
  `%LOCALAPPDATA%\Atril\synology-login.json` (Windows) or `~/.local/share/Atril/synology-login.json`, and Atril signs in
  by itself when it starts. Encryption: ASP.NET Core Data Protection, with its keys in `Atril\keys`; on Windows those keys
  are protected with DPAPI, so the file only works for your Windows account on this computer. Never in the browser,
  `appsettings` or the repository. Unticked, it only lives in the server's memory and is asked again after a restart.
  **Sign out** forgets it immediately, and a password the NAS rejects is deleted.
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

## Updates

Atril looks for new versions in this repository's **GitHub Releases** (free, no account or key needed) when it opens
and every 6 hours. When there is one, it shows **what's new** in every version since yours (the notes come from
`CHANGELOG.md`) and, next to the version number, an *Update to vX.Y.Z* button. *Check for updates* asks right away;
*Later* stops asking about that version.

- **Windows app installed with `install.ps1`** (or the portable zip): *Update now* downloads `Atril-Windows-x64.zip`
  (or `-arm64`) from the Release, checks its SHA-256, closes Atril, replaces the program folder and opens Atril again.
  If anything fails, the previous version is put back and Atril says so. Log: `%LOCALAPPDATA%\Atril\update.log`.
- **Browser version** (`dotnet run` or the published executable): it shows the notes and a link to the Release page.
- **Microsoft Store**: the Store updates it.

Settings (`appsettings.json` or `appsettings.Local.json`): `"Updates": { "Enabled": false }` stops the automatic check
(the *Check for updates* button keeps working); `"Repository": "owner/name"` reads the Releases of another repository.

For the updater to find a version, the Release must exist on GitHub: it's created by `release.yml` when you push the tag
(see [Versions](#versions)). The repository must be public, or the app can't read its Releases without a key.

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
3. GitHub Actions (`.github/workflows/release.yml`) checks for secrets, confirms the tag matches the project,
   builds the Windows app (`Atril-Windows-x64.zip`, `Atril-Windows-arm64.zip`, used by the in-app updater) and the
   browser version for Windows, macOS and Linux, and publishes the *Release* with the CHANGELOG notes for that version.
   Every Atril already installed offers the update the next time it opens.

   If a Release didn't get created (for example, the tag was pushed together with a new workflow file),
   run it by hand: `gh workflow run release.yml -f tag=vX.Y.Z`.

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
| `wwwroot/catalog.js` | Reads EPUB metadata and text: duplicate check and categories by area and topic |
| `wwwroot/sw.js` | Service worker: the browser version opens offline |
| `AtrilServer.cs` | ASP.NET Core server and `/api/*` endpoints (shared) |
| `Program.cs` | Starts the browser version |
| `Local/LocalLibrary.cs` | Books folder on this computer |
| `Synology/QuickConnectResolver.cs` | Finds the NAS through QuickConnect |
| `Synology/SynologyService.cs` | Session, listing and download with File Station |
| `Synology/CredentialStore.cs` | Saved Synology password, encrypted (Data Protection + DPAPI on Windows) |
| `Updates/UpdateService.cs` | Looks for new versions in GitHub Releases and installs them in the Windows app |
| `.github/workflows/release.yml` | Builds and publishes a GitHub Release for every `vX.Y.Z` tag |
| `windows/` | Windows app (WebView2): `install.ps1` installs it; `package.ps1` and `STORE.md` for an optional Microsoft Store release |

### Endpoints

Every `/api/local`, `/api/synology` and `/api/update` route requires the `X-Atril` header, so other websites
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
| `GET /api/local/external?path=` | Windows app: read an `.epub` opened with a double click, without copying it |
| `POST /api/local/import` | Windows app: copy into the library an `.epub` opened with a double click |
| `GET /api/synology/status` | Whether it's connected |
| `POST /api/synology/connect` | Find the NAS and sign in (`{ "quickConnectId", "username", "password", "otp", "remember" }`); with no username, uses the saved password |
| `POST /api/synology/disconnect` | Sign out and forget the password (also the saved one) |
| `GET /api/synology/folders?path=` | NAS folders and EPUB files |
| `GET /api/synology/book?path=&size=&save=` | Download from the NAS into the local folder (`save=false`: only pass it through) |
| `GET /api/synology/diagnostics` | Addresses tried by QuickConnect |
| `GET /api/update/check?force=` | Whether there's a newer version, with the notes of every version since the current one |
| `POST /api/update/install` | Windows app: download the newest version, install it and restart Atril |

## Privacy

See [`PRIVACY.md`](PRIVACY.md). Atril doesn't collect or send personal data.

## Third-party licenses

See [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
