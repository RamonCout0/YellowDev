#!/usr/bin/env bash
# ============================================================================
#  devil.sh — lançador das duas demos do Yellow Devil (Linux / desenvolvimento)
#  O equivalente para o notebook Windows da apresentação é o devil.ps1.
#
#    ./devil.sh grpc     Demo 1 — gRPC  (servidor + 2 clientes)
#    ./devil.sh mom      Demo 2 — MOM   (orquestrador + 2 consumidores + front)
#    ./devil.sh broker   instala/sobe o RabbitMQ (uma vez só)
#    ./devil.sh status   o que está de pé agora
#    ./devil.sh stop     derruba as demos
# ============================================================================
set -euo pipefail

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$RAIZ"

AMARELO=$'\e[1;33m'; VERMELHO=$'\e[1;31m'; VERDE=$'\e[1;32m'; CINZA=$'\e[0;90m'; FIM=$'\e[0m'
titulo() { printf '\n%s=== %s ===%s\n' "$AMARELO" "$1" "$FIM"; }
ok()     { printf '  %s[ok]%s %s\n'   "$VERDE"    "$FIM" "$1"; }
erro()   { printf '  %s[erro]%s %s\n' "$VERMELHO" "$FIM" "$1" >&2; }
info()   { printf '  %s%s%s\n'        "$CINZA"    "$1"   "$FIM"; }

PORTA_AMQP=5672      # back-end C# -> AMQP
PORTA_UI=15672       # Management UI do broker
PORTA_STOMP=15674    # front JS -> STOMP sobre WebSocket
PORTA_FRONT=8080     # http.server que serve o MomFront
FILA_SUMMONS=devil-summons

porta_aberta() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null && exec 3<&- && return 0 || return 1; }

# Abre um comando numa nova aba do terminal gráfico. Sem terminal, só instrui.
aba() {
  local nome="$1"; shift
  local cmd="$*"
  local term=""
  for t in ptyxis gnome-terminal konsole xfce4-terminal x-terminal-emulator; do
    command -v "$t" >/dev/null 2>&1 && { term="$t"; break; }
  done

  # Deixa a aba viva depois do Ctrl+C, pra plateia ler os logs finais.
  local wrap="cd '$RAIZ'; printf '\e]0;$nome\a'; $cmd; echo; echo '--- $nome encerrado (ENTER fecha) ---'; read -r"

  case "$term" in
    ptyxis)                        ptyxis --tab -d "$RAIZ" -- bash -c "$wrap" ;;
    gnome-terminal)                gnome-terminal --tab --title="$nome" --working-directory="$RAIZ" -- bash -c "$wrap" ;;
    konsole)                       konsole --new-tab --workdir "$RAIZ" -e bash -c "$wrap" ;;
    xfce4-terminal)                xfce4-terminal --tab --title="$nome" --working-directory="$RAIZ" -e "bash -c \"$wrap\"" ;;
    x-terminal-emulator)           x-terminal-emulator -e bash -c "$wrap" & ;;
    *) erro "nenhum terminal gráfico encontrado — rode à mão:"; info "$cmd"; return 0 ;;
  esac
  ok "aba '$nome' aberta"
  sleep 1   # dá tempo do processo anterior subir antes do próximo
}

compilar() {
  titulo "Compilando (restaura NuGet + gera os stubs do .proto + compila)"
  dotnet build -v quiet --nologo || { erro "build falhou"; exit 1; }
  ok "build concluído"
}

# ---------------------------------------------------------------- demo gRPC --
demo_grpc() {
  compilar
  titulo "DEMO 1 — gRPC (RPC + Protocol Buffers)"
  info "contrato : YellowDevilServer/Protos/yellow_devil.proto"
  info "servidor : porta 5254 (gRPC/HTTP2)   página da sala: porta 5255"
  info "as partículas do boss viajam como MENSAGENS DE UM STREAM RPC"
  echo
  aba "gRPC servidor"      "dotnet run --project YellowDevilServer"
  aba "gRPC cliente BOSS"  "sleep 2; dotnet run --project YellowDevilClient -- http://localhost:5254 boss"
  aba "gRPC cliente vazio" "sleep 3; dotnet run --project YellowDevilClient -- http://localhost:5254"
  echo
  ok "3 abas abertas: servidor + cliente com o boss + cliente vazio"
  info "no cliente BOSS: ENTER teleporta o monstro para o servidor"
  info "no cliente VAZIO: ENTER invoca o monstro de volta"
  info "navegador (opcional): http://localhost:5255"
}

# ----------------------------------------------------------------- demo MOM --
demo_mom() {
  if ! porta_aberta $PORTA_AMQP; then
    erro "o RabbitMQ não está de pé na porta $PORTA_AMQP"
    info "rode primeiro:  ./devil.sh broker"
    exit 1
  fi
  ok "broker respondendo em $PORTA_AMQP"
  porta_aberta $PORTA_STOMP || {
    erro "porta $PORTA_STOMP (web-stomp) fechada — o front JS não conecta"
    info "habilite o plugin:  sudo rabbitmq-plugins enable rabbitmq_web_stomp"
    exit 1
  }
  ok "web-stomp respondendo em $PORTA_STOMP"

  compilar
  titulo "DEMO 2 — MOM (fila de mensagens / RabbitMQ)"
  info "broker   : AMQP $PORTA_AMQP | STOMP/WS $PORTA_STOMP | UI $PORTA_UI (guest/guest)"
  info "as MESMAS partículas viram MENSAGENS NUMA FILA que os consumidores disputam"
  echo
  aba "MOM orquestrador" "dotnet run --project MomOrchestrator"
  aba "MOM terminal-A"   "sleep 2; dotnet run --project MomConsumer -- terminal-A"
  aba "MOM terminal-B"   "sleep 3; dotnet run --project MomConsumer -- terminal-B"
  aba "MOM front"        "cd MomFront && python3 -m http.server $PORTA_FRONT"
  echo
  sleep 1
  command -v xdg-open >/dev/null 2>&1 && {
    xdg-open "http://localhost:$PORTA_FRONT" >/dev/null 2>&1 &
    xdg-open "http://localhost:$PORTA_UI" >/dev/null 2>&1 &
    ok "navegador aberto no front e na Management UI"
  }
  ok "4 abas abertas: orquestrador + 2 consumidores + front"
  info "front: http://localhost:$PORTA_FRONT     Management UI: http://localhost:$PORTA_UI"
  info "clique INVOCAR: o boss se divide entre terminal-A, terminal-B e o navegador"
  info "Ctrl+C num consumidor no meio do teleporte => reentrega no outro"
}

