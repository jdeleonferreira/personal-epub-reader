# Atril

Lector de libros EPUB que corre en el navegador y se instala como app en tu PC.
Guarda los libros en la carpeta que elijas y puede traerlos de tu Synology.

Versión actual: ver `<Version>` en [`Atril.csproj`](Atril.csproj) y los cambios en [`CHANGELOG.md`](CHANGELOG.md).

## Requisitos

- .NET 8 SDK o más reciente (`dotnet --version` para comprobarlo)

## Ejecutar

```bash
cd Atril
dotnet run
```

Abre http://localhost:5080. En Visual Studio o Rider basta con abrir `Atril.csproj` y pulsar ▶.

## Primera vez después de clonar

```bash
git config core.hooksPath .githooks          # activa la revisión de secretos antes de cada commit
cp appsettings.Local.example.json appsettings.Local.json   # tu configuración personal (no se sube)
```

Opcional: escribe en `.git/info/atril-privado` (un texto por línea) datos tuyos que nunca deben
aparecer en el código, como tu ID de QuickConnect o tu usuario del NAS. El hook bloquea cualquier commit que los contenga.

## Carpeta de libros

Los EPUB viven en una carpeta de tu equipo; por defecto `Documentos/Atril`. Se cambia con
**Cambiar carpeta** en la biblioteca, donde se puede crear una carpeta nueva y mover los libros a ella.

- Lo que abras con **Abrir EPUB** o arrastres a la ventana se copia a esa carpeta.
- Lo que descargues del Synology se guarda ahí (si ya está, no se vuelve a bajar).
- Los EPUB que copies a la carpeta por tu cuenta (también en subcarpetas) aparecen solos en la biblioteca.
- **Eliminar** un libro borra también el archivo.

La ruta elegida se guarda en `%LOCALAPPDATA%\Atril\config.json` (Windows) o `~/.local/share/Atril/config.json`.
El navegador solo guarda la ficha de cada libro (título, portada) y el avance de lectura.
Si Atril se usa sin su servidor (versión web pura), los libros se guardan dentro del navegador.

## Conexión con Synology (QuickConnect)

El botón **Synology** muestra las carpetas del NAS y abre los EPUB directamente.
El navegador no habla con el NAS: el servidor de Atril hace de puente (`/api/synology/*`).

1. Pulsa **Synology** en la biblioteca.
2. Escribe el **ID de QuickConnect** (lo que va antes de `.quickconnect.to`), tu **usuario** y tu **contraseña** de DSM.
3. Si la cuenta tiene verificación en dos pasos, Atril pide el código una sola vez.

Qué se guarda y dónde:

- **Contraseña:** solo en la memoria del servidor de Atril mientras está encendido. Nunca en disco ni en el navegador.
  Al reiniciar Atril se pide de nuevo. **Cerrar sesión** la borra al instante.
- **ID de QuickConnect y usuario:** en el navegador (localStorage), para rellenar el formulario.
- **Token de dispositivo 2FA:** en `%LOCALAPPDATA%\Atril` (Windows) o `~/.local/share/Atril`, uno por usuario y NAS.

Opciones en `appsettings.Local.json`, sección `Synology` (todas opcionales):

- `QuickConnectId`: valor que aparece de entrada en el formulario.
- `CarpetaLibros`: carpeta del NAS con los libros, por ejemplo `/home` o `/home/Libros`. Atril no deja salir de ella. `/` muestra todas las carpetas compartidas.
- `Url`: dirección fija del NAS (`https://192.168.1.10:5001` o tu DDNS). Si se llena, QuickConnect no se usa.

Se recomienda una cuenta de DSM solo con permiso de lectura sobre la carpeta de libros.

### Cómo encuentra el NAS

1. Pregunta a `global.quickconnect.to` por tu ID y recibe las direcciones del NAS: red local, IP pública, DDNS y relay.
2. Las prueba todas a la vez con `/webman/pingpong.cgi` y se queda con la mejor que responda:
   red local → DDNS → IP pública → relay de Synology. El NAS se identifica con `ezid` (MD5 de su serverID).
