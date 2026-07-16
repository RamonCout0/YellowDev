# Yellow Devil — Teletransporte Distribuído via Fila de Mensagens (MOM)

**Trabalho 2** de **Sistemas Distribuídos** — Opção A (Filas de Mensagens, comunicação
ponto-a-ponto) usando **RabbitMQ** como broker real. Back-end em **C#** (AMQP) e
front-end em **JavaScript puro** (STOMP sobre WebSocket) — duas linguagens e dois
protocolos diferentes falando com o **mesmo** broker e as **mesmas** filas.

O trabalho 1 (gRPC, mesmo tema) está em [README-GRPC.md](README-GRPC.md); a visão geral,
em [README.md](README.md).

---

## 1. Estudo de caso

O **Yellow Devil** é o chefe de *Mega Man* (1987) famoso por **se desmontar em
partículas amarelas**, atravessar a tela e se **remontar** do outro lado — só é
vulnerável nesse trânsito. Aqui essa mecânica vira uma metáfora de sistema
distribuído:

- Um **orquestrador C#** "desintegra" o boss: cada pixel não-vazio do sprite ASCII
  52×36 (**1328 partículas**) vira **uma mensagem** publicada na fila
  `devil-particles`.
- **Consumidores concorrentes** disputam essa fila e vão **remontando** o boss, cada
  um desenhando as partículas que pegou:
  - um **terminal C#** (render ASCII reaproveitado do trabalho de gRPC);
  - uma **página web** em JavaScript com `<canvas>`, barra de vida estilo Mega Man e
    a música do chefe.
- O usuário pode **invocar** o boss (produz um chamado) e **atirar** nele com a tecla
  **ESPAÇO** (produz um tiro). Essas ações também passam pelo broker — logo o front é
  **produtor e consumidor ao mesmo tempo**.

O "teleporte entre janelas" é implementado com **reentrega AMQP pura**: se uma janela
já tem o boss completo e chega uma partícula chamada por outra janela, ela se
desintegra e **desconecta sem confirmar (ack)** a mensagem — o broker devolve essa
partícula à fila e a **reentrega** para a janela que chamou. O boss literalmente
"teletransporta" para quem o invocou, sem nenhum roteamento direcionado, só protocolo
por convenção sobre a reentrega da fila.

---

## 2. Por que fila ponto-a-ponto (e não pub/sub)?

A regra do estudo de caso é: **cada partícula do boss deve ser processada por
exatamente UM consumidor** (senão o boss apareceria duplicado, um pixel em cada
consumidor). Esse é precisamente o modelo de **fila de trabalho (work queue)
ponto-a-ponto**:

- a fila **distribui a carga** (as 1328 partículas) entre os consumidores que estiverem
  ativos — cada mensagem vai para **um** deles (competing consumers);
- com `prefetch=1` + **ack manual**, a distribuição é justa e, se um consumidor
  **falha** no meio do trabalho (Ctrl+C), as mensagens que ele não confirmou
  **voltam para a fila** e são **reentregues automaticamente** a outro consumidor —
  nada se perde;
- em **pub/sub** cada assinante receberia **uma cópia de todas** as partículas, o boss
  seria montado inteiro em cada assinante e a ideia de "dividir o trabalho / processar
  uma única vez" desapareceria. Pub/sub serve para **notificar todos**, não para
  **repartir tarefas**.

---

## 3. Respostas às 4 perguntas do professor

**a) Qual paradigma de comunicação foi usado?**
Middleware Orientado a Mensagens (MOM) com **filas ponto-a-ponto** (message queue /
work queue) no RabbitMQ. Comunicação **assíncrona e desacoplada** entre um produtor
(orquestrador) e vários consumidores concorrentes (terminal C# + navegador JS).

**b) Por que ele é adequado a este problema?**
Porque cada partícula do boss precisa ser tratada **uma única vez** e o trabalho deve
ser **repartido** entre consumidores. A fila faz exatamente isso: entrega cada mensagem
a um só consumidor, **balanceia a carga** entre os que estão ativos e, com ack manual +
`prefetch=1`, garante **reentrega** do que um consumidor deixou de confirmar ao falhar.
Também **desacopla no tempo**: o orquestrador pode publicar mesmo sem nenhum consumidor
online (as mensagens ficam na fila durável esperando).

