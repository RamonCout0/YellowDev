using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

// ============================================================================
//  MomOrchestrator — o "desintegrador" do Yellow Devil
// ----------------------------------------------------------------------------
//  Papel no MOM (Middleware Orientado a Mensagens):
//   * Declara as 4 filas duráveis do estudo de caso.
//   * Consome "devil-summons": a cada chamado, quebra o boss em partículas
//     (uma por pixel do sprite), embaralha e publica todas em "devil-particles".
//   * Consome "devil-hits": mantém o HP autoritativo do boss.
//  Comunicação AMQP (porta 5672). O front usa o MESMO broker via STOMP/WS.
// ============================================================================

// ---- Sprite do Yellow Devil (Mega Man, 1987) — 52x36 ----------------------
// Copiado de YellowDevilClient/Program.cs (não importado, para não acoplar os
// projetos de gRPC ao trabalho de MOM). Paleta: Y=amarelo, O=sombra,
// W=branco, R=vermelho, '.'=vazio.
string[] sprite =
{
    "......................YYYYYYYY......................",
    "...................YYYYYYYYYYYYYY...................",
    ".................YYYYYYYYYYYYYYYYYY.................",
    "...............YYYYYYYYYYYYYYYYYYYYYY...............",
    ".............YYYYYYYYYYYYYYYYYYYYYYYYYY.............",
    "............YYYYYOYYYYYYYYYYYYYYYYOYYYYY............",
    "...........YYYYYOYYYYYYYYYYYYYYYYYYOYYYYO...........",
    "..........YYYYYOYYYYYYYYYYYYYYYYYYYYOYYYYO..........",
    ".........YYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYO.........",
    "........YYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYO........",
    "........YYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYO........",
    ".......YYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYO.......",
    ".......YYYYYYYYYYYYYOOOOOOOOOOOOYYYYYYYYYYYYO.......",
    "......YYYOYYYYYYYYWWWWWWWRRRWWWWWWYYYYYYYYOYYO......",
    "......YYOYYYYYYYYWWWWWWWRRRRRWWWWWWYYYYYYYYOYO......",
    ".....YYYOYYYYYYYYWWWWWWRRRRRRWWWWWWYYYYYYYYOYYO.....",
    ".....YYYYYYYYYYYYYWWWWWWRRRRWWWWWWYYYYYYYYYYYYO.....",
    ".....YYYYYYYYYYYYYYYOOOOOOOOOOOOYYYYYYYYYYYYYYO.....",
    "....YYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYO....",
    "...YYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYO...",
    "..YYYYYOYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYOYYYYO..",
    ".YYYYYYYOYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYOYYYYYYO.",
    "YYYYYYYYYOYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYOYYYYYYYYO",
    "YYYYYYYYYYOYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYOYYYYYYYYYO",
    "YYYYYYYYYYOYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYOYYYYYYYYYO",
    "YYYYYYYYYYOYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYOYYYYYYYYYO",
    "YYYYYYYYYYOYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYOYYYYYYYYYO",
    "YYYYYYYYYOYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYOYYYYYYYYO",
    ".YYYYYYYOYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYOYYYYYYO.",
    "..OOOOOOYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYOOOOOO..",
    "....OOOOYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYOOOO....",
    "......OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO......",
    "..............YYYYYYYYY......YYYYYYYYY..............",
    ".............YYYYYYYYYYO....YYYYYYYYYYO.............",
    ".............YYYYYYYYYYO....YYYYYYYYYYO.............",
    "..............OOOOOOOOO......OOOOOOOOO..............",
};

const int HP_MAX = 28;
// Ritmo de publicação (ms entre partículas). Padrão 12ms (~16s pro boss inteiro,
// ~8s dividido entre dois consumidores). Ajustável: DEVIL_DELAY_MS=30 dotnet run...
int delayEntreParticulasMs =
    int.TryParse(Environment.GetEnvironmentVariable("DEVIL_DELAY_MS"), out var d) && d >= 0 ? d : 12;

// Nomes das filas (todas duráveis).
const string FILA_SUMMONS   = "devil-summons";
const string FILA_PARTICLES = "devil-particles";
const string FILA_HITS      = "devil-hits";
const string FILA_RETURN    = "devil-return"; // devolver o boss ao servidor

// JSON em camelCase nos dois lados (C# e JavaScript) para não haver divergência.
var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

// Lista fixa de partículas (uma por pixel não-vazio do sprite).
var particulasBase = new List<(int x, int y, char cor)>();
for (int y = 0; y < sprite.Length; y++)
    for (int x = 0; x < sprite[y].Length; x++)
        if (sprite[y][x] != '.')
            particulasBase.Add((x, y, sprite[y][x]));

int total = particulasBase.Count;
var random = new Random();

// Estado autoritativo do boss. Protegido por lock porque os handlers das
// filas podem rodar concorrentemente.
//   hp        -> vida atual (0..28)
//   bossLocal -> onde o boss está: "servidor" ou o nome da janela que o invocou.
int hp = HP_MAX;
string bossLocal = "servidor";
var hpLock = new object();

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=== ORQUESTRADOR do Yellow Devil (MOM / RabbitMQ) ===");
Console.WriteLine($"Sprite tem {total} particulas nao-vazias.");
Console.WriteLine($"Delay entre particulas: {delayEntreParticulasMs}ms (env DEVIL_DELAY_MS para ajustar).");

// ---- Conexão AMQP -----------------------------------------------------------
var factory = new ConnectionFactory { HostName = "localhost" };
await using var connection = await factory.CreateConnectionAsync();
await using var channel = await connection.CreateChannelAsync();

