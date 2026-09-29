#!/usr/bin/env bash
# Makes sure no credentials, access IDs or personal data get pushed to GitHub.
#
#   scripts/check-secrets.sh          checks what is staged for the commit (used by the pre-commit hook)
#   scripts/check-secrets.sh --all    checks every file in the repository (used by GitHub Actions)
#
# Private list: write in .git/info/atril-private (one entry per line) your QuickConnect ID,
# NAS username, own domains, etc. That file lives inside .git and is never pushed.
set -uo pipefail

mode="${1:---staged}"
root="$(git rev-parse --show-toplevel)"
cd "$root" || exit 2
private="$(git rev-parse --git-path info/atril-private)"
[ -s "$private" ] || private="$(git rev-parse --git-path info/atril-privado)"   # name used by earlier versions
self="scripts/check-secrets.sh"

if [ "$mode" = "--all" ]; then
  mapfile -t files < <(git ls-files)
else
  mapfile -t files < <(git diff --cached --name-only --diff-filter=ACMR)
fi

# Files that must never be in the repository
forbidden='(^|/)(appsettings\.Local\.json|appsettings\.[^/]*\.Local\.json|secrets\.json|\.env(\.[^/]*)?|[^/]*\.(pfx|p12|pem|key|snk|keystore)|synology-[^/]*\.txt|config\.json)$'

# Suspicious content (extended regular expressions; also catches common Spanish key names)
patterns=(
  '"(Password|Passwd|Secret|Token|ApiKey|ClientSecret|Clave|Contrase(n|ñ)a|Secreto)"[[:space:]]*:[[:space:]]*"[^"]+"'
  '-----BEGIN [A-Z ]*PRIVATE KEY-----'
  'gh[pousr]_[A-Za-z0-9]{36}'
  'github_pat_[A-Za-z0-9_]{20,}'
  'AKIA[0-9A-Z]{16}'
  'xox[baprs]-[A-Za-z0-9-]{10,}'
)
# In appsettings*.json connection values must stay empty (the real ones go in appsettings.Local.json)
config_pattern='"(QuickConnectId|Username|User|Usuario|Url)"[[:space:]]*:[[:space:]]*"[^"]+"'

failed=0
read_file() { if [ "$mode" = "--all" ]; then cat -- "$1" 2>/dev/null; else git show ":$1" 2>/dev/null; fi; }

for f in "${files[@]}"; do
  [ -z "$f" ] && continue
  [ "$f" = "$self" ] && continue
  if [[ "$f" =~ $forbidden ]]; then
    echo "✗ $f: private file, it can't be pushed"; failed=1; continue
  fi
  # Skip binaries (images, etc.) and empty files
  read_file "$f" | grep -Iq . || continue
  content="$(read_file "$f")" || continue

  for p in "${patterns[@]}"; do
    if line="$(printf '%s\n' "$content" | grep -nE -m1 -- "$p")"; then
      echo "✗ $f:${line%%:*}: looks like a credential"; failed=1
    fi
  done
  if [[ "$f" =~ (^|/)appsettings[^/]*\.json$ ]]; then
    if line="$(printf '%s\n' "$content" | grep -nE -m1 -- "$config_pattern")"; then
      echo "✗ $f:${line%%:*}: connection values belong in appsettings.Local.json, not here"; failed=1
    fi
  fi
  if [ -s "$private" ]; then
    if line="$(printf '%s\n' "$content" | grep -nFi -m1 -f <(grep -v '^[[:space:]]*\(#\|$\)' "$private"))"; then
      echo "✗ $f:${line%%:*}: contains an entry from your private list (.git/info/atril-private)"; failed=1
    fi
  fi
done

if [ "$failed" -ne 0 ]; then
  echo
  echo "Blocked so private data isn't published. Remove that data (or the file) and try again."
  exit 1
fi
echo "✓ No credentials or private data (${#files[@]} files checked)"
