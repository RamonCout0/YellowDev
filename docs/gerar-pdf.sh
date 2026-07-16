#!/usr/bin/env bash
# ============================================================================
#  gerar-pdf.sh — renderiza os HTMLs em PDF (via Chrome headless)
#
#    ./docs/gerar-pdf.sh     gera os 3 PDFs:
#       Entrega_YellowDevil_gRPC.pdf   <- docs/entrega-grpc.html   (máx 2 pág)
#       Entrega_YellowDevil_MOM.pdf    <- docs/entrega-mom.html    (máx 2 pág)
#       Apresentacao_YellowDevil.pdf   <- docs/apresentacao.html   (slides 16:9)
#
#  Editar o texto? Mexa no HTML correspondente e rode isto de novo.
#  Estilo das duas entregas: docs/entrega.css (compartilhado).
#
#  O limite de páginas não é decoração: nas entregas ele é a regra do trabalho,
#  e nos slides ele denuncia um slide que transbordou para uma página extra.
# ============================================================================
set -euo pipefail

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$RAIZ"

CHROME=""
for c in google-chrome chromium chromium-browser google-chrome-stable; do
  command -v "$c" >/dev/null 2>&1 && { CHROME="$c"; break; }
done
[ -z "$CHROME" ] && { echo "[erro] nenhum Chrome/Chromium encontrado" >&2; exit 1; }

gerar() {
  local html="$1" pdf="$2" limite="$3"
  [ -f "$html" ] || { echo "[erro] não achei $html" >&2; exit 1; }
  # --no-pdf-header-footer: sem o carimbo de data/URL que o Chrome põe por padrão.
  "$CHROME" --headless --disable-gpu --no-sandbox --no-pdf-header-footer \
            --print-to-pdf="$pdf" \
            --virtual-time-budget=6000 \
            "file://$RAIZ/$html" >/dev/null 2>&1

  local paginas
  paginas=$(pdfinfo "$pdf" 2>/dev/null | awk '/^Pages:/{print $2}')
  echo "[ok] $pdf  ($(du -h "$pdf" | cut -f1), ${paginas:-?} página(s))"

  if [ -n "$paginas" ] && [ "$paginas" -gt "$limite" ]; then
    echo "[ERRO] a entrega aceita no máximo $limite páginas e este PDF tem $paginas" >&2
    return 1
  fi
}

gerar "docs/entrega-grpc.html" "Entrega_YellowDevil_gRPC.pdf" 2

[ -f "assets/mom-terminais.png" ] || cat <<'AVISO'
[aviso] falta assets/mom-terminais.png (o print 2 da entrega: os dois terminais).
        Como tirar:
          1) ./devil.sh mom
          2) espere o boss se dividir entre terminal-A e terminal-B
          3) Ctrl+C num deles no meio do teleporte (aparece "<< REENTREGUE apos falha!")
          4) PrintScreen e salve em assets/mom-terminais.png
          5) rode ./docs/gerar-pdf.sh de novo — o print entra sozinho no PDF
AVISO

gerar "docs/entrega-mom.html" "Entrega_YellowDevil_MOM.pdf" 2

# Slides: 20 seções = 20 páginas. Mais que isso = algum slide transbordou.
gerar "docs/apresentacao.html" "Apresentacao_YellowDevil.pdf" 20