// Declara as 4 filas (duráveis).
foreach (var fila in new[] { FILA_SUMMONS, FILA_PARTICLES, FILA_HITS, FILA_RETURN })
    await channel.QueueDeclareAsync(
        queue: fila, durable: true, exclusive: false, autoDelete: false, arguments: null);
Console.WriteLine("[ORQUESTRADOR] Filas declaradas: devil-summons, devil-particles, devil-hits, devil-return.");

// Só uma mensagem de summons por vez (publicar tudo pode demorar).
await channel.BasicQosAsync(0, prefetchCount: 1, global: false);

// ---- Consumidor de "devil-summons": invoca / desintegra o boss --------------
var summonsConsumer = new AsyncEventingBasicConsumer(channel);
summonsConsumer.ReceivedAsync += async (_, ea) =>
{
    string corpo = Encoding.UTF8.GetString(ea.Body.Span);
    string chamadoPor;
    try
    {
        var chamado = JsonSerializer.Deserialize<Summons>(corpo, jsonOpts);
        chamadoPor = chamado?.ChamadoPor ?? "?";
    }
    catch (JsonException)
    {
        Console.WriteLine($"[ORQUESTRADOR] chamado invalido ignorado: {corpo}");
        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
        return;
    }

    long streamId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    Console.WriteLine($"[ORQUESTRADOR] chamado de '{chamadoPor}' — desintegrando o boss (stream {streamId})...");

    // HP volta ao máximo e o boss passa a "pertencer" a quem invocou.
    lock (hpLock) { hp = HP_MAX; bossLocal = chamadoPor; }

    // Embaralha as partículas para o efeito de desintegração/remontagem.
    var ordem = particulasBase.OrderBy(_ => random.Next()).ToList();

    for (int i = 0; i < ordem.Count; i++)
    {
        var (x, y, cor) = ordem[i];
        var particula = new Particle(streamId, chamadoPor, i, x, y, cor.ToString(), total);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(particula, jsonOpts);
        var props = new BasicProperties { Persistent = true };
        await channel.BasicPublishAsync(
            exchange: string.Empty, routingKey: FILA_PARTICLES,
            mandatory: false, basicProperties: props, body: body);

        if (delayEntreParticulasMs > 0)
            await Task.Delay(delayEntreParticulasMs);
    }

    Console.WriteLine($"[ORQUESTRADOR] {total} particulas publicadas em '{FILA_PARTICLES}' (stream {streamId}). HP {HP_MAX}/{HP_MAX}. Boss agora com '{chamadoPor}'.");

    // Ack SÓ depois de publicar tudo: se o orquestrador cair no meio, o
    // chamado é reentregue e o teleporte recomeça do zero.
    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
};

// ---- Consumidor de "devil-hits": mantém o HP do boss ------------------------
var hitsConsumer = new AsyncEventingBasicConsumer(channel);
hitsConsumer.ReceivedAsync += async (_, ea) =>
{
    string corpo = Encoding.UTF8.GetString(ea.Body.Span);
    string de;
    int dano;
    try
    {
        var tiro = JsonSerializer.Deserialize<Hit>(corpo, jsonOpts);
        de = tiro?.De ?? "?";
        dano = tiro?.Dano ?? 1;
    }
    catch (JsonException)
    {
        Console.WriteLine($"[ORQUESTRADOR] tiro invalido ignorado: {corpo}");
        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
        return;
    }

    int hpAtual;
    bool derrotado = false;
    lock (hpLock)
    {
        hp = Math.Max(0, hp - dano);
        hpAtual = hp;
        // HP zerou => o boss recupera a vida cheia e FOGE de volta para o servidor.
        if (hp == 0) { hp = HP_MAX; bossLocal = "servidor"; derrotado = true; }
    }
    Console.WriteLine($"[ORQUESTRADOR] tiro de {de} — HP do boss: {hpAtual}/{HP_MAX}");
    if (derrotado)
        Console.WriteLine($"[ORQUESTRADOR] boss DERROTADO por {de} — recupera vida ({HP_MAX}/{HP_MAX}) e volta para o servidor.");

    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
};

// ---- Consumidor de "devil-return": devolve o boss ao servidor ---------------
var returnConsumer = new AsyncEventingBasicConsumer(channel);
returnConsumer.ReceivedAsync += async (_, ea) =>
{
    string corpo = Encoding.UTF8.GetString(ea.Body.Span);
    string de = "?";
    try { de = JsonSerializer.Deserialize<Return>(corpo, jsonOpts)?.De ?? "?"; }
    catch (JsonException) { }

    lock (hpLock) { hp = HP_MAX; bossLocal = "servidor"; }
    Console.WriteLine($"[ORQUESTRADOR] boss DEVOLVIDO ao servidor por {de} — HP recuperado para {HP_MAX}/{HP_MAX}. Pronto para nova invocacao.");

    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
};

// Ack manual explícito em todos (autoAck: false).
await channel.BasicConsumeAsync(FILA_SUMMONS, autoAck: false, consumer: summonsConsumer);
await channel.BasicConsumeAsync(FILA_HITS, autoAck: false, consumer: hitsConsumer);
await channel.BasicConsumeAsync(FILA_RETURN, autoAck: false, consumer: returnConsumer);

Console.WriteLine("[ORQUESTRADOR] Aguardando chamados (devil-summons), tiros (devil-hits) e devolucoes (devil-return). Ctrl+C para sair.");

// Roda indefinidamente.
await Task.Delay(Timeout.Infinite);

// ---- Contratos das mensagens (JSON camelCase) -------------------------------
record Summons(string ChamadoPor);
record Hit(string De, int Dano);
record Return(string De);
record Particle(long StreamId, string ChamadoPor, int PartId, int X, int Y, string Cor, int Total);
