# ============================================================================
#  devil.ps1 — lançador das duas demos do Yellow Devil (Windows / apresentação)
#  O equivalente no Linux de desenvolvimento é o devil.sh (mesmos comandos).
#
#    .\devil.ps1 grpc     Demo 1 — gRPC  (servidor + 2 clientes)
#    .\devil.ps1 mom      Demo 2 — MOM   (orquestrador + 2 consumidores + front)
#    .\devil.ps1 broker   instala/sobe o RabbitMQ (uma vez só, pede admin)
#    .\devil.ps1 status   o que está de pé agora
#    .\devil.ps1 stop     derruba as demos
#
#  Se o PowerShell bloquear o script:
#    Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
# ============================================================================
param([string]$Comando = 'ajuda', [string]$Arg = '')

$ErrorActionPreference = 'Stop'
$Raiz = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $Raiz

$PortaAmqp  = 5672    # back-end C# -> AMQP
$PortaUi    = 15672   # Management UI do broker
$PortaStomp = 15674   # front JS -> STOMP sobre WebSocket
$PortaFront = 8080    # servidor http que serve o MomFront

function Titulo($t) { Write-Host "`n=== $t ===" -ForegroundColor Yellow }
function Ok($t)     { Write-Host "  [ok] $t"   -ForegroundColor Green }
function Erro($t)   { Write-Host "  [erro] $t" -ForegroundColor Red }
function Info($t)   { Write-Host "  $t"        -ForegroundColor DarkGray }

function PortaAberta($porta) {
  try {
    $c = New-Object Net.Sockets.TcpClient
    $ok = $c.ConnectAsync('127.0.0.1', $porta).Wait(600)
    $c.Close(); return $ok
  } catch { return $false }
}

