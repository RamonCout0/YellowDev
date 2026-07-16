@echo off
REM ===========================================================================
REM  devil.cmd — atalho do devil.ps1 no Windows.
REM
REM  Existe por dois motivos:
REM    1) duplo-clique num .ps1 ABRE O BLOCO DE NOTAS em vez de executar;
REM    2) a política de execução do PowerShell costuma bloquear scripts locais.
REM  Este .cmd resolve os dois: pode ser executado com duplo-clique e chama o
REM  PowerShell com a política liberada só para esta execução.
REM
REM    devil.cmd grpc      Demo 1 — gRPC
REM    devil.cmd mom       Demo 2 — MOM
REM    devil.cmd broker    instala/sobe o RabbitMQ (precisa de ADMIN)
REM    devil.cmd invocar   publica um chamado na fila
REM    devil.cmd status    o que está de pé
REM    devil.cmd stop      derruba as demos
REM ===========================================================================

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0devil.ps1" %*

REM Sem argumento = provavelmente duplo-clique: segura a janela para dar tempo
REM de ler a ajuda antes de o console fechar.
if "%~1"=="" pause
