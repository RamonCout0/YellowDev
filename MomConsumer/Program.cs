using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

// ============================================================================
//  MomConsumer — consumidor de TERMINAL do Yellow Devil
// ----------------------------------------------------------------------------
//  Recebe partículas da fila "devil-particles" e remonta o boss em ASCII no
//  terminal (mesmo render do trabalho de gRPC). É um dos DOIS tipos de
//  consumidor que concorrem pela fila (o outro é a página web em JavaScript).
//
//  Pontos que provam os requisitos do MOM:
//   * BasicQos(prefetch=1) + ack manual => distribuição justa entre consumidores
//     concorrentes e cada partícula processada por UM só consumidor.
//   * Ctrl+C no meio de um teleporte deixa mensagens não confirmadas, que o
//     broker REENTREGA a outro consumidor (ea.Redelivered == true).
// ============================================================================

// Nome deste terminal (aparece nos logs). args[0] ou "terminal-<PID>".
string nome = args.Length > 0 ? args[0] : $"terminal-{Environment.ProcessId}";

const string FILA_PARTICLES = "devil-particles";

// JSON camelCase — mesmo contrato do orquestrador C# e do front JavaScript.
var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

// "Trabalho" simulado por partícula (ms). Padrão 8ms; ajustável via env.
int delayProcessamentoMs =
    int.TryParse(Environment.GetEnvironmentVariable("DEVIL_CONSUMER_DELAY_MS"), out var dc) && dc >= 0 ? dc : 8;

// ---- Render ASCII copiado de YellowDevilClient/Program.cs -------------------
// (tela, DesenhaCelula, CorDoPixel, OFFSET_X, OFFSET_Y). Copiado, não importado.
const int OFFSET_X = 4;
const int OFFSET_Y = 2;
var tela = new Dictionary<(int x, int y), char>();

// Estado do teleporte atual.
long streamAtual = -1;
int recebidas = 0;

Console.OutputEncoding = Encoding.UTF8;
Console.CursorVisible = false;
Console.Clear();
Cabecalho($"[{nome}] Conectando ao broker... aguardando particulas do Yellow Devil.");
Cabecalho("Ctrl+C simula a FALHA deste consumidor: as particulas nao confirmadas voltam para a fila e sao reentregues.");

// ---- Conexão AMQP -----------------------------------------------------------
var factory = new ConnectionFactory { HostName = "localhost" };
await using var connection = await factory.CreateConnectionAsync();
await using var channel = await connection.CreateChannelAsync();

await channel.QueueDeclareAsync(
    queue: FILA_PARTICLES, durable: true, exclusive: false, autoDelete: false, arguments: null);

// ESSENCIAL: prefetch=1 => distribuição justa (round-robin) entre os
// consumidores concorrentes e reentrega visível na demo de interrupção.
await channel.BasicQosAsync(0, prefetchCount: 1, global: false);

var consumer = new AsyncEventingBasicConsumer(channel);
consumer.ReceivedAsync += async (_, ea) =>
{
    var p = JsonSerializer.Deserialize<Particle>(Encoding.UTF8.GetString(ea.Body.Span), jsonOpts);
    if (p is null)
    {
        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
        return;
    }

    // Novo teleporte => limpa a tela e recomeça a contagem.
    if (p.StreamId != streamAtual)
    {
        streamAtual = p.StreamId;
        recebidas = 0;
        tela.Clear();
        Console.Clear();
        Cabecalho($"[{nome}] Novo teleporte (stream {p.StreamId}) chamado por '{p.ChamadoPor}'. Ctrl+C simula falha.");
    }

    // Desenha o pixel recebido.
    char cor = string.IsNullOrEmpty(p.Cor) ? 'Y' : p.Cor[0];
    tela[(p.X, p.Y)] = cor;
    DesenhaCelula(p.X, p.Y);

    recebidas++;
    string log = $"[{nome}] particula {p.PartId} ({recebidas} recebidas)";
    if (ea.Redelivered) log += " << REENTREGUE apos falha!";
    Status(log);

    // Simula trabalho de "processar" a partícula antes de confirmar.
    if (delayProcessamentoMs > 0) await Task.Delay(delayProcessamentoMs);

    // Ack manual só depois de processar (autoAck: false).
    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
};

await channel.BasicConsumeAsync(FILA_PARTICLES, autoAck: false, consumer: consumer);

Cabecalho($"[{nome}] Pronto. Invoque o boss pelo front ou publique em devil-summons. Ctrl+C encerra (simula falha).");

// Roda indefinidamente.
await Task.Delay(Timeout.Infinite);

// ---- Helpers de texto -------------------------------------------------------
// Linha logo abaixo da área do sprite (36 pixels => 18 linhas de terminal).
void Cabecalho(string texto) => EscreveLinha(OFFSET_Y + 18 + 1, texto);
void Status(string texto)    => EscreveLinha(OFFSET_Y + 18 + 3, texto);

void EscreveLinha(int linha, string texto)
{
    try
    {
        Console.SetCursorPosition(0, linha);
        Console.Write(texto.PadRight(Math.Max(0, Console.WindowWidth - 1)));
    }
    catch (ArgumentOutOfRangeException) { Console.WriteLine(texto); }
}

// ---- Render ASCII (cópia de YellowDevilClient/Program.cs) -------------------
// Redesenha a célula do terminal que contém o pixel (y par em cima, y ímpar embaixo).
void DesenhaCelula(int pixelX, int pixelY)
{
    int topoY = (pixelY / 2) * 2; // pixel de cima da célula
    bool temCima = tela.TryGetValue((pixelX, topoY), out char cima);
    bool temBaixo = tela.TryGetValue((pixelX, topoY + 1), out char baixo);

    Console.SetCursorPosition(OFFSET_X + pixelX, OFFSET_Y + topoY / 2);

    if (temCima && temBaixo)
    {
        Console.ForegroundColor = CorDoPixel(cima);
        Console.BackgroundColor = CorDoPixel(baixo);
        Console.Write('▀');
        Console.ResetColor();
    }
    else if (temCima)
    {
        Console.ForegroundColor = CorDoPixel(cima);
        Console.Write('▀');
        Console.ResetColor();
    }
    else if (temBaixo)
    {
        Console.ForegroundColor = CorDoPixel(baixo);
        Console.Write('▄');
        Console.ResetColor();
    }
    else
    {
        Console.Write(' ');
    }
}

static ConsoleColor CorDoPixel(char pixel) => pixel switch
{
    'O' => ConsoleColor.DarkYellow,
    'W' => ConsoleColor.White,
    'R' => ConsoleColor.Red,
    _ => ConsoleColor.Yellow,
};

// ---- Contrato da mensagem (JSON camelCase) ----------------------------------
record Particle(long StreamId, string ChamadoPor, int PartId, int X, int Y, string Cor, int Total);