**c) O que mudaria se fosse pub/sub?**
Trocaríamos a fila direta por uma **exchange do tipo `fanout`**, e **cada assinante
teria sua própria fila** ligada a essa exchange. Consequência: **todos** os assinantes
receberiam **todas** as partículas — o boss inteiro seria montado em cada um, em vez de
dividido. Deixaria de haver "processado exatamente uma vez" e "divisão de carga"; passaria
a ser um modelo de **broadcast/notificação**. Bom para "avisar todo mundo que o boss
apareceu", ruim para "repartir a montagem".

**d) Como o broker desacopla os componentes?**
O RabbitMQ fica **no meio**: produtores e consumidores só conhecem **o nome da fila**,
nunca uns aos outros. Isso desacopla:
- **no tempo** — produtor e consumidor não precisam estar online juntos (filas
  duráveis guardam as mensagens);
- **no espaço** — ninguém precisa saber endereço/porta de ninguém, só do broker;
- **em linguagem e protocolo** — o back-end C# fala **AMQP** (porta 5672) e o
  navegador fala **STOMP sobre WebSocket** (porta 15674, plugin `web-stomp`), e mesmo
  assim trocam mensagens pelas **mesmas filas**, porque o broker traduz entre os
  protocolos. Os JSONs usam **camelCase** dos dois lados para não haver divergência de
  formato.

---

## 4. Componentes do projeto

| Componente         | Linguagem   | Protocolo          | Papel                                             |
|--------------------|-------------|--------------------|---------------------------------------------------|
| `MomOrchestrator/` | C#          | AMQP (5672)        | Produtor: desintegra o boss e publica partículas; guarda o HP autoritativo |
| `MomConsumer/`     | C#          | AMQP (5672)        | Consumidor de terminal (render ASCII)             |
| `MomFront/`        | JavaScript  | STOMP/WS (15674)   | Produtor **e** consumidor: invoca, desenha no `<canvas>`, atira |
| RabbitMQ           | (broker)    | AMQP + STOMP + UI  | Fila/reentrega; UI de gestão em 15672             |

### Filas (todas duráveis: `durable=true, exclusive=false, autoDelete=false`)

- **`devil-summons`** — o front publica `{ "chamadoPor": "<nome-da-janela>" }` para invocar o boss.
- **`devil-particles`** — o orquestrador publica uma mensagem por pixel:
  `{ "streamId": <long ms>, "chamadoPor": "<nome>", "partId": <int>, "x": <int>, "y": <int>, "cor": "<Y|O|W|R>", "total": <int> }`.
- **`devil-hits`** — o front publica `{ "de": "<nome>", "dano": 1 }` a cada tiro.
- **`devil-return`** — o front publica `{ "de": "<nome>" }` ao **devolver** o boss ao servidor.

### Ciclo do boss (inspirado no fluxo gRPC: servidor ↔ cliente)

O orquestrador guarda, além do HP, **onde o boss está** (`servidor` ou o nome da
janela que o invocou):

- **INVOCAR** (`devil-summons`) → o boss se desintegra no servidor e se remonta na
  janela; passa a "pertencer" a ela.
- **ATIRAR** (`devil-hits`) → baixa o HP autoritativo. Ao chegar a **0**, o boss é
  derrotado, **recupera a vida (28/28) e volta sozinho para o servidor**.
- **DEVOLVER** (`devil-return`) → a janela manda o boss de volta ao servidor (HP
  restaurado), liberando-o para outra janela invocar.

No front os botões são **contextuais**: sem o boss aparece só **INVOCAR**; com o boss
aparecem **ATIRAR** (também na tecla ESPAÇO) e **DEVOLVER AO SERVIDOR** — separados de
propósito para não devolver o boss sem querer ao tentar atirar.

---

## 5. Como executar (passo a passo)

> Pré-requisitos: **.NET SDK 10.0** e **Python 3** (só para servir a pasta do front).
> **Não usa Docker** — o RabbitMQ roda **nativo**, como serviço do sistema operacional.

### Atalho: tudo de uma vez

```bash
./devil.sh broker && ./devil.sh mom      # Linux
.\devil.ps1 broker; .\devil.ps1 mom      # Windows (o 'broker' pede ADMINISTRADOR)
```

O `broker` só precisa rodar **uma vez** na vida da máquina. O `mom` confere o broker,
compila e abre as 4 abas + o navegador. Os passos manuais equivalentes estão abaixo.

### 1) Subir o broker (uma vez só)

**Windows** — PowerShell **como administrador**:

