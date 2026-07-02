using Grpc.Core;
using Grpc.Net.Client;
using Megaman.Boss;

// Uso: dotnet run --project YellowDevilClient -- [endereco] [boss]
//   endereco: onde está o servidor (padrão: http://localhost:5254)
//   boss:     se presente, este cliente COMEÇA com o Yellow Devil
string endereco = args.FirstOrDefault(a => a.StartsWith("http")) ?? "http://localhost:5254";
bool comecaComMonstro = args.Contains("boss");

Console.CursorVisible = false;
Console.Clear();

// 1. Conecta ao Servidor
using var channel = GrpcChannel.ForAddress(endereco);
var client = new YellowDevilTransfer.YellowDevilTransferClient(channel);

// 2. Sprite do Yellow Devil (Mega Man, 1987) — 52x36 pixels
// Paleta: Y = amarelo | O = sombra (amarelo escuro) | W = branco | R = vermelho | . = vazio
// Cada célula do terminal guarda DOIS pixels empilhados, desenhados com os
// meios-blocos ▀/▄ (pixel de cima = cor da letra, pixel de baixo = cor do fundo).
// Só o cliente "boss" usa o sprite — os outros recebem os pixels pela rede.
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

const int OFFSET_X = 4; // posição do sprite na tela (em células do terminal)
const int OFFSET_Y = 2;

// "Framebuffer": guarda quais pixels existem e a letra da paleta de cada um
var tela = new Dictionary<(int x, int y), char>();

// 3. Só quem nasce como "boss" começa com o monstro desenhado
if (comecaComMonstro)
{
    for (int y = 0; y < sprite.Length; y++)
        for (int x = 0; x < sprite[y].Length; x++)
            if (sprite[y][x] != '.')
                tela[(x, y)] = sprite[y][x];

    RedesenhaTudo();
}

int linhaTexto = OFFSET_Y + (sprite.Length / 2) + 1;
var random = new Random();
string aviso = comecaComMonstro ? "O Yellow Devil está aqui." : "Terminal vazio.";

// 4. O ciclo: se o monstro está aqui, dá pra enviar; se não, dá pra invocar
while (true)
{
    bool tenhoOMonstro = tela.Count > 0;
    Mensagem($"{aviso} ENTER = {(tenhoOMonstro ? "enviar para o servidor" : "invocar do servidor")} (ou 'sair')");
    if ((Console.ReadLine() ?? "").Trim().Equals("sair", StringComparison.OrdinalIgnoreCase))
        break;

    if (tenhoOMonstro)
    {
        // ---- IDA (client streaming): deposita o monstro no servidor ----
        Mensagem("Teleportando...");

        // Cópia de segurança: se a rede falhar no meio, o monstro volta inteiro
        var backup = new Dictionary<(int x, int y), char>(tela);

        try
        {
            var partes = tela
                .Select((p, i) => new DevilPart
                {
                    PartId = i,
                    TargetX = p.Key.x,
                    TargetY = p.Key.y,
                    PixelData = Google.Protobuf.ByteString.CopyFrom((byte)p.Value)
                })
                .OrderBy(_ => random.Next()) // embaralha para o efeito de desintegração
                .ToList();

            using var call = client.Teleport();

            int enviadas = 0;
            foreach (var part in partes)
            {
                // Envia o pixel para o servidor
                await call.RequestStream.WriteAsync(part);

                // Remove o pixel do framebuffer e redesenha só a célula afetada
                tela.Remove((part.TargetX, part.TargetY));
                DesenhaCelula(part.TargetX, part.TargetY);

                // O timer do Windows tem resolução de ~15ms, então com 1300+
                // partículas um delay por partícula ficaria lento demais
                if (++enviadas % 4 == 0) await Task.Delay(1);
            }

            await call.RequestStream.CompleteAsync();

            // ESPERA a resposta do servidor antes de seguir. Sem este await a
            // chamada é abortada e o servidor quebra com
            // "Can't read messages after the request is complete".
            ReassemblyStatus status = await call;
            aviso = $"Servidor: {status.Message}";
        }
        catch (RpcException)
        {
            tela = backup;
            RedesenhaTudo();
            aviso = "Falha na conexão — o monstro voltou! O servidor está de pé?";
        }
    }
    else
    {
        // ---- VINDA (server streaming): tenta reivindicar o monstro ----
        Mensagem("Invocando...");

        int recebidas = 0;
        try
        {
            using var volta = client.TeleportBack(new ReturnRequest { Shuffle = true });

            await foreach (var part in volta.ResponseStream.ReadAllAsync())
            {
                char pixel = part.PixelData.Length > 0 ? (char)part.PixelData[0] : 'Y';
                tela[(part.TargetX, part.TargetY)] = pixel;
                DesenhaCelula(part.TargetX, part.TargetY);
                recebidas++;
            }

            aviso = recebidas > 0
                ? $"Ele chegou com {recebidas} partes!"
                : "O servidor não está com o monstro agora...";
        }
        catch (RpcException)
        {
            aviso = recebidas > 0
                ? $"Conexão caiu no meio — chegaram só {recebidas} partes!"
                : "Não consegui falar com o servidor... confira o endereço/IP.";
        }
    }
}

Mensagem("Até a próxima!");
Console.SetCursorPosition(0, linhaTexto + 1);
Console.CursorVisible = true;

// Redesenha a área inteira do sprite
void RedesenhaTudo()
{
    for (int y = 0; y < sprite.Length; y += 2)
        for (int x = 0; x < sprite[y].Length; x++)
            DesenhaCelula(x, y);
}

// Escreve na linha de status, limpando o que estava lá antes
void Mensagem(string texto)
{
    Console.SetCursorPosition(0, linhaTexto);
    Console.Write(texto.PadRight(Console.WindowWidth - 1));
    Console.SetCursorPosition(Math.Min(texto.Length + 1, Console.WindowWidth - 1), linhaTexto);
}

// Redesenha a célula do terminal que contém o pixel (y par em cima, y ímpar embaixo)
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
    }
    else if (temBaixo)
    {
        Console.ForegroundColor = CorDoPixel(baixo);
        Console.Write('▄');
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
