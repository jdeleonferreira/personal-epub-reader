# Third-party components

Atril includes these components (in `wwwroot/lib/` and, in the Windows app, as a NuGet package). Their licenses
allow redistributing them, including in commercial apps, as long as these notices are kept.

| Component | Use | License |
|---|---|---|
| [epub.js](https://github.com/futurepress/epub.js) 0.3.x | Parse and display EPUB books | BSD 2-Clause |
| [localForage](https://github.com/localForage/localForage) (bundled in epub.js) | epub.js internal storage | Apache 2.0 |
| [JSZip](https://github.com/Stuk/jszip) 3.10.1 | Open the EPUB ZIP container | MIT (or GPLv3 at your choice; Atril uses MIT) |
| [pako](https://github.com/nodeca/pako) (bundled in JSZip) | Decompression | MIT |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) (Windows app only) | Show the app in a window | WebView2 SDK license (BSD 3-Clause) |

The Literata and Instrument Sans fonts are loaded from Google Fonts (SIL Open Font License 1.1).
