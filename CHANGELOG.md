# Atril changelog

Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow [semantic versioning](https://semver.org/): MAJOR.MINOR.PATCH.

## [Unreleased]

## [1.1.0] - 2026-09-29

### Added
- **Windows app for the Microsoft Store** (`windows/`): its own window with WebView2, no browser or console.
  It runs the Atril server on a random 127.0.0.1 port, protected with a per-session token.
- MSIX package (x64 and ARM64) with `windows/package.ps1` and in every GitHub Release.
- Double-clicking an `.epub` in File Explorer opens it in Atril (in the same window if Atril is already open).
- Windows folder picker to choose the books folder.
- Privacy policy (`PRIVACY.md`) and Store publishing guide (`windows/STORE.md`).

### Changed
- **Everything is now in English**: user interface, sample book, code, file and class names, API routes,
  scripts and documentation. Libraries and settings saved by earlier versions keep working.
- The version now lives in `Directory.Build.props`, shared by the web and Windows apps.
- The server logic moved from `Program.cs` to `AtrilServer.cs` so both apps use it.
- Synology setting `CarpetaLibros` is now `BooksFolder` (the old name is still read).

## [1.0.0] - 2026-09-29

First version.

### Added
- EPUB library with covers, reading progress and a sample book.
- Reader with page or scroll layout, light/sepia/night backgrounds, font and size, line spacing, table of contents and progress bar.
- Books folder of your choice: EPUB files you open or download are saved there; files copied into it show up automatically. Books can be moved when changing folders.
- Synology connection through QuickConnect (home network, internet or relay) or a fixed address. Sign-in from the app, with two-step verification. The password is only kept in memory.
- Download books from the NAS into the local folder, skipping the ones already there.
- Version number shown in the app and in the health endpoint.
- Protection against publishing credentials: pre-commit hook, GitHub Actions check and private settings in `appsettings.Local.json`.
- Automatic GitHub Releases for Windows, macOS and Linux when a `vX.Y.Z` tag is pushed.

### Fixed
- Books whose CSS contains characters outside Latin-1 didn't open (`btoa` in epub.js).
- `replaceCss` error when importing books.
