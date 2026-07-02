using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Megaman.Boss; // O pacote do nosso proto

namespace YellowDevilServer.Services;

public class YellowDevilService : YellowDevilTransfer.YellowDevilTransferBase
{
    private const int OFFSET_X = 4; // onde o sprite é remontado na tela
    private const int OFFSET_Y = 2;

    // O monstro fica guardado em memória. "_dono" diz onde ele está agora:
    //   "fora"      -> com algum cliente gRPC (ou ainda não chegou)
    //   "servidor"  -> aqui, desenhado neste console
    //   "navegador" -> emprestado a um navegador da sala (as partes continuam aqui)
    private static readonly List<DevilPart> _armazem = new();
    private static readonly Dictionary<(int x, int y), char> _tela = new();
    private static readonly object _trava = new();
    private static string _dono = "fora";
    private static WebSocket? _navegadorDono;

    // ---- IDA: um cliente gRPC deposita o monstro aqui (client streaming) ----
    public override async Task<ReassemblyStatus> Teleport(
        IAsyncStreamReader<DevilPart> requestStream,
        ServerCallContext context)
    {
        // Prepara a tela do terminal
        Console.Clear();
        Console.CursorVisible = false; // Esconde a barrinha piscando

        lock (_trava)
        {
            _armazem.Clear();
            _tela.Clear();
            _dono = "fora"; // só vira "servidor" quando o monstro chegar inteiro
        }

        Console.SetCursorPosition(0, 0);
        Console.WriteLine("Aguardando partículas...");

        int maiorLinha = 0;

        try
        {
            await foreach (var part in requestStream.ReadAllAsync())
            {
                char pixel = part.PixelData.Length > 0 ? (char)part.PixelData[0] : 'Y';

                _armazem.Add(part);
                _tela[(part.TargetX, part.TargetY)] = pixel;
                DesenhaCelula(part.TargetX, part.TargetY);

                maiorLinha = Math.Max(maiorLinha, OFFSET_Y + part.TargetY / 2);

                // O timer do Windows tem resolução de ~15ms, então com 1300+
                // partículas um delay por partícula ficaria lento demais
                if (_armazem.Count % 4 == 0) await Task.Delay(1);
            }
        }
        catch
        {
            // O cliente caiu no meio do teleporte: descarta o monstro parcial
            // (o cliente restaura a cópia dele, então nada se perde)
            lock (_trava)
            {
                _armazem.Clear();
                _tela.Clear();
            }
            Console.Clear();
            Console.WriteLine("Teleporte interrompido — as partículas se dispersaram...");
            throw;
        }

        lock (_trava) _dono = "servidor";

        Console.ResetColor();
        Console.SetCursorPosition(0, Math.Min(maiorLinha + 2, Console.WindowHeight - 1));
        return new ReassemblyStatus
        {
            IsComplete = true,
            Message = $"Yellow Devil reconstruído com {_armazem.Count} partes!"
        };
    }

    // ---- SAÍDA: o próximo cliente gRPC que pedir leva o monstro (server streaming) ----
    public override async Task TeleportBack(
        ReturnRequest request,
        IServerStreamWriter<DevilPart> responseStream,
        ServerCallContext context)
    {
        // Retira o monstro do armazém de forma atômica: se dois clientes
        // pedirem ao mesmo tempo, só o primeiro leva (o outro recebe vazio).
        // Se um navegador estiver com ele, também não sai daqui.
        List<DevilPart> partes = new();
        lock (_trava)
        {
            if (_dono == "servidor")
            {
                partes = request.Shuffle
                    ? _armazem.OrderBy(_ => Random.Shared.Next()).ToList()
                    : _armazem.ToList();
                _armazem.Clear();
                _dono = "fora";
            }
        }

        if (partes.Count == 0) return; // não tem monstro disponível — stream vazio

        int enviadas = 0;
        foreach (var part in partes)
        {
            // Envia a parte para o cliente que requisitou
            await responseStream.WriteAsync(part);

            // E apaga do terminal daqui conforme ela "parte"
            _tela.Remove((part.TargetX, part.TargetY));
            DesenhaCelula(part.TargetX, part.TargetY);

            if (++enviadas % 4 == 0) await Task.Delay(1);
        }

        Console.SetCursorPosition(0, 0);
        Console.Write("O Yellow Devil foi embora...".PadRight(Console.WindowWidth - 1));
    }

