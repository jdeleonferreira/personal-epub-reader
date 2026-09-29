# Cambios de Atril

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/).
Las versiones siguen [versionado semántico](https://semver.org/lang/es/): MAYOR.MENOR.PARCHE.

## [Sin publicar]

## [1.1.0] - 2026-09-29

### Agregado
- **App de Windows para la Microsoft Store** (`windows/`): ventana propia con WebView2, sin navegador ni consola.
  Incluye el servidor de Atril en un puerto aleatorio de 127.0.0.1, protegido con un token por sesión.
- Paquete MSIX (x64 y ARM64) con `windows/empaquetar.ps1` y en cada Release de GitHub.
- Doble clic en un `.epub` del Explorador lo abre en Atril (si ya está abierto, en la misma ventana).
- Selector de carpetas de Windows para elegir la carpeta de libros.
- Política de privacidad (`PRIVACIDAD.md`) y guía de publicación (`windows/TIENDA.md`).

### Cambiado
- La versión está ahora en `Directory.Build.props`, compartida por la app web y la de Windows.
- La lógica del servidor pasó de `Program.cs` a `AtrilServidor.cs` para reutilizarla en las dos apps.

## [1.0.0] - 2026-09-29

Primera versión.

### Agregado
- Biblioteca de libros EPUB con portadas, progreso de lectura y libro de ejemplo.
- Lector con modo páginas o desplazamiento, fondos claro/sepia/noche, tipo y tamaño de letra, interlineado, índice y barra de progreso.
- Carpeta de libros elegible en el equipo: los EPUB abiertos o descargados se guardan ahí; los que se copien a la carpeta aparecen solos en la biblioteca. Al cambiar de carpeta se pueden mover los libros.
- Conexión con Synology por QuickConnect (red local, internet o relay) o por dirección fija. Inicio de sesión desde la app, con verificación en dos pasos. La contraseña solo se guarda en memoria.
- Descarga de libros del NAS a la carpeta local, sin volver a bajar los que ya están.
- Número de versión visible en la app y en `/api/salud`.
- Protección contra publicar credenciales: hook pre-commit, revisión en GitHub Actions y configuración privada en `appsettings.Local.json`.
- Publicación automática en GitHub Releases para Windows, macOS y Linux al subir una etiqueta `vX.Y.Z`.

### Corregido
- Libros cuyo CSS tiene caracteres fuera de Latin-1 no se abrían (`btoa` en epub.js).
- Error `replaceCss` al importar libros.
