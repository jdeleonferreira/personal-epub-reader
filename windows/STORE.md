# Publishing Atril on the Microsoft Store

The Windows app lives in this folder (`windows/`). It's a WebView2 window (the Edge engine) that
shows the same web app and runs the Atril server inside it, with no browser, console or port open to the network.

## 1. Try it on your PC

1. Turn on **Developer Mode**: *Settings → System → For developers*.
2. In PowerShell, at the project root:

   ```powershell
   ./windows/package.ps1 -Try
   ```

3. Look for **Atril** in the Start menu. Try: opening an EPUB, double-clicking an `.epub` in File Explorer,
   *Change folder → Choose with Windows…*, the Synology connection.
4. To remove it: `Get-AppxPackage Atril.Development | Remove-AppxPackage`.

You can also run it unpackaged from Rider or Visual Studio by opening `windows/Atril.Windows.csproj`.

## 2. Account and name in Partner Center (once)

1. Create the individual developer account (free) at <https://storedeveloper.microsoft.com>. It asks you to verify your identity.
2. In [Partner Center](https://partner.microsoft.com/dashboard/apps-and-games/overview) → **New product → MSIX or PWA app**.
3. **Reserve the name**. If "Atril" is taken, try "Atril Reader" or "Atril EPUB".
4. Open **Product management → Product identity** and copy:
   - `Package/Identity/Name` → variable **MSIX_IDENTITY_NAME**
   - `Package/Identity/Publisher` (starts with `CN=`) → variable **MSIX_PUBLISHER**
   - `Package/Properties/PublisherDisplayName` → variable **MSIX_PUBLISHER_NAME**
   - The reserved name → variable **MSIX_DISPLAY_NAME**
5. On GitHub: *Settings → Secrets and variables → Actions → **Variables** → New repository variable*, and create all four.
   They aren't secrets (they appear inside the published package), so they go in variables and not in the code.

## 3. Build the package

Every tagged version (`./scripts/new-version.ps1 X.Y.Z` and `git push --follow-tags`) adds
`Atril_X.Y.Z.0.msixbundle` (x64 + ARM64) with your Store identity to the GitHub Release.

You can also build it on your PC (requires the Windows SDK):

```powershell
./windows/package.ps1 -IdentityName "..." -Publisher "CN=..." -PublisherName "..." -DisplayName "Atril"
```

It doesn't need to be signed: the Store signs it during certification.

## 4. Create the submission

In Partner Center → your app → **Start your submission**:

| Section | What to enter |
|---|---|
| Pricing and availability | Free, all markets (or the ones you want) |
| Properties | Category **Books & reference**. Mark that the app accesses the network |
| Age ratings | IARC questionnaire: no violence, no adult content, no purchases, no user interaction, no location sharing |
| Packages | Upload the `.msixbundle` |
| Store listing | See the texts below, screenshots and the privacy policy |
| Notes for certification | See below |

**Privacy policy (URL):**
`https://github.com/jdeleonferreira/personal-epub-reader/blob/main/PRIVACY.md`

**Screenshots:** at least 1, 4 recommended, 1366×768 or larger. Suggested: library with covers,
reader in light mode, reader in night mode, Synology browser.

### Notes for certification

```
Atril is an EPUB reader. To test it, click "Open EPUB" and open any .epub file, or drag one into the window.
The Synology button requires a Synology NAS and is optional; the rest of the app works without it.
runFullTrust: Atril is a packaged .NET desktop app (WinForms + WebView2). It runs a local HTTP server
bound to 127.0.0.1 on a random port, reachable only from its own window (protected with a per-session token),
to read and save books in the folder chosen by the user and to talk to the user's own Synology NAS.
privateNetworkClientServer: needed to reach the user's NAS on the home network.
```

## Listing texts

**Name:** Atril

**Short description:** Read your EPUB books calmly on your PC, and open them from your Synology.

**Description:**

> Atril is a simple, carefully made EPUB reader, designed for long reading sessions.
>
> • Your library in a folder on your PC: whatever you open or download is saved there, and whatever you copy into the folder shows up on its own.
> • Page or scroll reading, with light, sepia or night background, font, size and line spacing.
> • Pick up every book where you left off.
> • Table of contents, progress bar and percentage read.
> • Connect to your Synology NAS through QuickConnect: browse your folders and open books directly. Your password is never stored.
> • Double-click any .epub file to open it in Atril.
>
> No accounts, no ads and no data collection.
> Atril can't open DRM-protected books (for example, those bought on Kindle or with Adobe DRM).

**Features (up to 20):**
- EPUB library in the folder you choose
- Page or continuous scroll layout
- Light, sepia and night backgrounds
- Adjustable font, size and line spacing
- Remembers your page in every book
- Table of contents and progress bar
- Synology connection through QuickConnect
- Opens .epub files with a double click

**Keywords:** epub, reader, books, ebook, synology, nas, reading

**Requirements:** Windows 10 version 1809 or later. Uses the Microsoft Edge WebView2 Runtime, included with Windows 10 and 11.

## Things to keep in mind

- **runFullTrust** is a restricted capability: the Store approves it for packaged desktop apps,
  but asks for the justification in the notes above.
- If certification reports something, the report says which test failed; fix it, bump the version (`new-version.ps1`) and resubmit.
- The package version is `MAJOR.MINOR.PATCH.0`: every submission needs a higher version than the previous one.
