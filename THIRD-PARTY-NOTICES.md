# Componentes de terceros

Atril incluye estos componentes (en `wwwroot/lib/` y, en la app de Windows, como paquete NuGet). Sus licencias permiten distribuirlos,
también en aplicaciones comerciales, siempre que se conserven estos avisos.

| Componente | Uso | Licencia |
|---|---|---|
| [epub.js](https://github.com/futurepress/epub.js) 0.3.x | Interpretar y mostrar los EPUB | BSD 2-Clause |
| [localForage](https://github.com/localForage/localForage) (incluido en epub.js) | Almacenamiento interno de epub.js | Apache 2.0 |
| [JSZip](https://github.com/Stuk/jszip) 3.10.1 | Abrir el ZIP del EPUB | MIT (o GPLv3, a elección; Atril usa MIT) |
| [pako](https://github.com/nodeca/pako) (incluido en JSZip) | Descompresión | MIT |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) (solo app de Windows) | Mostrar la app en una ventana | Licencia del SDK de WebView2 (BSD 3-Clause) |

Las fuentes Literata e Instrument Sans se cargan desde Google Fonts (SIL Open Font License 1.1).