```powershell
choco install rabbitmq -y   # o Chocolatey já instala o Erlang junto
# habilita os plugins (ajuste a versão na pasta):
& 'C:\Program Files\RabbitMQ Server\rabbitmq_server-4.1.0\sbin\rabbitmq-plugins.bat' enable rabbitmq_management rabbitmq_web_stomp
Restart-Service RabbitMQ
```

> **Não use `winget`:** o RabbitMQ Server **não tem pacote winget**. A
> [documentação oficial](https://www.rabbitmq.com/docs/install-windows) suporta só
> **Chocolatey** ou o **instalador `.exe`**. Sem o Chocolatey, instale na mão — primeiro o
> [Erlang/OTP 64-bit](https://www.erlang.org/downloads) (como admin), depois o
> `rabbitmq-server-*.exe`.

**Linux (Ubuntu/Debian)**:

```bash
sudo apt install -y rabbitmq-server
sudo rabbitmq-plugins enable rabbitmq_management rabbitmq_web_stomp
sudo systemctl enable --now rabbitmq-server
```

Os **dois plugins são obrigatórios**: `rabbitmq_management` dá a UI da porta 15672 (é o
**print do broker em execução** exigido na entrega) e `rabbitmq_web_stomp` abre a porta
15674, **sem a qual o front JavaScript não conecta**.

Abra a **Management UI** em <http://localhost:15672> (login **guest** / senha **guest**)
e confirme que o listener **web-stomp na porta 15674** está ativo.

### 2) Rodar o orquestrador (produtor / HP autoritativo)

```bash
dotnet run --project MomOrchestrator
```

Ele declara as 4 filas e fica aguardando chamados. **Teste isolado:** na Management UI,
em **Queues → devil-summons → Publish message**, publique o corpo
`{"chamadoPor":"teste"}`. Confira em **Queues → devil-particles** que aparecem **~1328
mensagens** acumuladas (ninguém consumindo ainda).

### 3) Rodar um (ou dois) consumidor(es) de terminal

```bash
dotnet run --project MomConsumer -- terminal-A
# opcional, em OUTRO terminal:
dotnet run --project MomConsumer -- terminal-B
```

Com **um** consumidor, ele **drena** o backlog e desenha o boss inteiro. Com **dois**
rodando juntos, publique outro chamado: o boss se **divide** entre `terminal-A` e
`terminal-B` (prova de que cada partícula é processada por **um só** consumidor).

### 4) Rodar o front-end (navegador)

```bash
cd MomFront
python3 -m http.server 8080
```

Abra <http://localhost:8080>.

---

## 6. Roteiro de demonstração

1. **Concorrência (duas linguagens):** deixe pelo menos um `MomConsumer` de terminal
   rodando e clique **"▶ INVOCAR O BOSS"** no navegador. O boss é **dividido** entre o
   terminal C# e o `<canvas>` do navegador — cada partícula aparece em **um só** lugar.

2. **Tiro (front produtor):** com o boss completo no navegador, aperte **ESPAÇO**. A
   barra de vida local cai e, no console do **orquestrador**, aparece
   `[ORQUESTRADOR] tiro de janela-xxxx — HP do boss: NN/28`. Prova de que o front também
   **produz** mensagens.

3. **Interrupção e reentrega:** invoque o boss com **dois** `MomConsumer` rodando e dê
   **Ctrl+C** em um deles **no meio** do teleporte. As partículas que ele não confirmou
   voltam à fila e são **reentregues** ao outro consumidor — os logs mostram
   `<< REENTREGUE apos falha!` (terminal) e `<< REENTREGUE!` (navegador).

4. **Teleporte entre duas abas (sem terminal):** feche os `MomConsumer`. Abra o front,
   invoque e deixe o boss **completo** na **aba 1**. Abra uma **aba 2** e clique
   **INVOCAR**: a aba 1 **se desintegra e se desconecta sem ack**, e o boss **reaparece
   remontado na aba 2** — a reentrega AMQP levou o boss "para quem chamou".

---

## 7. Onde ver os logs (comprovação)

- **Orquestrador (C#):** cada chamado, o total de partículas publicadas e cada tiro com
  o HP resultante.
- **MomConsumer (C#):** `[<nome>] particula <id> (<n> recebidas)`, com
  `<< REENTREGUE apos falha!` nas mensagens reentregues.
- **Front (navegador):** o `<pre id="log">` mostra cada partícula recebida (com
  `<< REENTREGUE!`), a invocação, os tiros e o teletransporte.
- **RabbitMQ Management UI** (<http://localhost:15672>): filas, taxa de entrega,
  mensagens *unacked* e *ready* em tempo real.