function EhAdmin {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Abre um comando numa nova aba do Windows Terminal (ou numa janela solta).
function Aba($nome, $cmd) {
  $wrap = "`$host.UI.RawUI.WindowTitle='$nome'; $cmd; Write-Host ''; Write-Host '--- $nome encerrado ---'"
  if (Get-Command wt -ErrorAction SilentlyContinue) {
    wt -w 0 nt --title $nome -d $Raiz powershell -NoExit -Command $wrap
  } else {
    Start-Process powershell -ArgumentList '-NoExit', '-Command', "Set-Location '$Raiz'; $wrap"
  }
  Ok "aba '$nome' aberta"
  Start-Sleep -Seconds 1   # dá tempo do processo anterior subir antes do próximo
}

function Compilar {
  Titulo 'Compilando (restaura NuGet + gera os stubs do .proto + compila)'
  dotnet build -v quiet --nologo
  if ($LASTEXITCODE -ne 0) { Erro 'build falhou'; exit 1 }
  Ok 'build concluído'
}

# --------------------------------------------------------------- demo gRPC --
function DemoGrpc {
  Compilar
  Titulo 'DEMO 1 — gRPC (RPC + Protocol Buffers)'
  Info 'contrato : YellowDevilServer/Protos/yellow_devil.proto'
  Info 'servidor : porta 5254 (gRPC/HTTP2)   página da sala: porta 5255'
  Info 'as partículas do boss viajam como MENSAGENS DE UM STREAM RPC'
  Write-Host ''
  Aba 'gRPC servidor'      'dotnet run --project YellowDevilServer'
  Aba 'gRPC cliente BOSS'  'Start-Sleep 2; dotnet run --project YellowDevilClient -- http://localhost:5254 boss'
  Aba 'gRPC cliente vazio' 'Start-Sleep 3; dotnet run --project YellowDevilClient -- http://localhost:5254'
  Write-Host ''
  Ok '3 abas abertas: servidor + cliente com o boss + cliente vazio'
  Info 'no cliente BOSS: ENTER teleporta o monstro para o servidor'
  Info 'no cliente VAZIO: ENTER invoca o monstro de volta'
  Info 'navegador (opcional): http://localhost:5255'
}

# ---------------------------------------------------------------- demo MOM --
function DemoMom {
  if (-not (PortaAberta $PortaAmqp)) {
    Erro "o RabbitMQ não está de pé na porta $PortaAmqp"
    Info 'rode primeiro:  .\devil.ps1 broker'
    exit 1
  }
  Ok "broker respondendo em $PortaAmqp"
  if (-not (PortaAberta $PortaStomp)) {
    Erro "porta $PortaStomp (web-stomp) fechada — o front JS não conecta"
    Info 'rode:  .\devil.ps1 broker   (como administrador)'
    exit 1
  }
  Ok "web-stomp respondendo em $PortaStomp"

  Compilar
  Titulo 'DEMO 2 — MOM (fila de mensagens / RabbitMQ)'
  Info "broker   : AMQP $PortaAmqp | STOMP/WS $PortaStomp | UI $PortaUi (guest/guest)"
  Info 'as MESMAS partículas viram MENSAGENS NUMA FILA que os consumidores disputam'
  Write-Host ''
  Aba 'MOM orquestrador' 'dotnet run --project MomOrchestrator'
  Aba 'MOM terminal-A'   'Start-Sleep 2; dotnet run --project MomConsumer -- terminal-A'
  Aba 'MOM terminal-B'   'Start-Sleep 3; dotnet run --project MomConsumer -- terminal-B'
  Aba 'MOM front'        "Set-Location MomFront; python -m http.server $PortaFront"
  Write-Host ''
  Start-Sleep -Seconds 1
  Start-Process "http://localhost:$PortaFront"
  Start-Process "http://localhost:$PortaUi"
  Ok '4 abas abertas: orquestrador + 2 consumidores + front'
  Info "front: http://localhost:$PortaFront     Management UI: http://localhost:$PortaUi"
  Info 'clique INVOCAR: o boss se divide entre terminal-A, terminal-B e o navegador'
  Info 'Ctrl+C num consumidor no meio do teleporte => reentrega no outro'
}

# ------------------------------------------------------------------ broker --
# NÃO existe pacote winget do RabbitMQ Server. A documentação oficial
# (https://www.rabbitmq.com/docs/install-windows) suporta dois caminhos no
# Windows: Chocolatey (que já resolve o Erlang sozinho) ou o instalador .exe.
# Usamos o Chocolatey; se não houver, instruímos e saímos sem mexer na máquina.
function Broker {
  Titulo 'RabbitMQ nativo (sem Docker)'

  if (PortaAberta $PortaAmqp) {
    Ok "já está de pé na porta $PortaAmqp"
  } else {
    if (-not (EhAdmin)) {
      Erro 'este comando precisa de PowerShell COMO ADMINISTRADOR'
      Info 'menu Iniciar > digite "PowerShell" > botão direito > "Executar como administrador"'
      exit 1
    }

    $servico = Get-Service -Name 'RabbitMQ*' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $servico) {
      if (-not (Get-Command choco -ErrorAction SilentlyContinue)) {
        Erro 'o RabbitMQ não está instalado e não achei o Chocolatey para instalá-lo'
        Write-Host ''
        Info 'Opção 1 — instale o Chocolatey (uma vez) e rode este comando de novo:'
        Write-Host '    Set-ExecutionPolicy Bypass -Scope Process -Force' -ForegroundColor Cyan
        Write-Host "    [System.Net.ServicePointManager]::SecurityProtocol = 3072" -ForegroundColor Cyan
        Write-Host "    iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))" -ForegroundColor Cyan
        Write-Host ''
        Info 'Opção 2 — instale na mão (Erlang PRIMEIRO, depois o RabbitMQ):'
        Write-Host '    https://www.erlang.org/downloads      (Erlang/OTP 64-bit, como admin)' -ForegroundColor Cyan
        Write-Host '    https://www.rabbitmq.com/docs/install-windows   (rabbitmq-server-*.exe)' -ForegroundColor Cyan
        Write-Host ''
        Info 'depois de qualquer uma das duas, rode:  .\devil.ps1 broker'
        exit 1
      }
      Info 'instalando o RabbitMQ via Chocolatey (ele já traz o Erlang junto)...'
      choco install rabbitmq -y
      if ($LASTEXITCODE -ne 0) { Erro 'o choco install falhou — veja a saída acima'; exit 1 }
      $servico = Get-Service -Name 'RabbitMQ*' -ErrorAction SilentlyContinue | Select-Object -First 1
    }

    if ($servico) { Start-Service $servico.Name -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 5
  }

  # Os plugins ficam num .bat dentro do sbin da versão instalada.
  $plugins = Get-ChildItem 'C:\Program Files\RabbitMQ Server\rabbitmq_server-*\sbin\rabbitmq-plugins.bat' `
                           -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($plugins) {
    Info 'habilitando os plugins management + web_stomp...'
    & $plugins.FullName enable rabbitmq_management rabbitmq_web_stomp
  } elseif (Get-Command rabbitmq-plugins -ErrorAction SilentlyContinue) {
    Info 'habilitando os plugins management + web_stomp (via PATH)...'
    rabbitmq-plugins enable rabbitmq_management rabbitmq_web_stomp
  } else {
    Erro 'não encontrei o rabbitmq-plugins.bat'
    Info 'procure a pasta sbin em: C:\Program Files\RabbitMQ Server\rabbitmq_server-*\sbin'
    Info 'e rode ali:  .\rabbitmq-plugins.bat enable rabbitmq_management rabbitmq_web_stomp'
    exit 1
  }

  $servico = Get-Service -Name 'RabbitMQ*' -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($servico) { Restart-Service $servico.Name -ErrorAction SilentlyContinue }
  Start-Sleep -Seconds 6

  if (PortaAberta $PortaAmqp) { Ok 'broker no ar' } else {
    Erro 'o broker não subiu'
    Info 'confira o serviço:  Get-Service RabbitMQ*'
    exit 1
  }
  if (-not (PortaAberta $PortaStomp)) {
    Erro "porta $PortaStomp (web-stomp) fechada — o front JS não vai conectar"
    Info 'o plugin rabbitmq_web_stomp habilitou mesmo? reinicie o serviço e tente de novo'
  }
  Write-Host ''
  Ok "AMQP $PortaAmqp (C#) | STOMP/WS $PortaStomp (front JS) | UI http://localhost:$PortaUi (guest/guest)"
}

# ----------------------------------------------------------------- invocar --
# Publica um chamado direto na fila, sem precisar do navegador. Serve para tirar
# print dos terminais e como plano B se o front falhar na apresentação.
function Invocar($quem) {
  if (-not (PortaAberta $PortaAmqp)) { Erro 'o broker não está de pé — rode .\devil.ps1 broker'; exit 1 }
  if (-not $quem) { $quem = 'terminal' }
  $par = "guest:guest"
  $b64 = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($par))
  $corpo = @{
    properties       = @{}
    routing_key      = 'devil-summons'
    payload          = (@{ chamadoPor = $quem } | ConvertTo-Json -Compress)
    payload_encoding = 'string'
  } | ConvertTo-Json -Compress
  try {
    $r = Invoke-RestMethod -Method Post -Uri "http://localhost:$PortaUi/api/exchanges/%2f/amq.default/publish" `
                           -Headers @{ Authorization = "Basic $b64" } -ContentType 'application/json' -Body $corpo
    if ($r.routed) {
      Ok "boss invocado por '$quem' — as 1328 particulas estao indo para a fila"
      Info 'os consumidores ativos vao dividir o boss entre si'
    } else { Erro "o broker nao roteou o chamado"; exit 1 }
  } catch { Erro "falha ao publicar: $_"; exit 1 }
}

