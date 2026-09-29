# Publicar Atril en la Microsoft Store

La app de Windows está en esta carpeta (`windows/`). Es una ventana con WebView2 (el motor de Edge)
que muestra la misma app web y lleva dentro el servidor de Atril, sin navegador, consola ni puerto abierto a la red.

## 1. Probarla en tu PC

1. Activa el **Modo de desarrollador**: *Configuración → Sistema → Para programadores*.
2. En PowerShell, en la raíz del proyecto:

   ```powershell
   ./windows/empaquetar.ps1 -Probar
   ```

3. Busca **Atril** en el menú Inicio. Prueba: abrir un EPUB, doble clic en un `.epub` del Explorador,
   *Cambiar carpeta → Elegir con Windows…*, conexión a Synology.
4. Para quitarla: `Get-AppxPackage Atril.Desarrollo | Remove-AppxPackage`.

También puedes ejecutarla sin empaquetar desde Rider o Visual Studio abriendo `windows/Atril.Windows.csproj`.

## 2. Cuenta y nombre en Partner Center (una sola vez)

1. Crea la cuenta de desarrollador individual (gratuita) en <https://storedeveloper.microsoft.com>. Pide verificar tu identidad.
2. En [Partner Center](https://partner.microsoft.com/dashboard/apps-and-games/overview) → **Nuevo producto → Aplicación MSIX o PWA**.
3. **Reserva el nombre**. Si "Atril" está tomado, prueba "Atril Reader" o "Atril EPUB".
4. Abre **Administración de productos → Identidad del producto** y copia:
   - `Package/Identity/Name` → variable **MSIX_IDENTIDAD**
   - `Package/Identity/Publisher` (empieza con `CN=`) → variable **MSIX_EDITOR**
   - `Package/Properties/PublisherDisplayName` → variable **MSIX_EDITOR_NOMBRE**
   - El nombre reservado → variable **MSIX_NOMBRE**
5. En GitHub: *Settings → Secrets and variables → Actions → **Variables** → New repository variable*, y crea las cuatro.
   No son secretos (aparecen dentro del paquete publicado), por eso van como variables y no en el código.

## 3. Generar el paquete

Cada versión etiquetada (`./scripts/nueva-version.ps1 X.Y.Z` y `git push --follow-tags`) crea en la
Release de GitHub el archivo `Atril_X.Y.Z.0.msixbundle` (x64 + ARM64) con tu identidad de la Tienda.

También puedes crearlo en tu PC (requiere el Windows SDK):

```powershell
./windows/empaquetar.ps1 -Identidad "..." -Editor "CN=..." -EditorNombre "..." -Nombre "Atril"
```

No hace falta firmarlo: la Tienda lo firma al certificarlo.

## 4. Crear el envío

En Partner Center → tu app → **Iniciar el envío**:

| Sección | Qué poner |
|---|---|
| Precios y disponibilidad | Gratis, todos los mercados (o los que quieras) |
| Propiedades | Categoría **Libros y referencia**. Marca que la app accede a la red |
| Clasificación por edad | Cuestionario IARC: sin violencia, sin contenido para adultos, sin compras, sin interacción entre usuarios, sin compartir ubicación |
| Paquetes | Sube el `.msixbundle` |
| Descripción de la tienda | Ver textos abajo, capturas y la política de privacidad |
| Notas para la certificación | Ver abajo |

**Política de privacidad (URL):**
`https://github.com/jdeleonferreira/personal-epub-reader/blob/main/PRIVACIDAD.md`

**Capturas:** mínimo 1, recomendado 4, 1366×768 o mayor. Sugeridas: biblioteca con portadas,
lector en modo claro, lector en modo noche, explorador de Synology.

### Notas para la certificación (en inglés, las leen revisores)

```
Atril is an EPUB reader. To test it, click "Abrir EPUB" and open any .epub file, or drag one into the window.
The Synology button requires a Synology NAS and is optional; the rest of the app works without it.
runFullTrust: Atril is a packaged .NET desktop app (WinForms + WebView2). It runs a local HTTP server
bound to 127.0.0.1 on a random port, reachable only from its own window (protected with a per-session token),
to read and save books in the folder chosen by the user and to talk to the user's own Synology NAS.
privateNetworkClientServer: needed to reach the user's NAS on the home network.
```

## Textos de la ficha

**Nombre:** Atril

**Descripción breve:** Lee tus libros EPUB con calma, en tu PC, y ábrelos desde tu Synology.

**Descripción:**

> Atril es un lector de libros EPUB sencillo y cuidado, pensado para leer largo rato.
>
> • Tu biblioteca en una carpeta de tu PC: lo que abras o descargues se guarda ahí, y lo que copies a la carpeta aparece solo.
> • Lectura por páginas o desplazamiento, con fondo claro, sepia o noche, tipo y tamaño de letra e interlineado.
> • Retoma cada libro donde lo dejaste.
> • Índice, barra de progreso y porcentaje leído.
> • Conexión con tu NAS Synology por QuickConnect: navega tus carpetas y abre los libros directamente. La contraseña nunca se guarda.
> • Doble clic en cualquier archivo .epub para abrirlo en Atril.
>
> Sin cuentas, sin publicidad y sin recopilar datos.
> Atril no abre libros protegidos con DRM (por ejemplo, los comprados en Kindle o con Adobe DRM).

**Funciones (hasta 20):**
- Biblioteca de libros EPUB en la carpeta que elijas
- Modo páginas o desplazamiento continuo
- Fondos claro, sepia y noche
- Tipo y tamaño de letra e interlineado ajustables
- Recuerda la página de cada libro
- Índice y barra de progreso
- Conexión con Synology por QuickConnect
- Abre archivos .epub con doble clic

**Palabras clave:** epub, lector, libros, ebook, synology, nas, lectura

**Requisitos:** Windows 10 versión 1809 o posterior. Usa Microsoft Edge WebView2 Runtime, incluido en Windows 10 y 11.

## Cosas a tener en cuenta

- **runFullTrust** es una capacidad restringida: la Tienda la aprueba para apps de escritorio empaquetadas,
  pero pide la justificación de las notas de arriba.
- Si la certificación reporta algo, el informe dice qué prueba falló; corrige, sube la versión (`nueva-version.ps1`) y vuelve a enviar.
- La versión del paquete es `MAYOR.MENOR.PARCHE.0`: cada envío debe tener una versión mayor que el anterior.
