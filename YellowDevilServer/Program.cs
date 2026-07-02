using Microsoft.AspNetCore.Server.Kestrel.Core;
using YellowDevilServer.Services;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Procura appsettings/wwwroot ao lado do executável, e não na pasta
    // de onde o comando foi rodado — assim funciona de qualquer lugar
    ContentRootPath = AppContext.BaseDirectory,
});

// Duas portas, em todas as interfaces de rede (0.0.0.0):
//   5254 -> clientes gRPC (exige HTTP/2)
//   5255 -> navegadores da sala (página + WebSocket, HTTP/1.1)
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(5254, o => o.Protocols = HttpProtocols.Http2);
    kestrel.ListenAnyIP(5255, o => o.Protocols = HttpProtocols.Http1);
});

// Adiciona o suporte ao gRPC
builder.Services.AddGrpc();

var app = builder.Build();

// O serviço gRPC (a parte avaliada do trabalho)
app.MapGrpcService<YellowDevilService>();

// A página da sala (wwwroot/index.html) e o canal WebSocket dos navegadores
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    await YellowDevilService.AtenderNavegador(ws);
});

app.Run();