3. Inicia sesión en la API de File Station y guarda la sesión en memoria. Si cambias de red, vuelve a buscar el NAS.

Si no conecta, revisa qué direcciones se probaron:

```bash
curl -H "X-Atril: 1" http://localhost:5080/api/synology/diagnostico
```

## Seguridad: qué nunca se sube a GitHub

| Protección | Dónde |
|---|---|
| Configuración personal en `appsettings.Local.json` (ignorado por git) | `.gitignore` |
| Certificados, llaves, `.env`, tokens 2FA y los propios libros ignorados | `.gitignore` |
| Revisión antes de cada commit: contraseñas, tokens, IDs de conexión, tu lista privada | `.githooks/pre-commit` → `scripts/revisar-secretos.sh` |
| La misma revisión más [Gitleaks](https://github.com/gitleaks/gitleaks) sobre todo el historial en cada push | `.github/workflows/ci.yml` |

En GitHub activa también **Settings → Code security → Secret scanning → Push protection**.

`appsettings.json` solo lleva valores genéricos; el hook rechaza cualquier `QuickConnectId`, `Usuario`,
`Url` o contraseña con valor en ese archivo.

## Versiones

Versionado semántico (`MAYOR.MENOR.PARCHE`). La versión se define **solo** en `Atril.csproj`;
la app la muestra junto al título y en `/api/salud`.

Para publicar una versión:

1. Anota los cambios en `CHANGELOG.md` bajo `## [Sin publicar]`.
2. Ejecuta (PowerShell):

   ```powershell
   ./scripts/nueva-version.ps1 1.1.0
   git push --follow-tags
   ```

   El script sube el número en `Atril.csproj` y `sw.js`, fecha la sección del CHANGELOG, hace commit y crea la etiqueta `v1.1.0`.
3. GitHub Actions (`release.yml`) revisa secretos, comprueba que la etiqueta coincida con el proyecto,
   compila para Windows, macOS y Linux y crea la *Release* con los `.zip` y las notas del CHANGELOG.

## Publicar a mano

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publicado
```

El ejecutable abre el navegador en http://localhost:5080 (se desactiva con `"AbrirNavegador": false`).
Solo escucha en `localhost`: otros equipos de la red no pueden usarlo.

## Estructura

| Archivo | Qué hace |
|---|---|
| `wwwroot/index.html` | La app (biblioteca, lector, carpeta, Synology) usando epub.js |
| `wwwroot/sw.js` | Service worker: la app abre sin conexión |
| `Program.cs` | Servidor ASP.NET Core y endpoints `/api/*` |
| `Local/BibliotecaLocal.cs` | Carpeta de libros del equipo |
| `Synology/QuickConnectResolver.cs` | Encuentra el NAS por QuickConnect |
| `Synology/SynologyService.cs` | Sesión, listado y descarga con File Station |

### Endpoints

Todos los de `/api/local` y `/api/synology` exigen la cabecera `X-Atril`, para que otras páginas
abiertas en el navegador no puedan usarlos.

| Ruta | Qué hace |
|---|---|
| `GET /api/salud` | Versión y estado |
| `GET/PUT /api/local/config` | Carpeta de libros (`{ "carpeta", "mover" }`) |
| `GET /api/local/explorar?ruta=` | Carpetas del equipo para elegir |
| `GET /api/local/libros` | EPUB en la carpeta |
| `GET/DELETE /api/local/libro?archivo=` | Leer o borrar un libro |
| `POST /api/local/libros?nombre=` | Guardar un EPUB en la carpeta |
| `POST /api/synology/conectar` | Buscar el NAS e iniciar sesión |
| `POST /api/synology/desconectar` | Cerrar sesión y olvidar la contraseña |
| `GET /api/synology/carpetas?ruta=` | Carpetas y EPUB del NAS |
| `GET /api/synology/libro?ruta=&tamano=` | Descargar del NAS a la carpeta local |
| `GET /api/synology/diagnostico` | Direcciones probadas por QuickConnect |

## Licencias de terceros

Ver [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
