#!/usr/bin/env bash
# Revisa que no se suban a GitHub credenciales, IDs de acceso ni datos personales.
#
#   scripts/revisar-secretos.sh           revisa lo que está preparado para el commit (lo usa el hook pre-commit)
#   scripts/revisar-secretos.sh --todo    revisa todos los archivos del repositorio (lo usa GitHub Actions)
#
# Lista personal: escribe en .git/info/atril-privado (un texto por línea) tu ID de QuickConnect,
# tu usuario del NAS, dominios propios, etc. Ese archivo vive dentro de .git y nunca se sube.
set -uo pipefail

modo="${1:---preparado}"
raiz="$(git rev-parse --show-toplevel)"
cd "$raiz" || exit 2
privado="$(git rev-parse --git-path info/atril-privado)"
este="scripts/revisar-secretos.sh"

if [ "$modo" = "--todo" ]; then
  mapfile -t archivos < <(git ls-files)
else
  mapfile -t archivos < <(git diff --cached --name-only --diff-filter=ACMR)
fi

# Archivos que nunca deben estar en el repositorio
prohibidos='(^|/)(appsettings\.Local\.json|appsettings\.[^/]*\.Local\.json|secrets\.json|\.env(\.[^/]*)?|[^/]*\.(pfx|p12|pem|key|snk|keystore)|synology-[^/]*\.txt|config\.json)$'

# Contenido sospechoso (expresiones regulares extendidas)
patrones=(
  '"(Clave|Password|Contrase(n|ñ)a|Passwd|Secret|Secreto|Token|ApiKey|ClientSecret)"[[:space:]]*:[[:space:]]*"[^"]+"'
  '-----BEGIN [A-Z ]*PRIVATE KEY-----'
  'gh[pousr]_[A-Za-z0-9]{36}'
  'github_pat_[A-Za-z0-9_]{20,}'
  'AKIA[0-9A-Z]{16}'
  'xox[baprs]-[A-Za-z0-9-]{10,}'
)
# En appsettings*.json los datos de conexión deben ir vacíos (los reales van en appsettings.Local.json)
patrones_config='"(QuickConnectId|Usuario|User|Username|Url)"[[:space:]]*:[[:space:]]*"[^"]+"'

fallo=0
leer() { if [ "$modo" = "--todo" ]; then cat -- "$1" 2>/dev/null; else git show ":$1" 2>/dev/null; fi; }

for f in "${archivos[@]}"; do
  [ -z "$f" ] && continue
  [ "$f" = "$este" ] && continue
  if [[ "$f" =~ $prohibidos ]]; then
    echo "✗ $f: es un archivo privado y no se puede subir"; fallo=1; continue
  fi
  # Saltar binarios (imágenes, etc.) y archivos vacíos
  leer "$f" | grep -Iq . || continue
  contenido="$(leer "$f")" || continue

  for p in "${patrones[@]}"; do
    if linea="$(printf '%s\n' "$contenido" | grep -nE -m1 -- "$p")"; then
      echo "✗ $f:${linea%%:*}: parece una credencial"; fallo=1
    fi
  done
  if [[ "$f" =~ (^|/)appsettings[^/]*\.json$ ]]; then
    if linea="$(printf '%s\n' "$contenido" | grep -nE -m1 -- "$patrones_config")"; then
      echo "✗ $f:${linea%%:*}: los datos de conexión van en appsettings.Local.json, no aquí"; fallo=1
    fi
  fi
  if [ -s "$privado" ]; then
    if linea="$(printf '%s\n' "$contenido" | grep -nFi -m1 -f <(grep -v '^[[:space:]]*\(#\|$\)' "$privado"))"; then
      echo "✗ $f:${linea%%:*}: contiene un dato de tu lista privada (.git/info/atril-privado)"; fallo=1
    fi
  fi
done

if [ "$fallo" -ne 0 ]; then
  echo
  echo "Se bloqueó para no publicar datos privados. Quita esos datos (o el archivo) y vuelve a intentarlo."
  exit 1
fi
echo "✓ Sin credenciales ni datos privados (${#archivos[@]} archivos revisados)"
