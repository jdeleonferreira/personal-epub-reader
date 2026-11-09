# Atril privacy policy

*Last updated: November 9, 2026*

Atril is an EPUB book reader. This policy explains which data it uses and where that data stays.
In short: **Atril doesn't collect, send or sell personal data. It has no accounts, ads or analytics.**

## Data stored on your device

Everything stays on your own device and is only used by Atril:

- **Your books:** the EPUB files you open or download are saved in the folder you choose (by default `Documents\Atril`).
- **Your library and reading progress:** title, author, cover thumbnail, the page you're on and your reading settings (theme, font, size).
- **What Atril works out about each book**, to spot duplicates and sort them by topic: ISBN and identifiers, estimated
  length, knowledge area and topic. It is computed on your device; the book's text is never sent anywhere.
- **Data to connect to your Synology NAS** (only if you use that feature):
  - The QuickConnect ID and username, to prefill the form.
  - A device token issued by your NAS when your account uses two-step verification, so the code isn't asked every time.
  - Your NAS password, **only if you tick "Remember password on this computer"**, encrypted (see below).
- **Window size and position.**

**Your NAS password** is kept in memory while Atril is open. If you tick *Remember password on this computer*, it is
also saved **encrypted** in your user's app data folder; on Windows the encryption keys are protected by Windows for your
account only. It is never saved in plain text, in the browser, or anywhere outside your device. *Sign out* erases it.

## Internet connections

Atril only connects to:

- **Your own Synology NAS**, when you use that feature, directly or through **QuickConnect**, Synology's service.
  To find your NAS, Atril asks the QuickConnect servers about the ID you enter.
  QuickConnect is covered by [Synology's privacy policy](https://www.synology.com/company/legal/privacy).
- **GitHub** (`api.github.com`), to check whether there is a new Atril version and, if you choose *Update now*, to
  download it. It's an anonymous request: GitHub sees your IP address and Atril's version number, nothing else.
  Covered by [GitHub's privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).
  Turn the automatic check off with `"Updates": { "Enabled": false }` in `appsettings.Local.json`.
- **Google Fonts**, to download the fonts used by the interface.
- **External links** you open from a book, in your browser.

Atril doesn't send your books, library or credentials to any other server, including the Atril author's.

## Deleting your data

- **Books:** delete them in Atril (*Delete*) or directly in your books folder.
- **Everything else:** removed when you uninstall Atril from Windows (*Settings → Apps*).

## Children

Atril isn't directed at children under 13 and doesn't collect anyone's data.

## Contact

Questions about this policy: open an issue at
[github.com/jdeleonferreira/personal-epub-reader](https://github.com/jdeleonferreira/personal-epub-reader/issues).
