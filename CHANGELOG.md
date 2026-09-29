# Cambios de Atril

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/).
Las versiones siguen [versionado semántico](https://semver.org/lang/es/): MAYOR.MENOR.PARCHE.

## [Sin publicar]

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
