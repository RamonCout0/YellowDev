![Capa do projeto by Ramon Couto Santos](assets/capagRPC.png)

```
Feito por Ramon Couto Santos & Aaron Goldberg Guerra.
```

# Yellow Devil — o mesmo boss, dois paradigmas de comunicação distribuída

#### Projetos da disciplina Desenvolvimento de Sistemas Distribuídos

O **Yellow Devil** é o chefe de *Mega Man* (1987) famoso por **se desmontar em
partículas**, atravessar a tela e **se remontar** do outro lado. Este repositório usa
essa mecânica como metáfora de sistema distribuído e entrega **dois trabalhos** com o
**mesmo tema**, trocando apenas a tecnologia de comunicação:

| # | Trabalho | O que a partícula do boss vira | Tecnologia | Comando | README |
|---|----------|-------------------------------|------------|---------|--------|
| 1 | **gRPC** | uma mensagem de um **stream RPC** | Protocol Buffers + HTTP/2 | `devil grpc` | [README-GRPC.md](README-GRPC.md) |
| 2 | **MOM** | uma mensagem **numa fila** disputada | RabbitMQ (AMQP + STOMP) | `devil mom` | [README-MOM.md](README-MOM.md) |

O sprite é o mesmo, o render ASCII é o mesmo, as ~1328 partículas são as mesmas. **Só o
meio de transporte muda** — e é exatamente essa a comparação que as duas apresentações
querem mostrar.

---

## Começando (3 comandos)

**Pré-requisitos:** [.NET SDK 10.0](https://dotnet.microsoft.com/download), Python 3
(só serve a pasta do front no MOM) e um terminal de 60×25 no mínimo.

### Windows (notebook da apresentação)

```powershell
.\devil.cmd broker    # uma vez só, como ADMINISTRADOR: instala o RabbitMQ (via Chocolatey)
.\devil.cmd grpc      # Demo 1
.\devil.cmd mom       # Demo 2
```

> **Use o `devil.cmd`, não o `.ps1` direto.** Duplo-clique num `.ps1` abre o **Bloco de
> Notas** em vez de executar, e a política de execução do PowerShell costuma bloquear
> scripts locais. O `.cmd` é um atalho que chama o `devil.ps1` com a política liberada.

### Linux (desenvolvimento)

```bash
./devil.sh broker     # uma vez só: apt install rabbitmq-server + plugins
./devil.sh grpc       # Demo 1
./devil.sh mom        # Demo 2
```

> **Sem Docker.** O broker roda **nativo**, como serviço do sistema: no Windows via
> **Chocolatey** (`choco install rabbitmq`, que já traz o Erlang), no Linux via `apt`. O
> `broker` só precisa rodar **uma vez** — depois ele sobe sozinho junto com a máquina.
> O RabbitMQ **não tem pacote winget**; se não quiser o Chocolatey, dá para usar o
> [instalador oficial](https://www.rabbitmq.com/docs/install-windows) (Erlang primeiro).

### Os outros comandos

| Comando | O que faz |
|---------|-----------|
| `devil grpc` | compila e abre **3 abas**: servidor gRPC + cliente com o boss + cliente vazio |
| `devil mom` | confere o broker, compila e abre **4 abas**: orquestrador + terminal-A + terminal-B + front web (e abre o navegador no front e na Management UI) |
| `devil broker` | instala/sobe o RabbitMQ e habilita os plugins `management` e `web_stomp` |
| `devil status` | mostra quais portas estão de pé |
| `devil stop` | derruba as demos (o broker continua) |

## Portas

| Porta | Quem usa | Demo |
|-------|----------|------|
| 5254 | clientes gRPC (HTTP/2) | gRPC |
| 5255 | página da sala (HTTP/1.1 + WebSocket) | gRPC |
| 5672 | back-end C# → broker (AMQP) | MOM |
| 15672 | Management UI do RabbitMQ (`guest`/`guest`) | MOM |
| 15674 | front JS → broker (STOMP sobre WebSocket) | MOM |
| 8080 | `http.server` que serve o `MomFront/` | MOM |

## Estrutura

```
devil.sh / devil.ps1        lançador das duas demos (Linux / Windows)
YellowDev.slnx              solution: pasta gRPC/ e pasta MOM/

YellowDevilServer/          [gRPC] servidor ASP.NET Core (Grpc.AspNetCore)
  Protos/yellow_devil.proto        >> o CONTRATO (a peça central do trabalho 1)
  Services/YellowDevilService.cs   implementação das 2 RPCs + canal WebSocket da sala
  wwwroot/index.html               página da sala (modo navegador)
YellowDevilClient/          [gRPC] cliente de console (Grpc.Net.Client)

MomOrchestrator/            [MOM] produtor: desintegra o boss e guarda o HP autoritativo
MomConsumer/                [MOM] consumidor de terminal (render ASCII)
MomFront/                   [MOM] front JS: produtor E consumidor (canvas + STOMP/WS)

Entrega_YellowDevil_gRPC.pdf   PDF de entrega do trabalho 1 (2 páginas)
Entrega_YellowDevil_MOM.pdf    PDF de entrega do trabalho 2 (2 páginas)
Apresentacao_YellowDevil.pdf   slides 16:9 dos DOIS trabalhos, para projetar na sala
docs/                          as fontes desses 3 PDFs  ->  ./docs/gerar-pdf.sh regera
assets/                        capa e capturas de tela
```

## Os PDFs

| Arquivo | Para que serve |
|---------|----------------|
| [Entrega_YellowDevil_gRPC.pdf](Entrega_YellowDevil_gRPC.pdf) | entrega do trabalho 1 no Moodle — problema, diagrama, 2 capturas, justificativa |
| [Entrega_YellowDevil_MOM.pdf](Entrega_YellowDevil_MOM.pdf) | entrega do trabalho 2 no Moodle — solução, abordagem, capturas, as 4 respostas |
| [Apresentacao_YellowDevil.pdf](Apresentacao_YellowDevil.pdf) | **os slides da sala** — 20 slides cobrindo os dois trabalhos, com o contrato, os diagramas e os comandos dentro |

Os três saem de `docs/` com **um comando**:

```bash
./docs/gerar-pdf.sh
```

> O gerador **falha de propósito** se uma entrega passar de 2 páginas (é a regra do
> trabalho) ou se um slide transbordar para uma página extra.

## Os dois paradigmas, lado a lado

Esta é a comparação para responder o professor nas duas apresentações:

| | **gRPC** (trabalho 1) | **MOM** (trabalho 2) |
|--|----------------------|----------------------|
| Acoplamento | cliente **conhece o endereço** do servidor | ninguém conhece ninguém — só o **nome da fila** |
| Tempo | **síncrono**: os dois têm que estar online juntos | **assíncrono**: a fila durável guarda a mensagem |
| Entrega | ponta a ponta, **1 cliente ↔ 1 servidor** | **N consumidores disputam**; cada mensagem vai para **um** |
| Contrato | `.proto` **tipado**, stub gerado pelo compilador | JSON por convenção (camelCase nos dois lados) |
| Se o outro lado cai | o stream **quebra**, a RPC dá erro | a mensagem **volta pra fila** e é **reentregue** |
| Bom para | chamada remota **tipada e de baixa latência** | **repartir trabalho** e desacoplar produtor de consumidor |