# -------------------------------------------------------------------- broker --
broker() {
  titulo "RabbitMQ nativo (sem Docker)"
  if porta_aberta $PORTA_AMQP; then
    ok "já está de pé na porta $PORTA_AMQP"
  else
    if ! command -v rabbitmqctl >/dev/null 2>&1; then
      info "instalando o pacote rabbitmq-server (pede sudo)..."
      sudo apt-get update -qq
      sudo apt-get install -y rabbitmq-server
    fi
    info "habilitando os plugins management + web_stomp..."
    sudo rabbitmq-plugins enable rabbitmq_management rabbitmq_web_stomp
    sudo systemctl enable --now rabbitmq-server
    sleep 3
    porta_aberta $PORTA_AMQP && ok "broker no ar" || { erro "o broker não subiu"; info "veja: sudo systemctl status rabbitmq-server"; exit 1; }
  fi
  porta_aberta $PORTA_STOMP || { info "habilitando web_stomp..."; sudo rabbitmq-plugins enable rabbitmq_web_stomp; }
  echo
  ok "AMQP $PORTA_AMQP (C#) | STOMP/WS $PORTA_STOMP (front JS) | UI http://localhost:$PORTA_UI (guest/guest)"
}

# ------------------------------------------------------------------ invocar --
# Publica um chamado direto na fila, sem precisar do navegador. Serve para tirar
# print dos terminais e como plano B se o front falhar na apresentação.
invocar() {
  porta_aberta $PORTA_AMQP || { erro "o broker não está de pé — rode ./devil.sh broker"; exit 1; }
  local quem="${2:-terminal}"
  local resp
  resp=$(curl -s -u guest:guest -X POST \
    "http://localhost:$PORTA_UI/api/exchanges/%2f/amq.default/publish" \
    -H 'content-type: application/json' \
    -d "{\"properties\":{},\"routing_key\":\"$FILA_SUMMONS\",\"payload\":\"{\\\"chamadoPor\\\":\\\"$quem\\\"}\",\"payload_encoding\":\"string\"}")
  if [[ "$resp" == *'"routed":true'* ]]; then
    ok "boss invocado por '$quem' — as 1328 particulas estao indo para a fila"
    info "os consumidores ativos vao dividir o boss entre si"
  else
    erro "o broker recusou o chamado: $resp"
    exit 1
  fi
}

# ------------------------------------------------------------ status / stop --
status() {
  titulo "Estado"
  for par in "$PORTA_AMQP:broker AMQP (C#)" "$PORTA_STOMP:broker STOMP/WS (front JS)" "$PORTA_UI:Management UI" \
             "5254:servidor gRPC" "5255:página da sala (gRPC)" "$PORTA_FRONT:front MOM"; do
    p="$(printf '%-6s' "${par%%:*}")"; nome="${par#*:}"
    if porta_aberta "${par%%:*}"; then ok  "$p $nome"
    else                               info "$p $nome — parado"; fi
  done
}

parar() {
  titulo "Derrubando as demos"
  pkill -f "dotnet run --project YellowDevil" 2>/dev/null && ok "clientes/servidor gRPC parados" || info "gRPC já estava parado"
  pkill -f "dotnet run --project Mom"         2>/dev/null && ok "orquestrador/consumidores parados" || info "MOM já estava parado"
  pkill -f "http.server $PORTA_FRONT"         2>/dev/null && ok "front parado" || info "front já estava parado"
  info "o broker continua de pé (sudo systemctl stop rabbitmq-server para derrubar)"
}

ajuda() {
  cat <<AJUDA
${AMARELO}Yellow Devil — o mesmo boss, dois paradigmas de comunicação distribuída${FIM}

  ${VERDE}./devil.sh grpc${FIM}     Demo 1 — gRPC: partículas = mensagens de um STREAM RPC
                      abre servidor + cliente com o boss + cliente vazio

  ${VERDE}./devil.sh mom${FIM}      Demo 2 — MOM: partículas = mensagens NUMA FILA disputada
                      abre orquestrador + terminal-A + terminal-B + front web

  ${VERDE}./devil.sh broker${FIM}   instala e sobe o RabbitMQ nativo (uma vez só, pede sudo)
  ${VERDE}./devil.sh invocar${FIM}  publica um chamado na fila sem usar o navegador
                      (para tirar print dos terminais / plano B na apresentação)
  ${VERDE}./devil.sh status${FIM}   mostra quais portas estão de pé
  ${VERDE}./devil.sh stop${FIM}     derruba as demos (o broker continua)

${CINZA}No notebook Windows da apresentação, use o devil.ps1 (mesmos comandos).${FIM}
AJUDA
}

case "${1:-ajuda}" in
  grpc)    demo_grpc ;;
  mom)     demo_mom ;;
  broker)  broker ;;
  invocar) invocar "$@" ;;
  status)  status ;;
  stop)    parar ;;
  *)       ajuda ;;
esac