    // ---- SALA DE AULA: navegadores conectados via WebSocket (porta 5255) ----
    // O navegador é só um "portal": as partes nunca saem do servidor,
    // ele apenas recebe os pixels para desenhar e devolve o controle depois.
    public static async Task AtenderNavegador(WebSocket ws)
    {
        var buffer = new byte[1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var recebido = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (recebido.MessageType == WebSocketMessageType.Close) break;

                string comando = Encoding.UTF8.GetString(buffer, 0, recebido.Count).Trim();

                if (comando == "claim") await EntregarAoNavegador(ws);
                else if (comando == "release") await ReceberDoNavegador(ws, navegadorVivo: true);
            }
        }
        catch { /* navegador caiu — tratado abaixo */ }

        // Se a página fechou segurando o monstro, ele volta para o servidor
        bool segurava;
        lock (_trava) segurava = _dono == "navegador" && _navegadorDono == ws;
        if (segurava) await ReceberDoNavegador(ws, navegadorVivo: false);
    }

    private static async Task EntregarAoNavegador(WebSocket ws)
    {
        bool consegui = false;
        lock (_trava)
        {
            if (_dono == "servidor")
            {
                _dono = "navegador";
                _navegadorDono = ws;
                consegui = true;
            }
        }

        if (!consegui)
        {
            await Enviar(ws, new { t = "m", s = "O monstro não está no servidor agora..." });
            return;
        }

        await Enviar(ws, new { t = "take" });

        int enviadas = 0;
        foreach (var part in _armazem.OrderBy(_ => Random.Shared.Next()).ToList())
        {
            char pixel = part.PixelData.Length > 0 ? (char)part.PixelData[0] : 'Y';

            // Apaga daqui e manda o pixel para a página desenhar
            _tela.Remove((part.TargetX, part.TargetY));
            DesenhaCelula(part.TargetX, part.TargetY);
            await Enviar(ws, new { t = "p", x = part.TargetX, y = part.TargetY, c = pixel.ToString() });

            if (++enviadas % 4 == 0) await Task.Delay(1);
        }

        Console.SetCursorPosition(0, 0);
        Console.Write("O monstro está passeando pela sala...".PadRight(Console.WindowWidth - 1));
    }

    private static async Task ReceberDoNavegador(WebSocket ws, bool navegadorVivo)
    {
        bool consegui = false;
        lock (_trava)
        {
            if (_dono == "navegador" && _navegadorDono == ws)
            {
                _dono = "servidor";
                _navegadorDono = null;
                consegui = true;
            }
        }

        if (!consegui) return;

        Console.SetCursorPosition(0, 0);
        Console.Write("Aguardando partículas...".PadRight(Console.WindowWidth - 1));

        int voltas = 0;
        foreach (var part in _armazem.OrderBy(_ => Random.Shared.Next()).ToList())
        {
            char pixel = part.PixelData.Length > 0 ? (char)part.PixelData[0] : 'Y';

            _tela[(part.TargetX, part.TargetY)] = pixel;
            DesenhaCelula(part.TargetX, part.TargetY);
            if (navegadorVivo) await Enviar(ws, new { t = "x", x = part.TargetX, y = part.TargetY });

            if (++voltas % 4 == 0) await Task.Delay(1);
        }

        if (navegadorVivo) await Enviar(ws, new { t = "gone" });
    }

    private static async Task Enviar(WebSocket ws, object mensagem)
    {
        if (ws.State != WebSocketState.Open) return;
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(mensagem);
            await ws.SendAsync(json, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { /* navegador caiu no meio do envio — o monstro volta na desconexão */ }
    }

    // Cada célula do terminal guarda 2 pixels empilhados (y par em cima, ímpar embaixo),
    // desenhados com ▀/▄: pixel de cima = cor da letra, pixel de baixo = cor do fundo
    private static void DesenhaCelula(int pixelX, int pixelY)
    {
        int topoY = (pixelY / 2) * 2; // pixel de cima da célula
        int celX = OFFSET_X + pixelX;
        int celY = OFFSET_Y + topoY / 2;

        // Ignora pixels que cairiam fora da janela do console
        if (celX < 0 || celX >= Console.WindowWidth || celY < 0 || celY >= Console.WindowHeight)
            return;

        bool temCima = _tela.TryGetValue((pixelX, topoY), out char cima);
        bool temBaixo = _tela.TryGetValue((pixelX, topoY + 1), out char baixo);

        Console.SetCursorPosition(celX, celY);

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

    // O pixel_data carrega um byte com a letra da paleta: Y, O, W ou R
    private static ConsoleColor CorDoPixel(char pixel) => pixel switch
    {
        'O' => ConsoleColor.DarkYellow,
        'W' => ConsoleColor.White,
        'R' => ConsoleColor.Red,
        _ => ConsoleColor.Yellow,
    };
}
