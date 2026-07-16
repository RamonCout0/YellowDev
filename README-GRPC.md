# Yellow Devil — Teletransporte entre terminais via gRPC

**Trabalho 1** de Desenvolvimento de Sistemas Distribuídos: comunicação remota entre
processos com **gRPC** e contrato em **Protocol Buffers**.
O trabalho 2 (MOM, mesmo tema) está em [README-MOM.md](README-MOM.md); a visão geral,
em [README.md](README.md).

O Yellow Devil (Mega Man, 1987) se desintegra no terminal do cliente e é remontado,
partícula por partícula, no terminal do servidor — **cada partícula é uma mensagem gRPC
transmitida por streaming**. Depois ele pode ser trazido de volta, no sentido inverso.

- **Contrato:** [`YellowDevilServer/Protos/yellow_devil.proto`](YellowDevilServer/Protos/yellow_devil.proto)
- **Servidor:** [`YellowDevilServer/`](YellowDevilServer/) — ASP.NET Core (`Grpc.AspNetCore`)
- **Cliente:** [`YellowDevilClient/`](YellowDevilClient/) — console (`Grpc.Net.Client`)

---

## 1. Comandos

**Pré-requisitos:** [.NET SDK 10.0](https://dotnet.microsoft.com/download) e um terminal
de pelo menos 60 colunas × 25 linhas.

### Atalho: tudo de uma vez

```bash
./devil.sh grpc      # Linux
.\devil.ps1 grpc     # Windows
```

Compila e abre as 3 abas (servidor + cliente com o boss + cliente vazio). O que ele faz
por baixo está nos passos manuais abaixo.

### Manual: instalar dependências, gerar os stubs e compilar

```bash
dotnet build
```

> **Um único comando faz as três coisas:** restaura os pacotes NuGet, **gera os stubs** e
> compila.

### Manual: executar

**Terminal 1 — servidor** (gRPC em `0.0.0.0:5254`; página da sala em `0.0.0.0:5255`):

```bash
dotnet run --project YellowDevilServer
```

**Terminal 2 — cliente que começa com o monstro** (flag `boss`):

```bash
dotnet run --project YellowDevilClient -- http://localhost:5254 boss
```

**Terminal 3 — cliente vazio** (recebe o monstro quando invocar):

```bash
dotnet run --project YellowDevilClient -- http://localhost:5254
```

> Apenas **um** cliente deve usar a flag `boss` — é ele que nasce com o monstro.

### Controles (no terminal do cliente)

| Tecla | Ação |
|-------|------|
| `ENTER` (com o monstro) | Teleporta o Yellow Devil para o servidor |
| `ENTER` (sem o monstro) | Invoca o monstro do servidor — o próximo que pedir, leva |
| `sair` + `ENTER` | Encerra o cliente |

---

## 2. O contrato (e por que ele é assim)

```protobuf
service YellowDevilTransfer {
  rpc Teleport     (stream DevilPart) returns (ReassemblyStatus);  // ida
  rpc TeleportBack (ReturnRequest)    returns (stream DevilPart);  // volta
}
```

**Duas operações remotas**, uma em cada sentido do streaming:

- **`Teleport` — client streaming.** O boss são ~1328 partículas. Mandar uma RPC unária
  por partícula custaria 1328 idas e voltas; mandar um `repeated` de uma vez entregaria
  o boss num piscar, **sem a animação de remontagem**. O client streaming resolve os
  dois: **uma conexão só**, e o servidor desenha **cada partícula à medida que chega** —
  a desmontagem/remontagem do Yellow Devil é literalmente o stream em andamento.
- **`TeleportBack` — server streaming.** O sentido inverso, mesma justificativa: o
  cliente pede uma vez e o servidor **empurra** as partículas de volta.

**Mensagem composta:** `DevilPart` tem **4 atributos** (`part_id`, `pixel_data`,
`target_x`, `target_y`) — a posição de destino viaja **junto** com o pixel, então o
receptor não precisa de estado nenhum para saber onde encaixar cada pedaço. `part_id`
carrega a ordem, e `pixel_data` é `bytes` para não amarrar o contrato a um formato de
cor (hoje é uma letra da paleta, poderia virar RGB sem quebrar o `.proto`).

`ReassemblyStatus` (`is_complete` + `message`) fecha a ida com uma **confirmação
tipada** em vez de um retorno vazio, e `ReturnRequest.shuffle` deixa o **cliente**
decidir se as partes voltam embaralhadas — a decisão fica no contrato, não escondida no
servidor.

## 3. Como ocorre a comunicação

```
   CLIENTE (console)                          SERVIDOR (ASP.NET Core)
   Grpc.Net.Client                            Grpc.AspNetCore
        │                                              │
        │  ── Teleport(stream DevilPart) ───────────►  │   desenha cada
        │      part_id/pixel_data/target_x/target_y    │   partícula que chega
        │      … ~1328 mensagens no MESMO stream …     │
        │  ◄──────────── ReassemblyStatus ───────────  │   "reconstruído!"
        │                                              │
        │  ── TeleportBack(ReturnRequest) ──────────►  │
        │  ◄──────────── stream DevilPart ───────────  │   apaga daqui,
        │                                              │   empurra pra lá
        └──────────── HTTP/2 · porta 5254 ─────────────┘
```

O cliente abre um **canal** (`GrpcChannel.ForAddress`) e chama métodos do **stub** como
se fossem locais — a serialização Protobuf, o enquadramento HTTP/2 e a rede ficam todos
escondidos pelo código gerado. É **RPC**: a chamada parece local, a execução é remota.

O servidor guarda o boss em memória (`_armazem`) e um `_dono` protegido por `lock`, de
modo que se **dois clientes pedirem ao mesmo tempo**, só o primeiro leva o monstro (o
outro recebe um stream vazio) — a exclusão é resolvida no servidor, não no cliente.

## 4. Arquivos gerados pelo compilador Protocol Buffers

Ninguém escreve isso à mão: o pacote **`Grpc.Tools`** roda o `protoc` **no `dotnet build`**
e gera, a partir do `.proto`:

| Arquivo gerado | O que tem dentro |
|----------------|------------------|
| `YellowDevilServer/obj/Debug/net10.0/Protos/YellowDevil.cs` | classes das mensagens (`DevilPart`, `ReassemblyStatus`, `ReturnRequest`) + serialização Protobuf |
| `YellowDevilServer/obj/Debug/net10.0/Protos/YellowDevilGrpc.cs` | `YellowDevilTransfer.YellowDevilTransferBase` — a classe base que o servidor herda |
| `YellowDevilClient/obj/Debug/net10.0/YellowDevil.cs` | as mesmas classes de mensagem, do lado do cliente |
| `YellowDevilClient/obj/Debug/net10.0/YellowDevilGrpc.cs` | `YellowDevilTransferClient` — o **stub** que o cliente chama |

Quem decide o que gerar é o `GrpcServices` no `.csproj`: `Server` no servidor, `Client`
no cliente, **a partir do mesmo arquivo `.proto`**. É essa fonte única que garante que
os dois lados nunca discordem do formato.

## 5. Vantagens do gRPC sobre REST **neste** cenário

- **Streaming de verdade.** O cenário é um fluxo contínuo de ~1328 partículas. Em REST
  seriam 1328 requisições HTTP (com todo o overhead de cabeçalho e conexão) ou um único
  JSON gigante que mataria a animação. O gRPC mantém **um stream aberto** sobre HTTP/2 e
  entrega partícula a partícula.
- **Contrato tipado e verificado no compilador.** Errar um campo do `.proto` **não
  compila**. Em REST, um JSON com o campo errado só quebra em runtime, no cliente.
- **Payload binário compacto.** `DevilPart` em Protobuf são poucos bytes; o mesmo objeto
  em JSON gastaria várias vezes mais só com nomes de campo e aspas — multiplicado por
  1328, por teleporte.
- **Stub pronto.** O cliente chama `client.Teleport()`; não há URL, verbo, código de
  status nem desserialização manual para escrever.
- **HTTP/2 nativo:** uma conexão multiplexada, sem custo de handshake por partícula.

> **Onde REST ganharia:** se o consumidor fosse um navegador qualquer, ou se as chamadas
> fossem esporádicas e independentes (um CRUD). Aqui não é o caso — é fluxo contínuo
> entre dois processos que nós controlamos.

---

## 6. Executar em PCs diferentes (mesma rede)

1. No PC servidor, descubra o IP local e libere a porta no firewall
   (PowerShell **como administrador** — só na primeira vez):

```powershell
ipconfig
netsh advfirewall firewall add rule name="YellowDevil gRPC" dir=in action=allow protocol=TCP localport=5254,5255
dotnet run --project YellowDevilServer
```

2. Em cada PC cliente (clone o repositório e use o IP do servidor):

```bash
dotnet run --project YellowDevilClient -- http://192.168.0.10:5254 boss
```

## 7. Modo sala de aula (navegador — sem instalar nada)

Qualquer pessoa na mesma rede (inclusive pelo celular) abre no navegador:

```
http://IP_DO_SERVIDOR:5255
```

O botão **INVOCAR O MONSTRO** puxa o Yellow Devil do servidor para a página, e
**DEVOLVER AO SERVIDOR** manda ele de volta. Não precisa de .NET, instalação nem
permissão de administrador — só o PC do servidor precisa liberar as portas no firewall.

> O navegador é só um **portal**: as partes nunca saem do servidor, ele apenas recebe os
> pixels por WebSocket para desenhar. **A parte avaliada do trabalho é o gRPC** (porta
> 5254) — a sala é um extra de apresentação.

> **Wi-Fi de instituição:** algumas redes isolam os dispositivos entre si (client
> isolation) mesmo estando na mesma rede — nesse caso ninguém alcança o IP do servidor.
> Teste com antecedência; se falhar, use o hotspot do celular do apresentador como rede
> local no dia.
