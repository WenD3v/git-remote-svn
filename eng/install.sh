#!/usr/bin/env bash
# Instala o git-remote-svn no Linux: copia o executavel para ~/.local/bin e confere git/svn.
# Uso: eng/install.sh [origem] [destino]
set -euo pipefail

dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
origem="${1:-$dir/../dist/linux-x64/git-remote-svn}"
destino="${2:-$HOME/.local/bin}"

if [ ! -f "$origem" ]; then
  echo "Executavel nao encontrado em $origem. Gere com: dotnet publish src/GitRemoteSvn -c Release -r linux-x64 -o dist/linux-x64" >&2
  exit 1
fi
mkdir -p "$destino"
install -m 755 "$origem" "$destino/git-remote-svn"
echo "git-remote-svn instalado em $destino"

case ":$PATH:" in
  *":$destino:"*) ;;
  *) echo "Atencao: $destino nao esta no PATH. Acrescente ao ~/.bashrc: export PATH=\"$destino:\$PATH\"" >&2 ;;
esac

faltando=""
for cmd in git svn; do
  if command -v "$cmd" >/dev/null 2>&1; then
    echo "  $cmd: $("$cmd" --version | head -n 1)"
  else
    faltando="$faltando $cmd"
  fi
done
if [ -n "$faltando" ]; then
  echo "Nao encontrado no PATH:$faltando. O git-remote-svn precisa de git e svn (ex.: sudo apt install git subversion)." >&2
fi
echo "Pronto. Em um terminal novo: git clone svn::https://servidor/svn/repo/projeto"