# ----------------------------------------------------------- status / stop --
function Status {
  Titulo 'Estado'
  @(
    @{p = $PortaAmqp;  n = 'broker AMQP (C#)' },
    @{p = $PortaStomp; n = 'broker STOMP/WS (front JS)' },
    @{p = $PortaUi;    n = 'Management UI' },
    @{p = 5254;        n = 'servidor gRPC' },
    @{p = 5255;        n = 'página da sala (gRPC)' },
    @{p = $PortaFront; n = 'front MOM' }
  ) | ForEach-Object {
    if (PortaAberta $_.p) { Ok "$($_.p)  $($_.n)" } else { Info "$($_.p)  $($_.n) — parado" }
  }
}

function Parar {
  Titulo 'Derrubando as demos'
  # Filtra pela linha de comando: matar todo dotnet/python da máquina derrubaria
  # o VS Code e qualquer outra coisa que o apresentador tenha aberto.
  $alvos = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
           Where-Object { $_.CommandLine -match 'YellowDevilServer|YellowDevilClient|MomOrchestrator|MomConsumer|http\.server' }
  $mortos = 0
  foreach ($a in $alvos) {
    try { Stop-Process -Id $a.ProcessId -Force -ErrorAction Stop; $mortos++ } catch { }
  }
  if ($mortos -gt 0) { Ok "$mortos processo(s) das demos encerrado(s)" } else { Info 'nada rodando' }
  Info 'o broker continua de pé (Stop-Service RabbitMQ para derrubar)'
}

function Ajuda {
  Write-Host "`nYellow Devil — o mesmo boss, dois paradigmas de comunicação distribuída" -ForegroundColor Yellow
  Write-Host ''
  Write-Host '  .\devil.ps1 grpc' -ForegroundColor Green -NoNewline
  Write-Host '     Demo 1 — gRPC: partículas = mensagens de um STREAM RPC'
  Write-Host '                      abre servidor + cliente com o boss + cliente vazio'
  Write-Host ''
  Write-Host '  .\devil.ps1 mom' -ForegroundColor Green -NoNewline
  Write-Host '      Demo 2 — MOM: partículas = mensagens NUMA FILA disputada'
  Write-Host '                      abre orquestrador + terminal-A + terminal-B + front web'
  Write-Host ''
  Write-Host '  .\devil.ps1 broker' -ForegroundColor Green -NoNewline
  Write-Host '   instala e sobe o RabbitMQ nativo (uma vez só, pede admin)'
  Write-Host '  .\devil.ps1 invocar' -ForegroundColor Green -NoNewline
  Write-Host '  publica um chamado na fila sem usar o navegador'
  Write-Host '  .\devil.ps1 status' -ForegroundColor Green -NoNewline
  Write-Host '   mostra quais portas estão de pé'
  Write-Host '  .\devil.ps1 stop' -ForegroundColor Green -NoNewline
  Write-Host '     derruba as demos (o broker continua)'
  Write-Host ''
  Write-Host '  No Linux de desenvolvimento, use o ./devil.sh (mesmos comandos).' -ForegroundColor DarkGray
}

switch ($Comando.ToLower()) {
  'grpc'    { DemoGrpc }
  'mom'     { DemoMom }
  'broker'  { Broker }
  'invocar' { Invocar $Arg }
  'status'  { Status }
  'stop'    { Parar }
  default   { Ajuda }
}
